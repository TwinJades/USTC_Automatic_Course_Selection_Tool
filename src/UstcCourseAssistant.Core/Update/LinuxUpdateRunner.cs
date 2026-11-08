using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UstcCourseAssistant.Core.Update;

/// <summary>以 root 运行的 Linux 包更新辅助进程。</summary>
public static class LinuxUpdateRunner
{
    public const string Switch = "--update-helper";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("apply" or "recover") || (args[0] == "apply" && args.Length != 4) || (args[0] == "recover" && args.Length != 2))
        {
            Console.Error.WriteLine("Usage: --update-helper apply <package-root> <transaction-id> <nonce> | recover <package-root>");
            return 2;
        }
        try
        {
            if (!OperatingSystem.IsLinux() || Geteuid() != 0) throw new UnauthorizedAccessException("Linux 更新辅助进程需要 root 权限。");
            if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("包根目录必须为绝对路径。");
            var transaction = new PackageUpdateTransaction(args[1]);
            bool recover = args[0] == "recover";
            if (recover && !Directory.Exists(transaction.ActivePath)) return 0;
            if (recover)
            {
                try { using var probe = transaction.AcquireLock(); }
                catch (IOException) { return 0; }
            }
            if (recover && !File.Exists(Path.Combine(transaction.ActivePath, "journal.json")))
            {
                using var emptyLock = transaction.AcquireLock();
                if (!Directory.Exists(Path.Combine(args[1], "app")) || Directory.Exists(Path.Combine(transaction.ActivePath, "backup"))) throw new InvalidDataException("缺少日志或旧程序不完整，拒绝恢复。");
                transaction.RecycleActive();
                return 0;
            }
            var journal = transaction.Load();
            if (journal.Rid != "linux-x64") throw new InvalidDataException("更新事务不是 Linux 包。");
            if (!recover)
            {
                if (!Guid.TryParse(args[2], out var id) || journal.Id != id || journal.Nonce != args[3] || !PackageUpdateTransaction.ProcessMatches(journal.ParentId, journal.ParentStartTicks)) throw new InvalidOperationException("辅助进程启动身份或父进程不匹配。");
                transaction.VerifyHelper(journal);
                string acknowledgment = Path.Combine(transaction.ActivePath, "helper-started");
                string temporary = acknowledgment + ".new";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(journal.Nonce);
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(temporary, acknowledgment);
                while (PackageUpdateTransaction.ProcessMatches(journal.ParentId, journal.ParentStartTicks)) await Task.Delay(250);
            }
            FileStream updateLock;
            if (recover)
            {
                try { updateLock = transaction.AcquireLock(); }
                catch (IOException) { return 0; }
            }
            else
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (true)
                {
                    try { updateLock = transaction.AcquireLock(); break; }
                    catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(100); }
                }
            }
            using var heldLock = updateLock;
            journal = transaction.Load();
            if (recover)
            {
                if (journal.Stage is not ("Committed" or "RolledBack") && PackageUpdateTransaction.ProcessMatches(journal.ParentId, journal.ParentStartTicks)) { Console.Error.WriteLine("更新父进程仍在运行。"); return 1; }
                if (journal.Stage == "Committed") transaction.PublishCommitted(journal);
                if (journal.Stage is not ("Committed" or "RolledBack")) transaction.Rollback(journal);
                transaction.Cleanup();
                return 0;
            }
            transaction.VerifyHelper(journal);
            try
            {
                RunSystemctl("stop");
                transaction.Apply(journal);
                RunSystemctl("start");
                await LinuxUpdateAuthorization.AuthorizeAsync(transaction, CancellationToken.None);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
                while (true)
                {
                    if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("新版本未在期限内就绪。");
                    try
                    {
                        if (File.Exists(transaction.ReadyPath) && new FileInfo(transaction.ReadyPath).Length > 0) { transaction.Commit(journal); break; }
                    }
                    catch (IOException) { }
                    catch (System.Text.Json.JsonException) { }
                    await Task.Delay(250);
                }
                transaction.Cleanup();
                return 0;
            }
            catch
            {
                if (Directory.Exists(transaction.ActivePath) && transaction.Load().Stage == "Committed") throw;
                RunSystemctl("stop");
                transaction.Rollback(journal);
                transaction.Cleanup();
                RunSystemctl("start");
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("更新失败: " + ex.Message);
            return 1;
        }
    }

    private static void RunSystemctl(string action)
    {
        using var process = Process.Start(new ProcessStartInfo("systemctl") { UseShellExecute = false, ArgumentList = { action, "ustc-course-assistant.service" } }) ?? throw new InvalidOperationException("无法运行 systemctl。");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("systemctl " + action + " 失败。");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();
}
