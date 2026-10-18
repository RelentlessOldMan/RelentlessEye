using System.Security.Cryptography;
using System.Text.Json;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Hub;

/// <summary>
/// <c>%LOCALAPPDATA%\RelentlessMonitor\hub.json</c>. Written with defaults and a fresh random token
/// the first time the hub runs; edit it to open the hub to other machines.
/// </summary>
internal sealed class HubConfig
{
    public int Port { get; set; } = Protocol.DefaultPort;

    /// <summary><c>localhost</c> (default; this machine only), <c>all</c> (every interface), or one IP address.</summary>
    public string Bind { get; set; } = "localhost";

    /// <summary>Connections from other machines must send this in their hello. Localhost ones needn't.</summary>
    public string Token { get; set; } = "";

    /// <summary>An app that stays offline this long is dropped from the monitor. 0 keeps them until forgotten.</summary>
    public double ForgetOfflineAfterHours { get; set; } = 24;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelentlessMonitor", "hub.json");

    private static readonly JsonSerializerOptions FileJson = new(Protocol.Json) { WriteIndented = true };

    /// <summary>Loads <paramref name="path"/>, creating it (with a new token) if missing or filling in a missing token.</summary>
    public static HubConfig LoadOrCreate(string path, HubLog log)
    {
        HubConfig? cfg = null;
        if (File.Exists(path))
        {
            try { cfg = JsonSerializer.Deserialize<HubConfig>(File.ReadAllText(path), FileJson); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                log.Warn($"config {path} unreadable ({ex.Message}); using defaults and leaving the file alone");
                return new HubConfig { Token = NewToken() };
            }
        }
        cfg ??= new HubConfig();
        if (string.IsNullOrWhiteSpace(cfg.Token) || !File.Exists(path))
        {
            if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = NewToken();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(cfg, FileJson));
                log.Info($"wrote config {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"could not write config {path}: {ex.Message}");
            }
        }
        return cfg;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
