// An app that feeds RelentlessMonitor only if it's installed: no package reference, just
// MonitorBridge.cs copied in. Runs the same with or without the client DLL or a hub.
//   dotnet run --project examples/optional/csharp/OptionalApp
// It finds RelentlessMonitor.Client.dll next to the exe, at %RELENTLESSMONITOR_CLIENT%, or in
// %LOCALAPPDATA%\RelentlessMonitor\client (publish.ps1 -InstallClient puts it there). Without it, it just runs.

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

using var monitor = MonitorBridge.Create("OptionalApp", new
{
    title = "Optional app (C#)",
    sections = new object[]
    {
        new
        {
            title = "Work",
            widgets = new object[]
            {
                new { id = "count", kind = "text", label = "Items done" },
                new { id = "rate", kind = "chart", label = "Items/s", min = 0, limit = 60 },
                new { id = "log", kind = "feed", span = 2 },
            },
        },
        new
        {
            title = "Controls", columns = 2,
            widgets = new object[]
            {
                new { id = "reset", kind = "button", label = "Reset count" },
                new { id = "fast", kind = "toggle", label = "Fast" },
            },
        },
    },
}, instance: Environment.ProcessId.ToString());

Console.WriteLine($"OptionalApp running ({monitor.Status}). Ctrl+C to stop.");

// The app's own logic never depends on the monitor.
int count = 0;
bool fast = false;
monitor.OnAction("reset", () => { Interlocked.Exchange(ref count, 0); monitor.Log("log", "count reset from the monitor", "warn"); });
monitor.OnToggle("fast", on => fast = on);
monitor.Provide("count", _ => count, "items done so far");
monitor.Start();

while (!stop.IsCancellationRequested)
{
    int batch = fast ? 20 : 5;
    Interlocked.Add(ref count, batch);
    monitor.Set("count", count);
    monitor.Set("rate", batch);
    if (count % 50 == 0) monitor.Log("log", $"reached {count}");
    try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { }
}
Console.WriteLine($"stopped at {count}");
