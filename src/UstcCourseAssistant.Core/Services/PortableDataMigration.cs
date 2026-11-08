using System.Security.Cryptography;
using System.Text.Json;
using UstcCourseAssistant.Core.Models;

namespace UstcCourseAssistant.Core.Services;

public enum PortableMigrationPhase { Copying, Verified, Committed, CleanupPending, Recycled }
public sealed record PortableMigrationResult(PortableMigrationPhase Phase, string Message);
public sealed record PortableMigrationFile(string Name, long Length, string Sha256, DateTime LastWriteTimeUtc);
public sealed record PortableMigrationState(string SourceRoot, PortableMigrationPhase Phase, List<PortableMigrationFile> Files);

/// <summary>必须在旧实例锁取得后、任何目标 Store 加载之前运行。</summary>
public sealed class PortableDataMigration
{
    private static readonly string[] Names = ["preferences.json", "automation-settings.json", "scheduled-selections.json", "operation-journal.json", "activity.jsonl"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly PackagePaths _paths;
    private readonly string _source;
    private readonly Action<string> _recycle;

    // sourceRoot 和 recycle 可注入隔离的测试目录及测试回收器；生产调用者必须传入旧 Route2 根。
    public PortableDataMigration(PackagePaths paths, string sourceRoot, Action<string>? recycle = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Path.IsPathFullyQualified(sourceRoot))
            throw new ArgumentException("旧数据根必须是绝对路径。", nameof(sourceRoot));
        _source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (Contains(_source, paths.Root) || Contains(paths.Root, _source))
            throw new IOException("旧数据目录与包目录不得重叠。");
        _recycle = recycle ?? (path => new RecycleService(paths.Data).Recycle(path));
    }

    public static string WindowsSourceRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USTC Course Assistant Route2");

    public PortableMigrationResult Run()
    {
        PackagePaths.EnsureNoReparsePoints(_paths.Data);
        PackagePaths.EnsureNoReparsePoints(_source);
        PackagePaths.EnsureNoReparsePoints(_paths.MigrationState);
        var state = LoadState();
        if (state is not null && !SamePath(state.SourceRoot, _source))
            throw new InvalidDataException("迁移记录的源目录不匹配，禁止自动迁移。");
        if (state?.Phase == PortableMigrationPhase.Recycled)
            return new(state.Phase, "旧数据迁移已完成。");
        if (state?.Phase is PortableMigrationPhase.Committed or PortableMigrationPhase.CleanupPending)
            return FinishCleanup(state);
        if (!Directory.Exists(_source))
        {
            if (state is not null) throw new IOException("迁移尚未提交，旧数据目录已不可用。");
            if (Inventory(_paths.Data).Count != 0) throw new IOException("目标目录已有数据但缺少迁移记录，请确认数据来源后使用新目录。 ");
            // 即使没有旧数据，也必须确认包内数据目录可写。
            SaveState(new(_source, PortableMigrationPhase.Recycled, []));
            return new(PortableMigrationPhase.Recycled, "没有旧数据，已初始化包内目录。");
        }

        var inventory = Inventory(_source);
        if (state is null)
        {
            state = new(_source, PortableMigrationPhase.Copying, inventory);
            CheckDestination(state);
            SaveState(state);
        }
        else
        {
            RequireSameInventory(state.Files, inventory);
            CheckDestination(state);
        }
        var staging = _paths.MigrationStaging;
        PackagePaths.EnsureNoReparsePoints(staging);
        Directory.CreateDirectory(staging);
        foreach (var file in state.Files)
            CopyVerified(Path.Combine(_source, file.Name), Path.Combine(staging, file.Name), file);
        ValidateStores(staging, state.Files);
        state = state with { Phase = PortableMigrationPhase.Verified };
        SaveState(state);
        RequireSameInventory(state.Files, Inventory(_source));
        CheckDestination(state);
        foreach (var file in state.Files)
            CopyVerified(Path.Combine(staging, file.Name), Path.Combine(_paths.Data, file.Name), file);
        RequireSameInventory(state.Files, Inventory(_source));
        state = state with { Phase = PortableMigrationPhase.Committed };
        SaveState(state);
        return FinishCleanup(state);
    }

    private PortableMigrationResult FinishCleanup(PortableMigrationState state)
    {
        try
        {
            if (Directory.Exists(_paths.MigrationStaging)) new RecycleService(_paths.Data).Recycle(_paths.MigrationStaging);
            if (Directory.Exists(_source))
            {
                PackagePaths.EnsureNoReparsePoints(_source);
                RequireSameInventory(state.Files, Inventory(_source));
                _recycle(_source);
                if (Directory.Exists(_source)) throw new IOException("回收操作未移除旧目录。");
            }
            state = state with { Phase = PortableMigrationPhase.Recycled };
            SaveState(state);
            return new(state.Phase, "旧数据已验证、提交并回收。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveState(state with { Phase = PortableMigrationPhase.CleanupPending });
            return new(PortableMigrationPhase.CleanupPending, "数据已提交，但旧目录尚未回收；下次启动将重试。" );
        }
    }

    private PortableMigrationState? LoadState()
    {
        if (!File.Exists(_paths.MigrationState)) return null;
        var state = JsonSerializer.Deserialize<PortableMigrationState>(File.ReadAllText(_paths.MigrationState), JsonOptions)
            ?? throw new InvalidDataException("迁移记录为空。");
        if (!Enum.IsDefined(state.Phase) || state.Files is null || state.Files.Any(f => f is null || !Allowed(f.Name) || f.Length < 0 || f.Sha256 is null || f.Sha256.Length != 64)
            || state.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
            throw new InvalidDataException("迁移记录无效。");
        return state;
    }

    private void SaveState(PortableMigrationState state)
    {
        PackagePaths.EnsureNoReparsePoints(_paths.MigrationState);
        PackagePaths.EnsureNoReparsePoints(_paths.MigrationState + ".bak");
        AtomicFileStore.WriteText(_paths.MigrationState, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static bool Allowed(string name) => Names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)
        || name.Equals(n + ".bak", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(n + ".tmp-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(n + ".corrupt-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(n + ".pre-2.2.1-", StringComparison.OrdinalIgnoreCase))
        && name == Path.GetFileName(name);

    private static List<PortableMigrationFile> Inventory(string root)
    {
        PackagePaths.EnsureNoReparsePoints(root);
        var result = new List<PortableMigrationFile>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            if (!Allowed(name)) continue;
            PackagePaths.EnsureNoReparsePoints(path);
            if (!File.Exists(path) || Directory.Exists(path)) throw new IOException("迁移数据项不是普通文件。");
            var info = new FileInfo(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            result.Add(new(name, stream.Length, Convert.ToHexString(SHA256.HashData(stream)), info.LastWriteTimeUtc));
        }
        if (result.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new IOException("迁移数据文件名大小写冲突。");
        return result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void RequireSameInventory(List<PortableMigrationFile> expected, List<PortableMigrationFile> actual)
    {
        if (expected.Count != actual.Count || expected.Zip(actual).Any(pair => !pair.First.Name.Equals(pair.Second.Name, StringComparison.OrdinalIgnoreCase)
            || pair.First.Length != pair.Second.Length || pair.First.Sha256 != pair.Second.Sha256 || pair.First.LastWriteTimeUtc != pair.Second.LastWriteTimeUtc))
            throw new IOException("旧数据在迁移期间发生变化；禁止回收。");
    }

    private void CheckDestination(PortableMigrationState state)
    {
        var expected = state.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Inventory(_paths.Data))
            if (!expected.TryGetValue(file.Name, out var source) || file.Length != source.Length || file.Sha256 != source.Sha256)
                throw new IOException("目标目录已有不同数据，请使用空的新程序目录迁移。");
    }

    private void CopyVerified(string source, string destination, PortableMigrationFile file)
    {
        PackagePaths.EnsureNoReparsePoints(source);
        PackagePaths.EnsureNoReparsePoints(destination);
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (existing.Length != file.Length || Convert.ToHexString(SHA256.HashData(existing)) != file.Sha256)
                throw new IOException("目标目录已有不同数据，请使用空的新程序目录迁移。");
            return;
        }
        // 独立目录不参与业务数据清单，崩溃后的半成品不会被误认为目标冲突。
        var copying = Path.Combine(_paths.MigrationStaging, "copying");
        PackagePaths.EnsureNoReparsePoints(copying);
        Directory.CreateDirectory(copying);
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(destination))));
        var temporary = Path.Combine(copying, key);
        PackagePaths.EnsureNoReparsePoints(temporary);
        if (File.Exists(temporary)) _recycle(temporary);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        File.SetLastWriteTimeUtc(temporary, file.LastWriteTimeUtc);
        using (var check = File.OpenRead(temporary))
            if (check.Length != file.Length || Convert.ToHexString(SHA256.HashData(check)) != file.Sha256)
                throw new IOException("迁移文件哈希校验失败。");
        File.Move(temporary, destination);
    }

    private void ValidateStores(string staging, List<PortableMigrationFile> files)
    {
        // Store.Load 可能重写；只在独立副本操作。
        var validation = Path.Combine(staging, "validation-" + Guid.NewGuid().ToString("N"));
        PackagePaths.EnsureNoReparsePoints(validation);
        Directory.CreateDirectory(validation);
        foreach (var file in files) CopyVerified(Path.Combine(staging, file.Name), Path.Combine(validation, file.Name), file);
        var preferences = Path.Combine(validation, "preferences.json");
        if (File.Exists(preferences) && JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllText(preferences)) is null)
            throw new InvalidDataException("偏好配置为空。");
        var automationCandidates = files.Where(f => f.Name.Equals("automation-settings.json", StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals("automation-settings.json.bak", StringComparison.OrdinalIgnoreCase)
            || f.Name.StartsWith("automation-settings.json.tmp-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (automationCandidates.Length != 0 && !automationCandidates.Any(f => IsValidAutomationCandidate(Path.Combine(validation, f.Name))))
            throw new InvalidDataException("任务配置及恢复副本不可用。");
        var automation = new AutomationStore(Path.Combine(validation, "automation-settings.json")).Load();
        if (automation.Warning is not null && automation.Warning.Contains("没有可用备份", StringComparison.Ordinal))
            throw new InvalidDataException("任务配置及恢复副本不可用。");
        var journalPath = Path.Combine(validation, "operation-journal.json");
        if (!File.Exists(journalPath) && files.Any(f => f.Name.Equals("operation-journal.json.bak", StringComparison.OrdinalIgnoreCase)
            || f.Name.StartsWith("operation-journal.json.tmp-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("真实操作主记录缺失但存在恢复副本，必须先人工核验；禁止迁移后放开操作保护。");
        _ = new ScheduledSelectionStore(Path.Combine(validation, "scheduled-selections.json")).Load();
        _ = new OperationJournalStore(Path.Combine(validation, "operation-journal.json")).Load();
        // 活动日志按现有 Store 的逐行容错策略验证，且只在验证副本执行保留期整理。
        _ = new LocalActivityLog(Path.Combine(validation, "activity.jsonl")).LoadRecent();
        new RecycleService(_paths.Data).Recycle(validation);
    }

    private static bool IsValidAutomationCandidate(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(path)) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Contains(string parent, string child) => SamePath(parent, child) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
