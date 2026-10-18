using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Ui.Panels;

/// <summary>Sends a widget's action to its app; the panel supplies it.</summary>
internal delegate Task<HubResponse> ActionSender(string widget, object? value);

/// <summary>
/// One widget on a panel. Draws itself from the app's whole state, so the panel can coalesce a
/// burst of updates into one redraw.
/// </summary>
internal abstract class WidgetView : StackPanel
{
    protected WidgetView(WidgetDef def)
    {
        Def = def;
        Margin = new Thickness(0, 0, 14, 12);
        if (!string.IsNullOrEmpty(def.Label) && ShowsLabelAbove)
            Children.Add(new TextBlock { Text = def.Label, Style = (Style)Application.Current.Resources["Label"] });
    }

    public WidgetDef Def { get; }

    protected virtual bool ShowsLabelAbove => true;

    public abstract void Render(AppState state);

    /// <summary>Called when the app goes offline or comes back: controls that act on the app enable/disable.</summary>
    public virtual void SetOnline(bool online) { }

    /// <summary>"Are you sure?" before an action. A dialog in the app; tests swap it out so nothing ever pops up.</summary>
    internal static Func<FrameworkElement, string, string, bool> Confirm { get; set; } = (owner, text, title)
        => MessageBox.Show(Window.GetWindow(owner)!, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public static WidgetView Create(WidgetDef def, ActionSender send) => def.Kind switch
    {
        WidgetKinds.KeyValue => new KeyValueView(def),
        WidgetKinds.Indicator => new IndicatorView(def),
        WidgetKinds.Progress => new ProgressView(def),
        WidgetKinds.Gauge => new GaugeView(def),
        WidgetKinds.Chart => new ChartView(def),
        WidgetKinds.Feed => new FeedView(def),
        WidgetKinds.Button => new ButtonView(def, send),
        WidgetKinds.Toggle => new ToggleView(def, send),
        WidgetKinds.Choice => new OptionsView(def, send, many: false),
        WidgetKinds.Flags => new OptionsView(def, send, many: true),
        _ => new TextView(def), // text, and any kind this monitor doesn't know yet
    };

    // ---- value helpers ----

    protected JsonElement? Value(AppState s) => s.Values.TryGetValue(Def.Id, out var v) ? v : null;

    protected static string Display(JsonElement? v) => v switch
    {
        null => "",
        { ValueKind: JsonValueKind.String } e => Str(e),
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        { ValueKind: JsonValueKind.Number } e => e.TryGetDouble(out var d) && double.IsFinite(d) ? Num(d) : e.GetRawText(),
        { ValueKind: JsonValueKind.True } => "yes",
        { ValueKind: JsonValueKind.False } => "no",
        { } e => e.GetRawText(),
    };

    /// <summary>A JSON string's text; one .NET can't decode (a lone surrogate escape) is shown raw instead of throwing.</summary>
    protected static string Str(JsonElement e)
    {
        try { return e.GetString() ?? ""; }
        catch (InvalidOperationException) { return e.GetRawText(); }
    }

    protected static string Num(double d) => Math.Abs(d) >= 1000 || d == Math.Floor(d)
        ? d.ToString("#,0.##", CultureInfo.CurrentCulture)
        : d.ToString("0.###", CultureInfo.CurrentCulture);

    protected static string? Prop(JsonElement? v, string name)
        => v is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var p) ? Display(p) : null;

    /// <summary>The value as a finite number, or null (NaN, infinities and out-of-range numbers are "no number").</summary>
    protected static double? Number(JsonElement? v, int depth = 0)
    {
        if (v is not { } e) return null;
        double d;
        if (e.ValueKind == JsonValueKind.Number) { if (!e.TryGetDouble(out d)) return null; }
        else if (e.ValueKind == JsonValueKind.String) { if (!double.TryParse(Str(e), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null; }
        else if (e.ValueKind == JsonValueKind.Object && depth < 4 && e.TryGetProperty("value", out var inner)) return Number(inner, depth + 1);
        else return null;
        return double.IsFinite(d) ? d : null;
    }

    /// <summary>A layout bound (min/max) that is safe to compute with.</summary>
    protected static double? Bound(double? d) => d is { } x && double.IsFinite(x) ? Math.Clamp(x, -1e300, 1e300) : null;

    protected static TextBlock Small(string text = "") => new() { Text = text, Foreground = Palette.Dim, FontSize = 11 };
}

internal sealed class TextView : WidgetView
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    public TextView(WidgetDef def) : base(def) => Children.Add(_text);
    public override void Render(AppState s) => _text.Text = Display(Value(s));
}

internal sealed class KeyValueView : WidgetView
{
    private readonly Grid _grid = new();

    public KeyValueView(WidgetDef def) : base(def)
    {
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(_grid);
    }

    public override void Render(AppState s)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        if (Value(s) is not { ValueKind: JsonValueKind.Object } obj)
        {
            if (Value(s) is { } other) Add(0, "", Display(other));
            return;
        }
        int row = 0;
        foreach (var p in obj.EnumerateObject()) Add(row++, p.Name, Display(p.Value));
    }

    private void Add(int row, string key, string value)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var k = Small(key);
        k.Margin = new Thickness(0, 1, 12, 1);
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        Grid.SetRow(k, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
        _grid.Children.Add(k);
        _grid.Children.Add(v);
    }
}

internal sealed class IndicatorView : WidgetView
{
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };

    public IndicatorView(WidgetDef def) : base(def)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_text);
        Children.Add(row);
    }

    public override void Render(AppState s)
    {
        var v = Value(s);
        // Either a bare state ("running") or {"state":..,"color":..,"text":..}.
        string state = Prop(v, "state") ?? Display(v);
        string? color = Prop(v, "color");
        if (color is null && Def.States is not null)
        {
            var match = Def.States.FirstOrDefault(kv => string.Equals(kv.Key, state, StringComparison.OrdinalIgnoreCase));
            color = match.Value;
        }
        _dot.Fill = v is null ? Palette.Track : Palette.Brush(color ?? state, Palette.Dim);
        _text.Text = Prop(v, "text") ?? state;
    }
}

internal sealed class ProgressView : WidgetView
{
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1, Background = Palette.Track, BorderThickness = new Thickness(0) };
    private readonly TextBlock _text = Small();

    public ProgressView(WidgetDef def) : base(def)
    {
        _bar.Foreground = Palette.Brush(def.Color);
        double min = Bound(def.Min) ?? 0, max = Bound(def.Max) ?? 1;
        if (max <= min) max = min + 1;
        _bar.Minimum = min;
        _bar.Maximum = max;
        Children.Add(_bar);
        _text.Margin = new Thickness(0, 3, 0, 0);
        Children.Add(_text);
    }

    public override void Render(AppState s)
    {
        var v = Value(s);
        var n = Number(v);
        _bar.IsIndeterminate = v is not null && n is null && Prop(v, "text") is null;
        _bar.Value = Math.Clamp(n ?? _bar.Minimum, _bar.Minimum, _bar.Maximum);
        double pct = (_bar.Value - _bar.Minimum) / Math.Max(1e-9, _bar.Maximum - _bar.Minimum) * 100;
        _text.Text = Prop(v, "text") ?? (n is null ? "" : $"{pct:0}%");
    }
}

internal sealed class GaugeView : WidgetView
{
    private const double Size = 96;
    private readonly Path _arc = new() { StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _text = new() { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _unit = Small();

    public GaugeView(WidgetDef def) : base(def)
    {
        var canvas = new Grid { Width = Size, Height = Size * 0.62, HorizontalAlignment = HorizontalAlignment.Left };
        canvas.Children.Add(new Path { Data = ArcGeometry(1), Stroke = Palette.Track, StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        _arc.Stroke = Palette.Brush(def.Color);
        canvas.Children.Add(_arc);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        label.Children.Add(_text);
        _unit.HorizontalAlignment = HorizontalAlignment.Center;
        _unit.Text = def.Unit ?? "";
        label.Children.Add(_unit);
        canvas.Children.Add(label);
        Children.Add(canvas);
    }

    public override void Render(AppState s)
    {
        var n = Number(Value(s));
        double min = Bound(Def.Min) ?? 0, max = Bound(Def.Max) ?? 100;
        double frac = n is null ? 0 : Math.Clamp((n.Value - min) / Math.Max(1e-9, max - min), 0, 1);
        _arc.Data = frac <= 0 ? null : ArcGeometry(frac);
        _text.Text = n is null ? "–" : Num(n.Value);
    }

    /// <summary>A top half-circle from the left, <paramref name="frac"/> of the way round.</summary>
    private static Geometry ArcGeometry(double frac)
    {
        double r = Size / 2 - 6, cx = Size / 2, cy = Size / 2;
        double angle = Math.PI * (1 - Math.Min(frac, 0.9999));
        var start = new Point(cx - r, cy);
        var end = new Point(cx + r * Math.Cos(angle), cy - r * Math.Sin(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }
}

internal sealed class ChartView : WidgetView
{
    private readonly Canvas _canvas = new() { Height = 70, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Polyline _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polygon _fill = new() { Opacity = 0.15 };
    private readonly TextBlock _last = Small();
    private readonly TextBlock _range = Small();
    private List<double> _points = [];

    public ChartView(WidgetDef def) : base(def)
    {
        var brush = Palette.Brush(def.Color);
        _line.Stroke = brush;
        _fill.Fill = brush;
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => Draw();
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1c, 0x22)), CornerRadius = new CornerRadius(3), Child = _canvas };
        Children.Add(border);
        var foot = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(_last, Dock.Left);
        _range.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(_last);
        foot.Children.Add(_range);
        Children.Add(foot);
    }

    public override void Render(AppState s)
    {
        _points = s.Streams.TryGetValue(Def.Id, out var items)
            ? items.Select(i => Number(i)).Where(n => n is not null).Select(n => n!.Value).ToList()
            : [];
        string unit = string.IsNullOrEmpty(Def.Unit) ? "" : " " + Def.Unit;
        _last.Text = _points.Count == 0 ? "no data" : Num(_points[^1]) + unit;
        Draw();
    }

    private void Draw()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w <= 0 || _points.Count == 0) { _line.Points = []; _fill.Points = []; _range.Text = ""; return; }
        double lo = Bound(Def.Min) ?? Math.Max(_points.Min(), -1e300), hi = Bound(Def.Max) ?? Math.Min(_points.Max(), 1e300);
        if (hi - lo < 1e-9 * Math.Max(1, Math.Abs(lo))) hi = lo + Math.Max(1, Math.Abs(lo) * 1e-6);
        int slots = Math.Max(2, Protocol.StreamLimit(Def));
        double step = w / (slots - 1);
        double x0 = w - step * (_points.Count - 1); // newest at the right edge
        var pts = new PointCollection(_points.Count + 2);
        for (int i = 0; i < _points.Count; i++)
        {
            double y = h - 3 - (Math.Clamp(_points[i], lo, hi) - lo) / (hi - lo) * (h - 6);
            pts.Add(new Point(x0 + i * step, y));
        }
        _line.Points = pts;
        var fill = new PointCollection(pts) { new Point(pts[^1].X, h), new Point(pts[0].X, h) };
        _fill.Points = fill;
        _range.Text = $"{Num(lo)} – {Num(hi)}";
    }
}

internal sealed class FeedView : WidgetView
{
    private readonly ListBox _list = new()
    {
        Height = 150, Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1c, 0x22)), BorderThickness = new Thickness(0),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5,
    };
    // The items on screen, in order. JsonElement's default Equals compares the document and position,
    // so it tells "the same item" from "an equal-looking new line" (repeated log lines stay distinct).
    private readonly List<JsonElement> _shown = [];

    public FeedView(WidgetDef def) : base(def)
    {
        _list.ItemContainerStyle = (Style)Application.Current.Resources["PlainItem"];
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        Children.Add(_list);
    }

    /// <summary>Lines on screen; for tests.</summary>
    internal IEnumerable<string> Lines => _list.Items.OfType<TextBlock>().Select(t => t.Text);

    public override void Render(AppState s)
    {
        var items = s.Streams.TryGetValue(Def.Id, out var l) ? l : [];
        // Usually the new history is the old one minus some oldest lines plus some new ones: find where
        // the last line shown sits now and only touch the ends. Anything else: redraw it all.
        int drop = -1, keep = 0;
        if (_shown.Count > 0)
        {
            var last = _shown[^1];
            for (int j = items.Count - 1; j >= 0; j--)
                if (items[j].Equals(last))
                {
                    keep = j + 1;
                    drop = _shown.Count - keep;
                    if (drop < 0 || !items[0].Equals(_shown[drop])) drop = -1;
                    break;
                }
        }
        else if (items.Count == 0) return;
        if (drop == 0 && keep == items.Count) return; // nothing new

        var sv = FindScroll();
        bool atBottom = sv is null || sv.VerticalOffset >= sv.ScrollableHeight - 2;
        if (drop < 0)
        {
            _list.Items.Clear();
            _shown.Clear();
            keep = 0;
        }
        else
        {
            for (int i = 0; i < drop; i++) _list.Items.RemoveAt(0);
            _shown.RemoveRange(0, drop);
        }
        for (int i = keep; i < items.Count; i++)
        {
            _list.Items.Add(Line(items[i]));
            _shown.Add(items[i]);
        }
        // Scroll the feed's own viewer: ScrollIntoView would also drag the whole panel to the feed.
        if (atBottom) Dispatcher.BeginInvoke(() => FindScroll()?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextBlock Line(JsonElement item)
    {
        string text = Prop(item, "text") ?? Display(item);
        string? level = Prop(item, "level");
        string? ts = Prop(item, "ts");
        if (ts is not null && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            text = $"{t.LocalDateTime:HH:mm:ss}  {text}";
        var brush = level?.ToLowerInvariant() switch
        {
            "warn" or "warning" => Palette.Brush("warn"),
            "error" => Palette.Brush("error"),
            "debug" or "trace" => Palette.Dim,
            _ => Palette.Fg,
        };
        return new TextBlock { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap };
    }

    private ScrollViewer? FindScroll()
    {
        if (VisualTreeHelper.GetChildrenCount(_list) == 0) return null;
        return (VisualTreeHelper.GetChild(_list, 0) as Border)?.Child as ScrollViewer;
    }
}

internal sealed class ButtonView : WidgetView
{
    private readonly Button _button = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 90 };
    private readonly TextBlock _result = Small();
    private readonly ActionSender _send;
    private bool _online = true, _enabledByApp = true, _busy;

    public ButtonView(WidgetDef def, ActionSender send) : base(def)
    {
        _send = send;
        _button.Content = def.Label ?? def.Id;
        if (!string.IsNullOrEmpty(def.Color)) _button.BorderBrush = Palette.Brush(def.Color);
        _button.Click += async (_, _) => await ClickAsync();
        Children.Add(_button);
        _result.Margin = new Thickness(0, 3, 0, 0);
        _result.TextWrapping = TextWrapping.Wrap;
        Children.Add(_result);
    }

    protected override bool ShowsLabelAbove => false;

    public override void Render(AppState s)
    {
        // Optional value: {"enabled":false,"label":"Stopping..."}
        var v = Value(s);
        _enabledByApp = !(v is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
        if (Prop(v, "label") is { Length: > 0 } label) _button.Content = label;
        Sync();
    }

    public override void SetOnline(bool online) { _online = online; Sync(); }

    private void Sync() => _button.IsEnabled = _online && _enabledByApp && !_busy;

    private async Task ClickAsync()
    {
        if (!string.IsNullOrEmpty(Def.Confirm) && !Confirm(this, Def.Confirm, Def.Label ?? Def.Id)) return;
        _busy = true; Sync();
        _result.Text = "…";
        _result.Foreground = Palette.Dim;
        try
        {
            var r = await _send(Def.Id, null);
            _result.Text = r.Ok ? (r.Result is { } e && e.ValueKind != JsonValueKind.Null ? Display(e) : "done") : r.Error;
            _result.Foreground = r.Ok ? Palette.Dim : Palette.Brush("error");
        }
        catch (Exception ex) { _result.Text = ex.Message; _result.Foreground = Palette.Brush("error"); } // async void: must not escape
        finally { _busy = false; Sync(); }
    }
}

internal sealed class ToggleView : WidgetView
{
    private readonly CheckBox _box = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _result = Small();
    private readonly ActionSender _send;
    private bool _appValue, _online = true, _busy;

    public ToggleView(WidgetDef def, ActionSender send) : base(def)
    {
        _send = send;
        _box.Content = new TextBlock { Text = def.Label ?? def.Id };
        _box.Click += async (_, _) => await FlipAsync();
        Children.Add(_box);
        _result.Margin = new Thickness(0, 3, 0, 0);
        Children.Add(_result);
    }

    protected override bool ShowsLabelAbove => false;

    public override void Render(AppState s)
    {
        _appValue = Value(s) is { ValueKind: JsonValueKind.True };
        if (!_busy) _box.IsChecked = _appValue;
    }

    public override void SetOnline(bool online) { _online = online; _box.IsEnabled = online && !_busy; }

    private async Task FlipAsync()
    {
        bool wanted = _box.IsChecked == true;
        _busy = true;
        _box.IsEnabled = false;
        try
        {
            var r = await _send(Def.Id, wanted);
            _result.Text = r.Ok ? "" : r.Error;
            _result.Foreground = Palette.Brush("error");
        }
        catch (Exception ex) { _result.Text = ex.Message; _result.Foreground = Palette.Brush("error"); } // async void: must not escape
        finally
        {
            _busy = false;
            _box.IsEnabled = _online;
            _box.IsChecked = _appValue; // the app decides; it confirms by setting the value
        }
    }
}

/// <summary>
/// Choice (pick one) and flags (any number on): a row of option buttons. The selection shown is always the
/// app's value; a click sends the wanted value and the app confirms by setting it.
/// </summary>
internal sealed class OptionsView : WidgetView
{
    private readonly WrapPanel _row = new();
    private readonly TextBlock _result = Small();
    private readonly ActionSender _send;
    private readonly bool _many;
    private readonly List<(OptionDef Option, Button Button)> _buttons = [];
    private HashSet<string> _on = new(StringComparer.Ordinal);
    private bool _online = true, _busy;

    public OptionsView(WidgetDef def, ActionSender send, bool many) : base(def)
    {
        _send = send;
        _many = many;
        foreach (var o in def.Options ?? [])
        {
            var b = new Button { Content = o.Label ?? o.Value, Tag = o.Value, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(9, 3, 9, 3) };
            b.Click += async (_, _) => await PickAsync(o);
            _buttons.Add((o, b));
            _row.Children.Add(b);
        }
        Children.Add(_row);
        _result.TextWrapping = TextWrapping.Wrap;
        Children.Add(_result);
        Paint();
    }

    public override void Render(AppState s)
    {
        var v = Value(s);
        _on = new HashSet<string>(StringComparer.Ordinal);
        if (v is { ValueKind: JsonValueKind.String } one) _on.Add(Str(one));
        else if (v is { ValueKind: JsonValueKind.Array } arr)
            foreach (var x in arr.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) _on.Add(Str(x));
        Paint();
    }

    public override void SetOnline(bool online) { _online = online; Paint(); }

    private void Paint()
    {
        foreach (var (o, b) in _buttons)
        {
            bool on = _on.Contains(o.Value);
            var color = Palette.Brush(o.Color ?? (Def.States?.TryGetValue(o.Value, out var c) == true ? c : null) ?? Def.Color, Palette.Accent);
            b.Background = on ? color : Palette.Track;
            b.BorderBrush = on ? color : Palette.Brush("#3a3f4a");
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            b.IsEnabled = _online && !_busy;
        }
    }

    private async Task PickAsync(OptionDef o)
    {
        if (_busy) return;
        bool on = _on.Contains(o.Value);
        if (!_many && on) return; // already the choice
        var confirm = o.Confirm ?? Def.Confirm;
        if (!string.IsNullOrEmpty(confirm) && !Confirm(this, confirm, $"{Def.Label ?? Def.Id}: {o.Label ?? o.Value}")) return;

        object wanted = _many
            ? (Def.Options ?? []).Select(x => x.Value).Where(x => x == o.Value ? !on : _on.Contains(x)).ToArray()
            : o.Value;
        _busy = true; Paint();
        _result.Text = "";
        try
        {
            var r = await _send(Def.Id, wanted);
            if (!r.Ok) { _result.Text = r.Error; _result.Foreground = Palette.Brush("error"); }
        }
        catch (Exception ex) { _result.Text = ex.Message; _result.Foreground = Palette.Brush("error"); } // async void: must not escape
        finally { _busy = false; Paint(); }
    }
}
