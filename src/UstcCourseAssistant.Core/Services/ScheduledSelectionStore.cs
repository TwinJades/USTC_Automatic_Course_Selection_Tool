using System.IO;
using System.Text.Json;
using UstcCourseAssistant.Core.Models;

namespace UstcCourseAssistant.Core.Services;

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
                throw LoadFailure("无法读取预约恢复目录，预约执行已禁用", ex);
            }

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw LoadFailure($"无法读取预约配置 {Path.GetFileName(candidate)}，预约执行已禁用", ex);
                }

                ScheduledSelectionSettings settings;
                bool legacy;
                try
                {
                    (settings, legacy) = Parse(bytes);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                {
                    errors.Add($"{Path.GetFileName(candidate)}：{ex.Message}");
                    continue;
                }

                var recovered = !candidate.Equals(_path, StringComparison.OrdinalIgnoreCase);
                if (!legacy && !recovered)
                {
                    return settings;
                }

                try
                {
                    // 迁移必须先保存实际候选的原始字节，再归档损坏主文件并提交新格式。
                    var migrationBackup = legacy ? BackupLegacyBytes(bytes) : null;
                    if (!legacy && settings.Appointment is { } appointment)
                    {
                        DowngradeRecoveredSnapshot(appointment);
                    }

                    var archived = recovered ? AtomicFileStore.ArchiveCorruptFile(_path) : string.Empty;
                    Save(settings);
                    var archiveNote = archived.Length == 0 ? string.Empty : $"；原文件保留为 {Path.GetFileName(archived)}";
                    LoadWarning = legacy
                        ? $"已将旧预约转换为单预约草稿，请核对后重新启用。完整备份：{Path.GetFileName(migrationBackup)}{archiveNote}。"
                        : $"预约主配置缺失或损坏，已从 {Path.GetFileName(candidate)} 恢复；旧快照不会自动执行{archiveNote}。";
                    return settings;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 提交失败不能继续尝试其他候选，否则会发布未经保护的迁移状态。
                    throw LoadFailure($"已找到预约配置 {Path.GetFileName(candidate)}，但无法安全保存迁移或恢复结果；预约执行已禁用", ex);
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
                Appointment = settings.Appointment is { } appointment ? ScheduledSelectionService.Copy(appointment) : null
            };
            Normalize(snapshot);
            AtomicFileStore.WriteText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
    }

    internal static void Normalize(ScheduledSelectionSettings settings)
    {
        if (settings.Appointment is { } appointment)
        {
            NormalizeAppointment(appointment);
        }
    }

    private static void NormalizeAppointment(ScheduledSelection appointment)
    {
        if (appointment.Id == Guid.Empty)
        {
            throw new InvalidDataException("预约标识缺失。");
        }

        if (!Enum.IsDefined(appointment.State) || !Enum.IsDefined(appointment.CompletionAction))
        {
            throw new InvalidDataException("预约包含无法识别的状态或完成方式。");
        }

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
            task.Status ??= "等待中";
            task.ConflictingCourseCodes ??= [];
        }
    }

    private static (ScheduledSelectionSettings Settings, bool Legacy) Parse(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("预约配置根节点必须是对象。");
        }

        var properties = root.EnumerateObject()
            .Where(property => property.Name is "Appointment" or "Appointments").ToArray();
        if (properties.Length != 1)
        {
            throw new InvalidDataException("预约配置必须且只能包含 Appointment 或旧版 Appointments。");
        }

        var property = properties[0];
        if (property.Name == "Appointment")
        {
            if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            {
                throw new InvalidDataException("单预约配置必须是对象或空值。");
            }

            var settings = new ScheduledSelectionSettings
            {
                Appointment = property.Value.Deserialize<ScheduledSelection>(JsonOptions)
            };
            Normalize(settings);
            return (settings, false);
        }

        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("旧版预约配置必须是数组。");
        }

        var appointments = property.Value.Deserialize<List<ScheduledSelection>>(JsonOptions)!;
        var ids = new HashSet<Guid>();
        foreach (var appointment in appointments)
        {
            if (appointment is null || !ids.Add(appointment.Id))
            {
                throw new InvalidDataException("旧版预约标识缺失或重复。");
            }

            NormalizeAppointment(appointment);
        }

        // 按原始执行状态选择，不能先套用备份恢复的保守降级。
        var selected = appointments.Where(appointment => appointment.State is ScheduledSelectionState.Draft
                or ScheduledSelectionState.Armed or ScheduledSelectionState.Queued or ScheduledSelectionState.Running)
            .OrderBy(appointment => appointment.ExecuteAt).ThenBy(appointment => appointment.CreatedAt).ThenBy(appointment => appointment.Id)
            .FirstOrDefault()
            ?? appointments.OrderByDescending(appointment => appointment.FinishedAt)
                .ThenByDescending(appointment => appointment.CreatedAt).ThenBy(appointment => appointment.Id).FirstOrDefault();
        if (selected is not null)
        {
            ScheduledSelectionService.ResetExecution(selected);
        }

        return (new ScheduledSelectionSettings { Appointment = selected }, true);
    }

    private string BackupLegacyBytes(byte[] bytes)
    {
        var backup = _path + ".pre-2.2.1-" + Guid.NewGuid().ToString("N") + ".json";
        using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return backup;
    }

    private static void DowngradeRecoveredSnapshot(ScheduledSelection appointment)
    {
        if (appointment.State is ScheduledSelectionState.Queued or ScheduledSelectionState.Running
            || appointment.StartedAt is not null || appointment.ExecutedTurnId is not null
            || appointment.SelectionFinished || appointment.ImportFinished || appointment.FinishedAt is not null
            || appointment.ConfirmedSelectedTaskIds.Count > 0)
        {
            appointment.State = ScheduledSelectionState.Interrupted;
            appointment.RealOperationsAuthorized = false;
            appointment.Result = "从旧备份恢复，无法确认执行阶段；禁止自动重放或自动导入，请人工核对。";
        }
        else if (appointment.State == ScheduledSelectionState.Armed)
        {
            appointment.State = ScheduledSelectionState.Draft;
            appointment.RealOperationsAuthorized = false;
            appointment.Result = "从旧备份恢复，预约已停用；请重新确认并启用。";
        }
    }

    private InvalidDataException LoadFailure(string message, Exception exception)
    {
        LoadWarning = $"{message}：{exception.Message}";
        return new InvalidDataException(LoadWarning, exception);
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
