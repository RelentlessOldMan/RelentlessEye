# RelentlessMonitor 📟

**A channel and live monitor for your dev apps: they describe their own panel and push values over TCP JSON lines; C# and Python clients included.**

- Apps connect to a small background **hub** over TCP and describe their own panel: status text,
  indicators, progress, gauges, charts, log feeds, buttons, toggles.
- They push values whenever they like, and can answer and send requests.
- The **monitor** (WPF) draws whatever each app described. It knows nothing about any app.
- Apps never block and stay silent when no hub is running. Start the hub later and they show up with
  their current state.

```
 your apps ──TCP 47800──►  RelentlessMonitor.Hub  ◄──  RelentlessMonitor (WPF)
 (C#, Python, anything     (background, keeps state,     rmon (command line)
  that writes JSON lines)   routes requests)
```

## Running it

```powershell
.\publish.ps1                    # hub, monitor and rmon into .\dist (client files in dist\client)
dist\RelentlessMonitor.Hub.exe   # background hub; --console to watch its log
dist\RelentlessMonitor.exe       # the monitor
dist\rmon.exe apps               # what's connected
```

**Hub settings** are in `%LOCALAPPDATA%\RelentlessMonitor\hub.json`:

- `port` (default 47800).
- `bind`: `localhost` by default, or `all` for other machines.
- `token`: required from other machines.
- `forgetOfflineAfterHours` (default 24).
- Flags `--port`, `--bind`, `--config` override the file.

**Remote apps and monitors** use `--hub host[:port]` and `--token …`, or the environment variables
`RELENTLESSMONITOR_HUB` and `RELENTLESSMONITOR_TOKEN`. In the monitor, use *Hub settings*.

## Feeding it from C#

```powershell
.\pack.ps1      # builds packages\RelentlessMonitor.Client.<ver>.nupkg (this repo's local feed)
dotnet add package RelentlessMonitor.Client -s C:\Playground\RelentlessMonitor\packages
```

```csharp
using RelentlessMonitor;

await using var mon = MonitorClient.Start("CodeCarver", l => l
    .Section("Job", s => s
        .Indicator("state", "State", ("idle", "gray"), ("running", "green"), ("failed", "red"))
        .Progress("progress", "Progress", span: 2)
        .Feed("log", limit: 300))
    .Section("Controls", s => s.Button("cancel", "Cancel", confirm: "Cancel the run?")));

mon.OnAction("cancel", () => cts.Cancel());
mon.Provide("status", _ => new { stage, done });          // rmon request CodeCarver.status

mon.Set("state", "running");
mon.Set("progress", new { value = 0.4, text = "40% (parsing)" });
mon.Log("log", "parsing 120 files");
var lease = await mon.RequestAsync("ComputeWarden.status"); // ask another app
```

`examples/csharp/ExampleApp` uses every widget kind:
`dotnet run --project examples/csharp/ExampleApp`.

## Feeding it from Python

Copy `clients/python/relentless_monitor.py` (stdlib only) next to your script.

```python
import relentless_monitor as rm

mon = rm.MonitorClient("CodeSpawner", rm.layout(
    rm.section("Job", rm.indicator("state", "State", states={"running": "green"}),
               rm.progress("progress", "Progress"), rm.feed("log")),
    rm.section("Controls", rm.button("cancel", "Cancel"))))
mon.on_action("cancel", lambda _: cancel())
mon.provide("status", lambda args: {"stage": stage})
mon.start()
mon.set("state", "running")
mon.log("log", "spawned 3 workers")
...
mon.close()   # sends what's queued (briefly) and disconnects
```

It needs Python 3.6 or later. `examples/python/example_app.py` uses every widget kind.

`set`, `log` and the C# `Set`/`Log` never throw and never block. Values that JSON can't hold (NaN,
cycles, odd objects) are made safe instead. Ids that aren't in the layout are ignored.

## Optional integration (no hard dependency)

An app can feed the monitor when it's installed and run exactly the same when it isn't. That means no
package reference and no import that can fail. `examples/optional` shows how:

- **C#:** copy `examples/optional/csharp/OptionalApp/MonitorBridge.cs` into your app. It loads
  `RelentlessMonitor.Client.dll` by reflection from:
  - next to your exe;
  - `%RELENTLESSMONITOR_CLIENT%` (the DLL or its folder);
  - `%LOCALAPPDATA%\RelentlessMonitor\client`.

  If the DLL isn't found, every call does nothing.
- **Python:** copy `examples/optional/python/monitor_optional.py`. It imports `relentless_monitor`
  from the import path, from `%RELENTLESSMONITOR_CLIENT%`, or from
  `%LOCALAPPDATA%\RelentlessMonitor\client`. If it isn't found, you get a stub that does nothing.

`.\publish.ps1 -InstallClient` copies both client files to `%LOCALAPPDATA%\RelentlessMonitor\client`,
so every optional app on the machine finds them. Each bridge's `status` says what it found.

## Other languages

Anything that can open a socket and write JSON lines can feed the hub. See
[docs/PROTOCOL.md](docs/PROTOCOL.md).

## rmon

```
rmon info | apps | app KEY | watch [APP] | request NAME [JSON] | action KEY WIDGET [JSON] | forget KEY | stop
     [--hub host:port] [--token X] [--to KEY]
```

## Layout of this repo

| path | what |
|---|---|
| `src/RelentlessMonitor.Client` | C# client, NuGet package `RelentlessMonitor.Client` (net8) |
| `src/RelentlessMonitor.Hub` | the hub |
| `src/RelentlessMonitor.Ui` | the WPF monitor (`RelentlessMonitor.exe`) |
| `src/RelentlessMonitor.Cli` | `rmon` |
| `clients/python` | single-file Python client |
| `examples` | ready-to-run C# and Python apps; `examples/optional` feeds the monitor only if it's installed |
| `tests` | end-to-end, router, protocol, Python client, and off-screen UI panel tests |
| `packages` | local NuGet feed (`pack.ps1`) |

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 📟

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
