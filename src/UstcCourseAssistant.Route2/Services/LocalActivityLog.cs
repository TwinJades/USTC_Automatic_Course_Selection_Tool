using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UstcCourseAssistant.Route2.Services;

public enum ActivityEventKind
{
    Detail,
    ApplicationLifecycle,
    Verification,
    ReadOnlyCheck,
    MonitoringStarted,
    MonitoringStopped,
    CourseAdd,
    CourseDrop,
    CourseRestore,
    Critical
}

public sealed record LocalLogEntry(DateTimeOffset At, string Level, string Message)
{
    public ActivityEventKind Kind { get; init; } = ActivityEventKind.Detail;

    [JsonIgnore]
    public bool IsKeyEvent => Kind != ActivityEventKind.Detail;

    [JsonIgnore]
    public bool IsEmphasized => Kind is ActivityEventKind.CourseAdd
        or ActivityEventKind.CourseDrop
        or ActivityEventKind.CourseRestore;

    [JsonIgnore]
    public string DisplayText => $"{At:MM-dd HH:mm:ss} [{Level}] {Message}";
}

public sealed partial class LocalActivityLog(string? path = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _path = path ?? AppDataPaths.ActivityLog;
    private readonly object _sync = new();
    private DateTimeOffset _lastRetentionAt = DateTimeOffset.MinValue;

    public IReadOnlyList<LocalLogEntry> LoadRecent()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var cutoff = DateTimeOffset.Now.AddDays(-14);
            var retained = new List<LocalLogEntry>();
            foreach (var line in File.ReadLines(_path))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<LocalLogEntry>(line, JsonOptions);
                    if (entry is not null && entry.At >= cutoff)
                    {
                        retained.Add(entry with { Message = Sanitize(entry.Message) });
                    }
                }
                catch (JsonException)
                {
                    // 单行损坏不影响其他日志，也不把损坏正文重新写回。
                }
            }

            Rewrite(retained);
            _lastRetentionAt = DateTimeOffset.Now;
            return retained;
        }
    }

    public LocalLogEntry Append(
        string level,
        string message,
        ActivityEventKind kind = ActivityEventKind.Detail)
    {
        lock (_sync)
        {
            if (File.Exists(_path) && DateTimeOffset.Now - _lastRetentionAt >= TimeSpan.FromHours(1))
            {
                LoadRecent();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var entry = new LocalLogEntry(DateTimeOffset.Now, Sanitize(level), Sanitize(message))
            {
                Kind = kind
            };
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return entry;
        }
    }

    public static string Sanitize(string input)
    {
        var value = input ?? string.Empty;
        value = SecretAssignmentRegex().Replace(value, "$1=[已脱敏]");
        value = AuthorizationRegex().Replace(value, "Authorization=[已脱敏]");
        value = JwtRegex().Replace(value, "[令牌已脱敏]");
        value = LongValueRegex().Replace(value, "[长值已脱敏]");
        value = UrlQueryRegex().Replace(value, "$1?[参数已脱敏]");
        return value.Length <= 1000 ? value : value[..1000] + "…";
    }

    private void Rewrite(IEnumerable<LocalLogEntry> entries)
    {
        var text = string.Join(Environment.NewLine, entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
        if (text.Length > 0)
        {
            text += Environment.NewLine;
        }

        AtomicFileStore.WriteText(_path, text, preserveBackup: false);

        // 旧版或中断写入可能留下副本；不能让其中的过期/未脱敏内容绕过 14 天规则。
        foreach (var residue in FindResidueFiles())
        {
            using var stream = new FileStream(residue, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    private IEnumerable<string> FindResidueFiles()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var fileName = Path.GetFileName(_path);
        return Directory.EnumerateFiles(directory)
            .Where(candidate =>
            {
                var candidateName = Path.GetFileName(candidate);
                return candidateName.Equals(fileName + ".bak", StringComparison.OrdinalIgnoreCase)
                       || candidateName.StartsWith(fileName + ".tmp-", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
    }

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|验证码|短信验证码|cookie|token|ticket|authorization)\s*[:=]\s*[^\s;,]+")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)authorization\s+[^\s]+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9+/=_-]{80,}\b")]
    private static partial Regex LongValueRegex();

    [GeneratedRegex(@"(https?://[^\s?]+)\?[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQueryRegex();
}
