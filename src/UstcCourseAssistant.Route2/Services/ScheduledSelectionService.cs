using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public sealed class ScheduledSelectionService
{
    public static TimeZoneInfo BeijingTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private readonly ScheduledSelectionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Guid? _restartRecoveryId;
    private ScheduledSelectionSettings _settings;
    private DateTimeOffset? _previousTickAt;
    private bool _restartScanPending;
    private bool _restored;

    public ScheduledSelectionService(ScheduledSelectionStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settings = _store.Load();
        _restartRecoveryId = _settings.Appointment is { } current && NeedsRestartRecovery(current) ? current.Id : null;
        _restartScanPending = _settings.Appointment?.State == ScheduledSelectionState.Armed;
        _previousTickAt = _timeProvider.GetUtcNow();
    }

    // 调用方编辑独立快照；只有持久化成功的事务才发布服务内存状态。
    public ScheduledSelectionSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return CopySettings(_settings);
            }
        }
    }

    public ScheduledSelection? Current => Settings.Appointment;
    public string? LoadWarning => _store.LoadWarning;
    public ScheduledSelection? PendingImport
    {
        get
        {
            var appointment = Current;
            return appointment is { State: ScheduledSelectionState.Running, SelectionFinished: true, ImportFinished: false,
                CompletionAction: ScheduledCompletionAction.ImportToMonitoring } ? appointment : null;
        }
    }

    public ScheduledSelection Get(Guid id)
    {
        lock (_sync)
        {
            return Copy(Find(_settings, id));
        }
    }

    public void UpsertDraft(ScheduledSelection appointment)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        var draft = Copy(appointment);
        Transact(settings =>
        {
            RequireSameAppointment(settings, draft.Id);
            if (settings.Appointment is { } current)
            {
                RequireState(current, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed);
                draft.CreatedAt = current.CreatedAt;
            }

            ResetExecution(draft);
            settings.Appointment = draft;
        });
    }

    public void BeginEdit(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed,
            ScheduledSelectionState.Completed, ScheduledSelectionState.Failed, ScheduledSelectionState.Missed,
            ScheduledSelectionState.Cancelled, ScheduledSelectionState.Interrupted);
        ResetExecution(appointment);
    });

    public void Arm(ScheduledSelection appointment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        var armed = Copy(appointment);
        Transact(settings =>
        {
            RequireSameAppointment(settings, armed.Id);
            if (settings.Appointment is { } existing)
            {
                RequireState(existing, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed);
                armed.CreatedAt = existing.CreatedAt;
            }

            ValidateForArming(armed, now);
            ResetExecution(armed);
            armed.RealOperationsAuthorized = true;
            armed.State = ScheduledSelectionState.Armed;
            armed.ExecuteAt = InBeijing(armed.ExecuteAt);
            armed.CreatedAt = InBeijing(armed.CreatedAt);
            settings.Appointment = armed;
        });
    }

    public ScheduledSelection? CollectDue(DateTimeOffset now, bool resumed)
    {
        lock (_sync)
        {
            if (!_restored)
            {
                RestoreAfterRestart(now);
            }

            var missedScan = resumed || _restartScanPending
                || (_previousTickAt is { } previous && AutomationTiming.IsSignificantlyLate(previous + TickInterval, now, TickInterval));
            ScheduledSelection? queued = null;
            if (_settings.Appointment is { State: ScheduledSelectionState.Armed } due && due.ExecuteAt <= now)
            {
                Transact(settings =>
                {
                    var appointment = Find(settings, due.Id);
                    if (!appointment.RealOperationsAuthorized)
                    {
                        appointment.State = ScheduledSelectionState.Draft;
                        appointment.Result = "预约授权无效，请核对后重新启用。";
                    }
                    else if (missedScan && !appointment.RunWhenMissed)
                    {
                        appointment.State = ScheduledSelectionState.Missed;
                        appointment.FinishedAt = InBeijing(now);
                        appointment.Result = "预约时间已错过，未勾选错过后补执行。";
                    }
                    else
                    {
                        appointment.State = ScheduledSelectionState.Queued;
                        appointment.Result = "已到预约时间，等待执行。";
                    }
                });
                if (_settings.Appointment?.State == ScheduledSelectionState.Queued)
                {
                    queued = Copy(_settings.Appointment);
                }
            }

            _previousTickAt = now;
            _restartScanPending = false;
            return queued;
        }
    }

    public void MarkRunning(Guid id, DateTimeOffset now) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Queued);
        if (!appointment.RealOperationsAuthorized || appointment.StartedAt is not null || appointment.SelectionFinished)
        {
            throw new InvalidOperationException("预约未经授权或已开始过，不能重新执行。");
        }

        appointment.State = ScheduledSelectionState.Running;
        appointment.StartedAt = InBeijing(now);
        appointment.Result = "正在执行一次选课检查。";
    });

    public void SetExecutedTurn(Guid id, TurnInfo turn) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireExecuting(appointment);
        AssignExecutedTurn(appointment, turn);
    });

    public void SaveExecutionProgress(Guid id, IEnumerable<AutomationTask> tasks, IEnumerable<Guid>? confirmedSelectedTaskIds = null)
    {
        var snapshot = tasks.Select(task => CopyTask(task)).ToList();
        var confirmed = confirmedSelectedTaskIds?.ToArray() ?? [];
        Transact(settings =>
        {
            var appointment = Find(settings, id);
            RequireExecuting(appointment);
            UpdateProgress(appointment, snapshot, confirmed);
        });
    }

    public void MarkSelectionFinished(Guid id, TurnInfo turn, IEnumerable<AutomationTask> tasks, IEnumerable<Guid>? confirmedSelectedTaskIds = null)
    {
        var snapshot = tasks.Select(task => CopyTask(task)).ToList();
        var confirmed = confirmedSelectedTaskIds?.ToArray() ?? [];
        Transact(settings =>
        {
            var appointment = Find(settings, id);
            RequireExecuting(appointment);
            AssignExecutedTurn(appointment, turn);
            UpdateProgress(appointment, snapshot, confirmed);
            appointment.SelectionFinished = true;
            appointment.ImportFinished = appointment.CompletionAction == ScheduledCompletionAction.Stop;
            appointment.Result = appointment.ImportFinished ? "一轮执行已结束，等待保存完成结果。" : "一轮执行已结束，等待导入剩余课程。";
        });
    }

    public void MarkImportFinished(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Running);
        if (!appointment.SelectionFinished || appointment.ExecutedTurnId is null)
        {
            throw new InvalidOperationException("选课阶段尚未完整保存，不能确认导入完成。");
        }

        appointment.ImportFinished = true;
        appointment.Result = "剩余课程导入已保存，等待保存完成结果。";
    });

    public void MarkFinished(Guid id, ScheduledSelectionState state, string result, DateTimeOffset now) => Transact(settings =>
    {
        if (state is not (ScheduledSelectionState.Completed or ScheduledSelectionState.Failed or ScheduledSelectionState.Interrupted))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "结束状态必须为完成、失败或中断。");
        }

        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Queued, ScheduledSelectionState.Running);
        if (state == ScheduledSelectionState.Completed && (!appointment.SelectionFinished || !appointment.ImportFinished))
        {
            throw new InvalidOperationException("执行或导入阶段尚未完整保存，不能标记预约完成。");
        }

        appointment.State = state;
        appointment.Result = result ?? string.Empty;
        appointment.FinishedAt = InBeijing(now);
    });

    public void Cancel(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed, ScheduledSelectionState.Queued);
        appointment.State = ScheduledSelectionState.Cancelled;
        appointment.FinishedAt = InBeijing(_timeProvider.GetUtcNow());
        appointment.Result = "预约已取消。";
    });

    public void RestoreAfterRestart(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_restored)
            {
                return;
            }

            if (_settings.Appointment is { } current && current.Id == _restartRecoveryId && NeedsRestartRecovery(current))
            {
                Transact(settings =>
                {
                    var appointment = Find(settings, current.Id);
                    if (!appointment.SelectionFinished)
                    {
                        appointment.State = ScheduledSelectionState.Interrupted;
                        appointment.FinishedAt = InBeijing(now);
                        appointment.Result = "程序在预约入队或执行后退出，实际结果需人工核对；禁止自动重放。";
                    }
                    else if (appointment.CompletionAction == ScheduledCompletionAction.Stop || appointment.ImportFinished)
                    {
                        appointment.ImportFinished = true;
                        appointment.State = ScheduledSelectionState.Completed;
                        appointment.FinishedAt = InBeijing(now);
                        appointment.Result = "已恢复完整保存的执行结果，未重新执行选退课。";
                    }
                    else if (appointment.ExecutedTurnId is null)
                    {
                        appointment.State = ScheduledSelectionState.Interrupted;
                        appointment.FinishedAt = InBeijing(now);
                        appointment.Result = "执行记录缺少实际轮次，禁止自动导入或重放，请人工核对。";
                    }
                    else
                    {
                        appointment.State = ScheduledSelectionState.Running;
                        appointment.Result = "选课阶段已完成，仅等待恢复幂等导入，不会重跑课程。";
                    }
                });
            }

            _restored = true;
        }
    }

    public void RestoreQueuedToArmed()
    {
        lock (_sync)
        {
            if (_settings.Appointment is not { State: ScheduledSelectionState.Queued } current)
            {
                return;
            }

            Transact(settings =>
            {
                var appointment = Find(settings, current.Id);
                if (appointment.StartedAt is not null || appointment.SelectionFinished)
                {
                    throw new InvalidOperationException("已开始的预约不能恢复为未执行状态。");
                }

                appointment.State = ScheduledSelectionState.Armed;
                appointment.Result = "程序关闭前已保存；下次启动按错过处理设置决定是否补执行。";
            });
        }
    }

    public static TurnInfo ResolveTurn(SessionValidationResult session, long expectedStudentId, long? selectedTurnId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.StudentId != expectedStudentId)
        {
            throw new InvalidOperationException("当前登录账号与预约所属账号不一致，预约不会执行。");
        }

        if (selectedTurnId is { } turnId)
        {
            return session.OpenTurns.FirstOrDefault(turn => turn.Id == turnId)
                ?? throw new InvalidOperationException($"当前选择轮次（{turnId}）已关闭或不可用，预约不会切换轮次。");
        }

        return session.OpenTurns.FirstOrDefault()
            ?? throw new InvalidOperationException("当前没有开放选课轮次，预约无法执行。");
    }

    public static int ImportRemainingTasks(ScheduledSelection appointment, AutomationSettings monitoring)
    {
        if (!appointment.SelectionFinished || appointment.ExecutedTurnId is null)
        {
            throw new InvalidOperationException("必须先完整保存选课阶段及实际轮次，才可以导入剩余课程。");
        }

        if (appointment.State is ScheduledSelectionState.Interrupted or ScheduledSelectionState.Failed
            || appointment.CompletionAction != ScheduledCompletionAction.ImportToMonitoring)
        {
            throw new InvalidOperationException("该预约不允许自动导入剩余课程。");
        }

        var tasks = monitoring.Tasks ?? [];
        var selected = appointment.ConfirmedSelectedTaskIds.ToHashSet();
        var keys = new HashSet<string>(tasks.Select(task => task.CourseCode.Trim()), StringComparer.OrdinalIgnoreCase);

        var priority = tasks.Count == 0 ? 0 : tasks.Max(task => task.Priority);
        var additions = new List<AutomationTask>();
        foreach (var task in appointment.Tasks.OrderBy(task => task.Priority))
        {
            var code = task.CourseCode.Trim();
            if (!task.Enabled || selected.Contains(task.Id) || code.Length == 0 || !keys.Add(code))
            {
                continue;
            }

            var imported = CopyTask(task, newId: true);
            imported.CourseCode = code;
            imported.Priority = checked(++priority);
            imported.Status = "等待中";
            additions.Add(imported);
        }

        if (monitoring.Tasks is null)
        {
            monitoring.Tasks = tasks;
        }

        tasks.AddRange(additions);
        return additions.Count;
    }

    public static DateTimeOffset CreateBeijingTime(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, BeijingTimeZone.GetUtcOffset(local));
    }

    public static AutomationTask CopyTask(AutomationTask task, bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid() : task.Id,
        Enabled = task.Enabled,
        CourseCode = task.CourseCode ?? string.Empty,
        CourseName = task.CourseName ?? "待预检查",
        Priority = task.Priority,
        ConflictingCourseCodes = (task.ConflictingCourseCodes ?? []).ToList(),
        Status = task.Status ?? "等待中"
    };

    public static ScheduledSelection Copy(ScheduledSelection appointment) => new()
    {
        Id = appointment.Id,
        CreatedAt = appointment.CreatedAt,
        ExecuteAt = appointment.ExecuteAt,
        RunWhenMissed = appointment.RunWhenMissed,
        StudentId = appointment.StudentId,
        ExecutedTurnId = appointment.ExecutedTurnId,
        ExecutedTurnName = appointment.ExecutedTurnName ?? string.Empty,
        CompletionAction = appointment.CompletionAction,
        State = appointment.State,
        Tasks = (appointment.Tasks ?? []).Select(task => CopyTask(task)).ToList(),
        RealOperationsAuthorized = appointment.RealOperationsAuthorized,
        ConfirmedSelectedTaskIds = (appointment.ConfirmedSelectedTaskIds ?? []).ToList(),
        SelectionFinished = appointment.SelectionFinished,
        ImportFinished = appointment.ImportFinished,
        StartedAt = appointment.StartedAt,
        FinishedAt = appointment.FinishedAt,
        Result = appointment.Result ?? string.Empty
    };

    private void Transact(Action<ScheduledSelectionSettings> change)
    {
        lock (_sync)
        {
            var candidate = CopySettings(_settings);
            change(candidate);
            ScheduledSelectionStore.Normalize(candidate);
            _store.Save(candidate);
            _settings = candidate;
        }
    }

    private static ScheduledSelectionSettings CopySettings(ScheduledSelectionSettings settings) => new()
    {
        Appointment = settings.Appointment is { } appointment ? Copy(appointment) : null
    };

    private static ScheduledSelection Find(ScheduledSelectionSettings settings, Guid id) =>
        settings.Appointment is { } appointment && appointment.Id == id
            ? appointment : throw new KeyNotFoundException("预约不存在或已被替换，拒绝过期操作。");

    private static void RequireSameAppointment(ScheduledSelectionSettings settings, Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException("预约标识不能为空。");
        }

        if (settings.Appointment is { } current && current.Id != id)
        {
            throw new InvalidOperationException("当前已存在一个预约，不能保存或启用另一个预约。");
        }
    }

    private static DateTimeOffset InBeijing(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, BeijingTimeZone);

    private static bool NeedsRestartRecovery(ScheduledSelection appointment) =>
        appointment.State is ScheduledSelectionState.Queued or ScheduledSelectionState.Running
        || (appointment.StartedAt is not null && appointment.FinishedAt is null
            && appointment.State is not (ScheduledSelectionState.Interrupted or ScheduledSelectionState.Failed));

    private static void RequireState(ScheduledSelection appointment, params ScheduledSelectionState[] allowed)
    {
        if (!allowed.Contains(appointment.State))
        {
            throw new InvalidOperationException($"预约当前状态为{StateText(appointment.State)}，不能执行该操作。");
        }
    }

    private static void RequireExecuting(ScheduledSelection appointment)
    {
        RequireState(appointment, ScheduledSelectionState.Running);
        if (appointment.SelectionFinished)
        {
            throw new InvalidOperationException("本次选课阶段已经结束，禁止重新执行或修改课程结果。");
        }
    }

    private static void AssignExecutedTurn(ScheduledSelection appointment, TurnInfo turn)
    {
        if (appointment.ExecutedTurnId is { } executed && executed != turn.Id)
        {
            throw new InvalidOperationException("不能改变预约已开始执行的轮次。");
        }

        appointment.ExecutedTurnId = turn.Id;
        appointment.ExecutedTurnName = $"{turn.DisplayText}（{turn.Id}）";
    }

    private static void UpdateProgress(ScheduledSelection appointment, List<AutomationTask> tasks, IEnumerable<Guid> confirmed)
    {
        var taskIds = appointment.Tasks.Select(task => task.Id).ToHashSet();
        if (tasks.Count != taskIds.Count || tasks.Select(task => task.Id).Distinct().Count() != taskIds.Count
            || tasks.Any(task => !taskIds.Contains(task.Id)))
        {
            throw new InvalidOperationException("运行中不能增删预约课程。");
        }

        appointment.Tasks = tasks;
        appointment.ConfirmedSelectedTaskIds = appointment.ConfirmedSelectedTaskIds.Concat(confirmed)
            .Where(taskIds.Contains).Distinct().ToList();
        foreach (var task in appointment.Tasks.Where(task => appointment.ConfirmedSelectedTaskIds.Contains(task.Id)))
        {
            task.Enabled = false;
        }
    }

    internal static void ResetExecution(ScheduledSelection appointment)
    {
        appointment.State = ScheduledSelectionState.Draft;
        appointment.RealOperationsAuthorized = false;
        appointment.ExecutedTurnId = null;
        appointment.ExecutedTurnName = string.Empty;
        appointment.SelectionFinished = false;
        appointment.ImportFinished = false;
        appointment.StartedAt = null;
        appointment.FinishedAt = null;
        appointment.ConfirmedSelectedTaskIds = [];
        appointment.Result = string.Empty;
    }

    private static void ValidateForArming(ScheduledSelection appointment, DateTimeOffset now)
    {
        if (appointment.Id == Guid.Empty || appointment.StudentId <= 0)
        {
            throw new InvalidOperationException("启用预约前必须验证所属账号。");
        }

        if (!appointment.RealOperationsAuthorized)
        {
            throw new InvalidOperationException("请先勾选真实选退课授权并确认预约。");
        }

        if (appointment.ExecuteAt <= now)
        {
            throw new InvalidOperationException("预约时间必须晚于当前北京时间。");
        }

        var enabled = appointment.Tasks.Where(task => task.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            throw new InvalidOperationException("请至少添加并启用一门有效课程。");
        }

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in enabled)
        {
            var code = task.CourseCode.Trim();
            if (code.Length == 0 || code.Any(character => char.IsWhiteSpace(character) || character is ',' or '，' or ';' or '；'))
            {
                throw new InvalidOperationException($"预约第 {appointment.Tasks.IndexOf(task) + 1} 行必须填写一个完整的教学班代码。");
            }

            if (task.ConflictingCourseCodes.Any(conflict => code.Equals(conflict.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"预约第 {appointment.Tasks.IndexOf(task) + 1} 行课程 {code} 的冲突旧课不能包含目标课自身。");
            }

            if (!codes.Add(code))
            {
                throw new InvalidOperationException($"预约第 {appointment.Tasks.IndexOf(task) + 1} 行教学班 {code} 存在重复启用任务。");
            }
        }
    }

    private static string StateText(ScheduledSelectionState state) => state switch
    {
        ScheduledSelectionState.Draft => "草稿",
        ScheduledSelectionState.Armed => "已启用",
        ScheduledSelectionState.Queued => "等待执行",
        ScheduledSelectionState.Running => "执行中",
        ScheduledSelectionState.Completed => "已完成",
        ScheduledSelectionState.Failed => "失败",
        ScheduledSelectionState.Missed => "已错过",
        ScheduledSelectionState.Cancelled => "已取消",
        ScheduledSelectionState.Interrupted => "已中断",
        _ => "未知状态"
    };
}
