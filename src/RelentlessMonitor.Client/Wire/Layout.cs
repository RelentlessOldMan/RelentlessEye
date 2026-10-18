using System.Text.Json.Serialization;

namespace RelentlessMonitor.Wire;

/// <summary>An app's panel: titled sections of widgets. The monitor draws exactly this; it knows nothing else about the app.</summary>
public sealed class LayoutDef
{
    /// <summary>Panel title; defaults to the app name.</summary>
    public string? Title { get; set; }
    public List<SectionDef> Sections { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<WidgetDef> AllWidgets => (Sections ?? []).SelectMany(s => s?.Widgets ?? []).Where(w => w is not null);

    /// <summary>
    /// A deep copy with nulls dropped (null sections, widgets, ids, kinds) and non-finite bounds removed
    /// (JSON can't carry NaN or infinity, so they would break sending the layout), so nothing downstream has
    /// to defend against a malformed layout from an app, and later changes to the original don't leak in.
    /// </summary>
    public static LayoutDef? Clean(LayoutDef? layout) => layout is null ? null : new LayoutDef
    {
        Title = layout.Title,
        Sections = (layout.Sections ?? []).Where(s => s is not null).Select(s => new SectionDef
        {
            Title = s.Title,
            Columns = s.Columns,
            Widgets = (s.Widgets ?? []).Where(w => w is not null && !string.IsNullOrEmpty(w.Id)).Select(w => new WidgetDef
            {
                Id = w.Id, Kind = string.IsNullOrEmpty(w.Kind) ? WidgetKinds.Text : w.Kind, Label = w.Label,
                States = w.States?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value),
                Min = Finite(w.Min), Max = Finite(w.Max), Unit = w.Unit, Limit = w.Limit, Span = w.Span, Color = w.Color, Confirm = w.Confirm,
            }).ToList(),
        }).ToList(),
    };

    private static double? Finite(double? d) => d is { } x && double.IsFinite(x) ? x : null;
}

public sealed class SectionDef
{
    public string? Title { get; set; }
    /// <summary>Grid columns; widgets flow left to right. Default 2.</summary>
    public int? Columns { get; set; }
    public List<WidgetDef> Widgets { get; set; } = [];
}

/// <summary>
/// One widget. <see cref="Id"/> is what the app's updates and the monitor's actions refer to.
/// Options apply per <see cref="Kind"/>; see <see cref="WidgetKinds"/>.
/// </summary>
public sealed class WidgetDef
{
    public string Id { get; set; } = "";
    /// <summary>One of <see cref="WidgetKinds"/>.</summary>
    public string Kind { get; set; } = WidgetKinds.Text;
    public string? Label { get; set; }
    /// <summary>Indicator: state value → color (name like <c>green</c>, or <c>#rrggbb</c>).</summary>
    public Dictionary<string, string>? States { get; set; }
    /// <summary>Gauge/chart/progress range.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? Unit { get; set; }
    /// <summary>Chart: points kept. Feed: lines kept. The hub caps this at 5000.</summary>
    public int? Limit { get; set; }
    /// <summary>Columns this widget spans in its section's grid.</summary>
    public int? Span { get; set; }
    /// <summary>Accent color for chart line, gauge, button.</summary>
    public string? Color { get; set; }
    /// <summary>Button: ask "are you sure?" before sending the action.</summary>
    public string? Confirm { get; set; }
}

/// <summary>
/// The widgets a monitor knows how to draw, and what a value means for each.
/// </summary>
public static class WidgetKinds
{
    /// <summary>Value: string (or anything; shown as text).</summary>
    public const string Text = "text";
    /// <summary>Value: object of label → value, shown as rows.</summary>
    public const string KeyValue = "kv";
    /// <summary>Value: state string, colored via <see cref="WidgetDef.States"/>; or <c>{"state":..,"color":..,"text":..}</c>.</summary>
    public const string Indicator = "indicator";
    /// <summary>Value: number in Min..Max (default 0..1), or <c>{"value":..,"text":..}</c>.</summary>
    public const string Progress = "progress";
    /// <summary>Value: number in Min..Max (default 0..100), with Unit.</summary>
    public const string Gauge = "gauge";
    /// <summary>Value: number, appended as the next point (stream).</summary>
    public const string Chart = "chart";
    /// <summary>Value: string, or <c>{"text":..,"level":"info|warn|error"}</c>, appended as the next line (stream).</summary>
    public const string Feed = "feed";
    /// <summary>No value needed; clicking sends an action. Value <c>{"enabled":false,"label":..}</c> changes it.</summary>
    public const string Button = "button";
    /// <summary>Value: bool. Flipping it sends an action with the new value; the app confirms by updating.</summary>
    public const string Toggle = "toggle";

    /// <summary>Kinds whose values append (and are retained as history) rather than replace.</summary>
    public static bool IsStream(string kind) => kind is Chart or Feed;
}

/// <summary>Builds a <see cref="LayoutDef"/> fluently: <c>new LayoutBuilder().Section("Run", s => s.Text("stage", "Stage"))</c>.</summary>
public sealed class LayoutBuilder
{
    private readonly LayoutDef _layout = new();

    public LayoutBuilder Title(string title) { _layout.Title = title; return this; }

    public LayoutBuilder Section(string? title, Action<SectionBuilder> build, int columns = 2)
    {
        var s = new SectionDef { Title = title, Columns = columns };
        build(new SectionBuilder(s));
        _layout.Sections.Add(s);
        return this;
    }

    public LayoutDef Build() => _layout;
}

public sealed class SectionBuilder(SectionDef section)
{
    private SectionBuilder Add(WidgetDef w) { section.Widgets.Add(w); return this; }

    public SectionBuilder Text(string id, string? label = null, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Text, Label = label, Span = span });

    public SectionBuilder KeyValue(string id, string? label = null, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.KeyValue, Label = label, Span = span });

    /// <summary>States map a value to a color, e.g. <c>("ok","green"), ("failed","red")</c>.</summary>
    public SectionBuilder Indicator(string id, string? label, params (string state, string color)[] states)
        => Add(new() { Id = id, Kind = WidgetKinds.Indicator, Label = label, States = states.ToDictionary(s => s.state, s => s.color) });

    public SectionBuilder Progress(string id, string? label = null, double max = 1, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Progress, Label = label, Min = 0, Max = max, Span = span });

    public SectionBuilder Gauge(string id, string? label = null, double min = 0, double max = 100, string? unit = null, string? color = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Gauge, Label = label, Min = min, Max = max, Unit = unit, Color = color });

    public SectionBuilder Chart(string id, string? label = null, int limit = 120, double? min = null, double? max = null, string? unit = null, string? color = null, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Chart, Label = label, Limit = limit, Min = min, Max = max, Unit = unit, Color = color, Span = span });

    public SectionBuilder Feed(string id, string? label = null, int limit = 200, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Feed, Label = label, Limit = limit, Span = span });

    public SectionBuilder Button(string id, string label, string? confirm = null, string? color = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Button, Label = label, Confirm = confirm, Color = color });

    public SectionBuilder Toggle(string id, string label)
        => Add(new() { Id = id, Kind = WidgetKinds.Toggle, Label = label });

    /// <summary>Anything not covered above.</summary>
    public SectionBuilder Widget(WidgetDef w) => Add(w);
}
