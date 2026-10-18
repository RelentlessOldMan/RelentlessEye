using System.Text;
using System.Text.Json;
using RelentlessEye;
using RelentlessEye.Wire;

// reye: poke at a RelentlessEye hub from the command line.
Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    reye [--hub HOST[:PORT]] [--token T] <command>

      info                         hub version, uptime, counts
      apps                         every app the hub knows (connected or offline)
      app KEY|NAME                 one app's layout and current values (JSON)
      watch [NAME]                 print panel changes live (Ctrl+C to stop)
      request NAME [JSON] [--to X] send a request, e.g. hub.apps or CodeCarver.progress
      action KEY|NAME WIDGET [JSON] press a button / set a toggle, as the monitor would
      forget KEY                   drop an offline app's panel
      stop                         shut the hub down (this machine only)

    The hub address defaults to RELENTLESSEYE_HUB, else localhost:47800.
    """;

var opts = new HubOptions();
var rest = new List<string>();
string? to = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--hub" when i + 1 < args.Length: opts.Hub = args[++i]; break;
        case "--token" when i + 1 < args.Length: opts.Token = args[++i]; break;
        case "--to" when i + 1 < args.Length: to = args[++i]; break;
        case "-h" or "--help" or "help": Console.WriteLine(Usage); return 0;
        default: rest.Add(args[i]); break;
    }
}
if (rest.Count == 0) { Console.WriteLine(Usage); return 2; }

var pretty = new JsonSerializerOptions(Protocol.Json) { WriteIndented = true };
await using var viewer = new MonitorViewer(opts, "reye");
viewer.Start();
if (!await viewer.WaitConnectedAsync(TimeSpan.FromSeconds(3)))
{
    var (host, port) = Protocol.ResolveHub(opts.Hub);
    Console.Error.WriteLine(viewer.LastRejection is { } why ? $"hub at {host}:{port} refused: {why}" : $"no hub at {host}:{port}");
    return 1;
}

string cmd = rest[0].ToLowerInvariant();
string? Arg(int i) => rest.Count > i ? rest[i] : null;
object? JsonArg(int i) => Arg(i) is { } s ? (TryJson(s, out var e) ? e : s) : null;

switch (cmd)
{
    case "info":
        return Print(await viewer.RequestAsync("hub.info"));

    case "apps":
    {
        var r = await viewer.RequestAsync("hub.apps");
        if (!r.Ok) return Print(r);
        var apps = r.Result!.Value.EnumerateArray().ToList();
        if (apps.Count == 0) { Console.WriteLine("no apps"); return 0; }
        foreach (var a in apps)
        {
            bool on = a.GetProperty("connected").GetBoolean();
            var provides = a.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array
                ? string.Join(", ", p.EnumerateArray().Select(c => c.GetProperty("name").GetString())) : "";
            Console.WriteLine($"{(on ? "●" : "○")} {a.GetProperty("key").GetString(),-40} {Str(a, "version"),-10} {(on ? Str(a, "remote") : "offline since " + Local(a, "lastSeen"))}"
                              + (provides.Length > 0 ? $"  provides: {provides}" : ""));
        }
        return 0;
    }

    case "app" when Arg(1) is { } key:
        return Print(await viewer.RequestAsync("hub.app", new { key }));

    case "request" when Arg(1) is { } name:
        return Print(await viewer.RequestAsync(name, JsonArg(2), to));

    case "action" when Arg(1) is { } key && Arg(2) is { } widget:
        return Print(await viewer.ActionAsync(key, widget, JsonArg(3)));

    case "forget" when Arg(1) is { } key:
        return Print(await viewer.ForgetAsync(key));

    case "stop":
        return Print(await viewer.RequestAsync("hub.shutdown"));

    case "watch":
    {
        var filter = Arg(1);
        bool Match(string key) => filter is null || key.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        void Line(string key, string text) { if (Match(key)) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {key}  {text}"); }

        foreach (var a in viewer.Apps) Line(a.Key, Summary(a));
        viewer.AppChanged += a => Line(a.Key, Summary(a));
        viewer.AppGone += (key, removed) => Line(key, removed ? "removed" : "offline");
        viewer.ConnectionChanged += up => Console.WriteLine(up ? "-- hub connected" : "-- hub connection lost; retrying");
        viewer.Updated += (a, m) =>
        {
            foreach (var (id, v) in m.Values ?? []) Line(a.Key, $"{id} = {Short(v)}");
            foreach (var (id, items) in m.Append ?? []) foreach (var v in items) Line(a.Key, $"{id} += {Short(v)}");
            foreach (var (id, items) in m.Streams ?? []) Line(a.Key, $"{id} := [{items.Count} items]");
        };
        await done.Task;
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown or incomplete command '{string.Join(' ', rest)}'");
        Console.WriteLine(Usage);
        return 2;
}

int Print(HubResponse r)
{
    if (!r.Ok) { Console.Error.WriteLine($"error: {r.Error}"); return 1; }
    Console.WriteLine(r.Result is { } e ? JsonSerializer.Serialize(e, pretty) : "ok");
    return 0;
}

static bool TryJson(string s, out JsonElement e)
{
    try { e = JsonDocument.Parse(s).RootElement.Clone(); return true; }
    catch (JsonException) { e = default; return false; }
}

static string Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

static string Local(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.TryGetDateTimeOffset(out var t) ? t.LocalDateTime.ToString("g") : "?";

static string Short(JsonElement v)
{
    var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
    return s.Length > 160 ? s[..157] + "..." : s;
}

static string Summary(AppState a)
    => $"{(a.Connected ? "online" : "offline")}, {a.Layout?.AllWidgets.Count() ?? 0} widgets, {a.Values.Count} values{(a.Version is null ? "" : ", v" + a.Version)}";
