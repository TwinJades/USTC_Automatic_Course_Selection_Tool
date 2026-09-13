using System.IO;
using System.Text.Json;

namespace UstcCourseAssistant.Route2.Services;

public sealed record LocalPreferences(
    bool KeepLoginState = true,
    string SavedAccount = "",
    bool AutoLoginEnabled = false,
    bool? OnboardingCompleted = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath
    {
        get
        {
            var testRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT");
            return !string.IsNullOrWhiteSpace(testRoot)
                ? Path.Combine(testRoot, "preferences.json")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "USTC Course Assistant Route2",
                    "preferences.json");
        }
    }

    public static LocalPreferences Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllText(SettingsPath))
                  ?? new LocalPreferences()
                : new LocalPreferences();
        }
        catch (IOException)
        {
            return new LocalPreferences();
        }
        catch (JsonException)
        {
            return new LocalPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new LocalPreferences();
        }
    }

    public static bool HasExistingInstallationData()
    {
        try
        {
            var root = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(root))
            {
                return false;
            }

            return File.Exists(SettingsPath)
                   || File.Exists(AppDataPaths.AutomationSettings)
                   || File.Exists(AppDataPaths.ActivityLog)
                   || File.Exists(AppDataPaths.OperationJournal)
                   || Directory.Exists(Path.Combine(root, "browser-profile"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法可靠判断时按旧用户处理，避免打断已经在使用软件的人。
            return true;
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException)
        {
            // 偏好保存失败不应影响认证或只读功能；不记录路径和任何会话信息。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
