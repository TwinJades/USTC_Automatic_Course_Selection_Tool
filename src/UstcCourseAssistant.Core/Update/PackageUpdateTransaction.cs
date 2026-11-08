using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Core.Update;

/// <summary>目录包更新的独立事务。调用方必须先安全停止业务、保存状态并冻结数据写入。</summary>
public sealed class PackageUpdateTransaction
{
    public sealed record Journal(Guid Id, string Nonce, string Rid, string Version, string Stage, int ParentId, long ParentStartTicks, bool HadService, string HelperManifestSha256, int HelperId = 0, long HelperStartTicks = 0);
    public sealed record Ready(Guid Id, string Nonce, int ProcessId, long ProcessStartTicks, string Version, string Rid);
    public sealed record LinuxAuthorization(Guid Id, string Nonce, string JournalSha256, int ServicePid, long ServiceStartTicks, int HelperId, long HelperStartTicks);
    private readonly string root, data, active, journalPath;
    public sealed class HelperTerminationUncertainException(Exception inner) : IOException("无法确认更新辅助进程已退出；禁止恢复服务或继续更新。", inner);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PackageUpdateTransaction(string packageRoot)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        data = Path.Combine(root, "data");
        active = OperatingSystem.IsLinux() ? Path.Combine(root, ".update-maintenance", "active") : Path.Combine(data, "updates", "active");
        journalPath = Path.Combine(active, "journal.json");
        PackageManifest.RejectReparseAncestors(root);
        PackageManifest.RejectReparseAncestors(data);
    }

    public string ActivePath => active;
    public string ReadyPath => OperatingSystem.IsLinux() ? Path.Combine(active, "ready-inbox", "ready.json") : Path.Combine(active, "ready.json");
    public string AuthorizationPath => Path.Combine(active, "linux-ready-authorization.json");
    public string CommitPath => Path.Combine(root, ".update-maintenance", "committed.json");

    public Journal BeginDownload(string rid, string version)
    {
        if (Directory.Exists(active)) throw new InvalidOperationException("存在待恢复的更新事务。");
        Directory.CreateDirectory(active);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(active, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        using var parent = Process.GetCurrentProcess();
        var journal = new Journal(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), rid, version, "Downloading", parent.Id, ProcessStartIdentity(parent), Directory.Exists(Path.Combine(root, "service")), "");
        Save(journal);
        return journal;
    }

    /// <summary>锁文件不得删除；调用方在准备及辅助进程接管之间保有锁。</summary>
    public FileStream AcquireLock()
    {
        var directory = Path.GetDirectoryName(active)!;
        PackageManifest.RejectReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public Journal Prepare(string archive, string sha256, string rid, string version, bool downloadInActive = false)
    {
        if (!Directory.Exists(Path.Combine(root, "app")) || !File.Exists(Path.Combine(root, PackageManifest.FileName)) || (rid == "linux-x64" && !Directory.Exists(Path.Combine(root, "service"))))
            throw new InvalidDataException("现有安装不完整，不能执行自动更新。");
        PackageManifest.ValidateInstalled(root, rid);
        PackageManifest.RejectReparseAncestors(active);
        if (downloadInActive)
        {
            if (!Directory.Exists(active) || Path.GetFullPath(archive) != Path.Combine(active, "download.zip") || Load().Stage != "Downloading") throw new InvalidOperationException("更新下载目录状态无效。");
        }
        else
        {
            BeginDownload(rid, version);
        }
        try
        {
            string payload = Path.Combine(active, "payload");
            var manifest = PackageManifest.ExtractVerified(archive, sha256, payload, rid, version);
            string launcher = rid == "win-x64" ? "USTC选课助手.cmd" : "ustc-assistant";
            if (!SameFile(Path.Combine(root, launcher), Path.Combine(payload, launcher)))
                throw new InvalidDataException("启动器协议不同，请手动解压安装。");
            string prefix = "app/";
            string helperSource = Path.Combine(payload, "app");
            if (!Directory.Exists(helperSource)) throw new InvalidDataException("更新包缺少辅助进程。");
            string helper = Path.Combine(active, "helper");
            CopyTree(helperSource, helper);
            string helperManifest = string.Join('\n', manifest.Files.Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(x => x.Path, StringComparer.Ordinal).Select(x => x.Sha256 + "  " + x.Path[prefix.Length..])) + "\n";
            if (helperManifest == "\n") throw new InvalidDataException("更新辅助进程清单为空。");
            string helperList = Path.Combine(active, "helper.sha256");
            WriteThrough(helperList, helperManifest);
            using var helperListStream = File.OpenRead(helperList);
            string helperDigest = Convert.ToHexStringLower(SHA256.HashData(helperListStream));
            var journal = Load() with { Stage = "Prepared", HelperManifestSha256 = helperDigest };
            Save(journal);
            return journal;
        }
        catch
        {
            RecycleActive();
            throw;
        }
    }

    /// <summary>父进程持锁准备后启动辅助进程，再退出并释放锁；辅助进程校验父进程身份并等待锁接管。</summary>
    public Process StartHelper(Journal journal)
    {
        ValidateIdentity(journal);
        VerifyHelper(journal);
        string executable = Path.Combine(active, "helper", OperatingSystem.IsWindows() ? "UstcCourseAssistant.Route2.exe" : "UstcCourseAssistant.Cli");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? WindowsUpdateRunner.Switch : LinuxUpdateRunner.Switch);
        start.ArgumentList.Add("apply"); start.ArgumentList.Add(root); start.ArgumentList.Add(journal.Id.ToString()); start.ArgumentList.Add(journal.Nonce);
        var process = Process.Start(start) ?? throw new IOException("无法启动更新辅助进程。");
        try
        {
            string acknowledgment = Path.Combine(active, "helper-started");
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(acknowledgment))
            {
                if (process.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("更新辅助进程未完成接管准备。");
                Thread.Sleep(50);
            }
            if (File.ReadAllText(acknowledgment) != journal.Nonce) throw new InvalidDataException("辅助进程接管证明不匹配。");
            return process;
        }
        catch (Exception failure)
        {
            // 父进程仍活着且持锁，helper 尚不能接管或开始切换。
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                if (!process.HasExited) throw new IOException("辅助进程仍在运行。");
            }
            catch (Exception terminationFailure) { throw new HelperTerminationUncertainException(new AggregateException(failure, terminationFailure)); }
            throw;
        }
    }

    public void VerifyHelper(Journal journal)
    {
        ValidateIdentity(journal);
        string list = Path.Combine(active, "helper.sha256");
        PackageManifest.RejectReparseAncestors(list);
        using (var stream = File.OpenRead(list))
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(journal.HelperManifestSha256))) throw new InvalidDataException("辅助进程清单摘要不匹配。");
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(list))
        {
            if (line.Length < 67 || line[64..66] != "  ") throw new InvalidDataException("辅助进程清单无效。");
            string relative = line[66..];
            if (relative.Split('/').Any(x => x is "" or "." or "..") || relative.Contains('\\') || relative.Contains(':') || !expected.Add(relative)) throw new InvalidDataException("辅助进程路径无效。");
            string path = Path.Combine(active, "helper", relative.Replace('/', Path.DirectorySeparatorChar));
            PackageManifest.RejectReparseAncestors(path);
            using var stream = File.OpenRead(path);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(line[..64]))) throw new InvalidDataException("辅助进程摘要不匹配。");
        }
        var actual = Directory.EnumerateFiles(Path.Combine(active, "helper"), "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(Path.Combine(active, "helper"), x).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected)) throw new InvalidDataException("辅助进程包含未知文件。");
    }

    public Journal Load()
    {
        PackageManifest.RejectReparseAncestors(journalPath);
        return JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) ?? throw new InvalidDataException("更新日志无效。");
    }

    /// <summary>辅助进程必须持有更新锁。移动前写入意图，恢复依据实际位置回滚。</summary>
    public void Apply(Journal journal)
    {
        ValidateIdentity(journal);
        if (journal.Stage != "Prepared") throw new InvalidOperationException("事务状态不允许安装。");
        var backup = Path.Combine(active, "backup");
        Directory.CreateDirectory(backup);
        using var helper = Process.GetCurrentProcess();
        journal = journal with { HelperId = helper.Id, HelperStartTicks = ProcessStartIdentity(helper) };
        Save(journal with { Stage = "Switching" });
        MoveOld("app"); MoveNew("app");
        MoveOld(PackageManifest.FileName); MoveNew(PackageManifest.FileName);
        if (journal.Rid == "linux-x64") { MoveOld("service"); MoveNew("service"); }
        Save(journal with { Stage = "PendingReady" });
    }

    public void Commit(Journal journal)
    {
        var current = Load();
        ValidateIdentity(journal);
        if (current.Stage != "PendingReady") throw new InvalidOperationException("事务不在等待就绪阶段。");
        var readyPath = ReadyPath;
        PackageManifest.RejectReparseAncestors(readyPath);
        using var readyStream = new FileStream(readyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (readyStream.Length is <= 0 or > 4096) throw new IOException("就绪证明尚未完成。");
        var ready = JsonSerializer.Deserialize<Ready>(readyStream) ?? throw new InvalidDataException("就绪证明无效。");
        if (ready.Id != journal.Id || ready.Nonce != journal.Nonce || ready.Version != journal.Version || ready.Rid != journal.Rid || !ProcessMatches(ready.ProcessId, ready.ProcessStartTicks)) throw new InvalidDataException("就绪证明不匹配。");
        if (journal.Rid == "linux-x64" && !LinuxUpdateAuthorization.IsServiceMain(ready.ProcessId)) throw new UnauthorizedAccessException("就绪进程不是 systemd 服务主进程。");
        Save(current with { Stage = "Committed" });
        if (journal.Rid == "linux-x64" && OperatingSystem.IsLinux())
        {
            PublishCommitted(current with { Stage = "Committed" });
        }
    }

    /// <summary>由新进程在只读验证配置、操作日志和 IPC/窗口后调用；不得先执行任务或迁移数据。</summary>
    public void SignalReady(Guid id, string nonce, string version, string rid)
    {
        var current = Load();
        if (current.Id != id || current.Nonce != nonce || current.Version != version || current.Rid != rid || current.Stage != "PendingReady") throw new InvalidOperationException("更新握手不匹配。");
        using var process = Process.GetCurrentProcess();
        if (rid == "linux-x64")
        {
            var authorizationPath = AuthorizationPath;
            var authorization = JsonSerializer.Deserialize<LinuxUpdateAuthorization.Authorization>(File.ReadAllText(authorizationPath)) ?? throw new InvalidDataException("缺少 Linux 更新授权。");
            if (authorization.Id != id || authorization.ServicePid != process.Id || authorization.ServiceStartTicks != ProcessStartIdentity(process) || authorization.HelperId != current.HelperId || authorization.HelperStartTicks != current.HelperStartTicks || !ProcessMatches(current.HelperId, current.HelperStartTicks)) throw new UnauthorizedAccessException("Linux 更新就绪进程未获授权。");
        }
        if (rid == "linux-x64")
        {
            using var ready = new FileStream(ReadyPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(ready, new Ready(id, nonce, process.Id, ProcessStartIdentity(process), version, rid));
            ready.Flush(true);
        }
        else WriteThrough(ReadyPath, JsonSerializer.Serialize(new Ready(id, nonce, process.Id, ProcessStartIdentity(process), version, rid), JsonOptions));
    }

    /// <summary>禁止执行任务直到提交；辅助进程退出后回收事务。</summary>
    public async Task WaitForCommitAndCleanupAsync(Guid id, string nonce, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(CommitPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(CommitPath));
                    if (document.RootElement.GetProperty("Id").GetGuid() == id && document.RootElement.GetProperty("Nonce").GetString() == nonce) return;
                }
                await Task.Delay(200, cancellationToken);
            }
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = Load();
            if (journal.Id != id || journal.Nonce != nonce) throw new InvalidDataException("更新事务身份不匹配。");
            if (journal.Stage == "Committed") break;
            if (journal.Stage != "PendingReady") throw new InvalidOperationException("更新未提交。");
            await Task.Delay(200, cancellationToken);
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream held;
            try { held = AcquireLock(); }
            catch (IOException) { await Task.Delay(250, cancellationToken); continue; }
            using (held) Cleanup();
            return;
        }
    }

    public bool TryCleanupCompleted()
    {
        if (!Directory.Exists(active)) return true;
        try
        {
            using var held = AcquireLock();
            var journal = Load();
            if (journal.Stage is not ("Committed" or "RolledBack")) return false;
            Cleanup();
            return true;
        }
        catch (IOException) { return false; }
    }

    public void Rollback(Journal journal)
    {
        ValidateIdentity(journal);
        var current = Load();
        if (current.Stage == "Committed") throw new InvalidOperationException("已提交事务不可回滚。");
        if (current.Stage is not ("Prepared" or "Downloading"))
        {
            Restore("app"); Restore(PackageManifest.FileName);
            if (journal.Rid == "linux-x64") Restore("service");
        }
        Save(current with { Stage = "RolledBack" });
    }

    public void PublishCommitted(Journal journal)
    {
        if (!OperatingSystem.IsLinux() || journal.Stage != "Committed") throw new InvalidOperationException("更新尚未提交。");
        WriteThrough(CommitPath, JsonSerializer.Serialize(new { journal.Id, journal.Nonce }, JsonOptions));
        File.SetUnixFileMode(CommitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public void Cleanup()
    {
        var journal = Load();
        if (journal.Stage is not ("Committed" or "RolledBack")) throw new InvalidOperationException("未完成事务不能清理。");
        RecycleActive();
    }

    public void RecycleActive() => new RecycleService(OperatingSystem.IsLinux() ? Path.Combine(root, ".update-maintenance") : data).Recycle(active);

    public static long ProcessStartIdentity(Process p)
    {
        if (!OperatingSystem.IsLinux()) return p.StartTime.ToUniversalTime().Ticks;
        string stat = File.ReadAllText($"/proc/{p.Id}/stat");
        int end = stat.LastIndexOf(')');
        if (end < 0) throw new InvalidDataException("进程状态格式无效。");
        string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19) throw new InvalidDataException("进程状态缺少启动时间。");
        return long.Parse(fields[19], CultureInfo.InvariantCulture);
    }

    public static bool ProcessMatches(int id, long ticks)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited && ProcessStartIdentity(p) == ticks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void ValidateIdentity(Journal journal)
    {
        var current = Load();
        if (current.Id != journal.Id || current.Nonce != journal.Nonce || current.Rid != journal.Rid || current.Version != journal.Version) throw new InvalidDataException("事务身份不匹配。");
    }
    private void MoveOld(string name)
    {
        string source = Path.Combine(root, name), target = Path.Combine(active, "backup", name);
        PackageManifest.RejectReparseAncestors(source);
        if (Directory.Exists(source)) Directory.Move(source, target);
        else if (File.Exists(source)) File.Move(source, target);
    }
    private void MoveNew(string name)
    {
        string source = Path.Combine(active, "payload", name), target = Path.Combine(root, name);
        PackageManifest.RejectReparseAncestors(source);
        if (Directory.Exists(source)) Directory.Move(source, target);
        else if (File.Exists(source)) File.Move(source, target);
        else throw new InvalidDataException("更新包缺少必需项目。");
    }
    private void Restore(string name)
    {
        string backup = Path.Combine(active, "backup", name), target = Path.Combine(root, name), payload = Path.Combine(active, "payload", name);
        PackageManifest.RejectReparseAncestors(backup); PackageManifest.RejectReparseAncestors(target);
        if (!File.Exists(backup) && !Directory.Exists(backup)) return;
        if (Directory.Exists(target)) Directory.Move(target, payload);
        else if (File.Exists(target)) File.Move(target, payload);
        if (Directory.Exists(backup)) Directory.Move(backup, target); else File.Move(backup, target);
    }
    private void Save(Journal journal)
    {
        WriteThrough(journalPath, JsonSerializer.Serialize(journal, JsonOptions));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(journalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
    private static void WriteThrough(string path, string text)
    {
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, leaveOpen: true)) { writer.Write(text); writer.Flush(); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static bool SameFile(string first, string second)
    {
        PackageManifest.RejectReparseAncestors(first); PackageManifest.RejectReparseAncestors(second);
        using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));
    }
    private static void CopyTree(string source, string destination)
    {
        PackageManifest.RejectReparseAncestors(source);
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            PackageManifest.RejectReparseAncestors(path);
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyTree(path, target);
            else
            {
                File.Copy(path, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(path));
            }
        }
    }
}
