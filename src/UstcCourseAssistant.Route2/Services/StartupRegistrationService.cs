using Microsoft.Win32;

namespace UstcCourseAssistant.Route2.Services;

public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "USTC Course Assistant Route2";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            SetExecutablePath(Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前程序路径。"));
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void SetExecutablePath(string executablePath)
    {
        var executable = System.IO.Path.GetFullPath(executablePath);
        if (!System.IO.File.Exists(executable) ||
            !string.Equals(System.IO.Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("开机启动目标必须是存在的程序文件。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法打开当前用户的开机启动设置。");
        key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
    }

    internal static string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    internal static void RestoreRegisteredCommand(string? command)
    {
        if (command is null)
        {
            SetEnabled(false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法恢复原开机启动设置。");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
