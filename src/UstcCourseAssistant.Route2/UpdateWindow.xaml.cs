using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using UstcCourseAssistant.Core.Models;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Route2;

public partial class UpdateWindow : Window
{
    private readonly AppRelease _release;
    private readonly Func<CancellationToken, Task> _install;
    private CancellationTokenSource? _cancellation;
    private bool _installing;
    public string PendingAppointmentDescription { get; set; } = "待执行预约将保留。";
    public event Action<AppUpdateState, double?>? UpdateStateChanged;

    public UpdateWindow(AppRelease release, Func<CancellationToken, Task> install)
    {
        InitializeComponent();
        _release = release;
        _install = install;
        VersionsText.Text = $"当前版本：V{GitHubUpdateService.CurrentVersion}    新版本：V{release.Version}";
        ReleaseNotesText.Text = release.Notes;
        DownloadUpdateButton.Content = "继续";
        UpdateStatusText.Text = "确认后将安全暂停任务，下载并验证目录包，然后重启。";
        Closing += UpdateWindow_Closing;
    }
    public void CompleteInstallationClose() { _installing = false; Close(); }
    private void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_release.PageUrl.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { UpdateStatusText.Text = "无法打开发布页：" + ex.Message; }
    }
    private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        InstallUpdateButton.Visibility = Visibility.Visible;
        InstallConfirmationPanel.Visibility = Visibility.Visible;
        InstallConfirmationText.Text = "安装将退出本程序并启动新版本。\n" + PendingAppointmentDescription + "\n更新期间错过的预约按其补执行选项处理。";
    }
    private void InstallConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (InstallUpdateButton is not null) InstallUpdateButton.IsEnabled = InstallConsentCheckBox.IsChecked == true && !_installing;
    }
    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installing || InstallConsentCheckBox.IsChecked != true) return;
        _installing = true;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        InstallUpdateButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        CancelUpdateButton.Content = "取消";
        UpdateStatusText.Text = "正在安全暂停、下载并验证更新……";
        UpdateStateChanged?.Invoke(AppUpdateState.Downloading, null);
        try { await _install(cancellation.Token); }
        catch (OperationCanceledException) { UpdateStatusText.Text = "更新已取消。"; UpdateStateChanged?.Invoke(AppUpdateState.Available, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or Win32Exception or System.Net.Http.HttpRequestException or System.Security.Cryptography.CryptographicException)
        { UpdateStatusText.Text = "安装未完成：" + ex.Message; UpdateStateChanged?.Invoke(AppUpdateState.Failed, null); }
        finally
        {
            _cancellation = null;
            _installing = false;
            DownloadProgress.IsIndeterminate = false;
            InstallUpdateButton.IsEnabled = InstallConsentCheckBox.IsChecked == true;
        }
    }
    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e) { if (_cancellation is not null) _cancellation.Cancel(); else Close(); }
    private void UpdateWindow_Closing(object? sender, CancelEventArgs e) { if (_installing) { _cancellation?.Cancel(); e.Cancel = true; } }
}
