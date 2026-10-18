using RelentlessMonitor.Wire;

namespace RelentlessMonitor;

/// <summary>
/// The display side: connects as a monitor, keeps a live copy of every app's state, and sends
/// actions. Events fire on a background thread; a UI marshals them to its own.
/// </summary>
public sealed class MonitorViewer : HubConnection
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AppState> _apps = new(StringComparer.Ordinal);

    public MonitorViewer(HubOptions? options = null, string name = "monitor")
        : base(options ?? new HubOptions(), name, Protocol.NewId()[..8], null, typeof(MonitorViewer).Assembly.GetName().Version?.ToString()) { }

    /// <summary>The hub (re)connected and is about to send every app afresh: drop what you show.</summary>
    public event Action? Reset;

    /// <summary>An app appeared, reconnected, or changed its layout. The state is a private copy.</summary>
    public event Action<AppState>? AppChanged;

    /// <summary>An app sent values; <c>state</c> already includes them. Draw from <c>update</c> to touch only what changed.</summary>
    public event Action<AppState, Message>? Updated;

    /// <summary>An app disconnected (<c>removed</c> false: show it offline) or the hub forgot it (true: drop it).</summary>
    public event Action<string, bool>? AppGone;

    /// <summary>Copies of every known app's state.</summary>
    public IReadOnlyList<AppState> Apps
    {
        get { lock (_lock) return _apps.Values.Select(Copy).ToList(); }
    }

    /// <summary>Clicks a button / flips a toggle on app <paramref name="key"/>.</summary>
    public Task<HubResponse> ActionAsync(string key, string widget, object? value = null, CancellationToken ct = default)
        => RequestAsync(Protocol.ActionRequest, new { widget, value }, to: key, ct: ct);

    /// <summary>Asks the hub to drop an offline app's panel.</summary>
    public Task<HubResponse> ForgetAsync(string key) => RequestAsync("hub.forget", new { key });

    private protected override Message BuildHello() => new() { Role = Roles.Monitor };

    private protected override void OnMessage(Message m)
    {
        switch (m.Type)
        {
            case MessageTypes.Welcome:
                lock (_lock) _apps.Clear();
                Reset?.Invoke();
                break;
            case MessageTypes.AppState when m.State is not null:
            {
                AppState copy;
                lock (_lock) { _apps[m.State.Key] = m.State; copy = Copy(m.State); }
                AppChanged?.Invoke(copy);
                break;
            }
            case MessageTypes.Update when m.Key is not null:
            {
                AppState copy;
                lock (_lock)
                {
                    if (!_apps.TryGetValue(m.Key, out var s)) return;
                    StateMerge.Apply(s, m);
                    s.LastSeen = m.Ts ?? DateTimeOffset.Now;
                    copy = Copy(s);
                }
                Updated?.Invoke(copy, m);
                break;
            }
            case MessageTypes.Gone when m.Key is not null:
            {
                bool removed = m.Removed == true;
                lock (_lock)
                {
                    if (removed) _apps.Remove(m.Key);
                    else if (_apps.TryGetValue(m.Key, out var s)) s.Connected = false;
                }
                AppGone?.Invoke(m.Key, removed);
                break;
            }
        }
    }

    private static AppState Copy(AppState s) => new()
    {
        Key = s.Key, App = s.App, Instance = s.Instance, Machine = s.Machine, Version = s.Version,
        Connected = s.Connected, ConnectedAt = s.ConnectedAt, LastSeen = s.LastSeen,
        Layout = s.Layout, Provides = s.Provides,
        Values = new(s.Values),
        Streams = s.Streams.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
    };
}
