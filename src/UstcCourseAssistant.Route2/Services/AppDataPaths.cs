using System.IO;

namespace UstcCourseAssistant.Route2.Services;

public static class AppDataPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string AutomationSettings => Path.Combine(Root, "automation-settings.json");
    public static string OperationJournal => Path.Combine(Root, "operation-journal.json");
    public static string ActivityLog => Path.Combine(Root, "activity.jsonl");

    private static string ResolveRoot()
    {
        var isolatedTestRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT");
        return string.IsNullOrWhiteSpace(isolatedTestRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USTC Course Assistant Route2")
            : Path.GetFullPath(isolatedTestRoot);
    }
}
