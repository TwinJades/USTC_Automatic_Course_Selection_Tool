using System.IO;
using System.Text.Json;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public sealed record AutomationLoadResult(AutomationSettings Settings, string? Warning);

public sealed class AutomationStore(string? path = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = path ?? AppDataPaths.AutomationSettings;

    public AutomationLoadResult Load()
    {
        var candidates = new[] { _path, _path + ".bak" }
            .Concat(FindTemporaryCandidates())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Exception? primaryError = null;
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var settings = JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(candidate), JsonOptions)
                               ?? throw new JsonException("配置内容为空。");
                Normalize(settings);
                if (!candidate.Equals(_path, StringComparison.OrdinalIgnoreCase))
                {
                    var archived = string.Empty;
                    try
                    {
                        archived = AtomicFileStore.ArchiveCorruptFile(_path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    var source = Path.GetFileName(candidate);
                    var archiveNote = archived.Length == 0 ? string.Empty : $"，损坏文件保留为 {Path.GetFileName(archived)}";
                    try
                    {
                        Save(settings);
                        return new AutomationLoadResult(settings, $"主配置损坏或未完成，已从 {source} 恢复{archiveNote}。");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new AutomationLoadResult(
                            settings,
                            $"主配置损坏或未完成，已从 {source} 读回任务，但暂时无法重建主配置{archiveNote}。请勿关闭程序并检查磁盘权限。");
                    }
                }

                return new AutomationLoadResult(settings, null);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                if (candidate.Equals(_path, StringComparison.OrdinalIgnoreCase))
                {
                    primaryError = ex;
                }
            }
        }

        if (primaryError is not null)
        {
            string archived = string.Empty;
            try
            {
                archived = AtomicFileStore.ArchiveCorruptFile(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var note = archived.Length == 0 ? string.Empty : $"，损坏文件保留为 {Path.GetFileName(archived)}";
            return new AutomationLoadResult(new AutomationSettings(), "任务配置损坏且没有可用备份，已加载空配置" + note + "。");
        }

        return new AutomationLoadResult(new AutomationSettings(), null);
    }

    public void Save(AutomationSettings settings)
    {
        Normalize(settings);
        AtomicFileStore.WriteText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void Normalize(AutomationSettings settings)
    {
        settings.IntervalSeconds = Math.Clamp(settings.IntervalSeconds, 2, 3600);
        settings.Tasks ??= [];
        foreach (var task in settings.Tasks)
        {
            task.CourseCode ??= string.Empty;
            task.CourseName ??= "待预检查";
            task.Status ??= "等待中";
            task.ConflictingCourseCodes ??= [];
        }
    }

    private IEnumerable<string> FindTemporaryCandidates()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var prefix = Path.GetFileName(_path) + ".tmp-";
        return Directory.EnumerateFiles(directory)
            .Where(candidate => Path.GetFileName(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
    }
}
