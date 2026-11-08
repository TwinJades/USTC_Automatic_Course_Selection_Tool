using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Cli;

/// <summary>Root-only management of the package's system service. The callback must obtain the daemon's persisted, safe-to-stop result.</summary>
[SupportedOSPlatform("linux")]
public sealed class ServiceInstaller
{
    public const string UnitName = "ustc-course-assistant.service";
    public const string AccountName = "ustc-assistant";
    internal const string CredentialPath = "/etc/credstore.encrypted/ustc-course-assistant.cred";
    private readonly string _root;
    private readonly string _data;
    private readonly Func<CancellationToken, Task<bool>> _prepareStop;

    public ServiceInstaller(string packageRoot, Func<CancellationToken, Task<bool>> prepareStop)
    {
        ArgumentNullException.ThrowIfNull(prepareStop);
        if (!Path.IsPathFullyQualified(packageRoot)) throw new ArgumentException("包根必须为绝对路径。", nameof(packageRoot));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        _data = Path.Combine(_root, "data");
        _prepareStop = prepareStop;
    }

    private const string UnitPath = "/etc/systemd/system/ustc-course-assistant.service";
    private const string UnitMarker = "# USTC Course Assistant managed unit\n";
    private const string SystemRecycleRoot = "/etc/systemd/system/.ustc-course-assistant-recycle";
    private string RootMarker => "# package-root " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_root));
    private const string AccountRecord = "/etc/systemd/system/.ustc-course-assistant-account";

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        RequireRoot();
        await ValidatePlatformAsync(cancellationToken);
        await ValidatePackageAsync(cancellationToken);
        if (File.Exists(UnitPath)) await ValidateInstalledPackageAsync(cancellationToken);
        var originalUnit = File.Exists(UnitPath) ? await File.ReadAllBytesAsync(UnitPath, cancellationToken) : null;
        var enabled = await RunAsync("systemctl", ["is-enabled", UnitName], cancellationToken, allowFailure: true);
        var wasEnabled = enabled.ExitCode == 0;
        var accountCheck = await RunAsync("getent", ["passwd", AccountName], cancellationToken, allowFailure: true);
        if (accountCheck.ExitCode == 0)
        {
            if (!File.Exists(AccountRecord)) throw new InvalidOperationException("同名账户不是由本程序确认创建的专用账户。 ");
            RejectLink(AccountRecord);
            var recordMetadata = await RunAsync("stat", ["-c", "%u %a", "--", AccountRecord], cancellationToken);
            if (recordMetadata.Output.Trim() != "0 600") throw new UnauthorizedAccessException("专用账户记录权限不安全。 ");
            var accountFields = accountCheck.Output.Trim().Split(':');
            if (accountFields.Length != 7 || !uint.TryParse(accountFields[2], out var accountUid) || accountUid == 0 || accountUid >= 1000 || accountFields[6] != "/usr/sbin/nologin" || File.ReadAllText(AccountRecord).Trim() != accountFields[2])
                throw new InvalidOperationException("专用账户记录无效。 ");
            await ValidateDataOwnershipAsync(accountUid, cancellationToken);
        }
        else if (File.Exists(AccountRecord)) throw new InvalidOperationException("专用账户记录存在但账户不存在。 ");
        var maintenancePath = Path.Combine(_root, ".update-maintenance");
        if (Path.Exists(maintenancePath))
        {
            RejectLink(maintenancePath);
            if (!Directory.Exists(maintenancePath)) throw new IOException("更新维护路径不是目录。 ");
            var maintenanceStat = await RunAsync("stat", ["-c", "%u %a", "--", maintenancePath], cancellationToken);
            var maintenanceParts = maintenanceStat.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (maintenanceParts.Length != 2 || maintenanceParts[0] != "0" || (Convert.ToInt32(maintenanceParts[1], 8) & 0x12) != 0)
                throw new UnauthorizedAccessException("更新维护目录必须由 root 拥有且不可由其他用户或组写入。 ");
        }
        var wasRunning = await StopIfRunningIfInstalledAsync(cancellationToken);
        try
        {
        var existing = await RunAsync("getent", ["passwd", AccountName], cancellationToken, allowFailure: true);
        if (existing.ExitCode == 0)
        {
            if (!File.Exists(AccountRecord)) throw new InvalidOperationException("同名账户不是由本程序确认创建的专用账户。 ");
            RejectLink(AccountRecord);
            var metadata = await RunAsync("stat", ["-c", "%u %a", "--", AccountRecord], cancellationToken);
            if (metadata.Output.Trim() != "0 600") throw new UnauthorizedAccessException("专用账户记录权限不安全。 ");
        }
        else
        {
            if (File.Exists(AccountRecord)) throw new InvalidOperationException("专用账户记录存在但账户不存在。 ");
            await RunAsync("useradd", ["--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", AccountName], cancellationToken);
            existing = await RunAsync("getent", ["passwd", AccountName], cancellationToken);
        }
        var fields = existing.Output.Trim().Split(':');
        if (fields.Length != 7 || !uint.TryParse(fields[2], out var uid) || uid == 0 || uid >= 1000 || fields[6] != "/usr/sbin/nologin")
            throw new InvalidOperationException("专用账户必须是不可登录的系统账户。 ");
        if (File.Exists(AccountRecord))
        {
            if (File.ReadAllText(AccountRecord).Trim() != uid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("专用账户 UID 与安装记录不一致。 ");
        }
        else
        {
            using var record = new FileStream(AccountRecord, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough });
            using var writer = new StreamWriter(record);
            writer.Write(uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        await ValidateDataOwnershipAsync(uid, cancellationToken);
        // Only package-owned program files are changed; data descendants are never recursively chowned.
        await RunAsync("chown", ["-R", "--no-dereference", "root:root", Path.Combine(_root, "app")], cancellationToken);
        await RunAsync("chmod", ["-R", "go-w", "--", Path.Combine(_root, "app")], cancellationToken);
        await RunAsync("chown", ["--no-dereference", "root:root", Path.Combine(_root, "ustc-assistant")], cancellationToken);
        await RunAsync("chmod", ["go-w", "--", Path.Combine(_root, "ustc-assistant")], cancellationToken);
        var maintenance = Path.Combine(_root, ".update-maintenance");
        if (Directory.Exists(maintenance) || File.Exists(maintenance))
        {
            RejectLink(maintenance);
            var metadata = await RunAsync("stat", ["-c", "%u %a", "--", maintenance], cancellationToken);
            var maintenanceFields = metadata.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (maintenanceFields.Length != 2 || maintenanceFields[0] != "0" || (Convert.ToInt32(maintenanceFields[1], 8) & 0x12) != 0)
                throw new UnauthorizedAccessException("更新维护目录必须由 root 拥有且不可由其他用户或组写入。 ");
        }
        else Directory.CreateDirectory(maintenance, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        RejectLink(maintenance);
        File.SetUnixFileMode(maintenance, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Directory.CreateDirectory(_data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await RunAsync("chown", ["--no-dereference", AccountName + ":" + AccountName, _data], cancellationToken);
        File.SetUnixFileMode(_data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = Path.Combine(_data, "tmp");
        Directory.CreateDirectory(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RejectLink(temp);
        await RunAsync("chown", ["--no-dereference", AccountName + ":" + AccountName, temp], cancellationToken);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await WriteUnitAsync(cancellationToken);
        await RunAsync("systemctl", ["daemon-reload"], cancellationToken);
        await RunAsync("systemctl", ["enable", UnitName], cancellationToken);
            if (wasRunning) await StartAsync(cancellationToken);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try
            {
                if (originalUnit is not null)
                {
                    if (File.Exists(UnitPath)) { EnsureOwnedUnit(); RecycleUnit(UnitPath); }
                    await File.WriteAllBytesAsync(UnitPath, originalUnit, CancellationToken.None);
                    File.SetUnixFileMode(UnitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
                else if (File.Exists(UnitPath)) { EnsureOwnedUnit(); RecycleUnit(UnitPath); }
                await RunAsync("systemctl", ["daemon-reload"], CancellationToken.None);
                await RunAsync("systemctl", [wasEnabled ? "enable" : "disable", UnitName], CancellationToken.None, allowFailure: !wasEnabled);
            }
            catch (Exception recovery) { failures.Add(new IOException("恢复服务配置失败。", recovery)); }
            if (wasRunning)
            {
                try { await StartAsync(CancellationToken.None); }
                catch (Exception recovery) { failures.Add(new IOException("恢复服务运行失败。", recovery)); }
            }
            throw new AggregateException("服务安装失败；有关恢复情况见内部异常。", failures);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        RequireRoot();
        await RunAsync("systemctl", ["start", UnitName], cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequireRoot();
        await StopIfRunningAsync(cancellationToken);
    }

    public async Task<string> StatusAsync(CancellationToken cancellationToken = default)
    {
        RequireRoot();
        var result = await RunAsync("systemctl", ["show", UnitName, "--property=LoadState,ActiveState,SubState"], cancellationToken);
        return result.Output.Trim();
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        RequireRoot();
        await StopIfRunningAsync(cancellationToken);
        await RunAsync("systemctl", ["disable", UnitName], cancellationToken);
        if (File.Exists(UnitPath))
        {
            EnsureOwnedUnit();
            RecycleUnit(UnitPath);
        }
        await RunAsync("systemctl", ["daemon-reload"], cancellationToken);
    }

    internal async Task<bool> StopIfRunningAsync(CancellationToken cancellationToken)
    {
        var state = await RunAsync("systemctl", ["show", UnitName, "--property=ActiveState", "--value"], cancellationToken);
        var value = state.Output.Trim();
        if (value is "inactive" or "failed") return false;
        if (value != "active") throw new InvalidOperationException("服务状态不稳定，不能安全停止：" + value);
        if (!await _prepareStop(cancellationToken)) throw new InvalidOperationException("未获得服务已持久化的安全停机结果，拒绝停止服务。");
        try
        {
            await RunAsync("systemctl", ["stop", UnitName], cancellationToken);
            var stopped = await RunAsync("systemctl", ["show", UnitName, "--property=ActiveState", "--value"], cancellationToken);
            if (stopped.Output.Trim() != "inactive") throw new InvalidOperationException("服务未正常停止，拒绝继续维护。");
        }
        catch (Exception failure)
        {
            try
            {
                await RunAsync("systemctl", ["stop", UnitName], CancellationToken.None);
                await RunAsync("systemctl", ["start", UnitName], CancellationToken.None);
            }
            catch (Exception recovery) { throw new AggregateException("安全停机失败且服务恢复失败。", failure, recovery); }
            throw;
        }
        return true;
    }
    internal async Task<bool> StopIfRunningIfInstalledAsync(CancellationToken cancellationToken)
    {
        var state = await RunAsync("systemctl", ["show", UnitName, "--property=LoadState", "--value"], cancellationToken);
        if (state.Output.Trim() == "not-found") return false;
        if (state.Output.Trim() != "loaded") throw new InvalidOperationException("服务注册状态异常，拒绝安装。 ");
        return await StopIfRunningAsync(cancellationToken);
    }


    internal async Task ValidateInstalledPackageAsync(CancellationToken cancellationToken)
    {
        RequireRoot();
        await ValidatePackageAsync(cancellationToken);
        if (!File.Exists(UnitPath)) throw new InvalidOperationException("服务尚未安装，不能修改系统凭据。 ");
        EnsureOwnedUnit();
        using var reader = new StreamReader(UnitPath);
        reader.ReadLine();
        if (reader.ReadLine() != RootMarker) throw new InvalidOperationException("已注册服务属于其他包目录，拒绝修改。 ");
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(_root, "app"), "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }).Prepend(Path.Combine(_root, "app")).Append(Path.Combine(_root, "ustc-assistant")))
        {
            RejectLink(path);
            var stat = await RunAsync("stat", ["-c", "%u %a", "--", path], cancellationToken);
            var fields = stat.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || fields[0] != "0" || (Convert.ToInt32(fields[1], 8) & 0x12) != 0)
                throw new UnauthorizedAccessException("程序文件不是 root 所有或可由其他用户写入：" + path);
        }
    }

    private async Task ValidateDataOwnershipAsync(uint uid, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_data)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(_data, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            RejectLink(path);
            var stat = await RunAsync("stat", ["-c", "%u", "--", path], cancellationToken);
            if (stat.Output.Trim() != uid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new UnauthorizedAccessException("已有数据不属于专用服务账户，拒绝安装；请先安全迁移数据所有权：" + path);
        }
    }

    internal async Task RefreshUnitAsync(CancellationToken cancellationToken)
    {
        await ValidateInstalledPackageAsync(cancellationToken);
        await WriteUnitAsync(cancellationToken);
        await RunAsync("systemctl", ["daemon-reload"], cancellationToken);
    }

    private async Task WriteUnitAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(UnitPath)!;
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        if (File.Exists(UnitPath)) EnsureOwnedUnit();
        var credential = File.Exists(CredentialPath) ? "LoadCredentialEncrypted=ustc-account:" + CredentialPath + "\n" : "";
        var text = UnitMarker + RootMarker + "\n[Unit]\nDescription=USTC Course Assistant\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nUser=" + AccountName + "\nGroup=" + AccountName + "\nExecStartPre=+" + Quote(Path.Combine(_root, "ustc-assistant")) + " recover-update\nExecStart=" + Quote(Path.Combine(_root, "ustc-assistant")) + " serve\nEnvironment=" + Quote("TMPDIR=" + Path.Combine(_data, "tmp")) + "\n" + credential + "Restart=on-failure\nRestartSec=5\nUMask=0077\nLimitCORE=0\nTimeoutStopSec=infinity\n\n[Install]\nWantedBy=multi-user.target\n";
        var temporary = UnitPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (File.Exists(UnitPath))
        {
            var backup = UnitPath + "." + Guid.NewGuid().ToString("N") + ".backup";
            File.Copy(UnitPath, backup);
            RecycleUnit(backup);
        }
        File.Move(temporary, UnitPath, overwrite: true);
    }

    private static void EnsureOwnedUnit()
    {
        RejectLink(UnitPath);
        using var reader = new StreamReader(UnitPath);
        if (reader.ReadLine() != UnitMarker.TrimEnd('\n'))
            throw new IOException("服务注册文件不属于本程序，拒绝覆盖或删除。 ");
    }

    private static void RecycleUnit(string path)
    {
        Directory.CreateDirectory(SystemRecycleRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RejectLink(SystemRecycleRoot);
        File.SetUnixFileMode(SystemRecycleRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        new RecycleService(SystemRecycleRoot).Recycle(path);
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(['\n', '\r', '\0']) >= 0) throw new IOException("包路径包含不支持的控制字符。");
        return "\"" + value.Replace("%", "%%").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    internal static void RequireRoot()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此操作仅支持 Linux。 ");
        if (geteuid() != 0) throw new UnauthorizedAccessException("此操作需要 root 或 sudo。 ");
    }

    private static async Task ValidatePlatformAsync(CancellationToken cancellationToken)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("仅支持 Linux x86_64。 ");
        if (!NativeLibrary.TryLoad("libc.so.6", out var libc)) throw new PlatformNotSupportedException("需要 glibc。 ");
        NativeLibrary.Free(libc);
        var version = await RunAsync("systemctl", ["--version"], cancellationToken);
        var parts = version.Output.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "systemd" || !int.TryParse(parts[1], out var number) || number < 250)
            throw new PlatformNotSupportedException("需要 systemd 250 或更高版本。 ");
        await RunAsync("systemd-creds", ["--version"], cancellationToken);
    }

    private async Task ValidatePackageAsync(CancellationToken cancellationToken)
    {
        for (var current = _root; current is not null; current = Path.GetDirectoryName(current))
        {
            RejectLink(current);
            var stat = await RunAsync("stat", ["-c", "%u %a", "--", current], cancellationToken);
            var fields = stat.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !uint.TryParse(fields[0], out var owner)) throw new IOException("无法检查包目录所有权。");
            var mode = Convert.ToInt32(fields[1], 8);
            if (owner != 0 || (mode & 0x12) != 0) throw new UnauthorizedAccessException("包目录及其所有父目录必须由 root 拥有且不可由其他用户或组写入：" + current);
        }
        foreach (var path in new[] { Path.Combine(_root, "app"), Path.Combine(_root, "ustc-assistant"), _data, Path.Combine(_root, "service") })
            if (File.Exists(path) || Directory.Exists(path)) RejectLink(path);
        if (!Directory.Exists(Path.Combine(_root, "app")) || !File.Exists(Path.Combine(_root, "ustc-assistant"))) throw new IOException("包缺少 app 或启动器。");
        // Refuse links anywhere within the executable tree rather than following attacker-controlled paths.
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(_root, "app"), "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })) RejectLink(path);
    }

    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝符号链接：" + path);
    }

    internal static Task<MaintenanceProcessRunner.ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? input = null, bool allowFailure = false, bool sensitive = false) =>
        MaintenanceProcessRunner.RunAsync(executable, arguments, cancellationToken, input, allowFailure, sensitive);

    [DllImport("libc")] private static extern uint geteuid();
}

internal static class MaintenanceProcessRunner
{
    internal sealed record ProcessResult(int ExitCode, string Output);
    internal sealed class ProcessTerminationUncertainException(Exception inner) : IOException("无法确认维护子进程已退出，拒绝继续清理。", inner);
    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, string? input = null, bool allowFailure = false, bool sensitive = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统工具：" + executable);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken); process.StandardInput.Close(); }
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0 && !allowFailure) throw new IOException(sensitive ? executable + " 执行失败。" : executable + " 执行失败：" + stderr.Trim());
            return new ProcessResult(process.ExitCode, stdout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            catch (Exception failure) { throw new ProcessTerminationUncertainException(failure); }
            throw;
        }
    }
}
