using System.Windows;
using System.Threading;
using System.IO;
using UstcCourseAssistant.Core.Services;
using UstcCourseAssistant.Core.Update;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\USTC.CourseAssistant.Route2.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    public string? UpdateFailureMessage { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Contains(WindowsUpdateRunner.Switch, StringComparer.Ordinal))
        {
            Shutdown(await WindowsUpdateRunner.RunAsync(e.Args));
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
        try
        {
            var arguments = e.Args.ToList();
            string? root = null;
            var rootIndex = arguments.IndexOf("--package-root");
            if (rootIndex >= 0)
            {
                if (rootIndex + 1 >= arguments.Count || arguments.LastIndexOf("--package-root") != rootIndex)
                    throw new ArgumentException("包根参数无效。 ");
                root = arguments[rootIndex + 1];
                arguments.RemoveRange(rootIndex, 2);
            }
            var isolated = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT");
            if (root is null && string.IsNullOrWhiteSpace(isolated))
            {
                var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Directory.GetParent(appDirectory)?.FullName;
                if (candidate is null || !string.Equals(Path.GetFileName(appDirectory), "app", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(Path.Combine(candidate, PackageManifest.FileName)))
                    throw new InvalidOperationException("无法确定目录包根；请通过包根启动器运行。 ");
                root = candidate;
            }
            if (root is not null && !Path.IsPathFullyQualified(root)) throw new ArgumentException("包根路径必须为绝对路径。 ");
            root = root is null ? null : Path.GetFullPath(root);
            var pendingIndex = arguments.IndexOf("--update-pending");
            if (pendingIndex >= 0)
            {
                if (root is null || pendingIndex + 2 >= arguments.Count || !Guid.TryParse(arguments[pendingIndex + 1], out var id))
                    throw new ArgumentException("更新握手参数无效。 ");
                var nonce = arguments[pendingIndex + 2];
                arguments.RemoveRange(pendingIndex, 3);
                var transaction = new PackageUpdateTransaction(root);
                var journal = transaction.Load();
                if (journal.Id != id || journal.Nonce != nonce || journal.Rid != "win-x64") throw new InvalidDataException("更新事务身份不匹配。 ");
                PendingUpdatePreflight.Validate(root, journal, typeof(App).Assembly.GetName().Version!);
                var readyWindow = new Window { Title = "USTC 选课助手 · 正在确认更新", Width = 400, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = "正在确认更新，请稍候……" };
                readyWindow.Show();
                transaction.SignalReady(id, nonce, journal.Version, "win-x64");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await transaction.WaitForCommitAndCleanupAsync(id, nonce, timeout.Token);
                readyWindow.Close();
            }
            else if (root is not null && string.IsNullOrWhiteSpace(isolated))
            {
                var transaction = new PackageUpdateTransaction(root);
                if (Directory.Exists(transaction.ActivePath) && !transaction.TryCleanupCompleted())
                    throw new InvalidOperationException("更新事务尚未完成，请使用包根启动器恢复。 ");
                using var updateLock = transaction.AcquireLock();
                if (Directory.Exists(transaction.ActivePath)) throw new InvalidOperationException("更新事务正在进行，请稍后通过包根启动器运行。 ");
            }
            arguments.Remove("--startup");
            if (arguments.Count != 0) throw new ArgumentException("不支持的启动参数。 ");
            if (!string.IsNullOrWhiteSpace(isolated))
            {
                if (!Path.IsPathFullyQualified(isolated)) throw new ArgumentException("测试数据目录必须是绝对路径。 ");
                AppDataPaths.Configure(isolated);
            }
            else
            {
                var paths = new PackagePaths(root!);
                var migration = new PortableDataMigration(paths, PortableDataMigration.WindowsSourceRoot).Run();
                AppDataPaths.Configure(paths.Data);
                if (StartupRegistrationService.IsEnabled()) StartupRegistrationService.SetEnabled(true);
                if (migration.Phase == PortableMigrationPhase.CleanupPending) UpdateFailureMessage = migration.Message;
            }
            var temp = Path.Combine(AppDataPaths.Root, "tmp");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
            Environment.SetEnvironmentVariable("TMPDIR", temp);
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法安全启动：" + ex.Message, "USTC 选课助手", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = new MainWindow();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
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
