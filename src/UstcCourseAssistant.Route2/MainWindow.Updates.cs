using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Animation;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private readonly GitHubUpdateService _updateService = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private AppUpdateState _updateState = AppUpdateState.Checking;
    private AppRelease? _availableRelease;
    private string? _updateError;
    private bool _updateInstalling;
    private bool _updateHandshakePending;
    private UpdateWindow? _updateWindow;

    private async Task InitializeUpdatesAsync()
    {
        var app = Application.Current as App;
        _updateHandshakePending = app?.UpdatedResultPath is not null || app?.UpdateFailureMessage is not null;
        if (app?.UpdatedResultPath is { } resultPath)
        {
            try
            {
                AppUpdateInstaller.WriteReady(resultPath);
                var result = await AppUpdateInstaller.WaitForResultAsync(resultPath, _updateCancellation.Token);
                AddActivity(result.Message, result.Success ? "更新" : "错误", ActivityEventKind.ApplicationLifecycle);
                _updateHandshakePending = !result.Success;
            }
            catch (Exception ex)
            {
                SetStatus("更新结果确认失败：" + ex.Message, StatusKind.Error);
                AddActivity(ex.Message, "更新错误");
            }
        }
        if (!_updateHandshakePending) _scheduledTimer.Start();
        SetBusy(_isBusy);
        await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        _updateState = AppUpdateState.Checking;
        RefreshUpdateIcon();
        try
        {
            _availableRelease = await _updateService.CheckAsync(GitHubUpdateService.CurrentVersion, _updateCancellation.Token);
            _updateState = _availableRelease is null ? AppUpdateState.UpToDate : AppUpdateState.Available;
            _updateError = null;
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _updateState = AppUpdateState.Failed;
            _updateError = ex.Message;
            AddActivity("更新检查失败：" + ex.Message, "更新");
        }
        RefreshUpdateIcon();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateState == AppUpdateState.Checking) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        if (_availableRelease is null)
        {
            await CheckForUpdateAsync();
            return;
        }
        var release = _availableRelease;
        _updateWindow = new UpdateWindow(_updateService, release, path => InstallUpdateAsync(release, path)) { Owner = this };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        var next = _scheduledService?.Current is { State: ScheduledSelectionState.Armed or ScheduledSelectionState.Queued } pending
            ? pending : null;
        _updateWindow.PendingAppointmentDescription = next is null ? "当前没有待执行预约。" : $"最近预约：{next.ExecuteAt:yyyy-MM-dd HH:mm:ss}（北京时间）";
        _updateWindow.UpdateStateChanged += (state, progress) =>
        {
            _updateState = state;
            RefreshUpdateIcon();
            if (progress is { } fraction && state == AppUpdateState.Downloading)
                UpdateButton.ToolTip = $"正在下载更新：{fraction:P0}";
        };
        _updateWindow.Show();
    }

    private async Task InstallUpdateAsync(AppRelease release, string stagedExecutablePath)
    {
        AppUpdateInstaller.ValidateInstallSupport();
        if (_realOperations.HasUnresolvedOperation) throw new InvalidOperationException("存在中断操作，请先读取实际状态并人工核对。");
        _updateInstalling = true;
        _scheduledTimer.Stop();
        PauseAutomation("正在安装更新，监测已暂停。", "更新");
        SetBusy(_isBusy);
        try
        {
            _scheduledService?.RestoreQueuedToArmed();
            if (!_scheduledRunning) _scheduledCancellation?.Cancel();
            while (_scheduledRunning || _manualWriteWaiting || _realOperations.IsOperationInProgress)
            {
                SetStatus("等待当前真实操作安全结束后安装更新。", StatusKind.Working);
                await Task.Delay(100, _lifetimeCancellation.Token);
            }
            if (_realOperations.HasUnresolvedOperation) throw new InvalidOperationException("当前操作需要人工核对，安装已停止。");
            _scheduledService?.RestoreQueuedToArmed();
            _scheduledCancellation?.Cancel();
            if (!SaveAutomationBeforeExecution()) throw new InvalidOperationException("任务配置未能保存，安装已停止。");
            AppUpdateInstaller.CreateAndStart(release, stagedExecutablePath, RunAtStartupCheckBox.IsChecked == true);
            _updateWindow?.CompleteInstallationClose();
            Close();
        }
        catch
        {
            _updateInstalling = false;
            if (!_automaticWritesPaused) _scheduledTimer.Start();
            SetBusy(false);
            throw;
        }
    }

    private void RefreshUpdateIcon()
    {
        var spinning = _updateState is AppUpdateState.Checking or AppUpdateState.Downloading;
        UpdateSpinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        UpdateAvailableIcon.Visibility = _updateState is AppUpdateState.Available or AppUpdateState.ReadyToInstall ? Visibility.Visible : Visibility.Collapsed;
        UpdateCurrentIcon.Visibility = _updateState == AppUpdateState.UpToDate ? Visibility.Visible : Visibility.Collapsed;
        UpdateFailedIcon.Visibility = _updateState == AppUpdateState.Failed ? Visibility.Visible : Visibility.Collapsed;
        UpdateSpinnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            spinning ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        var text = _updateState switch
        {
            AppUpdateState.Checking => "正在检查更新",
            AppUpdateState.UpToDate => "当前为最新版本；点击重新检查",
            AppUpdateState.Available => "发现新版本，点击查看并下载",
            AppUpdateState.Downloading => "正在下载更新",
            AppUpdateState.ReadyToInstall => "更新已经下载，可以安装",
            _ => "更新检查失败，点击重试：" + _updateError
        };
        UpdateButton.ToolTip = text;
        AutomationProperties.SetName(UpdateButton, text);
    }
}
