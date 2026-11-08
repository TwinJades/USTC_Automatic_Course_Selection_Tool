using System.Text.Json;
using UstcCourseAssistant.Core.Models;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Core.Update;

/// <summary>更新握手前的只读检查；绝不调用会修复或重写数据的 Store.Load。</summary>
public static class PendingUpdatePreflight
{
    public static void Validate(string packageRoot, PackageUpdateTransaction.Journal expected, Version assemblyVersion)
    {
        var transaction = new PackageUpdateTransaction(packageRoot);
        var journal = transaction.Load();
        if (journal.Id != expected.Id || journal.Nonce != expected.Nonce || journal.Stage != "PendingReady" || journal.Version != expected.Version || journal.Rid != expected.Rid || new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0)) != Version.Parse(journal.Version)) throw new InvalidDataException("待确认更新事务或程序集版本不匹配。");
        string data = Path.Combine(Path.GetFullPath(packageRoot), "data");
        PackageManifest.RejectReparseAncestors(data);
        ValidateJson<AutomationSettings>(data, "automation-settings.json");
        ValidateJson<ScheduledSelectionSettings>(data, "scheduled-selections.json");
        ValidateJson<OperationJournal>(data, "operation-journal.json");
        ValidateJson<RuntimeState>(data, "runtime-state.json");
        ValidateJson<LocalPreferences>(data, "preferences.json");
        string activity = Path.Combine(data, "activity.jsonl");
        if (File.Exists(activity))
        {
            PackageManifest.RejectReparseAncestors(activity);
            using var reader = new StreamReader(activity);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var entry = JsonDocument.Parse(line);
                if (entry.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("活动记录格式无效。");
            }
        }
    }

    private static void ValidateJson<T>(string root, string name)
    {
        string path = Path.Combine(root, name);
        string[] candidates = [path, path + ".bak"];
        bool exists = false;
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            exists = true;
            PackageManifest.RejectReparseAncestors(candidate);
            try
            {
                using var stream = File.OpenRead(candidate);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.Deserialize<T>() is null) throw new JsonException("根对象无效。");
                if (name == "scheduled-selections.json")
                {
                    var properties = document.RootElement.EnumerateObject().Where(p => p.Name is "Appointment" or "Appointments").ToArray();
                    if (properties.Length != 1 || (properties[0].Name == "Appointment" && properties[0].Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) || (properties[0].Name == "Appointments" && properties[0].Value.ValueKind != JsonValueKind.Array)) throw new JsonException("预约配置格式无效。");
                    if (properties[0].Name == "Appointments") _ = properties[0].Value.Deserialize<List<ScheduledSelection>>() ?? throw new JsonException();
                }
                if (name == "operation-journal.json")
                {
                    var operation = document.RootElement.Deserialize<OperationJournal>() ?? throw new JsonException();
                    if (operation.Active) throw new InvalidDataException("存在未解决的真实操作，不能确认更新。");
                }
                if (name == "runtime-state.json")
                {
                    var state = document.RootElement.Deserialize<RuntimeState>() ?? throw new JsonException();
                    if (state.SchemaVersion != 1 || state.StudentId is <= 0 || state.TurnId is <= 0 || state.SelectedTurnId is <= 0 || (state.MonitoringAuthorized && (state.StudentId is null || state.TurnId is null || state.SafetyPaused))) throw new JsonException("运行授权无效。");
                }
                return;
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
        }
        if (exists) throw new InvalidDataException("配置与备份均不可读取：" + name);
    }
}
