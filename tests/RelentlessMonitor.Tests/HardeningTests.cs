using System.Text.Json;
using RelentlessMonitor.Hub;
using RelentlessMonitor.Ui.Panels;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Tests;

/// <summary>Bad input, odd timing and misbehaving handlers: none of it may crash, wedge or corrupt anything.</summary>
public class HardeningTests
{
    private sealed class FakePeer(int id) : IPeer
    {
        public int Id { get; } = id;
        public bool IsLoopback => true;
        public string Remote => "127.0.0.1:" + Id;
        public List<Message> Sent { get; } = [];
        public bool Closed { get; private set; }
        public void SendLine(string json) { Protocol.TryParse(json, out var m, out _); Sent.Add(m!); }
        public void Close() => Closed = true;
    }

    private static Router NewRouter(HubConfig? cfg = null) => new(cfg ?? new HubConfig(), new HubLog(null, false), () => { });

    private static Message Hello(string app, LayoutDef? layout = null, string? session = null)
        => new() { Type = MessageTypes.Hello, Role = Roles.App, App = app, Machine = "PC", Layout = layout, Session = session };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- hub ----

    [Fact]
    public void A_malformed_layout_is_cleaned_not_fatal()
    {
        var r = NewRouter();
        var mon = new FakePeer(9);
        r.Hello(mon, new Message { Type = MessageTypes.Hello, Role = Roles.Monitor, App = "m" });
        var layout = new LayoutDef
        {
            Sections = [null!, new SectionDef { Widgets = [null!, new WidgetDef { Id = "" }, new WidgetDef { Id = "ok", Kind = "" }] }],
        };
        Assert.True(r.Hello(new FakePeer(1), Hello("Messy", layout)));
        var state = mon.Sent.Last(m => m.Type == MessageTypes.AppState).State!;
        var w = Assert.Single(state.Layout!.AllWidgets);
        Assert.Equal("ok", w.Id);
        Assert.Equal(WidgetKinds.Text, w.Kind);
        Assert.Equal(1, r.AppCount);
    }

    [Fact]
    public void A_null_layout_in_a_layout_message_does_not_break_the_entry()
    {
        var r = NewRouter();
        var app = new FakePeer(1);
        r.Hello(app, Hello("A", new LayoutBuilder().Section(null, s => s.Text("t")).Build()));
        r.Handle(app, new Message { Type = MessageTypes.Layout, Layout = null });
        r.Handle(app, new Message { Type = MessageTypes.Update, Values = new() { ["t"] = J("1") } });
        Assert.Equal(1, r.AppCount);
    }

    [Fact]
    public void The_same_session_reconnecting_takes_its_panel_back()
    {
        var r = NewRouter();
        var first = new FakePeer(1);
        Assert.True(r.Hello(first, Hello("Same", session: "s1")));
        var second = new FakePeer(2);
        Assert.True(r.Hello(second, Hello("Same", session: "s1")));
        Assert.Equal("PC/Same", second.Sent.Single(m => m.Type == MessageTypes.Welcome).Key);
        Assert.True(first.Closed);
        Assert.Equal(1, r.AppCount);

        r.Disconnected(first); // the old connection's late goodbye must not take the new one offline
        var third = new FakePeer(3);
        Assert.True(r.Hello(third, Hello("Same", session: "other")));
        Assert.Equal("PC/Same#2", third.Sent.Single(m => m.Type == MessageTypes.Welcome).Key);
    }

    [Fact]
    public void A_huge_forget_setting_does_not_overflow_the_sweep()
    {
        var r = NewRouter(new HubConfig { ForgetOfflineAfterHours = double.MaxValue });
        var app = new FakePeer(1);
        r.Hello(app, Hello("Old"));
        r.Disconnected(app);
        r.Sweep(DateTime.UtcNow);
        Assert.Equal(1, r.AppCount);
    }

    // ---- shared merge ----

    [Fact]
    public void Relayout_drops_values_of_removed_widgets_and_widgets_that_became_streams()
    {
        var state = new AppState { Layout = new LayoutBuilder().Section(null, s => s.Text("a").Text("b").Text("c")).Build() };
        StateMerge.Apply(state, new Message { Values = new() { ["a"] = J("1"), ["b"] = J("2"), ["c"] = J("3") } });

        StateMerge.Relayout(state, new LayoutBuilder().Section(null, s => s.Text("a").Chart("b")).Build());
        Assert.Equal(["a"], state.Values.Keys);
        Assert.False(state.Streams.ContainsKey("b"));

        StateMerge.Relayout(state, new LayoutDef()); // no widgets yet: keep everything
        Assert.Equal(["a"], state.Values.Keys);
    }

    [Fact]
    public void Ids_the_layout_does_not_know_are_ignored_and_unlaid_ids_are_capped()
    {
        var state = new AppState { Layout = new LayoutBuilder().Section(null, s => s.Text("known")).Build() };
        StateMerge.Apply(state, new Message { Values = new() { ["known"] = J("1"), ["ghost"] = J("2") }, Append = new() { ["ghost2"] = [J("1")] } });
        Assert.Equal(["known"], state.Values.Keys);
        Assert.Empty(state.Streams);

        var bare = new AppState();
        var values = Enumerable.Range(0, StateMerge.MaxUnlaidIds + 50).ToDictionary(i => "v" + i, _ => J("0"));
        StateMerge.Apply(bare, new Message { Values = values });
        Assert.Equal(StateMerge.MaxUnlaidIds, bare.Values.Count);
    }

    [Fact]
    public void Null_maps_and_lists_in_an_update_are_skipped()
    {
        var state = new AppState { Layout = new LayoutBuilder().Section(null, s => s.Chart("c")).Build() };
        StateMerge.Apply(state, new Message { Append = new() { ["c"] = null! }, Streams = new() { ["c"] = null! } });
        Assert.Empty(state.Streams.GetValueOrDefault("c") ?? []);
    }

    // ---- protocol ----

    [Fact]
    public void ToElement_never_throws()
    {
        Assert.Equal("NaN", Protocol.ToElement(double.NaN).GetString());
        Assert.Equal("Infinity", Protocol.ToElement(new { x = double.PositiveInfinity }).GetProperty("x").GetString());
        var doc = JsonDocument.Parse("{\"a\":1}");
        var e = doc.RootElement;
        doc.Dispose();
        Assert.Equal(JsonValueKind.Null, Protocol.ToElement(e).ValueKind);
        Assert.Equal(JsonValueKind.Null, Protocol.ToElement(default(JsonElement)).ValueKind);
        var cycle = new Node();
        cycle.Next = cycle;
        Assert.Equal(JsonValueKind.String, Protocol.ToElement(cycle).ValueKind); // falls back to ToString()
    }

    private sealed class Node { public Node? Next { get; set; } public override string ToString() => "node"; }

    [Theory]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:99999999999")]
    public void Out_of_range_ports_do_not_parse_as_ports(string address)
    {
        var (_, port) = Protocol.ResolveHub(address);
        Assert.Equal(Protocol.DefaultPort, port);
    }

    [Fact]
    public void Updates_too_big_for_one_line_are_split_and_add_up_to_the_same()
    {
        var items = Enumerable.Range(0, 400).Select(i => Protocol.ToElement(new string('x', 100) + i)).ToList();
        var m = new Message
        {
            Type = MessageTypes.Update,
            Values = new() { ["v"] = J("1") },
            Streams = new() { ["log"] = items },
            Append = new() { ["c"] = [J("5")] },
        };
        var lines = Protocol.SerializeLines(m, 8 * 1024).ToList();
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 8 * 1024));

        var state = new AppState { Layout = new LayoutBuilder().Section(null, s => s.Text("v").Feed("log", limit: 1000).Chart("c")).Build(), Streams = { ["log"] = [J("\"old\"")] } };
        foreach (var l in lines)
        {
            Assert.True(Protocol.TryParse(l, out var part, out _));
            StateMerge.Apply(state, part!);
        }
        Assert.Equal(items.Select(i => i.GetString()), state.Streams["log"].Select(i => i.GetString()));
        Assert.Equal(1, state.Values["v"].GetInt32());
        Assert.Equal([5], state.Streams["c"].Select(i => i.GetInt32()));
    }

    [Fact]
    public void A_response_too_big_becomes_an_error()
    {
        var m = new Message { Type = MessageTypes.Response, Corr = "q", Result = Protocol.ToElement(new string('x', 10_000)) };
        var line = Assert.Single(Protocol.SerializeLines(m, 1000));
        Assert.True(Protocol.TryParse(line, out var back, out _));
        Assert.Equal("q", back!.Corr);
        Assert.Contains("too large", back.Error);
    }

    // ---- clients ----

    [Fact]
    public async Task Bad_values_and_ids_never_throw_from_the_app_api()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        await using var app = new MonitorClient("Odd", l => l.Section(null, s => s.Text("t").Chart("c")), hub.AppOptions());
        app.Set("t", double.NaN);
        app.Set("c", double.PositiveInfinity);
        app.Set(null!, 1);
        app.Set(((string Id, object? Value)[])null!);
        app.Set("ghost", 1);
        app.Clear(null!);
        Assert.Null(app.Get("ghost"));
        Assert.Null(app.Get(null!));
        app.Start();
        await Wait.Until(() => viewer.Apps.FirstOrDefault(a => a.App == "Odd")?.Values.ContainsKey("t") == true, "values");
        var s = viewer.Apps.Single(a => a.App == "Odd");
        Assert.Equal("NaN", s.Values["t"].GetString());
        Assert.False(s.Values.ContainsKey("ghost"));

        var r = await viewer.RequestAsync("hub.info", new { x = double.NaN });
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public async Task A_throwing_viewer_handler_does_not_stop_the_feed()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        int calls = 0;
        viewer.Updated += (_, _) => { Interlocked.Increment(ref calls); throw new InvalidOperationException("handler bug"); };
        viewer.AppChanged += _ => throw new InvalidOperationException("handler bug");
        viewer.Start();
        await using var app = MonitorClient.Start("Thrower", l => l.Section(null, s => s.Text("t")), hub.AppOptions());
        Assert.True(await app.WaitConnectedAsync(TimeSpan.FromSeconds(5)));
        for (int i = 0; i < 3; i++)
        {
            app.Set("t", i);
            await Wait.Until(() => viewer.Apps.FirstOrDefault(a => a.App == "Thrower")?.Values.GetValueOrDefault("t") is { ValueKind: JsonValueKind.Number } v && v.GetInt32() == i, $"value {i}");
        }
        Assert.True(calls >= 1);
        Assert.True(viewer.Connected);
    }

    [Fact]
    public async Task A_bad_hub_port_just_never_connects()
    {
        await using var app = MonitorClient.Start("Nowhere", l => l.Section(null, s => s.Text("t")), new MonitorClientOptions
        {
            Hub = "127.0.0.1:99999", ConnectTimeout = TimeSpan.FromMilliseconds(200), RetryInterval = TimeSpan.FromMilliseconds(100),
        });
        app.Set("t", 1);
        Assert.False(await app.WaitConnectedAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(1, app.Get("t")!.Value.GetInt32());
    }

    [Fact]
    public async Task A_layout_changed_during_the_handshake_still_reaches_the_monitor()
    {
        await using var hub = new TestHub();
        await using var viewer = new MonitorViewer(hub.ViewerOptions());
        viewer.Start();
        await using var app = new MonitorClient("Early", l => l.Section(null, s => s.Text("a")), hub.AppOptions());
        app.Start();
        app.SetLayout(l => l.Section(null, s => s.Text("b"))); // races the hello / welcome
        app.Set("b", "yes");
        await Wait.Until(() => viewer.Apps.FirstOrDefault(a => a.App == "Early") is { } s
            && s.Layout?.AllWidgets.Select(w => w.Id).SequenceEqual(["b"]) == true
            && s.Values.ContainsKey("b"), "second layout");
    }

    [Fact]
    public void The_layout_getter_returns_a_copy()
    {
        var app = new MonitorClient("Copy", l => l.Section(null, s => s.Text("a")));
        app.Layout.Sections.Clear();
        Assert.Single(app.Layout.AllWidgets);
    }

    // ---- monitor UI (off-screen, no buttons with confirm: those open a real dialog) ----

    [Fact]
    public Task Widgets_survive_nan_huge_numbers_and_broken_strings() => Sta.Run(async () =>
    {
        var layout = new LayoutBuilder().Section(null, s => s
            .Text("t").Progress("p").Gauge("g", max: double.MaxValue).Chart("c").Feed("f")
            .Widget(new WidgetDef { Id = "p2", Kind = WidgetKinds.Progress, Min = double.NaN, Max = double.NegativeInfinity })).Build();
        Assert.Null(LayoutDef.Clean(layout)!.AllWidgets.Single(w => w.Id == "p2").Min);
        var state = new AppState { Key = "PC/X", App = "X", Connected = true, Layout = layout }; // raw, not cleaned: the panel must cope
        StateMerge.Apply(state, new Message
        {
            Values = new()
            {
                ["t"] = J("\"\\uD800 lone\""), ["p"] = J("1e400"), ["g"] = J("\"NaN\""), ["p2"] = J("{\"value\":{\"value\":{\"value\":{\"value\":{\"value\":{\"value\":1}}}}}}"),
            },
            Append = new() { ["c"] = [J("1e308"), J("-1e308"), J("\"Infinity\""), J("2")], ["f"] = [J("{\"text\":\"\\uDC00\"}")] },
        });
        var panel = new AppPanel("PC/X", (_, _) => Task.FromResult(new HubResponse(true, null, null)));
        panel.Render(state);
        panel.Measure(new System.Windows.Size(800, 2000));
        panel.Arrange(new System.Windows.Rect(0, 0, 800, 2000));
        panel.UpdateLayout();
        Assert.Empty(System.Windows.Application.Current.Windows); // nothing in these tests may open a window
        Assert.IsNotType<RelentlessMonitor.Ui.App>(System.Windows.Application.Current);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Feed_updates_incrementally_and_keeps_repeated_lines() => Sta.Run(async () =>
    {
        var def = new WidgetDef { Id = "f", Kind = WidgetKinds.Feed, Limit = 3 };
        var feed = new FeedView(def);
        var state = new AppState { Layout = new LayoutDef { Sections = [new SectionDef { Widgets = [def] }] } };

        void Add(string text) => StateMerge.Apply(state, new Message { Append = new() { ["f"] = [J($"{{\"text\":\"{text}\"}}")] } });
        Add("same"); Add("same");
        feed.Render(state);
        Assert.Equal(["same", "same"], feed.Lines);
        Add("same");
        feed.Render(state);
        Assert.Equal(["same", "same", "same"], feed.Lines);
        Add("new");
        feed.Render(state);
        Assert.Equal(["same", "same", "new"], feed.Lines);
        feed.Render(state); // nothing changed
        Assert.Equal(["same", "same", "new"], feed.Lines);

        StateMerge.Apply(state, new Message { Streams = new() { ["f"] = [J("{\"text\":\"fresh\"}")] } });
        feed.Render(state);
        Assert.Equal(["fresh"], feed.Lines);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("sc#1,0,0")]
    [InlineData("C:\\Windows\\x.png")]
    [InlineData("pack://application:,,,/x")]
    [InlineData("#12")]
    [InlineData("#gggggg")]
    [InlineData("averyveryveryveryveryverylongcolorname")]
    public void Palette_rejects_anything_but_hex_and_names(string s) => Assert.Null(Palette.TryParse(s));

    [Theory]
    [InlineData("#fff")]
    [InlineData("#80ff0000")]
    [InlineData("red")]
    public void Palette_accepts_hex_and_names(string s) => Assert.NotNull(Palette.TryParse(s));
}
