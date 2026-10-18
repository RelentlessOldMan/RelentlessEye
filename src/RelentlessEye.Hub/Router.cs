using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelentlessEye.Wire;

namespace RelentlessEye.Hub;

/// <summary>One connected client, as the router sees it.</summary>
internal interface IPeer
{
    int Id { get; }
    bool IsLoopback { get; }
    string Remote { get; }
    /// <summary>Queues one serialized line to this client. Never blocks; a client that falls hopelessly behind is closed.</summary>
    void SendLine(string json);
    void Close();

    /// <summary>Serializes now (the router may change the state it refers to right after) and queues.</summary>
    void Send(Message m) => SendLine(Protocol.Serialize(m));
}

/// <summary>
/// The hub's whole brain: who is connected, every app's layout and latest values, and request
/// routing. Pure channel: it only knows what apps tell it. All state is behind one lock; nothing in
/// here waits on I/O (sends just queue).
/// </summary>
internal sealed class Router
{
    private sealed class AppEntry
    {
        public required AppState State;
        public IPeer? Peer;
        public string? Session;
    }

    private sealed class Pending
    {
        public required IPeer Origin;
        public required string? OriginId;
        public required IPeer Target;
        public required DateTime Deadline;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, AppEntry> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _peerApp = []; // peer id → app key
    private readonly Dictionary<int, IPeer> _monitors = [];
    private readonly Dictionary<int, (string? App, string? Machine)> _monitorNames = []; // peer id → what its hello said
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly HubConfig _config;
    private readonly HubLog _log;
    private readonly Action _shutdown;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;

    public Router(HubConfig config, HubLog log, Action shutdown)
    {
        _config = config;
        _log = log;
        _shutdown = shutdown;
    }

    public static string Version { get; } = typeof(Router).Assembly.GetName().Version?.ToString(3) ?? "?";

    public TimeSpan RequestTimeout { get; set; } = Protocol.RequestTimeout;

    /// <summary>Handles a client's first message. False: refused (an error was sent; close the connection).</summary>
    public bool Hello(IPeer peer, Message hello)
    {
        string? refusal = hello.Type != MessageTypes.Hello ? "first message must be a hello"
            : hello.Role is not (Roles.App or Roles.Monitor) ? $"hello needs role '{Roles.App}' or '{Roles.Monitor}'"
            : string.IsNullOrWhiteSpace(hello.App) ? "hello needs an app name"
            : !peer.IsLoopback && !TokenOk(hello.Token) ? "wrong or missing token (connections from other machines need the hub's token)"
            : null;
        if (refusal is not null)
        {
            _log.Warn($"refused {peer.Remote}: {refusal}");
            peer.Send(new Message { Type = MessageTypes.Error, Text = refusal });
            return false;
        }

        lock (_lock)
        {
            if (hello.Role == Roles.Monitor)
            {
                _monitors[peer.Id] = peer;
                _monitorNames[peer.Id] = (hello.App, hello.Machine);
                peer.Send(new Message { Type = MessageTypes.Welcome, Key = $"monitor:{peer.Id}", Text = Banner() });
                foreach (var e in _apps.Values) peer.Send(new Message { Type = MessageTypes.AppState, State = e.State });
                _log.Info($"monitor {hello.App} connected from {peer.Remote}");
                return true;
            }

            var baseKey = Protocol.AppKey(hello.Machine, hello.App!, hello.Instance);
            var key = baseKey;
            // Two live copies with the same identity get separate panels rather than fighting over one;
            // the same client reconnecting before its old connection timed out takes its panel back.
            for (int n = 2; _apps.TryGetValue(key, out var taken) && taken.Peer is not null; n++)
            {
                if (hello.Session is { Length: > 0 } && taken.Session == hello.Session)
                {
                    var old = taken.Peer;
                    taken.Peer = null;
                    _peerApp.Remove(old.Id);
                    old.Close();
                    _log.Info($"app {key} reconnected; closing its old connection {old.Remote}");
                    break;
                }
                key = baseKey + "#" + n;
            }

            // Everything that can fail on bad input runs before anything is filed under the key.
            var layout = LayoutDef.Clean(hello.Layout);
            var provides = hello.Provides?.Where(c => c is not null && !string.IsNullOrEmpty(c.Name)).ToList();

            if (!_apps.TryGetValue(key, out var entry))
                _apps[key] = entry = new AppEntry { State = new AppState { Key = key } };
            entry.Peer = peer;
            entry.Session = hello.Session;
            _peerApp[peer.Id] = key;
            var s = entry.State;
            s.App = hello.App!;
            s.Instance = hello.Instance;
            s.Machine = hello.Machine;
            s.Version = hello.Version;
            s.Provides = provides;
            s.Connected = true;
            s.ConnectedAt = s.LastSeen = DateTimeOffset.Now;
            StateMerge.Relayout(s, layout);

            peer.Send(new Message { Type = MessageTypes.Welcome, Key = key, Text = Banner() });
            Broadcast(new Message { Type = MessageTypes.AppState, State = s });
            _log.Info($"app {key} connected from {peer.Remote} ({hello.Version ?? "no version"}, {s.Layout?.AllWidgets.Count() ?? 0} widgets)");
            return true;
        }
    }

    /// <summary>Every message after the hello.</summary>
    public void Handle(IPeer peer, Message m)
    {
        lock (_lock)
        {
            switch (m.Type)
            {
                case MessageTypes.Ping:
                    break;
                case MessageTypes.Update:
                {
                    if (!TryApp(peer, out var e)) return;
                    StateMerge.Apply(e.State, m);
                    e.State.LastSeen = DateTimeOffset.Now;
                    Broadcast(new Message
                    {
                        Type = MessageTypes.Update, Key = e.State.Key, Ts = e.State.LastSeen,
                        Values = m.Values, Append = m.Append, Streams = m.Streams,
                    });
                    break;
                }
                case MessageTypes.Layout:
                {
                    if (!TryApp(peer, out var e)) return;
                    StateMerge.Relayout(e.State, m.Layout);
                    e.State.LastSeen = DateTimeOffset.Now;
                    if (m.Provides is not null) e.State.Provides = m.Provides.Where(c => c is not null && !string.IsNullOrEmpty(c.Name)).ToList();
                    Broadcast(new Message { Type = MessageTypes.AppState, State = e.State });
                    break;
                }
                case MessageTypes.Request:
                    Route(peer, m);
                    break;
                case MessageTypes.Response:
                    // Only the app the request went to may answer it; anyone else's reply is ignored.
                    if (m.Corr is not null && _pending.TryGetValue(m.Corr, out var p) && p.Target.Id == peer.Id && _pending.Remove(m.Corr))
                        p.Origin.Send(new Message { Type = MessageTypes.Response, Corr = p.OriginId, Result = m.Result, Error = m.Error });
                    break;
                default:
                    peer.Send(new Message { Type = MessageTypes.Error, Corr = m.Id, Text = $"unknown message type '{m.Type}'" });
                    break;
            }
        }
    }

    public void Disconnected(IPeer peer)
    {
        lock (_lock)
        {
            _monitors.Remove(peer.Id);
            _monitorNames.Remove(peer.Id);
            foreach (var (id, p) in _pending.ToList())
            {
                if (p.Target.Id == peer.Id)
                {
                    _pending.Remove(id);
                    p.Origin.Send(new Message { Type = MessageTypes.Response, Corr = p.OriginId, Error = "the app disconnected before answering" });
                }
                else if (p.Origin.Id == peer.Id) _pending.Remove(id);
            }
            if (_peerApp.Remove(peer.Id, out var key) && _apps.TryGetValue(key, out var e) && e.Peer?.Id == peer.Id)
            {
                e.Peer = null;
                e.State.Connected = false;
                e.State.LastSeen = DateTimeOffset.Now;
                Broadcast(new Message { Type = MessageTypes.Gone, Key = key, Removed = false });
                _log.Info($"app {key} disconnected");
            }
        }
    }

    /// <summary>Called about once a second: times out requests, forgets long-offline apps.</summary>
    public void Sweep(DateTime utcNow)
    {
        lock (_lock)
        {
            foreach (var (id, p) in _pending.ToList())
                if (utcNow >= p.Deadline)
                {
                    _pending.Remove(id);
                    p.Origin.Send(new Message { Type = MessageTypes.Response, Corr = p.OriginId, Error = $"no answer within {RequestTimeout.TotalSeconds:0}s" });
                }
            if (_config.ForgetOfflineAfterHours > 0)
            {
                // Clamped: a huge setting means "practically never", not an overflow every second.
                var cutoff = DateTimeOffset.Now - TimeSpan.FromHours(Math.Min(_config.ForgetOfflineAfterHours, 24 * 365 * 100));
                foreach (var (key, e) in _apps.ToList())
                    if (e.Peer is null && e.State.LastSeen < cutoff) Forget(key);
            }
        }
    }

    public int AppCount { get { lock (_lock) return _apps.Count; } }

    private void Route(IPeer from, Message req)
    {
        var name = req.Name ?? "";
        string? to = req.To;
        if (to is null)
        {
            int dot = name.IndexOf('.');
            if (dot <= 0) { Reply(from, req, null, $"request '{name}' has no target: use App.name or set 'to'"); return; }
            to = name[..dot];
            name = name[(dot + 1)..];
        }
        if (string.Equals(to, "hub", StringComparison.OrdinalIgnoreCase)) { BuiltIn(from, req, name); return; }

        var target = FindApp(to);
        if (target?.Peer is null)
        {
            Reply(from, req, null, target is null ? $"no app '{to}'" : $"'{target.State.Key}' is offline");
            return;
        }
        var id = Protocol.NewId();
        _pending[id] = new Pending { Origin = from, OriginId = req.Id, Target = target.Peer, Deadline = DateTime.UtcNow + RequestTimeout };
        // Stamped by the hub, so the app can tell a monitor (a person at a panel) from another app.
        var fwd = new Message { Type = MessageTypes.Request, Id = id, Name = name, Args = req.Args };
        if (_peerApp.TryGetValue(from.Id, out var k) && _apps.TryGetValue(k, out var sender))
            (fwd.Key, fwd.Role, fwd.App, fwd.Machine) = (k, Roles.App, sender.State.App, sender.State.Machine);
        else if (_monitorNames.TryGetValue(from.Id, out var m))
            (fwd.Key, fwd.Role, fwd.App, fwd.Machine) = ($"monitor:{from.Id}", Roles.Monitor, m.App, m.Machine);
        else
            fwd.Key = $"peer:{from.Id}";
        target.Peer.Send(fwd);
    }

    /// <summary>Exact key, else the first connected (then any) app with that name.</summary>
    private AppEntry? FindApp(string to)
    {
        if (_apps.TryGetValue(to, out var exact)) return exact;
        var byName = _apps.Values.Where(e => string.Equals(e.State.App, to, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Peer is null).ThenBy(e => e.State.ConnectedAt).ToList();
        return byName.FirstOrDefault();
    }

    private void BuiltIn(IPeer from, Message req, string name)
    {
        string? Arg(string p) => req.Args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        switch (name.ToLowerInvariant())
        {
            case "info":
                Reply(from, req, new
                {
                    hub = "RelentlessEye", version = Version, protocol = Protocol.Version,
                    machine = Environment.MachineName, pid = Environment.ProcessId,
                    started = _started, uptimeSeconds = (long)(DateTimeOffset.Now - _started).TotalSeconds,
                    port = _config.Port, bind = _config.Bind,
                    apps = _apps.Count, connected = _apps.Values.Count(e => e.Peer is not null), monitors = _monitors.Count,
                }, null);
                break;
            case "apps":
                Reply(from, req, _apps.Values.OrderBy(e => e.State.Key, StringComparer.OrdinalIgnoreCase).Select(e => new
                {
                    e.State.Key, e.State.App, e.State.Instance, e.State.Machine, e.State.Version,
                    e.State.Connected, e.State.ConnectedAt, e.State.LastSeen, e.State.Provides,
                    remote = e.Peer?.Remote,
                }).ToList(), null);
                break;
            case "app":
            {
                var key = Arg("key");
                var e = key is null ? null : FindApp(key);
                Reply(from, req, e?.State, e is null ? $"no app '{key}'" : null);
                break;
            }
            case "forget":
            {
                var key = Arg("key");
                if (key is null || !_apps.TryGetValue(key, out var e)) Reply(from, req, null, $"no app '{key}'");
                else if (e.Peer is not null) Reply(from, req, null, $"'{key}' is connected; only offline apps can be forgotten");
                else { Forget(key); Reply(from, req, new { forgot = key }, null); }
                break;
            }
            case "shutdown":
                if (!from.IsLoopback) { Reply(from, req, null, "shutdown is only accepted from this machine"); break; }
                _log.Info($"shutdown requested by {from.Remote}");
                Reply(from, req, new { stopping = true }, null);
                _shutdown();
                break;
            default:
                Reply(from, req, null, $"unknown hub request '{name}' (try hub.info, hub.apps, hub.app, hub.forget, hub.shutdown)");
                break;
        }
    }

    private void Forget(string key)
    {
        _apps.Remove(key);
        Broadcast(new Message { Type = MessageTypes.Gone, Key = key, Removed = true });
        _log.Info($"forgot app {key}");
    }

    private bool TryApp(IPeer peer, out AppEntry entry)
    {
        if (_peerApp.TryGetValue(peer.Id, out var key) && _apps.TryGetValue(key, out entry!) && entry.Peer?.Id == peer.Id) return true;
        peer.Send(new Message { Type = MessageTypes.Error, Text = "only an app connection can send that" });
        entry = null!;
        return false;
    }

    private static void Reply(IPeer to, Message req, object? result, string? error)
    {
        var m = new Message { Type = MessageTypes.Response, Corr = req.Id, Error = error };
        if (error is null && result is not null) m.Result = Protocol.ToElement(result);
        to.Send(m);
    }

    private void Broadcast(Message m)
    {
        if (_monitors.Count == 0) return;
        var line = Protocol.Serialize(m);
        foreach (var mon in _monitors.Values) mon.SendLine(line);
    }

    private bool TokenOk(string? token)
    {
        if (string.IsNullOrEmpty(_config.Token) || string.IsNullOrEmpty(token)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_config.Token));
    }

    private string Banner() => $"RelentlessEye hub {Version} on {Environment.MachineName}";
}
