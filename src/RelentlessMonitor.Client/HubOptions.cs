using RelentlessMonitor.Wire;

namespace RelentlessMonitor;

/// <summary>Connection settings shared by <see cref="MonitorClient"/> and <see cref="MonitorViewer"/>.</summary>
public class HubOptions
{
    /// <summary><c>host[:port]</c>. Default: <see cref="Protocol.HubEnvVar"/>, else <c>localhost:47800</c>.</summary>
    public string? Hub { get; set; }

    /// <summary>Token for a hub on another machine. Default: <see cref="Protocol.TokenEnvVar"/>. Localhost needs none.</summary>
    public string? Token { get; set; }

    /// <summary>One connect attempt's limit. Short on purpose: the hub is there or it isn't.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often to look for the hub again while it isn't reachable.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages waiting to be written while connected; the oldest are dropped when full.</summary>
    public int QueueCapacity { get; set; } = 2000;
}
