using System.IO;
using System.Text.Json;

namespace RelentlessMonitor.Ui;

/// <summary>Where the monitor connects. Saved to <c>%LOCALAPPDATA%\RelentlessMonitor\monitor.json</c>.</summary>
internal sealed class MonitorSettings
{
    /// <summary><c>host[:port]</c>; empty means this machine (or RELENTLESSMONITOR_HUB).</summary>
    public string? Hub { get; set; }

    /// <summary>Needed only for a hub on another machine (its hub.json token).</summary>
    public string? Token { get; set; }

    /// <summary>Key of the app shown last time.</summary>
    public string? Selected { get; set; }

    public double? Width { get; set; }
    public double? Height { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelentlessMonitor", "monitor.json");

    public static MonitorSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
