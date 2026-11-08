using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace UstcCourseAssistant.Core.Update;

public static class LinuxUpdateAuthorization
{
    public sealed record Authorization(Guid Id, string JournalSha256, int ServicePid, long ServiceStartTicks, int HelperId, long HelperStartTicks);

    public static void SetServiceOwner(string path)
    {
        if (!OperatingSystem.IsLinux() || Geteuid() != 0 || Chown(path, ServiceUid(), uint.MaxValue) != 0) throw new UnauthorizedAccessException("无法设置服务更新目录所有者。");
    }
    public static void SetServiceOwnerIfRoot(string path)
    {
        if (OperatingSystem.IsLinux() && Geteuid() == 0) SetServiceOwner(path);
    }
    public static async Task AuthorizeAsync(PackageUpdateTransaction transaction, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || Geteuid() != 0) throw new UnauthorizedAccessException("更新授权要求 root 辅助进程。");
        var journal = transaction.Load();
        using var helper = Process.GetCurrentProcess();
        if (journal.Stage != "PendingReady" || journal.Rid != "linux-x64" || journal.HelperId != helper.Id || journal.HelperStartTicks != PackageUpdateTransaction.ProcessStartIdentity(helper)) throw new UnauthorizedAccessException("辅助进程身份不匹配。");
        uint uid = ServiceUid();
        int pid = 0;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (pid <= 0 && DateTimeOffset.UtcNow < deadline) { token.ThrowIfCancellationRequested(); pid = MainPid(); if (pid <= 0) await Task.Delay(100, token); }
        if (pid <= 0) throw new TimeoutException("systemd 服务没有主进程。");
        using var process = Process.GetProcessById(pid);
        string inbox = Path.GetDirectoryName(transaction.ReadyPath)!;
        Directory.CreateDirectory(inbox);
        if (Chown(inbox, 0, ServiceGid()) != 0) throw new IOException("无法设置就绪目录所属组。");
        File.SetUnixFileMode(inbox, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        using (var ready = new FileStream(transaction.ReadyPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) ready.Flush(true);
        if (Chown(transaction.ReadyPath, uid, uint.MaxValue) != 0) throw new IOException("无法设置就绪文件所有者。");
        var authorization = new Authorization(journal.Id, JournalHash(transaction), pid, PackageUpdateTransaction.ProcessStartIdentity(process), journal.HelperId, journal.HelperStartTicks);
        string temporary = transaction.AuthorizationPath + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, authorization); stream.Flush(true); }
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, transaction.AuthorizationPath);
    }

    public static async Task<PackageUpdateTransaction.Journal> WaitAsync(PackageUpdateTransaction transaction, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (Geteuid() != ServiceUid() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")) || !int.TryParse(Environment.GetEnvironmentVariable("SYSTEMD_EXEC_PID"), out int execPid) || execPid != Environment.ProcessId) throw new UnauthorizedAccessException("更新就绪仅允许 systemd 服务主进程。");
        string cgroup = await File.ReadAllTextAsync("/proc/self/cgroup", token);
        if (!cgroup.Split('\n').Any(x => x.EndsWith("/ustc-course-assistant.service", StringComparison.Ordinal))) throw new UnauthorizedAccessException("进程不在服务控制组中。");
        string path = transaction.AuthorizationPath;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!File.Exists(path)) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("等待更新授权超时。"); await Task.Delay(100, token); }
        PackageManifest.RejectReparseAncestors(path);
        var auth = JsonSerializer.Deserialize<Authorization>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException("更新授权无效。");
        var journal = transaction.Load();
        using var self = Process.GetCurrentProcess();
        if (journal.Stage != "PendingReady" || journal.Rid != "linux-x64" || auth.Id != journal.Id || auth.JournalSha256 != JournalHash(transaction) || auth.ServicePid != self.Id || auth.ServiceStartTicks != PackageUpdateTransaction.ProcessStartIdentity(self) || auth.ServicePid != MainPid() || auth.HelperId != journal.HelperId || auth.HelperStartTicks != journal.HelperStartTicks || !PackageUpdateTransaction.ProcessMatches(auth.HelperId, auth.HelperStartTicks)) throw new UnauthorizedAccessException("更新授权身份不匹配。");
        return journal;
    }

    private static string JournalHash(PackageUpdateTransaction transaction)
    {
        using var stream = File.OpenRead(Path.Combine(transaction.ActivePath, "journal.json"));
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static uint ServiceUid()
    {
        var fields = Run("getent", "passwd", "ustc-assistant").Trim().Split(':');
        if (fields.Length != 7 || fields[0] != "ustc-assistant" || !uint.TryParse(fields[2], out uint uid) || uid == 0) throw new InvalidDataException("专用服务账户不存在或无效。");
        return uid;
    }
    private static uint ServiceGid()
    {
        var fields = Run("getent", "passwd", "ustc-assistant").Trim().Split(':');
        if (fields.Length != 7 || !uint.TryParse(fields[3], out uint gid) || gid == 0) throw new InvalidDataException("专用服务组无效。");
        return gid;
    }
    public static bool IsServiceMain(int pid) => pid == MainPid();

    private static int MainPid() => int.TryParse(Run("systemctl", "show", "--property=MainPID", "--value", "ustc-course-assistant.service").Trim(), out int pid) ? pid : 0;
    private static string Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统工具。");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("系统工具失败：" + error);
        return output;
    }
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);
}
