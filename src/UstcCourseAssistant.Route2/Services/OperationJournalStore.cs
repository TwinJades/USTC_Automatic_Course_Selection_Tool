using System.IO;
using System.Text.Json;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public sealed class JournalLessonReference
{
    public long LessonId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
}

public sealed class OperationJournal
{
    public Guid WorkflowId { get; set; } = Guid.NewGuid();
    public bool Active { get; set; } = true;
    public string Owner { get; set; } = string.Empty;
    public long StudentId { get; set; }
    public long TurnId { get; set; }
    public string TargetCode { get; set; } = string.Empty;
    public List<string> RelatedCodes { get; set; } = [];
    public string Stage { get; set; } = "准备";
    public string CurrentAction { get; set; } = string.Empty;
    public string CurrentCourseCode { get; set; } = string.Empty;
    public List<string> VerifiedSteps { get; set; } = [];
    public int RecoveryProtocolVersion { get; set; }
    public bool CurrentRequestMayHaveBeenSent { get; set; }
    public bool TargetRequestMayHaveStarted { get; set; }
    public List<JournalLessonReference> ConfirmedDroppedLessons { get; set; } = [];
    public bool AutomaticRecoveryEvaluationStarted { get; set; }
    public bool AutomaticRestoreAttempted { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ResolvedAt { get; set; }
    public string SafetyNote { get; set; } = string.Empty;
}

public sealed record OperationJournalLoadResult(OperationJournal? ActiveJournal, string? Warning);

public sealed class OperationJournalStore(string? path = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = path ?? AppDataPaths.OperationJournal;
    private readonly object _sync = new();

    public OperationJournalLoadResult Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                return new OperationJournalLoadResult(null, null);
            }

            try
            {
                var journal = JsonSerializer.Deserialize<OperationJournal>(File.ReadAllText(_path), JsonOptions);
                if (journal is null)
                {
                    throw new JsonException("真实操作记录为空。");
                }

                return new OperationJournalLoadResult(journal is { Active: true } ? journal : null, null);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                string archived = string.Empty;
                try
                {
                    archived = AtomicFileStore.ArchiveCorruptFile(_path);
                }
                catch (Exception archiveError) when (archiveError is IOException or UnauthorizedAccessException)
                {
                }

                var recovered = TryRecoverActiveJournal();
                if (recovered is not null)
                {
                    Save(recovered);
                    var recoveryDetail = archived.Length == 0 ? string.Empty : "；损坏文件已保留为 " + Path.GetFileName(archived);
                    return new OperationJournalLoadResult(
                        recovered,
                        "上次真实操作记录损坏，已从安全副本恢复最后可确认阶段并保持真实操作锁定" + recoveryDetail + "。");
                }

                var synthetic = new OperationJournal
                {
                    Owner = "未知（事务记录损坏）",
                    Stage = "事务记录损坏，必须人工核对",
                    SafetyNote = "无法确认上次真实操作是否完成。",
                    UpdatedAt = DateTimeOffset.Now
                };
                Save(synthetic);
                var detail = archived.Length == 0 ? string.Empty : "；损坏文件已保留为 " + Path.GetFileName(archived);
                return new OperationJournalLoadResult(synthetic, "上次真实操作记录损坏，已启用安全锁" + detail + "。");
            }
        }
    }

    private OperationJournal? TryRecoverActiveJournal()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var candidates = new List<string> { _path + ".bak" };
        if (Directory.Exists(directory))
        {
            var prefix = Path.GetFileName(_path) + ".tmp-";
            candidates.AddRange(Directory.EnumerateFiles(directory)
                .Where(candidate => Path.GetFileName(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var journal = JsonSerializer.Deserialize<OperationJournal>(File.ReadAllText(candidate), JsonOptions);
                if (journal is { Active: true })
                {
                    return journal;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public void Save(OperationJournal journal)
    {
        lock (_sync)
        {
            journal.UpdatedAt = DateTimeOffset.Now;
            AtomicFileStore.WriteText(_path, JsonSerializer.Serialize(journal, JsonOptions));
        }
    }

    public void Resolve(OperationJournal journal, string note)
    {
        lock (_sync)
        {
            var previousActive = journal.Active;
            var previousStage = journal.Stage;
            var previousSafetyNote = journal.SafetyNote;
            var previousResolvedAt = journal.ResolvedAt;
            journal.Active = false;
            journal.Stage = "已由用户核对并解除保护";
            journal.SafetyNote = note;
            journal.ResolvedAt = DateTimeOffset.Now;
            try
            {
                Save(journal);
            }
            catch
            {
                journal.Active = previousActive;
                journal.Stage = previousStage;
                journal.SafetyNote = previousSafetyNote;
                journal.ResolvedAt = previousResolvedAt;
                throw;
            }
        }
    }
}
