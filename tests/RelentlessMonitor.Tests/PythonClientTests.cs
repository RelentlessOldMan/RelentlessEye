using System.Diagnostics;
using System.Text.Json;

namespace RelentlessMonitor.Tests;

/// <summary>Runs clients/python/relentless_monitor.py against a real hub. Skipped (passes) without a real Python.</summary>
public class PythonClientTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RelentlessMonitor.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    private const string Script = """
        import sys, time
        sys.path.insert(0, sys.argv[1])
        import relentless_monitor as rm
        mon = rm.MonitorClient("PyTest", rm.layout(rm.section("S",
            rm.text("stage"), rm.chart("c", limit=3), rm.button("go", "Go"), rm.toggle("t", "T"),
            rm.choice("mode", "Mode", ["auto", rm.option("hold", "Hold", color="red", confirm="Sure?")]),
            rm.flags("res", "Res", ["cpu", "gpu", "ram"]))),
            hub=sys.argv[2], machine="TESTPC", retry_interval=0.2)
        clicks = []
        mon.on_action("go", lambda v: clicks.append(1) or len(clicks))
        mon.on_toggle("t", lambda on: None)
        senders = []
        mon.on_choice("mode", lambda v: senders.append(mon.sender))
        mon.on_flags("res", lambda on: None)
        mon.provide("who", lambda args: [repr(mon.sender), mon.sender.role, mon.sender.is_monitor, senders[-1].app if senders else None])
        mon.provide("echo", lambda args: {"got": args})
        mon.set("stage", "before")
        for i in range(5): mon.set("c", i)
        mon.start()
        assert mon.wait_connected(5)
        mon.set("stage", "after")
        r = mon.request("hub.info")
        assert r.ok and r.result["hub"] == "RelentlessMonitor", r
        # Values nothing can encode as JSON must not raise or kill the connection.
        loop = []
        loop.append(loop)
        mon.set("stage", float("nan"))
        mon.set("stage", loop)
        mon.set(None, 1)
        mon.set("ghost", 1)
        mon.set_many(None)
        assert mon.get("ghost") is None
        r = mon.request("hub.info", {"x": float("inf"), "o": object(), "loop": loop})
        assert r.ok, r
        mon.set("stage", "after")
        print("ready", flush=True)
        sys.stdin.readline()
        mon.close()
        """;

    private const string OfflineScript = """
        import sys
        sys.path.insert(0, sys.argv[1])
        import relentless_monitor as rm
        assert rm._resolve_hub("h:70000")[1] == rm.DEFAULT_PORT
        assert rm._resolve_hub("h:0")[1] == rm.DEFAULT_PORT
        assert rm._resolve_hub("h:5")[1] == 5
        mon = rm.MonitorClient("Off", rm.layout(rm.section("S", rm.text("a"), rm.text("b"), rm.chart("c"))))
        mon.set("a", 1); mon.set("b", 2); mon.set("c", 3)
        mon.set_layout(rm.layout(rm.section("S", rm.text("a"), rm.chart("b"), rm.chart("c"))))
        assert mon.get("a") == 1 and mon.get("b") is None and mon.get("c") == 3, (mon.get("a"), mon.get("b"), mon.get("c"))
        mon.set_layout({"sections": []})  # no widgets: keep everything
        assert mon.get("a") == 1
        mon.set_layout("not a layout")
        assert mon.get("a") == 1
        import json
        items = ["x" * 100 + str(i) for i in range(400)]
        lines = rm._lines({"type": "update", "streams": {"log": items}}, 8192)
        assert len(lines) > 1 and all(len(l) <= 8192 for l in lines)
        got = []
        for l in lines:
            m = json.loads(l)
            got = list(m.get("streams", {}).get("log", got)) + m.get("append", {}).get("log", [])
        assert got == items
        assert "too large" in json.loads(rm._lines({"type": "response", "corr": "q", "result": "x" * 9999}, 1000)[0])["error"]
        print("ok")
        """;

    [Fact]
    public async Task Python_client_handles_bad_ports_and_relayouts_like_the_hub()
    {
        var python = FindPython();
        if (python is null) return;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"rm_pyoff_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, OfflineScript);
        try
        {
            var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(Path.Combine(RepoRoot, "clients", "python"));
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEndAsync();
            var errors = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000)) { proc.Kill(); Assert.Fail("python hung"); }
            Assert.True((await output).Trim() == "ok", await errors);
        }
        finally { File.Delete(scriptPath); }
    }

    [Fact]
    public async Task Python_client_feeds_the_panel_and_answers_requests_and_actions()
    {
        var python = FindPython();
        if (python is null) return;

        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();

        var scriptPath = Path.Combine(Path.GetTempPath(), $"rm_pytest_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, Script);
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(Path.Combine(RepoRoot, "clients", "python"));
        psi.ArgumentList.Add(hub.Address);
        using var proc = Process.Start(psi)!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (ready != "ready")
            {
                proc.Kill(); // it may be waiting on stdin; stderr only ends once it exits
                Assert.Fail($"python said '{ready}': {await proc.StandardError.ReadToEndAsync()}");
            }

            await Wait.Until(() => viewer.Apps.Any(a => a.App == "PyTest" && a.Values.TryGetValue("stage", out var v) && v.GetString() == "after"), "python values");
            var s = viewer.Apps.Single(a => a.App == "PyTest");
            Assert.Equal("TESTPC/PyTest", s.Key);
            Assert.Equal([2, 3, 4], s.Streams["c"].Select(e => e.GetInt32()));

            var r = await viewer.RequestAsync("PyTest.echo", new { x = 1 });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(1, r.Result!.Value.GetProperty("got").GetProperty("x").GetInt32());

            r = await viewer.ActionAsync(s.Key, "go");
            Assert.Equal(1, r.Result!.Value.GetInt32());

            r = await viewer.ActionAsync(s.Key, "t", true);
            Assert.True(r.Ok, r.Error);
            await Wait.Until(() => viewer.Apps.Single(a => a.App == "PyTest").Values.TryGetValue("t", out var v) && v.ValueKind == JsonValueKind.True, "toggle echoed");

            r = await viewer.ActionAsync(s.Key, "mode", "hold");
            Assert.True(r.Ok, r.Error);
            r = await viewer.ActionAsync(s.Key, "mode", "warp");
            Assert.Contains("not an option", r.Error);
            r = await viewer.ActionAsync(s.Key, "res", new[] { "ram", "cpu" });
            Assert.Equal(["cpu", "ram"], r.Result!.Value.EnumerateArray().Select(e => e.GetString()));
            await Wait.Until(() => viewer.Apps.Single(a => a.App == "PyTest").Values.TryGetValue("mode", out var v) && v.GetString() == "hold", "choice echoed");
            r = await viewer.RequestAsync("PyTest.who");
            var who = r.Result!.Value.EnumerateArray().ToList();
            Assert.StartsWith("monitor (monitor:", who[0].GetString());
            Assert.Equal("monitor", who[1].GetString());
            Assert.True(who[2].GetBoolean());
            Assert.Equal("monitor", who[3].GetString()); // the viewer's default name

            r = await viewer.ActionAsync(s.Key, "missing");
            Assert.Contains("missing", r.Error);

            await proc.StandardInput.WriteLineAsync();
            await Wait.Until(() => viewer.Apps.Single(a => a.App == "PyTest").Connected == false, "python offline");
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
            File.Delete(scriptPath);
        }
    }

    /// <summary>
    /// A real python.exe on PATH, or the py launcher. Never a bare "python": on Windows that can be the
    /// Microsoft Store alias, which opens the Store instead of running anything.
    /// </summary>
    private static string? FindPython()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(d => d.Length > 0 && !d.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => new[] { Path.Combine(d, "python.exe"), Path.Combine(d, "python3"), Path.Combine(d, "python") })
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe"));
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (p is null) continue;
                if (!p.WaitForExit(5000)) { p.Kill(); continue; }
                if (p.ExitCode == 0) return path;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }
}
