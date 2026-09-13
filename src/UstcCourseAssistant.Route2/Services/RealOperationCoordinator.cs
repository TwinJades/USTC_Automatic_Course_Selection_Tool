using System.IO;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public sealed class UnresolvedOperationException(string message) : InvalidOperationException(message);
public sealed class RealOperationBusyException(string message) : InvalidOperationException(message);
public sealed class WriteResultUncertainException(string message) : InvalidOperationException(message);

public enum InterruptedRestoreOutcome
{
    NotEligible,
    IdentityMismatch,
    AuthenticationRequired,
    StateReadFailed,
    StateNotEligible,
    Restored,
    Incomplete,
    ResultUnknown
}

public sealed record InterruptedRestoreResult(InterruptedRestoreOutcome Outcome, string Message);

public interface IProtectedMutationExecutor
{
    void BeginWorkflow(string owner, long studentId, long turnId, string targetCode, IEnumerable<string> relatedCodes);
    Task<VerifiedMutationResult> ExecuteAndVerifyAsync(
        CourseMutationKind kind,
        LessonInfo lesson,
        string stepName,
        CancellationToken cancellationToken);
    void RecordConfirmedDropBeforeTarget(LessonInfo lesson);
    void MarkTargetRequestWillStart();
    void CompleteWorkflow(string note);
    void RequireManualReview(string note);
}

public sealed class RealOperationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OperationJournalStore _store;
    private OperationJournal? _activeJournal;
    private int _leaseActive;

    public RealOperationCoordinator(OperationJournalStore store)
    {
        _store = store;
        var load = store.Load();
        _activeJournal = load.ActiveJournal;
        LoadWarning = load.Warning;
    }

    public string? LoadWarning { get; }
    public OperationJournal? ActiveJournal => _activeJournal;
    public bool HasUnresolvedOperation => _activeJournal is { Active: true };
    public bool IsOperationInProgress => Volatile.Read(ref _leaseActive) != 0;
    public bool HasEligibleInterruptedSwapRecovery => IsEligibleInterruptedSwapRecovery(_activeJournal);

    public async Task<ProtectedOperationLease> EnterAsync(
        string owner,
        IAutomationReadClient readClient,
        IAutomationMutationClient mutationClient,
        CancellationToken cancellationToken)
    {
        if (HasUnresolvedOperation)
        {
            throw new UnresolvedOperationException("存在尚未核对的中断操作，所有真实选退课已锁定。");
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            throw new RealOperationBusyException("另一个真实操作正在执行，已拒绝重复提交。");
        }

        Interlocked.Exchange(ref _leaseActive, 1);
        if (HasUnresolvedOperation)
        {
            ReleaseLease();
            throw new UnresolvedOperationException("存在尚未核对的中断操作，所有真实选退课已锁定。");
        }

        return new ProtectedOperationLease(this, _store, owner, readClient, mutationClient);
    }

    public async Task<InterruptedRestoreResult> TryRestoreConfirmedDropsAfterNetworkAsync(
        long studentId,
        long turnId,
        IAutomationReadClient readClient,
        IAutomationMutationClient mutationClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readClient);
        ArgumentNullException.ThrowIfNull(mutationClient);

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new InterruptedRestoreResult(
                InterruptedRestoreOutcome.NotEligible,
                "另一个真实操作仍在收尾；自动恢复已放弃，保持人工核对。");
        }

        Interlocked.Exchange(ref _leaseActive, 1);
        try
        {
            if (!IsEligibleInterruptedSwapRecovery(_activeJournal))
            {
                return new InterruptedRestoreResult(
                    InterruptedRestoreOutcome.NotEligible,
                    "事务记录不能证明目标课请求从未发送，禁止自动恢复。");
            }

            var journal = _activeJournal!;
            if (journal.StudentId != studentId || journal.TurnId != turnId)
            {
                BlockAutomaticRecovery(journal, "当前账号或选课轮次与中断换课记录不一致；禁止自动恢复。");
                return new InterruptedRestoreResult(
                    InterruptedRestoreOutcome.IdentityMismatch,
                    journal.SafetyNote);
            }

            journal.AutomaticRecoveryEvaluationStarted = true;
            journal.Stage = "网络恢复后正在只读核对旧课与目标课";
            journal.SafetyNote = "本次核对只允许进行一次；读取失败后不得自动恢复或重试。";
            _store.Save(journal);

            ShadowSnapshot snapshot;
            try
            {
                var codes = journal.ConfirmedDroppedLessons
                    .Select(item => item.Code)
                    .Append(journal.TargetCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                snapshot = await readClient.GetShadowSnapshotAsync(
                    studentId,
                    turnId,
                    codes,
                    cancellationToken);
            }
            catch (AuthenticationRequiredException)
            {
                BlockAutomaticRecovery(journal, "只读核对时认证失效；禁止自动恢复，必须人工确认。");
                return new InterruptedRestoreResult(
                    InterruptedRestoreOutcome.AuthenticationRequired,
                    journal.SafetyNote);
            }
            catch
            {
                BlockAutomaticRecovery(journal, "只读核对实际状态失败；禁止自动恢复或重试，必须人工确认。");
                return new InterruptedRestoreResult(
                    InterruptedRestoreOutcome.StateReadFailed,
                    journal.SafetyNote);
            }

            var targetMatches = snapshot.TargetLessons
                .Where(item => item.Code.Equals(journal.TargetCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var targetSelected = snapshot.SelectedLessons.Any(
                item => item.Code.Equals(journal.TargetCode, StringComparison.OrdinalIgnoreCase));
            if (targetSelected || targetMatches.Length != 1)
            {
                BlockAutomaticRecovery(
                    journal,
                    targetSelected
                        ? "只读核对发现目标课已经选中；禁止恢复旧课，等待人工确认。"
                        : "只读核对无法唯一确认目标课；禁止自动恢复，等待人工确认。");
                return new InterruptedRestoreResult(
                    InterruptedRestoreOutcome.StateNotEligible,
                    journal.SafetyNote);
            }

            var lessonsToRestore = new List<LessonInfo>();
            foreach (var reference in journal.ConfirmedDroppedLessons)
            {
                if (snapshot.SelectedLessons.Any(item => item.Id == reference.LessonId))
                {
                    BlockAutomaticRecovery(journal, "只读核对发现至少一门旧课已经选中；禁止再次恢复，等待人工确认。");
                    return new InterruptedRestoreResult(
                        InterruptedRestoreOutcome.StateNotEligible,
                        journal.SafetyNote);
                }

                var matches = snapshot.TargetLessons
                    .Where(item => item.Id == reference.LessonId
                                   && item.Code.Equals(reference.Code, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1)
                {
                    BlockAutomaticRecovery(journal, "只读核对无法唯一确认待恢复旧课；禁止自动恢复，等待人工确认。");
                    return new InterruptedRestoreResult(
                        InterruptedRestoreOutcome.StateNotEligible,
                        journal.SafetyNote);
                }

                lessonsToRestore.Add(matches[0]);
            }

            journal.AutomaticRestoreAttempted = true;
            journal.Stage = "严格条件已满足，开始一次串行旧课恢复";
            journal.SafetyNote = "每门已确认退课的旧课最多提交一次恢复请求；结束后仍保持暂停。";
            _store.Save(journal);

            var allRestored = true;
            foreach (var lesson in lessonsToRestore)
            {
                var restored = await ExecuteRecoveryStepAsync(
                    journal,
                    lesson,
                    readClient,
                    mutationClient,
                    cancellationToken);
                if (restored is null)
                {
                    return new InterruptedRestoreResult(
                        InterruptedRestoreOutcome.ResultUnknown,
                        journal.SafetyNote);
                }

                allRestored &= restored.Value;
            }

            journal.Stage = allRestored
                ? "旧课自动恢复已复核，等待人工确认"
                : "旧课自动恢复不完整，等待人工确认";
            journal.SafetyNote = allRestored
                ? "已逐门读取到旧课恢复状态；程序保持暂停，必须回到教务系统人工确认。"
                : "至少一门旧课未恢复；程序保持暂停，必须立即人工处理。";
            _store.Save(journal);
            return new InterruptedRestoreResult(
                allRestored ? InterruptedRestoreOutcome.Restored : InterruptedRestoreOutcome.Incomplete,
                journal.SafetyNote);
        }
        finally
        {
            ReleaseLease();
        }
    }

    public void MarkExternalInterruption(string note)
    {
        if (_activeJournal is not { Active: true } journal)
        {
            return;
        }

        journal.Stage = IsEligibleInterruptedSwapRecovery(journal)
            ? "系统或网络中断；旧课已确认退课且目标课请求未发送"
            : "系统事件中断，等待人工核对";
        journal.SafetyNote = note;
        try
        {
            _store.Save(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 内存安全锁继续生效；当前请求的取消/异常路径还会再次尝试落盘。
        }
    }

    public void AcknowledgeResolved(string note)
    {
        if (_activeJournal is not { Active: true } journal)
        {
            return;
        }

        _store.Resolve(journal, note);
        _activeJournal = null;
    }

    public void ProhibitAutomaticRecovery(string note)
    {
        if (_activeJournal is not { Active: true } journal
            || journal.AutomaticRecoveryEvaluationStarted
            || journal.AutomaticRestoreAttempted)
        {
            return;
        }

        BlockAutomaticRecovery(journal, note);
    }

    internal void SetJournal(OperationJournal? journal) => _activeJournal = journal;

    internal void ReleaseLease()
    {
        Interlocked.Exchange(ref _leaseActive, 0);
        _gate.Release();
    }

    private static bool IsEligibleInterruptedSwapRecovery(OperationJournal? journal) =>
        journal is
        {
            Active: true,
            RecoveryProtocolVersion: >= 1,
            CurrentRequestMayHaveBeenSent: false,
            TargetRequestMayHaveStarted: false,
            AutomaticRecoveryEvaluationStarted: false,
            AutomaticRestoreAttempted: false,
            StudentId: > 0,
            TurnId: > 0,
            ConfirmedDroppedLessons.Count: > 0
        };

    private void BlockAutomaticRecovery(OperationJournal journal, string note)
    {
        journal.AutomaticRecoveryEvaluationStarted = true;
        journal.Stage = "自动恢复条件不成立，等待人工核对";
        journal.SafetyNote = note;
        _store.Save(journal);
    }

    private async Task<bool?> ExecuteRecoveryStepAsync(
        OperationJournal journal,
        LessonInfo lesson,
        IAutomationReadClient readClient,
        IAutomationMutationClient mutationClient,
        CancellationToken cancellationToken)
    {
        journal.Stage = "恢复旧课请求即将提交";
        journal.CurrentAction = "恢复旧课";
        journal.CurrentCourseCode = lesson.Code;
        journal.CurrentRequestMayHaveBeenSent = true;
        journal.SafetyNote = "本次恢复请求最多提交一次；如中断则结果不确定。";
        _store.Save(journal);

        ControlledOperationResponse response;
        try
        {
            response = await mutationClient.ExecuteOnceAsync(
                CourseMutationKind.Add,
                journal.StudentId,
                journal.TurnId,
                lesson.Id,
                cancellationToken);
        }
        catch
        {
            journal.Stage = "恢复旧课写入结果不确定，等待人工核对";
            journal.SafetyNote = "恢复请求可能已经发送；禁止自动重试，必须人工确认。";
            _store.Save(journal);
            return null;
        }

        if (!response.Completed)
        {
            journal.Stage = "恢复旧课服务器结果不确定，等待人工核对";
            journal.SafetyNote = "恢复请求已经发送但未取得最终结果；禁止自动重试。";
            _store.Save(journal);
            return null;
        }

        ShadowSnapshot finalSnapshot;
        try
        {
            finalSnapshot = await readClient.GetShadowSnapshotAsync(
                journal.StudentId,
                journal.TurnId,
                [lesson.Code],
                cancellationToken);
        }
        catch
        {
            journal.Stage = "恢复旧课最终状态读取失败，结果不确定";
            journal.SafetyNote = "不得自动重试恢复请求，必须人工确认。";
            _store.Save(journal);
            return null;
        }

        var restored = finalSnapshot.SelectedLessons.Any(item => item.Id == lesson.Id);
        journal.CurrentRequestMayHaveBeenSent = false;
        journal.Stage = "恢复旧课最终状态已读取";
        journal.VerifiedSteps.Add($"网络恢复后恢复旧课 {lesson.Code} → {(restored ? "已选" : "未选")}");
        journal.SafetyNote = "当前恢复请求状态已知；整个中断流程仍等待人工确认。";
        _store.Save(journal);
        return restored;
    }
}

public sealed class ProtectedOperationLease : IProtectedMutationExecutor, IDisposable
{
    private readonly RealOperationCoordinator _coordinator;
    private readonly OperationJournalStore _store;
    private readonly string _owner;
    private readonly IAutomationReadClient _readClient;
    private readonly IAutomationMutationClient _mutationClient;
    private OperationJournal? _journal;
    private bool _completed;
    private bool _manualReviewRequired;
    private bool _disposed;

    internal ProtectedOperationLease(
        RealOperationCoordinator coordinator,
        OperationJournalStore store,
        string owner,
        IAutomationReadClient readClient,
        IAutomationMutationClient mutationClient)
    {
        _coordinator = coordinator;
        _store = store;
        _owner = owner;
        _readClient = readClient;
        _mutationClient = mutationClient;
    }

    public void BeginWorkflow(
        string owner,
        long studentId,
        long turnId,
        string targetCode,
        IEnumerable<string> relatedCodes)
    {
        if (_journal is not null)
        {
            throw new InvalidOperationException("同一个保护范围内不能并行开始两个真实操作流程。");
        }

        _journal = new OperationJournal
        {
            Owner = string.IsNullOrWhiteSpace(owner) ? _owner : owner,
            StudentId = studentId,
            TurnId = turnId,
            TargetCode = targetCode,
            RelatedCodes = relatedCodes
                .Append(targetCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RecoveryProtocolVersion = 1,
            Stage = "流程已落盘，尚未提交真实操作",
            SafetyNote = "若程序在此后中断，下次启动必须先读取实际状态。"
        };
        _completed = false;
        _manualReviewRequired = false;
        try
        {
            _store.Save(_journal);
            _coordinator.SetJournal(_journal);
        }
        catch
        {
            // 尚未提交任何真实请求，无法建立持久保护时必须拒绝开始流程。
            _journal = null;
            throw;
        }
    }

    public async Task<VerifiedMutationResult> ExecuteAndVerifyAsync(
        CourseMutationKind kind,
        LessonInfo lesson,
        string stepName,
        CancellationToken cancellationToken)
    {
        var journal = _journal ?? throw new InvalidOperationException("真实操作必须先建立可恢复流程记录。");
        journal.Stage = "真实请求即将提交";
        journal.CurrentAction = kind == CourseMutationKind.Add ? "选课" : "退课";
        journal.CurrentCourseCode = lesson.Code;
        journal.CurrentRequestMayHaveBeenSent = true;
        journal.SafetyNote = stepName;
        _store.Save(journal);

        ControlledOperationResponse response;
        try
        {
            response = await _mutationClient.ExecuteOnceAsync(
                kind,
                journal.StudentId,
                journal.TurnId,
                lesson.Id,
                cancellationToken);
            journal.Stage = "真实请求已返回，等待最终状态复核";
            _store.Save(journal);
        }
        catch (Exception ex) when (ex is not AuthenticationRequiredException and not OperationCanceledException)
        {
            journal.Stage = "写入提交结果不确定，等待人工核对";
            journal.SafetyNote = "请求可能已经发送；禁止自动读取后继续、恢复或重试。";
            _store.Save(journal);
            throw new WriteResultUncertainException("真实写入遇到网络或接口异常，结果不确定；禁止自动恢复或重试。");
        }
        catch
        {
            journal.Stage = "提交过程被中断，结果不确定";
            journal.SafetyNote = "必须重新登录并人工核对。";
            _store.Save(journal);
            throw;
        }

        if (!response.Completed)
        {
            journal.Stage = "服务器未给出写入最终结果，等待人工核对";
            journal.SafetyNote = "请求已经发送但完成状态未知；禁止自动恢复或重试。";
            _store.Save(journal);
            throw new WriteResultUncertainException("服务器未在限定时间内给出写入最终结果；禁止自动恢复或重试。");
        }

        ShadowSnapshot finalSnapshot;
        try
        {
            finalSnapshot = await _readClient.GetShadowSnapshotAsync(
                journal.StudentId,
                journal.TurnId,
                [lesson.Code],
                cancellationToken);
        }
        catch
        {
            journal.Stage = "最终状态复核失败，结果不确定";
            journal.SafetyNote = "不得自动重复此操作。";
            _store.Save(journal);
            throw;
        }

        var finallySelected = finalSnapshot.SelectedLessons.Any(item => item.Id == lesson.Id);
        var expectedSelected = kind == CourseMutationKind.Add;
        var expectedStateReached = finallySelected == expectedSelected;
        journal.CurrentRequestMayHaveBeenSent = false;
        journal.Stage = "本步骤最终状态已读取";
        journal.VerifiedSteps.Add($"{stepName}：{lesson.Code} → {(finallySelected ? "已选" : "未选")}");
        journal.SafetyNote = "当前步骤状态已知；整个流程完成前仍保持保护。";
        _store.Save(journal);

        return new VerifiedMutationResult(
            expectedStateReached,
            finallySelected,
            $"{response.Message} 最终状态：{(finallySelected ? "已选" : "未选")}。");
    }

    public void RecordConfirmedDropBeforeTarget(LessonInfo lesson)
    {
        var journal = _journal ?? throw new InvalidOperationException("真实操作必须先建立可恢复流程记录。");
        if (journal.CurrentRequestMayHaveBeenSent || journal.TargetRequestMayHaveStarted)
        {
            throw new InvalidOperationException("当前存在可能已经发送的请求，不能记录目标课未发送检查点。");
        }

        if (journal.ConfirmedDroppedLessons.All(item => item.LessonId != lesson.Id))
        {
            journal.ConfirmedDroppedLessons.Add(new JournalLessonReference
            {
                LessonId = lesson.Id,
                Code = lesson.Code,
                CourseName = lesson.CourseName
            });
        }

        journal.Stage = "旧课退课已确认，目标课请求尚未发送";
        journal.CurrentAction = "等待提交目标课";
        journal.CurrentCourseCode = journal.TargetCode;
        journal.SafetyNote = "如网络此时中断，只能在严格只读核对后尝试一次恢复旧课。";
        _store.Save(journal);
    }

    public void MarkTargetRequestWillStart()
    {
        var journal = _journal ?? throw new InvalidOperationException("真实操作必须先建立可恢复流程记录。");
        if (journal.CurrentRequestMayHaveBeenSent)
        {
            throw new InvalidOperationException("上一真实请求状态尚未确认，禁止开始目标课请求。");
        }

        journal.TargetRequestMayHaveStarted = true;
        journal.Stage = "目标课请求即将提交，此后禁止自动恢复";
        journal.CurrentAction = "选目标课";
        journal.CurrentCourseCode = journal.TargetCode;
        journal.SafetyNote = "从此检查点起，目标请求可能发送；任何异常都必须人工核对。";
        _store.Save(journal);
    }

    public void CompleteWorkflow(string note)
    {
        if (_journal is not { } journal)
        {
            return;
        }

        _store.Resolve(journal, note);
        _coordinator.SetJournal(null);
        _completed = true;
        _manualReviewRequired = false;
        _journal = null;
    }

    public void RequireManualReview(string note)
    {
        if (_journal is not { } journal)
        {
            return;
        }

        journal.Stage = "需要人工核对，真实操作已锁定";
        journal.SafetyNote = note;
        _store.Save(journal);
        _coordinator.SetJournal(journal);
        _manualReviewRequired = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_journal is { Active: true } journal && !_completed && !_manualReviewRequired)
            {
                journal.Stage = journal.CurrentRequestMayHaveBeenSent
                    ? "写入结果可能不确定，等待人工核对"
                    : "流程未正常结束，等待人工核对";
                journal.SafetyNote = journal.CurrentRequestMayHaveBeenSent
                    ? "请求可能已经发送；禁止自动恢复或重试，必须人工确认。"
                    : "程序没有确认整个真实操作流程完成。";
                try
                {
                    _store.Save(journal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    journal.SafetyNote += " 本次磁盘写入失败；当前进程仍保持锁定。";
                }

                _coordinator.SetJournal(journal);
            }
        }
        finally
        {
            _coordinator.ReleaseLease();
        }
    }
}
