using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UstcCourseAssistant.Core.Update;

/// <summary>Windows 桌面程序的更新辅助模式；在单实例锁之前运行。</summary>
public static class WindowsUpdateRunner
{
    public const string Switch = "--update-helper";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length < 3 || args[0] != Switch || args[1] is not ("apply" or "recover") ||
                (args[1] == "apply" ? args.Length != 5 : args.Length != 3) || !Path.IsPathFullyQualified(args[2]))
                throw new ArgumentException("更新辅助参数无效。");
            string root = Path.GetFullPath(args[2]);
            var transaction = new PackageUpdateTransaction(root);
            bool recover = args[1] == "recover";
            string? processPath = Environment.ProcessPath;
            string helperPath = Path.Combine(transaction.ActivePath, "helper", "UstcCourseAssistant.Route2.exe");
            string installedPath = Path.Combine(root, "app", "UstcCourseAssistant.Route2.exe");
            bool fromHelper = processPath is not null && string.Equals(Path.GetFullPath(processPath), helperPath, StringComparison.OrdinalIgnoreCase);
            bool fromInstalled = processPath is not null && string.Equals(Path.GetFullPath(processPath), installedPath, StringComparison.OrdinalIgnoreCase);
            if ((!recover && !fromHelper) || (recover && !fromHelper && !fromInstalled)) throw new InvalidOperationException("更新辅助程序位置无效。");
            if (recover && !Directory.Exists(transaction.ActivePath)) return 0;
            if (recover)
            {
                try { using var probe = transaction.AcquireLock(); }
                catch (IOException) { return 1; }
            }
            if (recover && !File.Exists(Path.Combine(transaction.ActivePath, "journal.json")))
            {
                using var emptyLock = transaction.AcquireLock();
                if (!Directory.Exists(Path.Combine(root, "app")) || Directory.Exists(Path.Combine(transaction.ActivePath, "backup"))) throw new InvalidDataException("缺少日志或旧程序不完整，拒绝恢复。");
                transaction.RecycleActive();
                return 0;
            }
            var journal = transaction.Load();
            if (recover && fromHelper) transaction.VerifyHelper(journal);
            if (journal.Rid != "win-x64") throw new InvalidDataException("更新平台不匹配。");
            if (!recover)
            {
                if (!Guid.TryParseExact(args[3], "D", out var id) || journal.Id != id || journal.Nonce != args[4] || !PackageUpdateTransaction.ProcessMatches(journal.ParentId, journal.ParentStartTicks)) throw new InvalidOperationException("辅助进程启动身份或父进程不匹配。");
                transaction.VerifyHelper(journal);
                string acknowledgment = Path.Combine(transaction.ActivePath, "helper-started");
                string temporary = acknowledgment + ".new";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(journal.Nonce); writer.Flush(); stream.Flush(true);
                }
                File.Move(temporary, acknowledgment);
                while (PackageUpdateTransaction.ProcessMatches(journal.ParentId, journal.ParentStartTicks)) await Task.Delay(250);
            }
            FileStream updateLock;
            if (recover)
            {
                try { updateLock = transaction.AcquireLock(); }
                catch (IOException) { return 1; }
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
                if (journal.Stage is not ("Committed" or "RolledBack") && PackageUpdateTransaction.ProcessMatches(journal.ParentId, journal.ParentStartTicks)) throw new InvalidOperationException("更新父进程仍在运行。");
                if (journal.Stage is not ("Committed" or "RolledBack")) transaction.Rollback(journal);
                // helper 本身位于 active 内，不能在执行期间清理；已安装程序则可以。
                if (fromInstalled) transaction.Cleanup();
                return 0;
            }
            transaction.VerifyHelper(journal);
            Process? child = null;
            try
            {
                transaction.Apply(journal);
                child = Process.Start(new ProcessStartInfo(Path.Combine(root, "app", "UstcCourseAssistant.Route2.exe"))
                {
                    UseShellExecute = false, WorkingDirectory = root,
                    ArgumentList = { "--package-root", root, "--update-pending", journal.Id.ToString(), journal.Nonce }
                }) ?? throw new InvalidOperationException("无法启动新版本。");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
                while (true)
                {
                    if (child.HasExited || DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("新版本未在期限内就绪。");
                    try
                    {
                        if (File.Exists(transaction.ReadyPath) && new FileInfo(transaction.ReadyPath).Length > 0) { transaction.Commit(journal); break; }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                    await Task.Delay(250);
                }
                return 0;
            }
            catch
            {
                if (Directory.Exists(transaction.ActivePath) && transaction.Load().Stage == "Committed") throw;
                if (child is { HasExited: false }) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
                transaction.Rollback(journal);
                Process.Start(new ProcessStartInfo(Path.Combine(root, "app", "UstcCourseAssistant.Route2.exe")) { UseShellExecute = false, WorkingDirectory = root, ArgumentList = { "--package-root", root } });
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("更新失败: " + ex.Message);
            return 1;
        }
    }
}
