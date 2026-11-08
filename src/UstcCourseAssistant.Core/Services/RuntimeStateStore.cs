using System.Text.Json;

namespace UstcCourseAssistant.Core.Services;

/// <summary>持久化的监测授权；任务启用状态本身不代表授权。</summary>
public sealed record RuntimeState
{
    public int SchemaVersion { get; init; } = 1;
    public bool MonitoringAuthorized { get; init; }
    public long? StudentId { get; init; }
    public long? TurnId { get; init; }
    public long? SelectedTurnId { get; init; }
    public bool SafetyPaused { get; init; }
}

public sealed record RuntimeStateLoadResult(RuntimeState State, string? Warning);

public sealed class RuntimeStateStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.GetFullPath(path);
    private readonly object _sync = new();

    public RuntimeStateLoadResult Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    // A missing primary with recovery artifacts is not a fresh installation.
                    if (File.Exists(_path + ".bak") || HasTemporaryFiles())
                        return Locked("运行授权主文件缺失；恢复副本不能自动授权，请人工核对。");
                    return new(new RuntimeState(), null);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Locked("运行授权目录无法读取；自动执行已锁定，请人工核对。");
            }
            try
            {
                var text = File.ReadAllText(_path);
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(nameof(RuntimeState.SchemaVersion), out _)
                    || !root.TryGetProperty(nameof(RuntimeState.MonitoringAuthorized), out _)
                    || !root.TryGetProperty(nameof(RuntimeState.StudentId), out _)
                    || !root.TryGetProperty(nameof(RuntimeState.TurnId), out _)
                    || !root.TryGetProperty(nameof(RuntimeState.SelectedTurnId), out _)
                    || !root.TryGetProperty(nameof(RuntimeState.SafetyPaused), out _))
                    throw new JsonException();
                var state = JsonSerializer.Deserialize<RuntimeState>(text, Options) ?? throw new JsonException();
                Validate(state);
                return new(state, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return Locked("运行授权文件无法读取或无效；自动执行已锁定，请人工核对。");
            }
        }
    }

    public void Save(RuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        lock (_sync)
            AtomicFileStore.WriteText(_path, JsonSerializer.Serialize(state, Options));
    }

    private static void Validate(RuntimeState state)
    {
        if (state.SchemaVersion != 1 || state.StudentId is <= 0 || state.TurnId is <= 0 || state.SelectedTurnId is <= 0
            || (state.MonitoringAuthorized && (state.StudentId is null || state.TurnId is null || state.SafetyPaused)))
            throw new InvalidDataException("运行授权状态无效。");
    }

    private bool HasTemporaryFiles()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory)) return false;
        var prefix = Path.GetFileName(_path) + ".tmp-";
        return Directory.EnumerateFiles(directory).Any(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal));
    }

    private static RuntimeStateLoadResult Locked(string warning) => new(new RuntimeState { SafetyPaused = true }, warning);
}
