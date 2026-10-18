using System.Collections.Concurrent;
using System.Text.Json;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor;

/// <summary>The answer to a request. Never thrown: a missing hub, timeout or handler failure is <see cref="Error"/>.</summary>
public sealed record HubResponse(bool Ok, JsonElement? Result, string? Error)
{
    public static HubResponse Fail(string error) => new(false, null, error);

    /// <summary>The result deserialized as <typeparamref name="T"/>, or default when failed or empty.</summary>
    public T? As<T>()
    {
        if (!Ok || Result is not { } r || r.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        try { return r.Deserialize<T>(Protocol.Json); }
        catch (JsonException) { return default; }
    }

    public override string ToString() => Ok ? Result?.GetRawText() ?? "null" : $"error: {Error}";
}

/// <summary>
/// What apps and monitors share: one reconnecting connection to the hub, requests in both
/// directions, and named handlers this side answers. Nothing here blocks or throws when the hub is
/// missing; it keeps looking in the background.
/// </summary>
public abstract class HubConnection : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HubResponse>> _pending = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _provides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _provideDescriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _session = Protocol.NewId();
    private static readonly AsyncLocal<RequestSender?> _sender = new();

    /// <summary>
    /// Inside an action or <see cref="Provide(string, Func{JsonElement?, object?}, string?)"/> handler: who sent the
    /// request being answered, as stamped by the hub. Null outside a handler.
    /// </summary>
    public static RequestSender? Sender => _sender.Value;
    private int _started, _disposed;

    private protected HubConnection(HubOptions options, string app, string? instance, string? machine, string? version)
    {
        Options = options;
        App = app;
        Instance = instance;
        Machine = machine ?? Environment.MachineName;
        Version = version;
        Link = new HubLink(options, BuildHelloCore, AfterWelcome, Pull, Dispatch, OnConnectionCore);
    }

    private protected HubLink Link { get; }
    public HubOptions Options { get; }
    public string App { get; }
    public string? Instance { get; }
    public string Machine { get; }
    public string? Version { get; }

    /// <summary>The key the hub filed this connection under (from its welcome), e.g. <c>PC/CodeCarver</c>.</summary>
    public string? Key { get; private set; }

    public bool Connected => Link.Connected;

    /// <summary>Why the hub last refused this connection (wrong token...), or null.</summary>
    public string? LastRejection => Link.LastRejection;

    /// <summary>Raised on a background thread when the hub connection comes up or drops.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Starts looking for the hub in the background. Returns immediately.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Link.Start();
    }

    /// <summary>Waits until connected (true) or <paramref name="timeout"/> passes (false). Optional: nothing needs it.</summary>
    public Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct = default) => Link.WaitConnectedAsync(timeout, ct);

    /// <summary>
    /// Answers requests named <paramref name="name"/> (callers may write <c>App.name</c>). The handler
    /// gets the request's args; its return value (any JSON-serializable object) is the result, and a
    /// thrown exception becomes the error. Declare handlers before <see cref="Start"/> so the hub
    /// lists them; later ones work but are listed from the next reconnect.
    /// </summary>
    public void Provide(string name, Func<JsonElement?, CancellationToken, Task<object?>> handler, string? description = null)
    {
        _provides[name] = handler;
        _provideDescriptions[name] = description;
    }

    public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null)
        => Provide(name, (args, _) => Task.FromResult(handler(args)), description);

    /// <summary>
    /// Sends a request through the hub: <c>hub.apps</c>, <c>OtherApp.something</c>, or a name with
    /// <paramref name="to"/> set to an app name or key. Fails fast when not connected.
    /// </summary>
    public async Task<HubResponse> RequestAsync(string name, object? args = null, string? to = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!Link.Connected) return HubResponse.Fail("not connected to hub");
        var id = Protocol.NewId();
        var tcs = new TaskCompletionSource<HubResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var m = new Message { Type = MessageTypes.Request, Id = id, Name = name, To = to };
            if (args is not null) m.Args = Protocol.ToElement(args); // never throws
            if (!Link.Send(m)) return HubResponse.Fail("not connected to hub");
            // A little longer than the hub's own forward timeout, so its error normally arrives first.
            var wait = timeout ?? Protocol.RequestTimeout + TimeSpan.FromSeconds(5);
            try { return await tcs.Task.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return HubResponse.Fail($"no response within {wait.TotalSeconds:0.#}s"); }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private protected IReadOnlyList<Capability> Capabilities
        => _provideDescriptions.Select(p => new Capability(p.Key, p.Value)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private protected abstract Message BuildHello();
    private protected virtual IEnumerable<Message> Replay() => [];
    private protected virtual IEnumerable<Message> PullPending() => [];
    private protected virtual void OnMessage(Message m) { }
    private protected virtual void OnConnection(bool connected) { }

    /// <summary>A <c>$action</c> request (a button or toggle in a monitor). Default: not supported.</summary>
    private protected virtual Task<object?> OnActionAsync(string widget, JsonElement? value, CancellationToken ct)
        => throw new InvalidOperationException($"no action handler for '{widget}'");

    private Message BuildHelloCore()
    {
        var hello = BuildHello();
        hello.Type = MessageTypes.Hello;
        hello.App = App;
        hello.Instance = Instance;
        hello.Machine = Machine;
        hello.Version = Version;
        hello.Token = Options.Token;
        hello.Session = _session;
        return hello;
    }

    private IEnumerable<Message> AfterWelcome() => Replay();
    private IEnumerable<Message> Pull() => PullPending();

    private void OnConnectionCore(bool connected, string? rejection)
    {
        if (!connected)
            foreach (var id in _pending.Keys.ToList())
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(HubResponse.Fail("hub connection lost"));
        OnConnection(connected);
        ConnectionChanged?.Invoke(connected);
    }

    private void Dispatch(Message m)
    {
        switch (m.Type)
        {
            case MessageTypes.Welcome:
                Key = m.Key;
                OnMessage(m);
                break;
            case MessageTypes.Response:
                if (m.Corr is not null && _pending.TryRemove(m.Corr, out var tcs))
                    tcs.TrySetResult(m.Error is null ? new HubResponse(true, m.Result, null) : HubResponse.Fail(m.Error));
                break;
            case MessageTypes.Request:
                // Off the read thread: a slow handler must not hold up everything else from the hub.
                _ = Task.Run(() => AnswerAsync(m));
                break;
            default:
                OnMessage(m);
                break;
        }
    }

    private async Task AnswerAsync(Message req)
    {
        var resp = new Message { Type = MessageTypes.Response, Corr = req.Id };
        _sender.Value = new RequestSender(req.Key ?? "", req.Role, req.App, req.Machine); // flows into the handler, not out of this task
        try
        {
            var ct = _stopping.Token;
            object? result;
            if (req.Name == Protocol.ActionRequest)
            {
                string? widget = null;
                JsonElement? value = null;
                if (req.Args is { ValueKind: JsonValueKind.Object } a)
                {
                    if (a.TryGetProperty("widget", out var w) && w.ValueKind == JsonValueKind.String) widget = w.GetString();
                    if (a.TryGetProperty("value", out var v)) value = v.Clone();
                }
                if (string.IsNullOrEmpty(widget)) throw new ArgumentException("action without a widget id");
                result = await OnActionAsync(widget, value, ct).ConfigureAwait(false);
            }
            else
            {
                var name = req.Name ?? "";
                if (!_provides.TryGetValue(name, out var handler)
                    && !(name.StartsWith(App + ".", StringComparison.OrdinalIgnoreCase) && _provides.TryGetValue(name[(App.Length + 1)..], out handler)))
                    throw new InvalidOperationException($"{App} does not provide '{name}'");
                result = await handler(req.Args?.Clone(), ct).ConfigureAwait(false);
            }
            if (result is not null) resp.Result = Protocol.ToElement(result);
        }
        catch (Exception ex)
        {
            resp.Error = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
        }
        Link.Send(resp);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await BeforeStopAsync().ConfigureAwait(false);
        _stopping.Cancel();
        await Link.StopAsync().ConfigureAwait(false);
        foreach (var id in _pending.Keys.ToList())
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(HubResponse.Fail("closed"));
        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private protected virtual Task BeforeStopAsync() => Task.CompletedTask;
}

/// <summary>Who sent a request, as the hub saw it. The hub stamps this; the sender can't choose its role.</summary>
/// <param name="Key">The sender's key: an app key like <c>PC/CodeCarver</c>, or <c>monitor:3</c>.</param>
/// <param name="Role"><c>monitor</c> (someone at a panel, or <c>rmon</c>) or <c>app</c>. Null from a hub older than 1.1.</param>
/// <param name="App">The sender's app name, e.g. <c>RelentlessMonitor</c>, <c>rmon</c> or <c>CodeCarver</c>.</param>
/// <param name="Machine">The machine the sender says it runs on.</param>
public sealed record RequestSender(string Key, string? Role, string? App, string? Machine)
{
    public bool IsMonitor => Role == Roles.Monitor;
    public override string ToString() => App is null ? Key : $"{App} ({Key})";
}
