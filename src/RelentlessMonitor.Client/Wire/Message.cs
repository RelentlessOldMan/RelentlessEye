using System.Text.Json;

namespace RelentlessMonitor.Wire;

/// <summary>
/// Every message on the wire, one JSON object per line. One flat shape for all types: fields that
/// don't apply are null and left out. Unknown fields are ignored on read, so the protocol grows by
/// adding fields. See docs/PROTOCOL.md.
/// </summary>
public sealed class Message
{
    public int V { get; set; } = Protocol.Version;

    /// <summary>One of <see cref="MessageTypes"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Message id; a request's id comes back as its response's <see cref="Corr"/>.</summary>
    public string? Id { get; set; }

    /// <summary>On a response: the id of the request it answers.</summary>
    public string? Corr { get; set; }

    public DateTimeOffset? Ts { get; set; }

    // ---- hello ----

    /// <summary><c>app</c> (feeds a panel) or <c>monitor</c> (displays panels).</summary>
    public string? Role { get; set; }
    public string? App { get; set; }
    public string? Instance { get; set; }

    /// <summary>
    /// Random per client object, the same across its reconnects. A hello with the session of a
    /// connection the hub still thinks is live takes that panel over instead of opening <c>#2</c>.
    /// </summary>
    public string? Session { get; set; }
    public string? Machine { get; set; }
    public string? Version { get; set; }
    /// <summary>Shared secret; required from non-localhost connections.</summary>
    public string? Token { get; set; }
    /// <summary>Request names this app answers (besides its own widget actions).</summary>
    public List<Capability>? Provides { get; set; }

    // ---- layout / update ----

    /// <summary>The app's panel definition (hello, layout).</summary>
    public LayoutDef? Layout { get; set; }

    /// <summary>Widget id → value (update). Chart and feed values append one item; everything else replaces.</summary>
    public Dictionary<string, JsonElement>? Values { get; set; }

    /// <summary>Widget id → items to append to a chart or feed (update; batches several at once).</summary>
    public Dictionary<string, List<JsonElement>>? Append { get; set; }

    /// <summary>Widget id → whole retained history, replacing what the hub holds (update; sent on reconnect).</summary>
    public Dictionary<string, List<JsonElement>>? Streams { get; set; }

    /// <summary>Hub → monitor: which app a message is about (<c>machine/app/instance</c>).</summary>
    public string? Key { get; set; }

    // ---- request / response ----

    /// <summary>Request target: an app key, or an app name (first connected instance wins). Null: taken from <see cref="Name"/>'s prefix.</summary>
    public string? To { get; set; }
    /// <summary>Request name, e.g. <c>progress</c>, <c>CodeCarver.progress</c>, <c>hub.apps</c>, or <see cref="Protocol.ActionRequest"/>.</summary>
    public string? Name { get; set; }
    public JsonElement? Args { get; set; }
    public JsonElement? Result { get; set; }
    public string? Error { get; set; }

    // ---- hub → monitor ----

    /// <summary>Full state of one app (type <c>app</c>).</summary>
    public AppState? State { get; set; }

    /// <summary>On <c>gone</c>: true when the hub forgot the app entirely (drop its panel), false when it just went offline.</summary>
    public bool? Removed { get; set; }

    /// <summary>Free text: welcome banner, error detail.</summary>
    public string? Text { get; set; }
}

public static class MessageTypes
{
    /// <summary>Client → hub, first message: identity, role, layout, capabilities.</summary>
    public const string Hello = "hello";
    /// <summary>Hub → client: accepted. Carries the client's key.</summary>
    public const string Welcome = "welcome";
    /// <summary>App → hub: replace the panel layout.</summary>
    public const string Layout = "layout";
    /// <summary>App → hub → monitors: new widget values.</summary>
    public const string Update = "update";
    public const string Request = "request";
    public const string Response = "response";
    /// <summary>Hub → monitor: full state of one app (on monitor connect, app hello, layout change).</summary>
    public const string AppState = "app";
    /// <summary>Hub → monitor: an app disconnected (panel stays, shown offline) or was removed.</summary>
    public const string Gone = "gone";
    /// <summary>Hub → client: something was wrong with the last message (or the hello, then the hub closes).</summary>
    public const string Error = "error";
    /// <summary>Either way, no reply. Each side sends one after <see cref="Protocol.PingInterval"/> of quiet and drops a peer silent for <see cref="Protocol.DeadAfter"/>.</summary>
    public const string Ping = "ping";
}

public static class Roles
{
    public const string App = "app";
    public const string Monitor = "monitor";
}

public sealed class Capability
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Capability() { }
    public Capability(string name, string? description = null) { Name = name; Description = description; }
}

/// <summary>What the hub knows about one app; sent to monitors.</summary>
public sealed class AppState
{
    public string Key { get; set; } = "";
    public string App { get; set; } = "";
    public string? Instance { get; set; }
    public string? Machine { get; set; }
    public string? Version { get; set; }
    public bool Connected { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public LayoutDef? Layout { get; set; }
    public List<Capability>? Provides { get; set; }
    /// <summary>Latest value of every non-stream widget.</summary>
    public Dictionary<string, JsonElement> Values { get; set; } = [];
    /// <summary>Retained history of chart and feed widgets, oldest first.</summary>
    public Dictionary<string, List<JsonElement>> Streams { get; set; } = [];
}
