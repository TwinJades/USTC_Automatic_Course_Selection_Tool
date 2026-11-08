using System.IO;

namespace UstcCourseAssistant.Core.Services;

public static class AppDataPaths
{
    private static string? _root;
    public static string Root => _root ?? ResolveRoot();

    public static void Configure(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
            throw new ArgumentException("数据目录必须是绝对路径。", nameof(dataRoot));
        var full = Path.GetFullPath(dataRoot);
        if (_root is not null && !string.Equals(_root, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("数据目录已经初始化。 ");
        Directory.CreateDirectory(full);
        _root = full;
    }

    public static string AutomationSettings => Path.Combine(Root, "automation-settings.json");
    public static string ScheduledSelections => Path.Combine(Root, "scheduled-selections.json");
    public static string OperationJournal => Path.Combine(Root, "operation-journal.json");
    public static string ActivityLog => Path.Combine(Root, "activity.jsonl");

    private static string ResolveRoot()
    {
        var isolatedTestRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(isolatedTestRoot) || !Path.IsPathFullyQualified(isolatedTestRoot))
            throw new InvalidOperationException("必须先配置包内数据目录，或为开发测试指定绝对隔离目录。 ");
        return Path.GetFullPath(isolatedTestRoot);
    }
}
