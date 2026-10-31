using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class UpdateWindow : Window
{
    private readonly GitHubUpdateService _service;
    private readonly AppRelease _release;
    private readonly Func<string, Task> _install;
    private CancellationTokenSource? _downloadCancellation;
    private bool _installing;
    public string? StagedExecutablePath { get; private set; }
    public string PendingAppointmentDescription { get; set; } = "待执行预约将保留；最近预约时间请在主窗口“定时抢课”查看。";
    public event Action<AppUpdateState, double?>? UpdateStateChanged;

    public void CompleteInstallationClose()
    {
        _installing = false;
        Close();
    }

    public UpdateWindow(GitHubUpdateService service, AppRelease release, Func<string, Task> install)
    {
        InitializeComponent();
        _service = service;
        _release = release;
        _install = install;
        VersionsText.Text = $"当前版本：V{GitHubUpdateService.CurrentVersion}    新版本：V{release.Version}";
        ReleaseNotesText.Text = release.Notes;
        Closing += UpdateWindow_Closing;
    }

    private void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_release.PageUrl.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { UpdateStatusText.Text = $"无法打开发布页：{ex.Message}"; }
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        DownloadUpdateButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        CancelUpdateButton.Content = "取消下载";
        UpdateStateChanged?.Invoke(AppUpdateState.Downloading, 0);
        try
        {
            StagedExecutablePath = await _service.DownloadAsync(_release, new Progress<double>(fraction =>
            {
                DownloadProgress.Value = Math.Clamp(fraction, 0, 1) * 100;
                UpdateStatusText.Text = $"正在下载并验证：{DownloadProgress.Value:F1}%";
                UpdateStateChanged?.Invoke(AppUpdateState.Downloading, Math.Clamp(fraction, 0, 1));
            }), cancellation.Token);
            UpdateStatusText.Text = "更新已下载并通过验证，可以安装。";
            UpdateStateChanged?.Invoke(AppUpdateState.ReadyToInstall, 1);
            DownloadUpdateButton.Visibility = Visibility.Collapsed;
            InstallUpdateButton.Visibility = Visibility.Visible;
            InstallConfirmationPanel.Visibility = Visibility.Visible;
            InstallConfirmationText.Text = "安装将退出本程序并启动新版本。\n" + PendingAppointmentDescription +
                "\n更新期间错过的执行时间将按照预约各自的补执行选项处理。\n新程序文件名包含版本号，原有自建快捷方式需要自行修改。";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "下载已取消，原程序仍可继续使用。";
            UpdateStateChanged?.Invoke(AppUpdateState.Available, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or System.Security.Cryptography.CryptographicException)
        {
            UpdateStatusText.Text = $"更新下载失败：{ex.Message}";
            UpdateStateChanged?.Invoke(AppUpdateState.Failed, null);
        }
        finally
        {
            _downloadCancellation = null;
            DownloadUpdateButton.IsEnabled = true;
            CancelUpdateButton.Content = "关闭";
        }
    }

    private void InstallConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (InstallUpdateButton is not null) InstallUpdateButton.IsEnabled = InstallConsentCheckBox.IsChecked == true && !_installing;
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installing || StagedExecutablePath is null || InstallConsentCheckBox.IsChecked != true) return;
        _installing = true;
        InstallUpdateButton.IsEnabled = false;
        CancelUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在等待现有操作安全结束并准备安装……";
        try { await _install(StagedExecutablePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException or Win32Exception)
        {
            UpdateStatusText.Text = $"安装未完成：{ex.Message}";
            UpdateStateChanged?.Invoke(AppUpdateState.Failed, null);
        }
        finally
        {
            _installing = false;
            InstallUpdateButton.IsEnabled = InstallConsentCheckBox.IsChecked == true;
            CancelUpdateButton.IsEnabled = true;
        }
    }

    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null) _downloadCancellation.Cancel();
        else Close();
    }

    private void UpdateWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_installing) { e.Cancel = true; return; }
        _downloadCancellation?.Cancel();
    }
}
