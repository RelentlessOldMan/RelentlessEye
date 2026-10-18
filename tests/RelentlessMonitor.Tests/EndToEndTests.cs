using System.Diagnostics;
using System.Text.Json;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Tests;

public class EndToEndTests
{
    private static Action<LayoutBuilder> SampleLayout => l => l
        .Title("Sample")
        .Section("Run", s => s
            .Text("stage", "Stage")
            .Indicator("state", "State", ("ok", "green"), ("bad", "red"))
            .Progress("progress", "Progress")
            .Chart("cpu", "CPU", limit: 5)
            .Feed("log", "Log", limit: 3)
            .Button("go", "Go")
            .Toggle("paused", "Paused"));

    private static AppState? Find(MonitorViewer v, string app) => v.Apps.FirstOrDefault(a => a.App == app);

    [Fact]
    public async Task Values_set_before_the_hub_connects_reach_the_monitor()
    {
        await using var hub = new TestHub();
        await using var app = new MonitorClient("Sample", SampleLayout, hub.AppOptions());
        app.Set("stage", "parsing");
        app.Set("cpu", 1); app.Set("cpu", 2);
        app.Log("log", "hello");
        app.Start();
        Assert.True(await app.WaitConnectedAsync(TimeSpan.FromSeconds(5)));

        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        await Wait.Until(() => Find(viewer, "Sample")?.Values.ContainsKey("stage") == true, "replayed values");

        var s = Find(viewer, "Sample")!;
        Assert.Equal("TESTPC/Sample", s.Key);
        Assert.True(s.Connected);
        Assert.Equal("Sample", s.Layout!.Title);
        Assert.Equal("parsing", s.Values["stage"].GetString());
        Assert.Equal([1, 2], s.Streams["cpu"].Select(e => e.GetInt32()));
        Assert.Equal("hello", s.Streams["log"][0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Live_updates_replace_values_and_cap_streams()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        await using var app = MonitorClient.Start("Sample", SampleLayout, hub.AppOptions());
        Assert.True(await app.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        await Wait.Until(() => Find(viewer, "Sample") is not null, "app panel");

        app.Set("stage", "one");
        app.Set("stage", "two");
        for (int i = 1; i <= 8; i++) app.Set("cpu", i);
        app.Set(("progress", 0.5), ("state", "ok"));
        await Wait.Until(() => Find(viewer, "Sample")?.Values.ContainsKey("state") == true
                               && Find(viewer, "Sample")!.Streams.GetValueOrDefault("cpu")?.LastOrDefault().ValueKind == JsonValueKind.Number
                               && Find(viewer, "Sample")!.Streams["cpu"][^1].GetInt32() == 8, "updates");

        var s = Find(viewer, "Sample")!;
        Assert.Equal("two", s.Values["stage"].GetString());
        Assert.Equal(0.5, s.Values["progress"].GetDouble());
        Assert.Equal([4, 5, 6, 7, 8], s.Streams["cpu"].Select(e => e.GetInt32())); // limit 5
    }

    [Fact]
    public async Task Monitor_actions_run_the_app_handler_and_return_its_result()
    {
        await using var hub = new TestHub();
        await using var app = new MonitorClient("Sample", SampleLayout, hub.AppOptions());
        int clicks = 0;
        app.OnAction("go", () => Interlocked.Increment(ref clicks));
        app.OnToggle("paused", _ => { });
        app.Start();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        await Wait.Until(() => Find(viewer, "Sample")?.Connected == true, "app panel");
        var key = Find(viewer, "Sample")!.Key;

        var r = await viewer.ActionAsync(key, "go");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, clicks);

        r = await viewer.ActionAsync(key, "paused", true);
        Assert.True(r.Ok, r.Error);
        await Wait.Until(() => Find(viewer, "Sample")!.Values.TryGetValue("paused", out var v) && v.ValueKind == JsonValueKind.True, "toggle echoed");

        r = await viewer.ActionAsync(key, "nope");
        Assert.False(r.Ok);
        Assert.Contains("nope", r.Error);
    }

    [Fact]
    public async Task Apps_answer_named_requests_from_other_apps()
    {
        await using var hub = new TestHub();
        await using var carver = new MonitorClient("CodeCarver", SampleLayout, hub.AppOptions());
        carver.Provide("progress", args => new { done = 3, of = args?.GetProperty("total").GetInt32() }, "how far along");
        carver.Provide("boom", _ => throw new InvalidOperationException("kaboom"));
        carver.Start();
        await using var asker = MonitorClient.Start("Asker", l => { }, hub.AppOptions());
        Assert.True(await carver.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await asker.WaitConnectedAsync(TimeSpan.FromSeconds(5)));

        var r = await asker.RequestAsync("CodeCarver.progress", new { total = 10 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(10, r.Result!.Value.GetProperty("of").GetInt32());

        r = await asker.RequestAsync("progress", new { total = 4 }, to: "codecarver"); // app name, any case
        Assert.Equal(4, r.Result!.Value.GetProperty("of").GetInt32());

        r = await asker.RequestAsync("CodeCarver.boom");
        Assert.Equal("kaboom", r.Error);

        r = await asker.RequestAsync("Nobody.x");
        Assert.Contains("no app", r.Error);

        r = await asker.RequestAsync("noTarget");
        Assert.Contains("no target", r.Error);

        var apps = await asker.RequestAsync("hub.apps");
        var carverInfo = apps.Result!.Value.EnumerateArray().Single(a => a.GetProperty("app").GetString() == "CodeCarver");
        Assert.Equal(["boom", "progress"], carverInfo.GetProperty("provides").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Hub_info_reports_itself()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        Assert.True(await viewer.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        var r = await viewer.RequestAsync("hub.info");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("RelentlessMonitor", r.Result!.Value.GetProperty("hub").GetString());
        Assert.Equal(1, r.Result!.Value.GetProperty("monitors").GetInt32());
    }

    [Fact]
    public async Task A_disconnected_app_goes_offline_and_comes_back_without_duplicated_history()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();

        var app = MonitorClient.Start("Sample", SampleLayout, hub.AppOptions());
        app.Set("cpu", 1); app.Set("cpu", 2);
        await Wait.Until(() => Find(viewer, "Sample")?.Streams.GetValueOrDefault("cpu")?.Count == 2, "first history");
        await app.DisposeAsync();
        await Wait.Until(() => Find(viewer, "Sample")?.Connected == false, "offline");

        await using var again = new MonitorClient("Sample", SampleLayout, hub.AppOptions());
        again.Set("cpu", 7); // a restarted app brings its own history, replacing the old
        again.Start();
        await Wait.Until(() => Find(viewer, "Sample")?.Connected == true, "back online");
        await Wait.Until(() => Find(viewer, "Sample")!.Streams["cpu"].Count == 1, "history replaced");
        Assert.Equal(7, Find(viewer, "Sample")!.Streams["cpu"][0].GetInt32());
        Assert.Single(viewer.Apps);
    }

    [Fact]
    public async Task Two_live_copies_with_the_same_identity_get_separate_panels()
    {
        await using var hub = new TestHub();
        await using var a = MonitorClient.Start("Twin", l => { }, hub.AppOptions());
        await using var b = MonitorClient.Start("Twin", l => { }, hub.AppOptions());
        Assert.True(await a.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await b.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        await Wait.Until(() => a.Key is not null && b.Key is not null, "keys");
        Assert.NotEqual(a.Key, b.Key);
        Assert.Contains(new[] { a.Key, b.Key }, k => k == "TESTPC/Twin#2");
    }

    [Fact]
    public async Task Forgetting_an_offline_app_removes_its_panel()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        var app = MonitorClient.Start("Temp", l => { }, hub.AppOptions());
        await Wait.Until(() => Find(viewer, "Temp")?.Connected == true, "panel");
        var key = Find(viewer, "Temp")!.Key;

        var r = await viewer.ForgetAsync(key);
        Assert.False(r.Ok); // still connected

        await app.DisposeAsync();
        await Wait.Until(() => Find(viewer, "Temp")?.Connected == false, "offline");
        r = await viewer.ForgetAsync(key);
        Assert.True(r.Ok, r.Error);
        await Wait.Until(() => Find(viewer, "Temp") is null, "removed");
    }

    [Fact]
    public async Task Layout_changes_reach_the_monitor()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        await using var app = MonitorClient.Start("Sample", SampleLayout, hub.AppOptions());
        await Wait.Until(() => Find(viewer, "Sample") is not null, "panel");

        app.SetLayout(l => l.Title("Changed").Section(null, s => s.Text("only")));
        await Wait.Until(() => Find(viewer, "Sample")?.Layout?.Title == "Changed", "new layout");
        Assert.Equal("only", Find(viewer, "Sample")!.Layout!.AllWidgets.Single().Id);
    }

    [Fact]
    public async Task With_no_hub_nothing_blocks()
    {
        var opts = new MonitorClientOptions { Hub = "127.0.0.1:1", RetryInterval = TimeSpan.FromSeconds(30) };
        var sw = Stopwatch.StartNew();
        await using (var app = MonitorClient.Start("Lonely", SampleLayout, opts))
        {
            for (int i = 0; i < 10_000; i++) { app.Set("cpu", i); app.Set("stage", i.ToString()); }
            var r = await app.RequestAsync("hub.info");
            Assert.False(r.Ok);
            Assert.False(app.Connected);
            Assert.Equal(9999, app.Get("cpu")!.Value.GetInt32()); // latest kept locally for when a hub appears
            Assert.Equal("9999", app.Get("stage")!.Value.GetString());
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }
}
