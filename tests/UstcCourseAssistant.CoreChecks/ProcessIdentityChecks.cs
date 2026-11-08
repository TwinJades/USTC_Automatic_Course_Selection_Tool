using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using UstcCourseAssistant.Core.Update;

static class ProcessIdentityChecks
{
    public static void WriteIdentity(int processId)
    {
        using var process = Process.GetProcessById(processId);
        Console.WriteLine(PackageUpdateTransaction.ProcessStartIdentity(process).ToString(CultureInfo.InvariantCulture));
    }

    public static async Task RunAsync()
    {
        using var self = Process.GetCurrentProcess();
        long identity = PackageUpdateTransaction.ProcessStartIdentity(self);
        if (OperatingSystem.IsLinux())
        {
            string stat = File.ReadAllText($"/proc/{self.Id}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long startTime = long.Parse(fields[19], CultureInfo.InvariantCulture);
            if (identity != startTime) throw new InvalidOperationException("Linux 进程身份必须使用 /proc 的原始 starttime。");
        }
        else if (OperatingSystem.IsWindows() && identity != self.StartTime.ToUniversalTime().Ticks)
        {
            throw new InvalidOperationException("Windows 进程身份必须使用 UTC 启动时间 ticks。");
        }

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到测试进程路径。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--process-identity-probe");
        start.ArgumentList.Add(self.Id.ToString(CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动进程身份测试子进程。");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        string output = await stdout;
        string error = await stderr;
        if (child.ExitCode != 0) throw new InvalidOperationException($"进程身份测试子进程失败：{error}");
        if (!long.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long otherIdentity) || otherIdentity != identity)
            throw new InvalidOperationException($"父子进程读取的父进程身份不一致：{identity} 与 {output.Trim()}。");
        Console.WriteLine("[通过] 跨进程启动身份一致性");
    }
}
