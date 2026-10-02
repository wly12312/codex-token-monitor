using System.IO;
using System.Text.Json;

namespace CodexTokenMonitor;

public sealed class MonitorSettings
{
    public bool Topmost { get; set; }
    public bool ShowRemainingQuota { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    private static string SettingsPath => new MonitorPaths().SettingsPath;
    public static MonitorSettings Load()
    {
        try { return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return new(); }
    }
    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}
