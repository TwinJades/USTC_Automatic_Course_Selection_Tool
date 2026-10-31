using System.IO;
using System.Windows;
using System.Windows.Threading;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private ScheduledSelectionService? _scheduledService;
    private readonly Dictionary<Guid, CancellationTokenSource> _scheduledCancellation = [];
    private readonly DispatcherTimer _scheduledTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTimeOffset? _previousScheduledTick;
    private bool _scheduledRunning;
    private bool _scheduledResumeScan = true;
    private bool _scheduledLoginActive;
    private Guid? _activeScheduledId;

    private void InitializeScheduling()
    {
        try
        {
            var store = new ScheduledSelectionStore();
            _scheduledService = new ScheduledSelectionService(store, TimeProvider.System);
            _scheduledService.RestoreAfterRestart(DateTimeOffset.Now);
            if (store.LoadWarning is { } warning) AddActivity(warning, "配置恢复", ActivityEventKind.ScheduledSelection);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _scheduledService = null;
            AddActivity("预约配置无法加载：" + ex.Message, "配置错误", ActivityEventKind.ScheduledSelection);
        }
        InitializeScheduledUi();
        _scheduledTimer.Tick += ScheduledTimer_Tick;
    }

    private void ScheduledTimer_Tick(object? sender, EventArgs e)
    {
        if (_scheduledService is null || _updateInstalling || _updateHandshakePending || _automaticWritesPaused) return;
        var now = DateTimeOffset.Now;
        var resumed = _scheduledResumeScan || (_previousScheduledTick is { } previous
            && AutomationTiming.IsSignificantlyLate(previous.AddMilliseconds(250), now, TimeSpan.FromMilliseconds(250)));
        _previousScheduledTick = now;
        _scheduledResumeScan = false;
        try
        {
            var changed = false;
            foreach (var appointment in _scheduledService.PendingImports)
            {
                if (_scheduledCancellation.ContainsKey(appointment.Id)) continue;
                CompleteScheduledImport(appointment);
                changed = true;
            }
            var due = _scheduledService.CollectDue(now, resumed);
            foreach (var appointment in due)
            {
                if (_scheduledCancellation.ContainsKey(appointment.Id)) continue;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
                _scheduledCancellation.Add(appointment.Id, cancellation);
                _ = RunScheduledAppointmentAsync(appointment, cancellation);
                changed = true;
            }
            if (changed || resumed || due.Count > 0) RefreshScheduledUi();
        }
        catch (Exception ex)
        {
            _scheduledTimer.Stop();
            ScheduledStatusText.Text = "预约已暂停：" + ex.Message;
            AddActivity(ScheduledStatusText.Text, "错误", ActivityEventKind.ScheduledSelection);
        }
    }

    private async Task RunScheduledAppointmentAsync(ScheduledSelection appointment, CancellationTokenSource cancellation)
    {
        try
        {
            await EnqueueOnUiAsync(true, appointment.ExecuteAt, appointment.CreatedAt, async token =>
            {
                var current = _scheduledService!.Get(appointment.Id);
                if (current.State != ScheduledSelectionState.Queued || _updateInstalling || _updateHandshakePending) return false;
                if (_realOperations.HasUnresolvedOperation || _automaticWritesPaused)
                    throw new InvalidOperationException("自动真实操作已经暂停，请人工核对后重新启用预约。");
                _scheduledService.MarkRunning(current.Id, DateTimeOffset.Now);
                using var safety = CancellationTokenSource.CreateLinkedTokenSource(token, _sessionWritesCancellation.Token);
                token = safety.Token;
                _scheduledRunning = true;
                _activeScheduledId = current.Id;
                SetBusy(_isBusy);
                AddActivity("定时抢课开始。", "预约", ActivityEventKind.ScheduledSelection);
                var session = await EnsureScheduledSessionAsync(current.StudentId, token);
                token.ThrowIfCancellationRequested();
                var turn = ScheduledSelectionService.ResolveTurn(session, current);
                _scheduledService.SetExecutedTurn(current.Id, turn);
                var tasks = current.Tasks;
                foreach (var task in tasks)
                {
                    task.StudentId = current.StudentId;
                    task.TurnId = turn.Id;
                    task.TurnName = turn.DisplayText;
                }
                var confirmed = current.ConfirmedSelectedTaskIds.ToHashSet();
                foreach (var task in tasks.OrderBy(task => task.Priority))
                {
                    if (!task.Enabled) continue;
                    token.ThrowIfCancellationRequested();
                    var result = await ExecuteCourseAsync(task, token);
                    foreach (var id in result.ConfirmedSelectedTaskIds ?? []) confirmed.Add(id);
                    if (confirmed.Contains(task.Id))
                    {
                        AutomationTaskRules.DisableConflictingLowerPriorityTasks(tasks, task,
                            (level, message, kind) => AddActivity(message, level, kind));
                        ApplyConfirmedSelection(task);
                    }
                    _scheduledService.SaveExecutionProgress(current.Id, tasks, confirmed);
                    SaveAutomationSettings();
                    RefreshScheduledUi();
                    if (result.ShouldPause || _realOperations.HasUnresolvedOperation)
                        throw new InvalidOperationException(result.Summary);
                }
                _scheduledService.MarkSelectionFinished(current.Id, turn, tasks, confirmed);
                current = _scheduledService.Get(current.Id);
                if (current.CompletionAction == ScheduledCompletionAction.ImportToMonitoring)
                    CompleteScheduledImport(current);
                else _scheduledService.MarkFinished(current.Id, ScheduledSelectionState.Completed, "已执行一轮。", DateTimeOffset.Now);
                AddActivity("定时抢课已完成一轮。", "预约", ActivityEventKind.ScheduledSelection);
                return true;
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SaveScheduledFailure(appointment.Id, true, "执行中断，请人工核对。");
        }
        catch (Exception ex)
        {
            StopAutomaticWrites(ex.Message);
            SaveScheduledFailure(appointment.Id, false, ex.Message);
            ScheduledStatusText.Text = ex.Message;
            AddActivity("定时抢课停止：" + ex.Message, "错误", ActivityEventKind.ScheduledSelection);
        }
        finally
        {
            _scheduledCancellation.Remove(appointment.Id);
            cancellation.Dispose();
            if (_activeScheduledId == appointment.Id)
            {
                _activeScheduledId = null;
                _scheduledRunning = false;
            }
            RefreshScheduledUi();
            SetBusy(_isBusy);
        }
    }

    private void SaveScheduledFailure(Guid id, bool cancelled, string reason)
    {
        try
        {
            var current = _scheduledService!.Get(id);
            if (current.State == ScheduledSelectionState.Running && !current.SelectionFinished)
                _scheduledService.MarkFinished(id, cancelled ? ScheduledSelectionState.Interrupted : ScheduledSelectionState.Failed,
                    reason, DateTimeOffset.Now);
            else if (current.State == ScheduledSelectionState.Queued)
            {
                if (cancelled) _scheduledService.Cancel(id);
                else _scheduledService.MarkFinished(id, ScheduledSelectionState.Failed, reason, DateTimeOffset.Now);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _automaticWritesPaused = true;
            _scheduledTimer.Stop();
            ScheduledStatusText.Text = "预约结果保存失败，执行保持暂停：" + ex.Message;
            AddActivity(ScheduledStatusText.Text, "配置错误", ActivityEventKind.ScheduledSelection);
        }
    }

    private void CompleteScheduledImport(ScheduledSelection appointment)
    {
        if (_realOperations.HasUnresolvedOperation)
        {
            _scheduledService!.MarkFinished(appointment.Id, ScheduledSelectionState.Interrupted,
                "存在中断操作，禁止自动导入和启动监测。", DateTimeOffset.Now);
            return;
        }
        if (appointment.CompletionAction == ScheduledCompletionAction.Stop)
        {
            _scheduledService!.MarkFinished(appointment.Id, ScheduledSelectionState.Completed, "已执行一轮。", DateTimeOffset.Now);
            return;
        }
        var settings = new AutomationSettings
        {
            IntervalSeconds = _automationSettings.IntervalSeconds,
            EnableIntervalJitter = _automationSettings.EnableIntervalJitter,
            EnableSchedule = _automationSettings.EnableSchedule,
            StartAt = _automationSettings.StartAt,
            EndAt = _automationSettings.EndAt,
            RunAtWindowsStartup = _automationSettings.RunAtWindowsStartup,
            Tasks = _automationTasks.Select(task => ScheduledSelectionService.CopyTask(task)).ToList()
        };
        var added = ScheduledSelectionService.ImportRemainingTasks(appointment, settings);
        _automationStore.Save(settings);
        _automationSettings = settings;
        _automationTasks.Clear();
        foreach (var task in settings.Tasks) _automationTasks.Add(task);
        _scheduledService!.MarkImportFinished(appointment.Id);
        _scheduledService.MarkFinished(appointment.Id, ScheduledSelectionState.Completed,
            $"执行完成，新增 {added} 门监测课程。", DateTimeOffset.Now);
        if (_automationTasks.Any(task => task.Enabled))
        {
            AutomationArmCheckBox.IsChecked = true;
            _automationRunning = true;
            ScheduleNextAutomationTick();
        }
        UpdateAutomationControls();
    }

    private void CancelScheduledWaiting(Guid id)
    {
        if (_scheduledCancellation.TryGetValue(id, out var cancellation)
            && _scheduledService?.Get(id).State != ScheduledSelectionState.Running) cancellation.Cancel();
    }

    private async Task<SessionValidationResult> EnsureScheduledSessionAsync(long expectedStudentId, CancellationToken token)
    {
        var login = EnsureLoginWindow();
        if (login.IsBrowserReady)
        {
            try
            {
                using var bridge = await login.CreateSessionBridgeAsync(token);
                var result = await new ReadOnlyEamsClient(bridge).ValidateAsync(token);
                if (result.StudentId != expectedStudentId) throw new InvalidOperationException("预约账号与当前账号不一致。");
                ApplyRecoveredSession(result, -1);
                return result;
            }
            catch (AuthenticationRequiredException)
            {
                AddActivity("预约执行前认证失效，检查自动登录条件。", "认证");
            }
        }
        if (!_preferences.AutoLoginEnabled || !_hasSavedCredential)
            throw new InvalidOperationException("预约需要登录，尚未启用保存凭据的自动登录。");
        if (_scheduledLoginActive) throw new InvalidOperationException("已有登录流程正在执行。");
        _scheduledLoginActive = true;
        _monitoringReadCancellation?.Cancel();
        _validationCancellation?.Cancel();
        _mutationEpoch++;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Authenticated(object? sender, EventArgs args) => completion.TrySetResult();
        void StatusChanged(object? sender, AutoLoginStatusEventArgs args)
        {
            if (args.Status is AutoLoginStatus.VerificationRequired or AutoLoginStatus.ManualLoginRequired
                or AutoLoginStatus.Suppressed or AutoLoginStatus.Error)
                completion.TrySetException(new InvalidOperationException(args.Message));
        }
        login.AuthenticatedPageReached += Authenticated;
        login.AutoLoginStatusChanged += StatusChanged;
        try
        {
            if (!login.IsBrowserReady) login.Show();
            else login.RequestAuthentication();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
            using var bridge = await login.CreateSessionBridgeAsync(token);
            var result = await new ReadOnlyEamsClient(bridge).ValidateAsync(token);
            if (result.StudentId != expectedStudentId) throw new InvalidOperationException("自动登录账号与预约账号不一致。");
            ApplyRecoveredSession(result, -1);
            return result;
        }
        finally
        {
            login.AuthenticatedPageReached -= Authenticated;
            login.AutoLoginStatusChanged -= StatusChanged;
            _scheduledLoginActive = false;
        }
    }
}
