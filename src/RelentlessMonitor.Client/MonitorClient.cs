using System.Collections.Concurrent;
using System.Text.Json;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor;

public sealed class MonitorClientOptions : HubOptions
{
    /// <summary>Tells apart several copies of the same app on one machine (e.g. a repo name). Optional.</summary>
    public string? Instance { get; set; }

    /// <summary>Default: <see cref="Environment.MachineName"/>.</summary>
    public string? Machine { get; set; }

    /// <summary>Shown in the monitor. Default: the entry assembly's version.</summary>
    public string? Version { get; set; }

    /// <summary>On dispose, how long to wait for the last values to reach the hub.</summary>
    public TimeSpan FlushOnDispose { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// An app's link to the monitor. Describe the panel once, then <see cref="Set(string, object?)"/>
/// values whenever they change. Calls never block and never throw: while no hub is reachable the
/// latest values are just kept, and handed to the hub (with chart/feed history) when it appears.
/// </summary>
/// <example><code>
/// var mon = new MonitorClient("CodeCarver", l => l
///     .Section("Run", s => s
///         .Indicator("state", "State", ("idle", "gray"), ("running", "green"), ("failed", "red"))
///         .Progress("progress", "Progress")
///         .Feed("log", "Log", span: 2)
///         .Button("cancel", "Cancel", confirm: "Cancel the run?")));
/// mon.OnAction("cancel", () => cts.Cancel());
/// mon.Start();
/// mon.Set("state", "running");
/// mon.Set("log", "parsing 120 files");   // feed: appends a line
/// </code></example>
public sealed class MonitorClient : HubConnection
{
    private readonly object _lock = new();
    private readonly AppState _state = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _actions = new(StringComparer.Ordinal);
    private readonly MonitorClientOptions _options;
    private Dictionary<string, WidgetDef> _widgets;
    private Dictionary<string, JsonElement> _pendingValues = [];
    private Dictionary<string, List<JsonElement>> _pendingAppend = [];
    private Dictionary<string, List<JsonElement>> _pendingStreams = [];
    private bool _layoutDirty;
    /// <summary>The layout the last hello carried; if it changed before the replay, the replay resends it.</summary>
    private LayoutDef? _helloLayout;
    /// <summary>True from the replay snapshot until the connection drops: changes after the snapshot must go out as pending.</summary>
    private bool _live;

    public MonitorClient(string app, LayoutDef layout, MonitorClientOptions? options = null)
        : this(app, layout, options ?? new MonitorClientOptions(), 0) { }

    public MonitorClient(string app, Action<LayoutBuilder> layout, MonitorClientOptions? options = null)
        : this(app, BuildLayout(layout), options) { }

    private MonitorClient(string app, LayoutDef layout, MonitorClientOptions options, int _)
        : base(options, app, options.Instance, options.Machine, options.Version ?? DefaultVersion())
    {
        if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("app name required", nameof(app));
        _options = options;
        StateMerge.Relayout(_state, layout ?? new LayoutDef());
        _widgets = StateMerge.Widgets(_state.Layout);
    }

    /// <summary>Creates and starts a client in one call.</summary>
    public static MonitorClient Start(string app, Action<LayoutBuilder> layout, MonitorClientOptions? options = null)
    {
        var c = new MonitorClient(app, layout, options);
        c.Start();
        return c;
    }

    /// <summary>A copy of the current layout (changing it changes nothing; use <see cref="SetLayout(LayoutDef)"/>).</summary>
    public LayoutDef Layout { get { lock (_lock) return LayoutDef.Clean(_state.Layout)!; } }

    /// <summary>Replaces the panel layout; monitors redraw. Values of widgets that remain are kept.</summary>
    public void SetLayout(LayoutDef layout)
    {
        lock (_lock)
        {
            StateMerge.Relayout(_state, layout ?? new LayoutDef()); // stores a cleaned copy
            _widgets = StateMerge.Widgets(_state.Layout);
            if (_live) _layoutDirty = true;
        }
        Link.Wake();
    }

    public void SetLayout(Action<LayoutBuilder> layout) => SetLayout(BuildLayout(layout));

    /// <summary>
    /// Sets a widget's value. For a chart or feed this appends one point or line. The value is any
    /// JSON-serializable object; see <see cref="WidgetKinds"/> for what each kind expects.
    /// </summary>
    public void Set(string id, object? value)
    {
        if (id is null) return;
        var e = Protocol.ToElement(value);
        lock (_lock) SetLocked(id, e);
        Link.Wake();
    }

    /// <summary>Sets several widgets in one update.</summary>
    public void Set(params (string Id, object? Value)[] values)
    {
        if (values is null) return;
        var elements = values.Where(v => v.Id is not null).Select(v => (v.Id, Protocol.ToElement(v.Value))).ToList();
        lock (_lock) foreach (var (id, e) in elements) SetLocked(id, e);
        Link.Wake();
    }

    /// <summary>Appends to a chart or feed (same as <see cref="Set(string, object?)"/> on those).</summary>
    public void Append(string id, object? item) => Set(id, item);

    /// <summary>Appends a feed line with a level (<c>info</c>, <c>warn</c>, <c>error</c>); the monitor colors it.</summary>
    public void Log(string id, string text, string level = "info") => Set(id, new { text, level, ts = DateTimeOffset.Now });

    /// <summary>Empties a chart/feed's history, or blanks a plain widget.</summary>
    public void Clear(string id)
    {
        if (id is null) return;
        lock (_lock)
        {
            if (IsStream(id))
            {
                _state.Streams[id] = [];
                if (_live) { _pendingAppend.Remove(id); _pendingStreams[id] = []; }
            }
            else SetLocked(id, Protocol.ToElement(null));
        }
        Link.Wake();
    }

    /// <summary>The latest value this app set for a widget (for a chart/feed: its last item).</summary>
    public JsonElement? Get(string id)
    {
        if (id is null) return null;
        lock (_lock)
        {
            if (_state.Values.TryGetValue(id, out var v)) return v;
            if (_state.Streams.TryGetValue(id, out var s) && s.Count > 0) return s[^1];
            return null;
        }
    }

    /// <summary>
    /// Runs when someone clicks button <paramref name="widgetId"/> (or flips that toggle; the handler
    /// gets the new value). The return value goes back to the monitor; a thrown exception is shown as the error.
    /// </summary>
    public void OnAction(string widgetId, Func<JsonElement?, CancellationToken, Task<object?>> handler) => _actions[widgetId] = handler;

    public void OnAction(string widgetId, Func<JsonElement?, object?> handler)
        => OnAction(widgetId, (v, _) => Task.FromResult(handler(v)));

    public void OnAction(string widgetId, Action handler)
        => OnAction(widgetId, (_, _) => { handler(); return Task.FromResult<object?>(null); });

    /// <summary>A toggle: runs <paramref name="handler"/> with the requested state, then shows that state.</summary>
    public void OnToggle(string widgetId, Action<bool> handler)
        => OnAction(widgetId, (v, _) =>
        {
            bool on = v is { ValueKind: JsonValueKind.True };
            handler(on);
            Set(widgetId, on);
            return Task.FromResult<object?>(on);
        });

    /// <summary>Waits until everything set so far has been written to the hub. False on timeout or no hub.</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        Link.Wake();
        while (Connected)
        {
            bool empty;
            lock (_lock) empty = !_layoutDirty && _pendingValues.Count == 0 && _pendingAppend.Count == 0 && _pendingStreams.Count == 0;
            if (empty && Link.Idle) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return false;
    }

    private void SetLocked(string id, JsonElement e)
    {
        if (_widgets.Count > 0 && !_widgets.ContainsKey(id)) return; // not in the layout: nothing would show it
        bool stream = IsStream(id);
        StateMerge.Apply(_state, new Message { Values = new() { [id] = e } });
        if (!_live) return; // kept in _state; replayed in full on connect
        if (stream)
        {
            if (!_pendingAppend.TryGetValue(id, out var list)) _pendingAppend[id] = list = [];
            list.Add(e);
            int limit = Protocol.StreamLimit(_widgets[id]);
            if (list.Count > limit) list.RemoveRange(0, list.Count - limit);
        }
        else _pendingValues[id] = e;
    }

    private bool IsStream(string id) => _widgets.TryGetValue(id, out var w) && WidgetKinds.IsStream(w.Kind);

    private protected override Message BuildHello()
    {
        lock (_lock)
        {
            // _state.Layout is never changed in place (SetLayout swaps in a new copy), so this is a stable snapshot.
            _helloLayout = _state.Layout;
            return new Message { Role = Roles.App, Layout = _state.Layout, Provides = Capabilities.ToList() };
        }
    }

    private protected override IEnumerable<Message> Replay()
    {
        Message update;
        Message? layout = null;
        lock (_lock)
        {
            // The hello carried the layout; this brings a fresh (or restarted) hub fully up to date.
            _live = true;
            _layoutDirty = !ReferenceEquals(_state.Layout, _helloLayout); // SetLayout between hello and welcome
            _pendingValues = [];
            _pendingAppend = [];
            _pendingStreams = [];
            update = new Message
            {
                Type = MessageTypes.Update,
                Values = new(_state.Values),
                Streams = _state.Streams.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            };
            if (_layoutDirty) { layout = new Message { Type = MessageTypes.Layout, Layout = _state.Layout }; _layoutDirty = false; }
        }
        return layout is null ? [update] : [layout, update];
    }

    private protected override IEnumerable<Message> PullPending()
    {
        var batch = new List<Message>(2);
        lock (_lock)
        {
            if (_layoutDirty)
            {
                batch.Add(new Message { Type = MessageTypes.Layout, Layout = _state.Layout });
                _layoutDirty = false;
            }
            if (_pendingValues.Count > 0 || _pendingAppend.Count > 0 || _pendingStreams.Count > 0)
            {
                batch.Add(new Message
                {
                    Type = MessageTypes.Update,
                    Values = _pendingValues.Count > 0 ? _pendingValues : null,
                    Append = _pendingAppend.Count > 0 ? _pendingAppend : null,
                    Streams = _pendingStreams.Count > 0 ? _pendingStreams : null,
                });
                _pendingValues = [];
                _pendingAppend = [];
                _pendingStreams = [];
            }
        }
        return batch;
    }

    private protected override void OnConnection(bool connected)
    {
        if (!connected) lock (_lock) _live = false;
    }

    private protected override Task<object?> OnActionAsync(string widget, JsonElement? value, CancellationToken ct)
        => _actions.TryGetValue(widget, out var h) ? h(value, ct) : base.OnActionAsync(widget, value, ct);

    private protected override async Task BeforeStopAsync()
    {
        if (Connected) await FlushAsync(_options.FlushOnDispose).ConfigureAwait(false);
    }

    private static LayoutDef BuildLayout(Action<LayoutBuilder> build)
    {
        var b = new LayoutBuilder();
        build(b);
        return b.Build();
    }

    private static string? DefaultVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        if (info is not null && info.IndexOf('+') is int plus and > 0) info = info[..plus];
        return info ?? asm?.GetName().Version?.ToString();
    }
}
