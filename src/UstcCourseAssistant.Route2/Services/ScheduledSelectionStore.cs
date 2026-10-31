using System.IO;
using System.Text.Json;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public sealed class ScheduledSelectionStore(string? path = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.GetFullPath(path ?? AppDataPaths.ScheduledSelections);
    private readonly object _sync = new();

    public string? LoadWarning { get; private set; }

    public ScheduledSelectionSettings Load()
    {
        lock (_sync)
        {
            LoadWarning = null;
            var errors = new List<string>();
            string[] candidates;
            try
            {
                candidates = new[] { _path, _path + ".bak" }
                    .Concat(FindTemporaryCandidates())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadWarning = $"无法读取预约恢复目录，预约执行已禁用：{ex.Message}";
                throw new InvalidDataException(LoadWarning, ex);
            }
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                ScheduledSelectionSettings settings;
                try
                {
                    settings = JsonSerializer.Deserialize<ScheduledSelectionSettings>(File.ReadAllText(candidate), JsonOptions)
                               ?? throw new JsonException("预约配置内容为空。");
                    Normalize(settings);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add($"{Path.GetFileName(candidate)}：{ex.Message}");
                    continue;
                }

                if (candidate.Equals(_path, StringComparison.OrdinalIgnoreCase))
                {
                    return settings;
                }

                foreach (var appointment in settings.Appointments)
                {
                    if (appointment.State is ScheduledSelectionState.Queued or ScheduledSelectionState.Running
                        || appointment.StartedAt is not null || appointment.ExecutedTurnId is not null
                        || appointment.SelectionFinished || appointment.ImportFinished || appointment.FinishedAt is not null
                        || appointment.ConfirmedSelectedTaskIds.Count > 0)
                    {
                        appointment.State = ScheduledSelectionState.Interrupted;
                        appointment.Result = "从旧备份恢复，无法确认执行阶段；禁止自动重放或自动导入，请人工核对。";
                    }
                    else if (appointment.State == ScheduledSelectionState.Armed)
                    {
                        appointment.State = ScheduledSelectionState.Draft;
                        appointment.RealOperationsAuthorized = false;
                        appointment.Result = "从旧备份恢复，预约已停用；请重新确认并启用。";
                    }
                }

                try
                {
                    // 先保留损坏主文件，禁止在保护恢复来源之前覆盖它。
                    var archived = AtomicFileStore.ArchiveCorruptFile(_path);
                    Save(settings);
                    var archiveNote = archived.Length == 0 ? string.Empty : $"；原文件保留为 {Path.GetFileName(archived)}";
                    LoadWarning = $"预约主配置缺失或损坏，已从 {Path.GetFileName(candidate)} 恢复；旧快照不会自动执行{archiveNote}。";
                    return settings;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoadWarning = $"已找到预约备份 {Path.GetFileName(candidate)}，但无法安全重建主配置；预约执行已禁用：{ex.Message}";
                    throw new InvalidDataException(LoadWarning, ex);
                }
            }

            if (errors.Count == 0)
            {
                return new ScheduledSelectionSettings();
            }

            LoadWarning = "预约配置及所有恢复副本均不可用，预约执行已禁用；原文件已保留。" + string.Join("；", errors);
            throw new InvalidDataException(LoadWarning);
        }
    }

    public void Save(ScheduledSelectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            var snapshot = new ScheduledSelectionSettings
            {
                Appointments = (settings.Appointments ?? []).Select(ScheduledSelectionService.Copy).ToList()
            };
            Normalize(snapshot);
            AtomicFileStore.WriteText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
    }

    internal static void Normalize(ScheduledSelectionSettings settings)
    {
        settings.Appointments ??= [];
        var ids = new HashSet<Guid>();
        foreach (var appointment in settings.Appointments)
        {
            if (appointment is null || appointment.Id == Guid.Empty || !ids.Add(appointment.Id))
            {
                throw new InvalidDataException("预约标识缺失或重复。");
            }

            if (!Enum.IsDefined(appointment.State) || !Enum.IsDefined(appointment.CompletionAction))
            {
                throw new InvalidDataException("预约包含无法识别的状态或完成方式。");
            }

            appointment.TurnName ??= string.Empty;
            appointment.ExecutedTurnName ??= string.Empty;
            appointment.Result ??= string.Empty;
            appointment.Tasks ??= [];
            appointment.ConfirmedSelectedTaskIds ??= [];
            appointment.CreatedAt = appointment.CreatedAt.ToOffset(TimeSpan.FromHours(8));
            appointment.ExecuteAt = appointment.ExecuteAt.ToOffset(TimeSpan.FromHours(8));
            appointment.StartedAt = appointment.StartedAt?.ToOffset(TimeSpan.FromHours(8));
            appointment.FinishedAt = appointment.FinishedAt?.ToOffset(TimeSpan.FromHours(8));
            var taskIds = new HashSet<Guid>();
            foreach (var task in appointment.Tasks)
            {
                if (task is null || task.Id == Guid.Empty || !taskIds.Add(task.Id))
                {
                    throw new InvalidDataException("预约课程标识缺失或重复。");
                }

                task.CourseCode ??= string.Empty;
                task.CourseName ??= "待预检查";
                task.TurnName ??= string.Empty;
                task.Status ??= "等待中";
                task.ConflictingCourseCodes ??= [];
            }
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
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
