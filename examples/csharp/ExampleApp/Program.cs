using System.Diagnostics;
using RelentlessEye;

// A pretend worker that shows every widget kind. Run it with or without the hub; start and stop the
// hub while it runs and the panel catches up. Copy the parts you need.
//   dotnet run --project examples/csharp/ExampleApp [-- --hub otherpc:47800 --token XXXX]

string? hub = null, token = null;
for (int i = 0; i + 1 < args.Length; i += 2)
    if (args[i] == "--hub") hub = args[i + 1];
    else if (args[i] == "--token") token = args[i + 1];

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

// 1. Describe the panel. This is all the monitor knows about this app.
await using var mon = new MonitorClient("ExampleApp", l => l
    .Section("Job", s => s
        .Indicator("state", "State", ("idle", "gray"), ("running", "green"), ("paused", "orange"), ("failed", "red"))
        .Text("stage", "Stage")
        .Progress("progress", "Progress", span: 2)
        .KeyValue("counts", "Counts", span: 2))
    .Section("Machine", s => s
        .Gauge("memory", "Memory", max: 1024, unit: "MB")
        .Chart("cpu", "CPU %", limit: 120, min: 0, max: 100, color: "#3b82f6"))
    .Section("Log", s => s.Feed("log", limit: 300), columns: 1)
    .Section("Workers", s => s
        .Table("workers", "Workers (click one to follow it)", [new() { Key = "on", Label = "", Align = "center" },
            new() { Key = "name", Label = "Worker" }, new() { Key = "files", Label = "Files", Align = "right" }, "task"], pick: true), columns: 1)
    .Section("Controls", s => s
        .Button("restart", "Restart job")
        .Button("fail", "Simulate failure", confirm: "Make the job fail?", color: "red")
        .Toggle("paused", "Paused")
        .Choice("speed", "Speed", ["slow", "normal", new RelentlessEye.Wire.OptionDef { Value = "fast", Color = "orange", Confirm = "Run flat out?" }], span: 2)
        .Flags("verbose", "Log extra", "timings", "memory"), columns: 3),
    new MonitorClientOptions { Hub = hub, Token = token, Instance = Environment.ProcessId.ToString() });

// 2. Handle the panel's buttons, toggles, choices and flags.
bool paused = false;
int restarts = 0;
var failNow = false;
mon.OnAction("restart", () => { Interlocked.Increment(ref restarts); mon.Log("log", "restart requested from the monitor", "warn"); });
mon.OnAction("fail", () => { failNow = true; });
mon.OnToggle("paused", on => { paused = on; mon.Log("log", on ? "paused" : "resumed"); });
mon.OnChoice("speed", v => mon.Log("log", $"speed set to {v} by {MonitorClient.Sender}"));
mon.OnFlags("verbose", on => mon.Log("log", on.Count == 0 ? "no extra logging" : "extra logging: " + string.Join(", ", on)));
mon.OnPick("workers", id => mon.Log("log", $"following {id}"));
mon.Set("speed", "normal");
mon.Set("verbose", Array.Empty<string>());
object[] Workers(int step, string stage) => [.. Enumerable.Range(1, 3).Select(w => new
{
    id = $"w{w}", name = $"worker {w}", files = step * 4 * w,
    on = new { dot = true, color = paused ? "orange" : w == 3 && step % 30 < 10 ? "gray" : "green", tip = paused ? "paused" : "busy" },
    task = $"{stage} batch {step / 10 + w}",
})];

// 3. Answer requests from other apps or tools: `reye request ExampleApp.status`.
double pct = 0;
mon.Provide("status", _ => new { state = paused ? "paused" : "running", progress = pct, restarts }, "current job state");

mon.ConnectionChanged += up => Console.WriteLine(up ? $"hub connected as {mon.Key}" : "hub not connected (still running; will retry)");
mon.Start();
Console.WriteLine("ExampleApp running. Ctrl+C to stop.");

// 4. Push values whenever they change. These never block, hub or no hub.
var stages = new[] { "scanning", "parsing", "analyzing", "writing" };
var proc = Process.GetCurrentProcess();
var lastCpu = proc.TotalProcessorTime;
var lastTick = Stopwatch.GetTimestamp();
int job = 0;

var machine = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        proc.Refresh();
        var now = Stopwatch.GetTimestamp();
        var cpu = (proc.TotalProcessorTime - lastCpu).TotalMilliseconds / Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds / Environment.ProcessorCount * 100;
        lastCpu = proc.TotalProcessorTime;
        lastTick = now;
        mon.Set("cpu", Math.Round(cpu, 1));
        mon.Set("memory", Math.Round(proc.WorkingSet64 / 1048576.0));
        try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { }
    }
});

while (!stop.IsCancellationRequested)
{
    job++;
    int seenRestarts = restarts;
    mon.Set(("state", "running"), ("paused", paused));
    mon.Log("log", $"job {job} started");
    bool failed = false;
    for (int step = 0; step <= 100 && !stop.IsCancellationRequested; step++)
    {
        if (paused) mon.Set("state", "paused");
        while (paused && !stop.IsCancellationRequested) await Task.Delay(200);
        if (restarts != seenRestarts) break;
        if (failNow) { failNow = false; failed = true; break; }
        pct = step / 100.0;
        var stage = stages[Math.Min(step / 25, stages.Length - 1)];
        mon.Set(("state", "running"), ("stage", stage), ("progress", new { value = pct, text = $"{step}% ({stage})" }),
            ("counts", new Dictionary<string, object> { ["job"] = job, ["files"] = step * 12, ["restarts"] = restarts }),
            ("workers", Workers(step, stage)));
        if (step % 20 == 0) mon.Log("log", $"job {job}: {stage} ({step}%)");
        try { await Task.Delay(150, stop.Token); } catch (OperationCanceledException) { }
    }
    if (failed)
    {
        mon.Set("state", "failed");
        mon.Log("log", $"job {job} failed (simulated)", "error");
        try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { }
    }
    else if (!stop.IsCancellationRequested && restarts == seenRestarts)
    {
        mon.Log("log", $"job {job} done");
        mon.Set("state", "idle");
        try { await Task.Delay(1500, stop.Token); } catch (OperationCanceledException) { }
    }
}

await machine;
mon.Set("state", "idle");
mon.Log("log", "ExampleApp stopping");
