using System.Text.Json;

namespace RelentlessMonitor.Wire;

/// <summary>
/// How an <c>update</c> changes an <see cref="AppState"/>. The hub and every monitor apply updates
/// with this same code, so what a monitor shows is always what the hub holds.
/// </summary>
public static class StateMerge
{
    /// <summary>Applies <paramref name="update"/>'s values, appends and stream replacements to <paramref name="state"/>.</summary>
    /// <remarks>
    /// Ids the layout doesn't have are ignored (so an app can't grow the hub without bound); with no
    /// layout yet, up to <see cref="MaxUnlaidIds"/> ids are kept until one arrives.
    /// </remarks>
    public static void Apply(AppState state, Message update)
    {
        var widgets = Widgets(state.Layout);
        state.Values ??= [];
        state.Streams ??= [];

        if (update.Streams is not null)
            foreach (var (id, items) in update.Streams)
            {
                if (!Accepts(state, widgets, id)) continue;
                var list = new List<JsonElement>((items ?? []).Select(Clone));
                Trim(list, LimitOf(widgets, id));
                state.Streams[id] = list;
            }

        if (update.Values is not null)
            foreach (var (id, value) in update.Values)
            {
                if (!Accepts(state, widgets, id)) continue;
                if (IsStream(widgets, id)) AppendItems(state, widgets, id, [value]);
                else state.Values[id] = Clone(value);
            }

        if (update.Append is not null)
            foreach (var (id, items) in update.Append)
            {
                if (items is null || !Accepts(state, widgets, id)) continue;
                if (IsStream(widgets, id)) AppendItems(state, widgets, id, items);
                else if (items.Count > 0) state.Values[id] = Clone(items[^1]); // appending to a plain widget: keep the last
            }
    }

    /// <summary>With no layout, how many distinct ids an app may park values under.</summary>
    public const int MaxUnlaidIds = 500;

    /// <summary>
    /// After a layout change: drop values and history of widgets that left, values of widgets that
    /// became streams, and history beyond the new limits. The layout is stored as a cleaned copy.
    /// </summary>
    public static void Relayout(AppState state, LayoutDef? layout)
    {
        state.Layout = LayoutDef.Clean(layout);
        state.Values ??= [];
        state.Streams ??= [];
        var widgets = Widgets(state.Layout);
        if (widgets.Count == 0) return; // no layout yet: keep everything until one arrives
        foreach (var id in state.Streams.Keys.ToList())
        {
            if (!widgets.TryGetValue(id, out var w) || !WidgetKinds.IsStream(w.Kind)) state.Streams.Remove(id);
            else Trim(state.Streams[id], Protocol.StreamLimit(w));
        }
        foreach (var id in state.Values.Keys.ToList())
            if (!widgets.TryGetValue(id, out var w) || WidgetKinds.IsStream(w.Kind)) state.Values.Remove(id);
    }

    private static bool Accepts(AppState state, Dictionary<string, WidgetDef> widgets, string id)
        => widgets.Count > 0 ? widgets.ContainsKey(id)
            : state.Values.ContainsKey(id) || state.Streams.ContainsKey(id) || state.Values.Count + state.Streams.Count < MaxUnlaidIds;

    public static Dictionary<string, WidgetDef> Widgets(LayoutDef? layout)
    {
        var map = new Dictionary<string, WidgetDef>(StringComparer.Ordinal);
        if (layout is null) return map;
        foreach (var w in layout.AllWidgets)
            if (!string.IsNullOrEmpty(w.Id)) map[w.Id] = w;
        return map;
    }

    private static bool IsStream(Dictionary<string, WidgetDef> widgets, string id)
        => widgets.TryGetValue(id, out var w) && WidgetKinds.IsStream(w.Kind);

    private static int LimitOf(Dictionary<string, WidgetDef> widgets, string id)
        => widgets.TryGetValue(id, out var w) ? Protocol.StreamLimit(w) : Protocol.DefaultFeedLimit;

    private static void AppendItems(AppState state, Dictionary<string, WidgetDef> widgets, string id, IEnumerable<JsonElement> items)
    {
        if (!state.Streams.TryGetValue(id, out var list)) state.Streams[id] = list = [];
        foreach (var item in items) list.Add(Clone(item));
        Trim(list, LimitOf(widgets, id));
    }

    private static void Trim(List<JsonElement> list, int limit)
    {
        if (list.Count > limit) list.RemoveRange(0, list.Count - limit);
    }

    /// <summary>JsonElements from a parsed message point into that message's document; clone before keeping.</summary>
    private static JsonElement Clone(JsonElement e) => e.Clone();
}
