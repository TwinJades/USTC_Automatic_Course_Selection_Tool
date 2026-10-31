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

    private void MergeMonitoringSnapshot(AutomationTask snapshot, long epoch)
    {
        if (epoch != _mutationEpoch) return;
        var actual = _automationTasks.FirstOrDefault(task => task.Id == snapshot.Id);
        if (actual is null || actual.StudentId != snapshot.StudentId || actual.TurnId != snapshot.TurnId) return;
        actual.Status = snapshot.Status;
        actual.CourseName = snapshot.CourseName;
        actual.Enabled = snapshot.Enabled;
    }

    private void QueueMonitoringWrite(Guid id)
    {
        if (_monitoringPending.ContainsKey(id)) return;
        var cancellation = new CancellationTokenSource();
        _monitoringPending.Add(id, cancellation);
        _ = CompleteMonitoringWriteAsync(id, cancellation);
    }

    private async Task CompleteMonitoringWriteAsync(Guid id, CancellationTokenSource cancellation)
    {
        try
        {
            await EnqueueOnUiAsync(false, DateTimeOffset.Now, DateTimeOffset.Now, async token =>
            {
                var task = _automationTasks.FirstOrDefault(item => item.Id == id);
                if (!_automationRunning || task?.Enabled != true || AutomationArmCheckBox.IsChecked != true
                    || _updateInstalling || _updateHandshakePending || !IsInAutomationSchedule()) return false;
                _activeMonitoringTaskId = id;
                AutomationCycleResult result;
                try { result = await ExecuteCourseAsync(task, token); }
                finally { _activeMonitoringTaskId = null; }
                if (result.ConfirmedSelectedTaskIds?.Contains(id) == true) ApplyConfirmedSelection(task);
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

    private async Task<AutomationCycleResult> ExecuteCourseAsync(AutomationTask task, CancellationToken token)
    {
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(token, _sessionWritesCancellation.Token);
        token = safety.Token;
        if (_realOperations.HasUnresolvedOperation) throw new InvalidOperationException("存在待核对的真实操作，执行已停止。");
        if (_loginWindow?.IsBrowserReady != true || task.StudentId is not { } studentId || task.TurnId is not { } turnId)
            throw new InvalidOperationException("课程账号、轮次或登录状态不完整。");
        using var bridge = await _loginWindow.CreateSessionBridgeAsync(token);
        var reader = new ReadOnlyEamsClient(bridge);
        var session = await reader.ValidateAsync(token);
        token.ThrowIfCancellationRequested();
        if (_activeMonitoringTaskId == task.Id && (!_automationRunning || AutomationArmCheckBox.IsChecked != true))
            throw new OperationCanceledException("监测授权已撤销。");
        if (session.StudentId != studentId) throw new InvalidOperationException("执行账号与课程所属账号不一致。");
        if (session.OpenTurns.All(turn => turn.Id != turnId))
        {
            task.Enabled = false;
            task.Status = "所属选课轮次已关闭";
            return new AutomationCycleResult(false, task.Status);
        }
        token.ThrowIfCancellationRequested();
        _mutationEpoch++;
        var mutation = new ControlledEamsClient(bridge);
        using var lease = await _realOperations.EnterAsync("课程选退课", reader, mutation, token);
        SetBusy(_isBusy);
        try
        {
            var engine = new Route2AutomationEngine(reader, lease, studentId, turnId,
                (level, message, kind) => AddActivity(message, level, kind));
            return await engine.RunOnceAsync([task], true, token);
        }
        finally
        {
            _mutationEpoch++;
            _ = Dispatcher.BeginInvoke(() => SetBusy(_isBusy), DispatcherPriority.Background);
        }
    }

    private void ApplyConfirmedSelection(AutomationTask selected)
    {
        AutomationTaskRules.DisableConflictingLowerPriorityTasks(_automationTasks, selected,
            (level, message, kind) => AddActivity(message, level, kind));
        foreach (var task in _automationTasks.Where(task => task.StudentId == selected.StudentId
                     && task.TurnId == selected.TurnId
                     && task.CourseCode.Trim().Equals(selected.CourseCode.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            task.Enabled = false;
            task.Status = "已选中（最终状态确认）";
        }
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
        foreach (var pair in _scheduledCancellation.ToArray())
        {
            if (_scheduledService?.Get(pair.Key).State != ScheduledSelectionState.Running) pair.Value.Cancel();
        }
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
