using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelentlessEye.Wire;

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
                Options = w.Options?.Where(o => o is not null && !string.IsNullOrEmpty(o.Value))
                    .Select(o => new OptionDef { Value = o.Value, Label = o.Label, Color = o.Color, Confirm = o.Confirm }).ToList(),
                Columns = w.Columns?.Where(c => c is not null && !string.IsNullOrEmpty(c.Key))
                    .Select(c => new ColumnDef { Key = c.Key, Label = c.Label, Align = c.Align, Width = c.Width is > 0 and < 10000 ? c.Width : null }).ToList(),
                Pick = w.Pick,
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
    /// <summary>Button, choice: ask "are you sure?" before sending the action.</summary>
    public string? Confirm { get; set; }
    /// <summary>Choice, flags: what can be picked. On the wire each is a plain string, or an object with a label, color or confirm.</summary>
    public List<OptionDef>? Options { get; set; }
    /// <summary>Table: its columns, left to right.</summary>
    public List<ColumnDef>? Columns { get; set; }
    /// <summary>Table: rows can be clicked, which sends an action with the row's <c>id</c>.</summary>
    public bool? Pick { get; set; }
}

/// <summary>One column of a table. Rows are objects keyed by <see cref="Key"/>.</summary>
public sealed class ColumnDef
{
    public string Key { get; set; } = "";
    /// <summary>Header; defaults to <see cref="Key"/>.</summary>
    public string? Label { get; set; }
    /// <summary><c>left</c> (default), <c>center</c> or <c>right</c>.</summary>
    public string? Align { get; set; }
    /// <summary>Pixels. Without it a column fits its content; the last one takes what's left.</summary>
    public double? Width { get; set; }

    public static implicit operator ColumnDef(string key) => new() { Key = key };
}

/// <summary>One pickable option of a choice or flags widget. <see cref="Value"/> is what the action carries.</summary>
[JsonConverter(typeof(OptionDefConverter))]
public sealed class OptionDef
{
    public string Value { get; set; } = "";
    /// <summary>Shown instead of <see cref="Value"/>.</summary>
    public string? Label { get; set; }
    /// <summary>Its color when selected; else the widget's <see cref="WidgetDef.States"/> entry for it, else the widget color.</summary>
    public string? Color { get; set; }
    /// <summary>Ask "are you sure?" before picking this one (overrides the widget's <see cref="WidgetDef.Confirm"/>).</summary>
    public string? Confirm { get; set; }

    public static implicit operator OptionDef(string value) => new() { Value = value };
}

/// <summary>Reads an option from a string or an object; writes a bare string when there's nothing but the value.</summary>
public sealed class OptionDefConverter : JsonConverter<OptionDef>
{
    public override OptionDef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return new OptionDef { Value = reader.GetString() ?? "" };
            case JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False:
                using (var doc = JsonDocument.ParseValue(ref reader)) return new OptionDef { Value = doc.RootElement.GetRawText() };
            case JsonTokenType.StartObject:
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    string? Get(string name)
                    {
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ValueKind is JsonValueKind.Null ? null : p.Value.GetRawText();
                        return null;
                    }
                    return new OptionDef { Value = Get("value") ?? "", Label = Get("label"), Color = Get("color"), Confirm = Get("confirm") };
                }
            default:
                reader.Skip(); // null or anything odd: an option with no value, which Clean drops
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, OptionDef value, JsonSerializerOptions options)
    {
        if (value.Label is null && value.Color is null && value.Confirm is null) { writer.WriteStringValue(value.Value); return; }
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        if (value.Label is not null) writer.WriteString("label", value.Label);
        if (value.Color is not null) writer.WriteString("color", value.Color);
        if (value.Confirm is not null) writer.WriteString("confirm", value.Confirm);
        writer.WriteEndObject();
    }
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
    /// <summary>Value: one of <see cref="WidgetDef.Options"/>, shown highlighted. Picking one sends an action with it; the app confirms by updating.</summary>
    public const string Choice = "choice";
    /// <summary>Value: array of the <see cref="WidgetDef.Options"/> that are on. Clicking one sends an action with the whole new array; the app confirms by updating.</summary>
    public const string Flags = "flags";
    /// <summary>
    /// Value: an array of rows (objects keyed by column), or <c>{"rows":[..],"picked":"id"}</c>. A cell is a
    /// string, a number, or <c>{"text","color","dot","tip"}</c>. With <see cref="WidgetDef.Pick"/>, clicking a
    /// row sends an action with its <c>id</c>; the app confirms by setting <c>picked</c>.
    /// </summary>
    public const string Table = "table";

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

    /// <summary>Pick one of <paramref name="options"/> (strings, or <see cref="OptionDef"/> for a label, color or confirm).</summary>
    public SectionBuilder Choice(string id, string? label, IEnumerable<OptionDef> options, string? confirm = null, string? color = null, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Choice, Label = label, Options = options.ToList(), Confirm = confirm, Color = color, Span = span });

    public SectionBuilder Choice(string id, string? label, params string[] options)
        => Choice(id, label, options.Select(o => (OptionDef)o));

    /// <summary>Switch any of <paramref name="options"/> on or off.</summary>
    public SectionBuilder Flags(string id, string? label, IEnumerable<OptionDef> options, string? color = null, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Flags, Label = label, Options = options.ToList(), Color = color, Span = span });

    public SectionBuilder Flags(string id, string? label, params string[] options)
        => Flags(id, label, options.Select(o => (OptionDef)o));

    /// <summary>
    /// A table with <paramref name="columns"/> (keys, or <see cref="ColumnDef"/> for a label, alignment or
    /// width). With <paramref name="pick"/>, rows can be clicked; see <see cref="MonitorClient.OnPick"/>.
    /// </summary>
    public SectionBuilder Table(string id, string? label, IEnumerable<ColumnDef> columns, bool pick = false, string? confirm = null, int? span = null)
        => Add(new() { Id = id, Kind = WidgetKinds.Table, Label = label, Columns = columns.ToList(), Pick = pick ? true : null, Confirm = confirm, Span = span });

    public SectionBuilder Table(string id, string? label, params string[] columns)
        => Table(id, label, columns.Select(c => (ColumnDef)c));

    /// <summary>Anything not covered above.</summary>
    public SectionBuilder Widget(WidgetDef w) => Add(w);
}
