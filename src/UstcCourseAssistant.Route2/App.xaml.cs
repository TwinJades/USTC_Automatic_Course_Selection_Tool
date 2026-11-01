using System.Windows;
using System.Threading;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\USTC.CourseAssistant.Route2.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    // 安装结果确认成功前，主窗口必须保持自动操作暂停。
    public string? UpdatedResultPath { get; private set; }
    public string? UpdateFailureMessage { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            try
            {
                if (e.Args.Length != 2) throw new InvalidOperationException("更新辅助模式需要且仅接受一个安装清单路径。");
                var result = await AppUpdateInstaller.ApplyUpdateAsync(e.Args[1]);
                if (!result.Success)
                    MessageBox.Show(result.Message, "USTC 选课助手 · 更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(result.Success ? 0 : 1);
            }
            catch (Exception ex)
            {
                MessageBox.Show("更新辅助模式无法运行：" + ex.Message, "USTC 选课助手 · 更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "路线二程序已经在运行。为避免重复监测和重复选退课，本次启动已停止。",
                "USTC 选课助手 · 单实例保护",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _ownsSingleInstanceMutex = true;
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--updated")
        {
            try
            {
                if (e.Args.Length != 2) throw new InvalidOperationException("更新启动参数不完整。");
                UpdatedResultPath = AppUpdateInstaller.ValidateUpdatedStartup(e.Args[1]);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法验证更新启动：" + ex.Message, "USTC 选课助手 · 更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }
        else if (e.Args.Length > 0 && e.Args[0] == "--update-failed")
        {
            try
            {
                if (e.Args.Length != 2) throw new InvalidOperationException("更新失败结果参数不完整。");
                UpdateFailureMessage = System.Text.Json.JsonSerializer.Deserialize<Models.UpdateInstallResult>(
                    System.IO.File.ReadAllText(e.Args[1]))?.Message ?? "更新失败，原程序已保留。";
            }
            catch (Exception ex)
            {
                UpdateFailureMessage = "无法读取更新失败结果：" + ex.Message;
            }
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
        if (UpdateFailureMessage is not null)
            MessageBox.Show(MainWindow, UpdateFailureMessage, "USTC 选课助手 · 更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
        base.OnExit(e);
    }
}
