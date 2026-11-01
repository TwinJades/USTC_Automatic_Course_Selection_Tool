using System.IO;
using System.Windows;
using System.Windows.Threading;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private readonly CourseOperationQueue _courseOperations = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _monitoringPending = [];
    private CancellationTokenSource? _monitoringReadCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _mutationEpoch;
    private bool _manualWriteWaiting;
    private Guid? _activeMonitoringTaskId;
    private CancellationTokenSource _sessionWritesCancellation = new();
    private bool _automaticWritesPaused;

    private Task<T> EnqueueOnUiAsync<T>(bool scheduled, DateTimeOffset dueAt, DateTimeOffset createdAt,
        Func<CancellationToken, Task<T>> execute, CancellationToken token) =>
        _courseOperations.EnqueueAsync(scheduled, dueAt, createdAt,
            cancellation => Dispatcher.InvokeAsync(async () =>
            {
                try { return await execute(cancellation); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    StopAutomaticWrites(ex.Message);
                    throw;
                }
            }).Task.Unwrap(), token);

    private bool IsCurrentContext(CourseExecutionContext context) =>
        _sessionValidation is { } session && session.StudentId == context.StudentId
        && TurnSelector.SelectedItem is TurnInfo turn && turn.Id == context.TurnId
        && session.OpenTurns.Any(item => item.Id == context.TurnId);

    private void MergeMonitoringSnapshot(AutomationTask snapshot, long epoch, CourseExecutionContext context)
    {
        if (epoch != _mutationEpoch || !IsCurrentContext(context)
            || (_monitoringContext is not null && _monitoringContext != context)) return;
        var actual = _automationTasks.FirstOrDefault(task => task.Id == snapshot.Id);
        if (actual is null) return;
        actual.Status = snapshot.Status;
        actual.CourseName = snapshot.CourseName;
        actual.Enabled = snapshot.Enabled;
    }

    private void QueueMonitoringWrite(Guid id, CourseExecutionContext context)
    {
        if (_monitoringPending.ContainsKey(id)) return;
        var cancellation = new CancellationTokenSource();
        _monitoringPending.Add(id, cancellation);
        _ = CompleteMonitoringWriteAsync(id, context, cancellation);
    }

    private async Task CompleteMonitoringWriteAsync(Guid id, CourseExecutionContext context, CancellationTokenSource cancellation)
    {
        try
        {
            await EnqueueOnUiAsync(false, DateTimeOffset.Now, DateTimeOffset.Now, async token =>
            {
                var task = _automationTasks.FirstOrDefault(item => item.Id == id);
                if (!_automationRunning || _monitoringContext != context || !IsCurrentContext(context)
                    || _automaticWritesPaused || _readNetworkRecoveryPending || task?.Enabled != true || AutomationArmCheckBox.IsChecked != true
                    || _updateInstalling || _updateHandshakePending || !IsInAutomationSchedule()) return false;
                _activeMonitoringTaskId = id;
                AutomationCycleResult result;
                try { result = await ExecuteCourseAsync(task, context, token); }
                finally { _activeMonitoringTaskId = null; }
                if (result.ConfirmedSelectedTaskIds?.Contains(id) == true) ApplyConfirmedSelection(task, context);
                if (result.ShouldPause) StopAutomaticWrites(result.Summary);
                SaveAutomationSettings();
                return true;
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AddActivity("尚未开始的监测操作已取消。", "信息");
        }
        catch (Exception ex)
        {
            StopAutomaticWrites(ex.Message);
        }
        finally
        {
            _monitoringPending.Remove(id);
            cancellation.Dispose();
            AutomationTasksGrid.Items.Refresh();
            SetBusy(_isBusy);
        }
    }

    private void EnsureExecutionAuthorized(AutomationTask task, CourseExecutionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_automaticWritesPaused || _updateInstalling || _updateHandshakePending || !IsCurrentContext(context))
            throw new OperationCanceledException("执行上下文已经变化或真实操作已暂停。", token);
        if (_activeMonitoringTaskId == task.Id)
        {
            if (!_automationRunning || _monitoringContext != context || AutomationArmCheckBox.IsChecked != true)
                throw new OperationCanceledException("监测授权已撤销。", token);
        }
        else if (!_scheduledRunning || _scheduledContext != context
                 || (_monitoringContext is not null && _monitoringContext != context)
                 || _scheduledService?.Current is not
                 { State: ScheduledSelectionState.Running, RealOperationsAuthorized: true } appointment
                 || appointment.StudentId != context.StudentId || !appointment.Tasks.Any(item => item.Id == task.Id))
        {
            throw new OperationCanceledException("预约授权已撤销。", token);
        }
    }

    private async Task<AutomationCycleResult> ExecuteCourseAsync(AutomationTask task, CourseExecutionContext context, CancellationToken token)
    {
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(token, _sessionWritesCancellation.Token);
        token = safety.Token;
        EnsureExecutionAuthorized(task, context, token);
        if (_realOperations.HasUnresolvedOperation) throw new InvalidOperationException("存在待核对的真实操作，执行已停止。");
        if (_loginWindow?.IsBrowserReady != true) throw new InvalidOperationException("登录状态不完整。");
        using var bridge = await _loginWindow.CreateSessionBridgeAsync(token);
        EnsureExecutionAuthorized(task, context, token);
        var reader = new ReadOnlyEamsClient(bridge);
        var session = await reader.ValidateAsync(token);
        EnsureExecutionAuthorized(task, context, token);
        if (session.StudentId != context.StudentId) throw new InvalidOperationException("执行账号与当前授权账号不一致。");
        if (session.OpenTurns.All(turn => turn.Id != context.TurnId))
        {
            task.Status = "当前选课轮次已关闭";
            return new AutomationCycleResult(true, task.Status);
        }
        _mutationEpoch++;
        var mutation = new ControlledEamsClient(bridge);
        using var lease = await _realOperations.EnterAsync("课程选退课", reader, mutation, token);
        SetBusy(_isBusy);
        try
        {
            EnsureExecutionAuthorized(task, context, token);
            var engine = new Route2AutomationEngine(reader, lease, context.StudentId, context.TurnId,
                (level, message, kind) => AddActivity(message, level, kind));
            return await engine.RunOnceAsync([task], true, token);
        }
        finally
        {
            _mutationEpoch++;
            _ = Dispatcher.BeginInvoke(() => SetBusy(_isBusy), DispatcherPriority.Background);
        }
    }

    private void ApplyConfirmedSelection(AutomationTask selected, CourseExecutionContext context)
    {
        if (!IsCurrentContext(context) || (_monitoringContext is not null && _monitoringContext != context)) return;
        var matches = _automationTasks.Where(task => task.CourseCode.Trim().Equals(
            selected.CourseCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return;
        var actual = matches[0];
        AutomationTaskRules.DisableConflictingLowerPriorityTasks(_automationTasks, actual,
            (level, message, kind) => AddActivity(message, level, kind));
        actual.Enabled = false;
        actual.Status = "已选中（最终状态确认）";
        foreach (var pair in _monitoringPending.ToArray())
        {
            var task = _automationTasks.FirstOrDefault(task => task.Id == pair.Key);
            if (task?.Enabled != true && pair.Key != _activeMonitoringTaskId) pair.Value.Cancel();
        }
        _mutationEpoch++;
    }

    private void CancelMonitoringWaiting()
    {
        _monitoringReadCancellation?.Cancel();
        foreach (var pair in _monitoringPending.ToArray())
        {
            if (pair.Key != _activeMonitoringTaskId) pair.Value.Cancel();
        }
    }

    private void InvalidateWriteSession()
    {
        _sessionWritesCancellation.Cancel();
        _sessionWritesCancellation.Dispose();
        _sessionWritesCancellation = new CancellationTokenSource();
    }

    private void StopAutomaticWrites(string reason)
    {
        PauseAutomation(reason, "错误");
        _automaticWritesPaused = true;
        _scheduledTimer.Stop();
        if (_scheduledService?.Current?.State != ScheduledSelectionState.Running) _scheduledCancellation?.Cancel();
        SetStatus(reason, StatusKind.Error);
    }

    private bool SaveAutomationBeforeExecution()
    {
        SaveAutomationSettings();
        try
        {
            _automationStore.Save(_automationSettings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("任务保存失败，执行已停止：" + ex.Message, StatusKind.Error);
            return false;
        }
    }
}
