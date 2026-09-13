using System.Text.Json;
using System.IO;

namespace UstcCourseAssistant;

public sealed class LocalStore
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USTC Course Assistant");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private string SettingsPath => Path.Combine(_root, "settings.json");
    private string LogPath => Path.Combine(_root, "activity.jsonl");

    public AppSettings Load()
    {
        Directory.CreateDirectory(_root);
        if (!File.Exists(SettingsPath)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), _json) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, _json));
    }

    public void AppendLog(AppLog log)
    {
        Directory.CreateDirectory(_root);
        File.AppendAllText(LogPath, JsonSerializer.Serialize(log, _json) + Environment.NewLine);
        TrimLogs();
    }

    private void TrimLogs()
    {
        if (!File.Exists(LogPath)) return;
        var cutoff = DateTimeOffset.Now.AddDays(-14);
        var retained = File.ReadLines(LogPath).Where(line =>
        {
            try { return JsonSerializer.Deserialize<AppLog>(line, _json)?.At >= cutoff; }
            catch { return false; }
        }).ToList();
        File.WriteAllLines(LogPath, retained);
    }
}
