using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RelentlessMonitor.Ui.Panels;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Tests;

/// <summary>
/// Builds the monitor's panels off-screen on a private STA thread; no window is ever shown.
/// </summary>
public class UiPanelTests
{
    private static LayoutDef ExampleLayout() => new LayoutBuilder()
        .Section("Job", s => s
            .Indicator("state", "State", ("idle", "gray"), ("running", "green"))
            .Text("stage", "Stage")
            .Progress("progress", "Progress", span: 2)
            .KeyValue("counts", "Counts")
            .Gauge("mem", "Memory", max: 1024, unit: "MB"))
        .Section("Stream", s => s.Chart("cpu", "CPU", limit: 5, min: 0, max: 100).Feed("log", limit: 3), columns: 1)
        .Section("Controls", s => s.Button("go", "Go").Toggle("paused", "Paused").Widget(new WidgetDef { Id = "x", Kind = "hologram" }))
        .Build();

    private static AppState State(LayoutDef layout, bool connected = true)
    {
        var s = new AppState { Key = "PC/App", App = "App", Connected = connected, Layout = layout };
        StateMerge.Apply(s, new Message
        {
            Values = new()
            {
                ["state"] = J("\"running\""), ["stage"] = J("\"parsing\""),
                ["progress"] = J("""{"value":0.25,"text":"25% (parsing)"}"""),
                ["counts"] = J("""{"files":12,"errors":0}"""), ["mem"] = J("512"),
                ["paused"] = J("false"), ["x"] = J("\"what\""),
            },
            Append = new()
            {
                ["cpu"] = [J("10"), J("20"), J("30"), J("40"), J("50"), J("60")],
                ["log"] = [J("""{"text":"one"}"""), J("""{"text":"two","level":"warn"}"""), J("""{"text":"three","level":"error"}"""), J("""{"text":"four"}""")],
            },
        });
        return s;
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public Task Panel_draws_every_widget_kind_from_the_layout() => Sta.Run(async () =>
    {
        var panel = new AppPanel("PC/App", (_, _) => Task.FromResult(new HubResponse(true, null, null)));
        panel.Render(State(ExampleLayout()));
        Layout(panel);

        var texts = Texts(panel);
        Assert.Contains("running", texts);
        Assert.Contains("parsing", texts);
        Assert.Contains("25% (parsing)", texts);
        Assert.Contains("files", texts);
        Assert.Contains("512", texts);
        Assert.Contains("MB", texts);
        Assert.Contains("what", texts); // unknown kinds fall back to text

        var dot = Find<Ellipse>(panel).First(e => e.Width == 12);
        Assert.Equal(Palette.Brush("green").Color, ((SolidColorBrush)dot.Fill).Color);

        Assert.Equal(0.25, Find<ProgressBar>(panel).Single().Value, 3);

        var line = Find<Polyline>(panel).Single();
        Assert.Equal(5, line.Points.Count); // limited to the chart's 5

        var feed = Find<ListBox>(panel).Single();
        Assert.Equal(["two", "three", "four"], feed.Items.Cast<TextBlock>().Select(t => t.Text));
        Assert.Equal(Palette.Brush("error").Color, ((SolidColorBrush)((TextBlock)feed.Items[1]!).Foreground).Color);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Offline_apps_cannot_be_clicked_and_come_back() => Sta.Run(async () =>
    {
        var panel = new AppPanel("PC/App", (_, _) => Task.FromResult(new HubResponse(true, null, null)));
        var layout = ExampleLayout();
        panel.Render(State(layout, connected: false));
        Assert.False(Find<Button>(panel).Single().IsEnabled);
        Assert.False(Find<CheckBox>(panel).Single().IsEnabled);

        panel.Render(State(layout, connected: true));
        Assert.True(Find<Button>(panel).Single().IsEnabled);
        Assert.True(Find<CheckBox>(panel).Single().IsEnabled);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Button_sends_its_action_and_shows_the_reply() => Sta.Run(async () =>
    {
        var sent = new List<(string, object?)>();
        var reply = new TaskCompletionSource<HubResponse>();
        var panel = new AppPanel("PC/App", (w, v) => { sent.Add((w, v)); return reply.Task; });
        panel.Render(State(ExampleLayout()));
        var button = Find<Button>(panel).Single();

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal([("go", (object?)null)], sent);
        Assert.False(button.IsEnabled); // busy until the app answers

        reply.SetResult(new HubResponse(true, J("\"started\""), null));
        await Pump();
        Assert.True(button.IsEnabled);
        Assert.Contains("started", Texts(panel));
    });

    [Fact]
    public Task Toggle_shows_what_the_app_says_not_what_was_clicked() => Sta.Run(async () =>
    {
        var reply = new TaskCompletionSource<HubResponse>();
        object? sentValue = null;
        var panel = new AppPanel("PC/App", (_, v) => { sentValue = v; return reply.Task; });
        var layout = ExampleLayout();
        panel.Render(State(layout));
        var box = Find<CheckBox>(panel).Single();

        box.IsChecked = true;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(true, sentValue);

        reply.SetResult(new HubResponse(false, null, "app said no"));
        await Pump();
        Assert.False(box.IsChecked); // the app never set it
        Assert.Contains("app said no", Texts(panel));

        // The app confirms by setting the value.
        var on = State(layout);
        on.Values["paused"] = J("true");
        panel.Render(on);
        Assert.True(box.IsChecked);
    });

    [Fact]
    public Task New_layout_rebuilds_and_same_layout_keeps_controls() => Sta.Run(async () =>
    {
        var panel = new AppPanel("PC/App", (_, _) => Task.FromResult(new HubResponse(true, null, null)));
        panel.Render(State(ExampleLayout()));
        var button = Find<Button>(panel).Single();

        panel.Render(State(ExampleLayout())); // equal layout from a fresh hello: keep the controls
        Assert.Same(button, Find<Button>(panel).Single());

        var smaller = new LayoutBuilder().Section("Only", s => s.Text("stage", "Stage")).Build();
        panel.Render(State(smaller));
        Assert.Empty(Find<Button>(panel));
        Assert.Contains("parsing", Texts(panel));

        panel.Render(new AppState { Key = "PC/App", App = "App", Connected = true });
        Assert.Contains("This app hasn't sent a layout.", Texts(panel));
        await Task.CompletedTask;
    });

    [Fact]
    public Task Odd_values_do_not_throw() => Sta.Run(async () =>
    {
        var panel = new AppPanel("PC/App", (_, _) => Task.FromResult(new HubResponse(true, null, null)));
        var s = new AppState { Key = "PC/App", App = "App", Connected = true, Layout = ExampleLayout() };
        foreach (var id in new[] { "state", "stage", "progress", "counts", "mem", "paused", "go" })
            s.Values[id] = J("""[1,{"a":null},"x"]""");
        s.Streams["cpu"] = [J("\"abc\""), J("null"), J("{}")];
        s.Streams["log"] = [J("42"), J("null"), J("""{"ts":"not a time","text":"t"}""")];
        panel.Render(s);
        Layout(panel);
        s.Values.Clear();
        s.Streams.Clear();
        panel.Render(s);
        Layout(panel);
        await Task.CompletedTask;
    });

    // ---- helpers ----

    private static void Layout(FrameworkElement e)
    {
        e.Measure(new Size(900, double.PositiveInfinity));
        e.Arrange(new Rect(new Point(), new Size(900, e.DesiredSize.Height)));
        e.UpdateLayout();
    }

    private static Task Pump() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    private static List<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        void Walk(object node)
        {
            if (node is T t) found.Add(t);
            if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(root);
        return found;
    }

    private static List<string> Texts(DependencyObject root) => Find<TextBlock>(root).Select(t => t.Text).ToList();
}

/// <summary>One STA thread with a running dispatcher and the monitor's theme resources, shared by UI tests.</summary>
/// <remarks>
/// A plain <see cref="Application"/>, never the monitor's App: that one opens the main window, and a test
/// must never put anything on the user's screen.
/// </remarks>
internal static class Sta
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/RelentlessMonitor;component/Theme.xaml", UriKind.Relative)));
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "ui-tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.Result;
    });

    public static Task Run(Func<Task> test) => Ui.Value.InvokeAsync(test).Task.Unwrap();
}
