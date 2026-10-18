using System.Text.Json;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Tests;

/// <summary>Choice and flags widgets, and the sender the hub stamps on requests.</summary>
public class ControlsTests
{
    private static Action<LayoutBuilder> ControlsLayout => l => l
        .Section("Mode", s => s
            .Choice("mode", "Mode", ["auto", "steady", new OptionDef { Value = "hold", Label = "Hold", Color = "red", Confirm = "Hold everything?" }])
            .Flags("res", "Resources", "cpu", "gpu", "ram"));

    private static AppState? Find(MonitorViewer v, string app) => v.Apps.FirstOrDefault(a => a.App == app);

    [Fact]
    public void Options_are_bare_strings_on_the_wire_unless_they_need_more()
    {
        var layout = new LayoutBuilder().Section(null, s => s.Choice("m", "M", ["a", new OptionDef { Value = "b", Color = "red" }])).Build();
        var json = JsonSerializer.Serialize(layout, Protocol.Json);
        Assert.Contains("""["a",{"value":"b","color":"red"}]""", json);

        var back = LayoutDef.Clean(JsonSerializer.Deserialize<LayoutDef>(
            """{"sections":[{"widgets":[{"id":"m","kind":"choice","options":["a",{"value":"b","label":"Bee","confirm":"ok?"},null,"",3,{"label":"no value"}]}]}]}""",
            Protocol.Json))!;
        var opts = back.AllWidgets.Single().Options!;
        Assert.Equal(["a", "b", "3"], opts.Select(o => o.Value));
        Assert.Equal("Bee", opts[1].Label);
        Assert.Equal("ok?", opts[1].Confirm);
    }

    [Fact]
    public async Task Choice_and_flags_reach_the_app_checked_and_are_shown_back()
    {
        await using var hub = new TestHub();
        await using var app = new MonitorClient("Warden", ControlsLayout, hub.AppOptions());
        var picks = new List<string>();
        IReadOnlyList<string>? flags = null;
        RequestSender? sender = null;
        app.OnChoice("mode", v => { picks.Add(v); sender = MonitorClient.Sender; });
        app.OnFlags("res", on => flags = on);
        app.Start();
        await using var viewer = new MonitorViewer(hub.ViewerOptions(), "RelentlessMonitor");
        viewer.Start();
        await Wait.Until(() => Find(viewer, "Warden")?.Connected == true, "app panel");
        var key = Find(viewer, "Warden")!.Key;

        var r = await viewer.ActionAsync(key, "mode", "steady");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(["steady"], picks);
        await Wait.Until(() => Find(viewer, "Warden")!.Values.TryGetValue("mode", out var v) && v.GetString() == "steady", "choice shown");

        Assert.NotNull(sender);
        Assert.True(sender!.IsMonitor);
        Assert.Equal("RelentlessMonitor", sender.App);
        Assert.StartsWith("monitor:", sender.Key);
        Assert.Null(MonitorClient.Sender); // only inside a handler

        r = await viewer.ActionAsync(key, "mode", "warp");
        Assert.Contains("not an option", r.Error);
        r = await viewer.ActionAsync(key, "mode", 3);
        Assert.False(r.Ok);
        Assert.Equal(["steady"], picks);

        r = await viewer.ActionAsync(key, "res", new[] { "ram", "cpu", "ram" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(["cpu", "ram"], flags); // layout order, no duplicates
        await Wait.Until(() => Find(viewer, "Warden")!.Values.TryGetValue("res", out var v) && v.GetArrayLength() == 2, "flags shown");

        r = await viewer.ActionAsync(key, "res", new[] { "cpu", "tpu" });
        Assert.Contains("tpu", r.Error);
        r = await viewer.ActionAsync(key, "res", "cpu");
        Assert.False(r.Ok);
        Assert.Equal(["cpu", "ram"], flags);
    }

    [Fact]
    public async Task An_app_sending_an_action_is_stamped_as_an_app()
    {
        await using var hub = new TestHub();
        await using var target = new MonitorClient("Warden", ControlsLayout, hub.AppOptions());
        RequestSender? sender = null;
        target.OnChoice("mode", _ => sender = MonitorClient.Sender);
        target.Provide("who", _ => MonitorClient.Sender?.ToString());
        target.Start();
        await using var other = MonitorClient.Start("Sneaky", l => { }, hub.AppOptions());
        Assert.True(await target.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await other.WaitConnectedAsync(TimeSpan.FromSeconds(5)));

        var r = await other.RequestAsync(Protocol.ActionRequest, new { widget = "mode", value = "auto" }, to: "Warden");
        Assert.True(r.Ok, r.Error);
        Assert.False(sender!.IsMonitor);
        Assert.Equal("app", sender.Role);
        Assert.Equal("TESTPC/Sneaky", sender.Key);
        Assert.Equal("Sneaky", sender.App);

        r = await other.RequestAsync("Warden.who");
        Assert.Equal("Sneaky (TESTPC/Sneaky)", r.Result!.Value.GetString());
    }
}
