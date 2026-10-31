using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

/// <summary>单文件辅助安装，以及新进程就绪与安装结果握手。</summary>
public static class AppUpdateInstaller
{
    private const string ManifestName = "install-request.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void ValidateInstallSupport()
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))
            throw new InvalidOperationException("检查和下载仍可使用；应用内安装仅支持可写目录中的 Windows 单文件发布版，不支持 dotnet run 或普通开发输出。");
        var path = CurrentExecutable();
        if (string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能替换 dotnet 宿主；请运行 Windows 单文件发布版。");
        EnsureNoReparsePoints(path);
        var directory = Path.GetDirectoryName(path)!;
        var probe = Path.Combine(directory, ".update-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("安装目录不可写。检查和下载仍可使用，请将单文件发布版放在可写目录后安装。", ex);
        }
        // 更新产物与权限探针都使用回收站，禁止永久删除。
        FileSystem.DeleteFile(probe, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>主窗口保存配置并还原等待队列后调用，随后由主窗口正常关闭。</summary>
    public static string CreateAndStart(AppRelease release, string stagedExecutablePath, bool runAtStartup)
    {
        ValidateInstallSupport();
        var original = CurrentExecutable();
        var staged = Path.GetFullPath(stagedExecutablePath);
        EnsureNoReparsePoints(staged);
        GitHubUpdateService.ValidateExecutableVersion(staged, release.Version);
        if (!string.Equals(Path.GetFileName(staged), GitHubUpdateService.ExecutableName(release.Version), StringComparison.Ordinal))
            throw new InvalidOperationException("暂存程序名称与已验证发布版本不一致。");
        if (GitHubUpdateService.NormalizeVersion(release.Version) <= GitHubUpdateService.CurrentVersion)
            throw new InvalidOperationException("安装目标不是比当前程序更新的正式版本。");
        var stageDirectory = Path.GetDirectoryName(staged)!;
        var destination = Path.Combine(Path.GetDirectoryName(original)!, GitHubUpdateService.ExecutableName(release.Version));
        if (File.Exists(destination) && !FilesEqual(staged, destination))
            throw new IOException("目标新版本文件已存在且内容不同，禁止覆盖：" + destination);
        using var parent = Process.GetCurrentProcess();
        var request = new UpdateInstallRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            original, staged, destination, runAtStartup, Path.Combine(stageDirectory, "install-result.json"));
        var manifest = Path.Combine(stageDirectory, ManifestName);
        if (File.Exists(manifest) || File.Exists(request.ResultPath) || File.Exists(request.ResultPath + ".ready"))
            throw new IOException("本次暂存目录已存在安装记录，请重新下载后安装；旧记录已保留。");
        ValidateRequest(request, manifest, requireHelperPath: false);
        AtomicFileStore.WriteText(manifest, JsonSerializer.Serialize(request, JsonOptions), preserveBackup: false);
        var start = new ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = stageDirectory };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(manifest);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新辅助进程；当前程序没有退出。");
        return manifest;
    }

    /// <summary>只允许暂存程序在取得主应用单实例互斥之前执行。</summary>
    public static async Task<UpdateInstallResult> ApplyUpdateAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var manifest = Path.GetFullPath(manifestPath);
        var request = LoadRequest(manifest);
        ValidateRequest(request, manifest, requireHelperPath: true);
        Process? newProcess = null;
        string? previousStartupCommand = null;
        var registrationChanged = false;
        var parentExited = false;
        try
        {
            await WaitForParentAsync(request, cancellationToken).ConfigureAwait(false);
            parentExited = true;
            var stageCopy = request.DestinationPath + ".install-" + Guid.NewGuid().ToString("N");
            if (!File.Exists(request.DestinationPath))
            {
                if (!CopyFile(request.StagedExecutablePath, stageCopy, failIfExists: true))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将新程序复制到安装目录。");
                GitHubUpdateService.ValidateExecutableVersion(stageCopy, GitHubUpdateService.CurrentVersion);
                if (!FilesEqual(stageCopy, request.StagedExecutablePath))
                    throw new InvalidDataException("同目录安装暂存文件校验失败。");
                if (!MoveFile(stageCopy, request.DestinationPath))
                {
                    // 其他进程可能同时创建目标；只有内容完全一致时才复用。
                    if (!File.Exists(request.DestinationPath) || !FilesEqual(request.DestinationPath, request.StagedExecutablePath))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "新程序目标文件冲突，未覆盖任何文件。");
                }
            }
            else if (!FilesEqual(request.DestinationPath, request.StagedExecutablePath))
            {
                throw new IOException("新程序目标已存在且内容不同，禁止覆盖。");
            }
            previousStartupCommand = StartupRegistrationService.GetRegisteredCommand();
            if (request.RunAtStartup)
            {
                // 在注册表写入前标记，部分写入失败时也恢复原设置。
                registrationChanged = true;
                StartupRegistrationService.SetExecutablePath(request.DestinationPath);
            }
            newProcess = StartProgram(request.DestinationPath, "--updated", request.ResultPath);
            await WaitForReadyAsync(request, newProcess, cancellationToken).ConfigureAwait(false);
            var message = "新版本已启动并确认就绪。";
            try
            {
                FileSystem.DeleteFile(request.OriginalPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
            {
                message += "旧程序无法移入回收站，已保留：" + ex.Message;
            }
            var success = new UpdateInstallResult(true, message, request.DestinationPath);
            WriteResult(request.ResultPath, success);
            return success;
        }
        catch (Exception ex)
        {
            var message = "更新安装失败：" + ex.Message;
            if (registrationChanged)
            {
                try { StartupRegistrationService.RestoreRegisteredCommand(previousStartupCommand); }
                catch (Exception restoreError) { message += " 恢复开机启动设置失败：" + restoreError.Message; }
            }
            var failure = new UpdateInstallResult(false, message, request.DestinationPath);
            // 先发布失败结果，再启动原程序，保证原程序能显示实际原因。
            try { WriteResult(request.ResultPath, failure); }
            catch (Exception resultError)
            {
                failure = failure with { Message = failure.Message + " 写入安装结果失败：" + resultError.Message };
            }
            if (parentExited && (newProcess is null || newProcess.HasExited))
            {
                try
                {
                    using var original = StartProgram(request.OriginalPath, "--update-failed", request.ResultPath);
                }
                catch (Exception restartError)
                {
                    failure = failure with { Message = failure.Message + " 原程序重启失败：" + restartError.Message };
                    try { WriteResult(request.ResultPath, failure); }
                    catch (Exception resultError)
                    {
                        failure = failure with { Message = failure.Message + " 写入安装结果失败：" + resultError.Message };
                    }
                }
            }
            return failure;
        }
        finally
        {
            newProcess?.Dispose();
        }
    }

    /// <summary>验证 --updated 启动来源；不会解除预约与监测暂停状态。</summary>
    public static string ValidateUpdatedStartup(string resultPath)
    {
        var result = Path.GetFullPath(resultPath);
        var manifest = Path.Combine(Path.GetDirectoryName(result)!, ManifestName);
        var request = LoadRequest(manifest);
        ValidateRequest(request, manifest, requireHelperPath: false);
        if (!SamePath(result, request.ResultPath) || !SamePath(CurrentExecutable(), request.DestinationPath) ||
            !string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location) || !FilesEqual(CurrentExecutable(), request.StagedExecutablePath))
            throw new InvalidDataException("更新启动结果路径、程序身份或暂存程序内容不匹配。");
        GitHubUpdateService.ValidateExecutableVersion(CurrentExecutable(), GitHubUpdateService.CurrentVersion);
        return result;
    }

    /// <summary>仅在主窗口、全部配置及安全门控初始化完成后调用。</summary>
    public static void WriteReady(string resultPath)
    {
        var result = ValidateUpdatedStartup(resultPath);
        var readyPath = result + ".ready";
        if (File.Exists(readyPath)) throw new InvalidDataException("本次更新就绪标志已存在，禁止复用旧安装握手。");
        using var process = Process.GetCurrentProcess();
        AtomicFileStore.WriteText(readyPath,
            JsonSerializer.Serialize(new UpdateReady(process.Id, CurrentExecutable(), GitHubUpdateService.CurrentVersion.ToString()), JsonOptions),
            preserveBackup: false);
    }

    /// <summary>失败或超时后，主窗口必须继续暂停自动真实操作。</summary>
    public static async Task<UpdateInstallResult> WaitForResultAsync(string resultPath, CancellationToken cancellationToken = default)
    {
        var result = ValidateUpdatedStartup(resultPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(65));
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                if (File.Exists(result))
                {
                    var value = JsonSerializer.Deserialize<UpdateInstallResult>(await File.ReadAllTextAsync(result, timeout.Token).ConfigureAwait(false))
                        ?? throw new InvalidDataException("更新结果文件为空。");
                    if (!SamePath(value.InstalledPath, CurrentExecutable()))
                        throw new InvalidDataException("更新结果中的程序路径与当前程序不符。");
                    return value;
                }
            } while (await timer.WaitForNextTickAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateInstallResult(false, "等待更新安装结果超时（65 秒）；自动监测和预约保持暂停，旧程序不会被自动删除。", CurrentExecutable());
        }
        throw new InvalidOperationException("更新结果等待意外结束。");
    }

    private static async Task WaitForParentAsync(UpdateInstallRequest request, CancellationToken cancellationToken)
    {
        Process parent;
        try { parent = Process.GetProcessById(request.ParentProcessId); }
        catch (ArgumentException)
        {
            // 父进程可能在启动辅助程序后、辅助程序首次调度前就已经退出。
            return;
        }
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTimeUtcTicks ||
                    !SamePath(parent.MainModule?.FileName ?? string.Empty, request.OriginalPath))
                    throw new InvalidDataException("父进程 PID、启动时间或程序路径不符，拒绝安装。");
            }
            catch (InvalidOperationException) when (parent.HasExited)
            {
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("等待原程序退出超时（60 秒），未替换程序。", ex);
            }
        }
    }

    private static async Task WaitForReadyAsync(UpdateInstallRequest request, Process process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                if (process.HasExited) throw new InvalidOperationException("新程序在确认就绪前已退出。");
                if (File.Exists(request.ResultPath + ".ready"))
                {
                    var ready = JsonSerializer.Deserialize<UpdateReady>(await File.ReadAllTextAsync(request.ResultPath + ".ready", timeout.Token).ConfigureAwait(false))
                        ?? throw new InvalidDataException("新程序就绪标志为空。");
                    if (ready.ProcessId != process.Id || !SamePath(ready.ExecutablePath, request.DestinationPath) ||
                        !Version.TryParse(ready.Version, out var version) || GitHubUpdateService.NormalizeVersion(version) != GitHubUpdateService.CurrentVersion ||
                        !SamePath(process.MainModule?.FileName ?? string.Empty, request.DestinationPath))
                        throw new InvalidDataException("新程序就绪标志的 PID、路径或版本校验失败。");
                    return;
                }
            } while (await timer.WaitForNextTickAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("新程序就绪超时（60 秒）；保留旧程序，不启动第二个主程序。", ex);
        }
    }

    private static UpdateInstallRequest LoadRequest(string path) =>
        JsonSerializer.Deserialize<UpdateInstallRequest>(File.ReadAllText(path))
        ?? throw new InvalidDataException("更新安装清单为空。");

    private static void ValidateRequest(UpdateInstallRequest request, string manifest, bool requireHelperPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("应用内安装仅支持 Windows。");
        foreach (var path in new[] { manifest, request.OriginalPath, request.StagedExecutablePath, request.DestinationPath, request.ResultPath })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装清单含非规范绝对路径。");
            EnsureNoReparsePoints(path);
        }
        if (request.ParentProcessId <= 0 || request.ParentStartTimeUtcTicks <= 0)
            throw new InvalidDataException("安装清单中的父进程标识无效。");
        var stageDirectory = Path.GetDirectoryName(request.StagedExecutablePath)!;
        if (!SamePath(Path.GetDirectoryName(manifest)!, stageDirectory) || Path.GetFileName(manifest) != ManifestName ||
            !SamePath(Path.GetDirectoryName(request.ResultPath)!, stageDirectory) || Path.GetFileName(request.ResultPath) != "install-result.json" ||
            !SamePath(Path.GetDirectoryName(request.DestinationPath)!, Path.GetDirectoryName(request.OriginalPath)!) ||
            SamePath(request.OriginalPath, request.DestinationPath) || SamePath(request.StagedExecutablePath, request.DestinationPath) ||
            SamePath(request.StagedExecutablePath, request.OriginalPath) ||
            SamePath(stageDirectory, Path.GetDirectoryName(request.OriginalPath)!) ||
            !File.Exists(request.OriginalPath) && !File.Exists(request.ResultPath + ".ready") ||
            !File.Exists(request.StagedExecutablePath) || !string.Equals(Path.GetExtension(request.OriginalPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(request.OriginalPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新清单中的暂存、目标、原程序或结果路径不符。");
        var info = FileVersionInfo.GetVersionInfo(request.StagedExecutablePath);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        var executableName = GitHubUpdateService.ExecutableName(version);
        if (!string.Equals(Path.GetFileName(request.StagedExecutablePath), executableName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(request.DestinationPath), executableName, StringComparison.Ordinal) ||
            (requireHelperPath && (!SamePath(CurrentExecutable(), request.StagedExecutablePath) || version != GitHubUpdateService.CurrentVersion ||
                                  !string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))))
            throw new InvalidDataException("更新程序、目标文件名或辅助进程身份与已验证版本不匹配。");
        if (File.Exists(request.OriginalPath))
        {
            var originalInfo = FileVersionInfo.GetVersionInfo(request.OriginalPath);
            var originalVersion = new Version(originalInfo.FileMajorPart, originalInfo.FileMinorPart, originalInfo.FileBuildPart);
            if (originalVersion >= version) throw new InvalidDataException("更新清单不能降级或重复安装同一版本。");
        }
        if (requireHelperPath && (File.Exists(request.ResultPath) || File.Exists(request.ResultPath + ".ready")))
            throw new InvalidDataException("更新清单已被使用，禁止重放安装。");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装路径不能包含符号链接或目录联接：" + current);
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        using var left = File.OpenRead(first);
        using var right = File.OpenRead(second);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(left), SHA256.HashData(right));
    }

    private static Process StartProgram(string executablePath, string argument, string value)
    {
        var start = new ProcessStartInfo(executablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executablePath)! };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add(value);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动程序：" + executablePath);
    }

    private static void WriteResult(string resultPath, UpdateInstallResult result) =>
        AtomicFileStore.WriteText(resultPath, JsonSerializer.Serialize(result, JsonOptions), preserveBackup: false);

    private static string CurrentExecutable() => Path.GetFullPath(Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定当前程序路径。"));

    private static bool SamePath(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFile(string existingFile, string newFile, [MarshalAs(UnmanagedType.Bool)] bool failIfExists);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFile(string existingFile, string newFile);
}
