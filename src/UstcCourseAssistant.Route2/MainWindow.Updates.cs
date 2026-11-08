using System.IO;
using System.Windows;
using UstcCourseAssistant.Core.Models;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private async Task CheckUpdateAsync()
    {
        if (_updates is null) { UpdateButton.ToolTip = "隔离模式不检查更新。"; return; }
        UpdateSpinner.Visibility = Visibility.Visible;
        UpdateAvailableIcon.Visibility = UpdateCurrentIcon.Visibility = UpdateFailedIcon.Visibility = Visibility.Collapsed;
        try { _release = await _updates.CheckAsync(GitHubUpdateService.CurrentVersion, _lifetime.Token); UpdateButton.ToolTip = _release is null ? "当前为最新版本。" : "发现更新。"; UpdateAvailableIcon.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible; UpdateCurrentIcon.Visibility = _release is null ? Visibility.Visible : Visibility.Collapsed; }
        catch (Exception ex) { UpdateButton.ToolTip = "更新检查失败：" + LocalActivityLog.Sanitize(ex.Message); UpdateFailedIcon.Visibility = Visibility.Visible; }
        finally { UpdateSpinner.Visibility = Visibility.Collapsed; }
    }
    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        if (_release is null) { await CheckUpdateAsync(); if (_release is null) return; }
        var release = _release;
        _updateWindow = new UpdateWindow(release, token => InstallUpdateAsync(release, token)) { Owner = this };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        if (_controller.Snapshot.Appointment is { } appointment) _updateWindow.PendingAppointmentDescription = $"预约时间：{appointment.ExecuteAt:yyyy-MM-dd HH:mm:ss zzz}";
        _updateWindow.Show();
    }
    private async Task InstallUpdateAsync(AppRelease release, CancellationToken cancellationToken)
    {
        if (_updates is null) throw new InvalidOperationException("更新服务不可用。 ");
        _saveTimer.Stop();
        if (!_controller.Snapshot.MonitoringRunning && !await SaveSettingsAsync()) throw new InvalidOperationException("任务设置无效，不能安装更新。 ");
        IsEnabled = false;
        try
        {
            var prepared = await _controller.ExecuteAsync(Request("update prepare-stop"), cancellationToken);
            if (!prepared.Success) throw new InvalidOperationException(prepared.Message);
            var transaction = await _updates.DownloadAndPrepareAsync(release, cancellationToken);
            try { transaction.Transaction.StartHelper(transaction.Journal); }
            finally { transaction.Lock.Dispose(); }
            _updateWindow?.CompleteInstallationClose(); Close();
        }
        catch (UstcCourseAssistant.Core.Update.PackageUpdateTransaction.HelperTerminationUncertainException)
        {
            StatusText.Text = "无法确认更新辅助进程已停止；业务保持冻结，请退出并使用启动器恢复。";
            throw;
        }
        catch
        {
            await _controller.ExecuteAsync(Request("update cancel-prepare"), CancellationToken.None);
            IsEnabled = true;
            throw;
        }
    }
}
