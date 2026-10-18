using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelentlessMonitor.Wire;

/// <summary>Constants and serialization rules shared by the hub, monitors and every app.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int DefaultPort = 47800;

    /// <summary><c>host[:port]</c> of the hub; default <c>localhost:47800</c>.</summary>
    public const string HubEnvVar = "RELENTLESSMONITOR_HUB";
    /// <summary>Shared token for connecting to a hub on another machine.</summary>
    public const string TokenEnvVar = "RELENTLESSMONITOR_TOKEN";

    /// <summary>Request name the monitor uses when someone clicks a button or flips a toggle. Args: <c>{"widget":id,"value":..}</c>.</summary>
    public const string ActionRequest = "$action";

    /// <summary>A line longer than this is dropped instead of buffered without bound.</summary>
    public const int MaxLineChars = 1024 * 1024;

    /// <summary>
    /// Longest line a client accepts from the hub. Bigger than <see cref="MaxLineChars"/> because one
    /// app snapshot carries all its chart/feed history.
    /// </summary>
    public const int MaxHubLineChars = 64 * 1024 * 1024;

    /// <summary>Upper bound the hub puts on any chart/feed <see cref="WidgetDef.Limit"/>.</summary>
    public const int MaxStreamLimit = 5000;
    public const int DefaultChartLimit = 120;
    public const int DefaultFeedLimit = 200;

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(60);

    /// <summary>How long the hub waits for an app to answer a forwarded request.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static int StreamLimit(WidgetDef w)
        => Math.Clamp(w.Limit ?? (w.Kind == WidgetKinds.Chart ? DefaultChartLimit : DefaultFeedLimit), 1, MaxStreamLimit);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Message m) => JsonSerializer.Serialize(m, Json);

    /// <summary>
    /// <paramref name="m"/> as wire lines, each within <paramref name="maxChars"/> (the hub drops longer
    /// ones). An update too big for one line (a long history replay, a burst of log lines) becomes
    /// several that add up to the same: a split stream replacement continues as appends. A single item
    /// too big to send on its own is left out. A response too big becomes an error response.
    /// </summary>
    public static IEnumerable<string> SerializeLines(Message m, int maxChars = MaxLineChars)
    {
        var line = Serialize(m);
        if (line.Length <= maxChars) return [line];
        if (m.Type == MessageTypes.Response)
            return [Serialize(new Message { Type = MessageTypes.Response, Corr = m.Corr, Error = $"result too large to send (over {maxChars} characters)" })];
        if (m.Type != MessageTypes.Update) return [line]; // nothing sensible to split; the hub reports it
        return SplitUpdate(m, maxChars);
    }

    private static List<string> SplitUpdate(Message m, int maxChars)
    {
        int budget = maxChars - 512 - (m.Key?.Length ?? 0); // room for the envelope
        var lines = new List<string>();
        Message? chunk = null;
        int size = 0;

        void Flush()
        {
            if (chunk is not null) lines.Add(Serialize(chunk));
            chunk = null;
            size = 0;
        }
        Message Room(int need)
        {
            if (chunk is not null && size + need > budget) Flush();
            chunk ??= new Message { Type = m.Type, Key = m.Key, Ts = m.Ts };
            size += need;
            return chunk;
        }
        // Measured as written, so escaping differences between the element's source and the output don't matter.
        static int Cost(string id, JsonElement e) => JsonSerializer.Serialize(id).Length + JsonSerializer.Serialize(e, Json).Length + 8;

        if (m.Streams is not null)
            foreach (var (id, items) in m.Streams)
            {
                bool first = true;
                if (items.Count == 0) { (Room(id.Length + 16).Streams ??= [])[id] = []; continue; }
                foreach (var item in items)
                {
                    int c = Cost(id, item);
                    if (c > budget) continue;
                    var into = Room(c);
                    // The first piece replaces the history; the rest add to it.
                    var map = first || (into.Streams?.ContainsKey(id) ?? false) ? (into.Streams ??= []) : (into.Append ??= []);
                    if (!map.TryGetValue(id, out var list)) map[id] = list = [];
                    list.Add(item);
                    first = false;
                }
            }
        if (m.Values is not null)
            foreach (var (id, value) in m.Values)
            {
                int c = Cost(id, value);
                if (c <= budget) (Room(c).Values ??= [])[id] = value;
            }
        if (m.Append is not null)
            foreach (var (id, items) in m.Append)
                foreach (var item in items)
                {
                    int c = Cost(id, item);
                    if (c > budget) continue;
                    var map = Room(c).Append ??= [];
                    if (!map.TryGetValue(id, out var list)) map[id] = list = [];
                    list.Add(item);
                }
        Flush();
        return lines;
    }

    public static bool TryParse(string line, out Message? message, out string? error)
    {
        message = null;
        error = null;
        try { message = JsonSerializer.Deserialize<Message>(line, Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        { error = $"invalid JSON: {ex.Message}"; return false; }
        if (message is null) { error = "empty message"; return false; }
        if (message.V > Version) { error = $"protocol version {message.V} not supported (up to {Version})"; message = null; return false; }
        if (string.IsNullOrWhiteSpace(message.Type)) { error = "missing 'type'"; message = null; return false; }
        message.Type = message.Type.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Any value as JSON, never throwing: NaN and infinities become the strings <c>"NaN"</c>,
    /// <c>"Infinity"</c>, <c>"-Infinity"</c>; a value that can't be serialized becomes its
    /// <c>ToString()</c>. The result owns its memory (safe after the caller's document is disposed).
    /// </summary>
    public static JsonElement ToElement(object? value)
    {
        try
        {
            return value switch
            {
                null => Null,
                JsonElement { ValueKind: JsonValueKind.Undefined } => Null,
                JsonElement e => e.Clone(),
                _ => JsonSerializer.SerializeToElement(value, ValueJson),
            };
        }
        catch (Exception) when (value is JsonElement) { return Null; } // its document was disposed
        catch (Exception)
        {
            try { return JsonSerializer.SerializeToElement(value!.ToString(), Json); }
            catch (Exception) { return Null; }
        }
    }

    private static readonly JsonElement Null = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>For values apps send: like <see cref="Json"/>, but NaN/Infinity are written as strings instead of failing.</summary>
    private static readonly JsonSerializerOptions ValueJson = new(Json) { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>The identity the hub keys an app by.</summary>
    public static string AppKey(string? machine, string app, string? instance)
        => $"{machine ?? "?"}/{app}{(string.IsNullOrEmpty(instance) ? "" : "/" + instance)}";

    /// <summary>Parses <c>host</c>, <c>host:port</c> or <c>[v6]:port</c>; falls back to <see cref="HubEnvVar"/>, then localhost.</summary>
    public static (string Host, int Port) ResolveHub(string? address)
    {
        address ??= Environment.GetEnvironmentVariable(HubEnvVar);
        if (string.IsNullOrWhiteSpace(address)) return ("localhost", DefaultPort);
        address = address.Trim();
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var p) && p is > 0 and <= 65535)
            return (address[..colon], p);
        if (address.StartsWith('[') && address.LastIndexOf("]:", StringComparison.Ordinal) is int b and > 0 && int.TryParse(address[(b + 2)..], out var p6) && p6 is > 0 and <= 65535)
            return (address[1..b], p6);
        return (address, DefaultPort);
    }
}
