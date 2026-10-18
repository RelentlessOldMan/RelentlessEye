using System.Windows.Media;

namespace RelentlessEye.Ui.Panels;

/// <summary>Turns the color names apps send (<c>green</c>, <c>#22c55e</c>) into brushes that suit the dark theme.</summary>
internal static class Palette
{
    private static readonly Dictionary<string, Color> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"] = C("#22c55e"), ["red"] = C("#ef4444"), ["orange"] = C("#f97316"), ["yellow"] = C("#eab308"),
        ["amber"] = C("#f59e0b"), ["blue"] = C("#3b82f6"), ["cyan"] = C("#06b6d4"), ["teal"] = C("#14b8a6"),
        ["purple"] = C("#a855f7"), ["pink"] = C("#ec4899"), ["gray"] = C("#6b7280"), ["grey"] = C("#6b7280"),
        ["white"] = C("#e6e8ec"), ["black"] = C("#111111"),
        // Meaningful aliases, so apps can say what they mean instead of a hue.
        ["ok"] = C("#22c55e"), ["good"] = C("#22c55e"), ["warn"] = C("#f59e0b"), ["warning"] = C("#f59e0b"),
        ["error"] = C("#ef4444"), ["bad"] = C("#ef4444"), ["info"] = C("#3b82f6"), ["off"] = C("#6b7280"),
    };

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(C("#3b82f6")));
    public static readonly SolidColorBrush Dim = Freeze(new SolidColorBrush(C("#8b919c")));
    public static readonly SolidColorBrush Fg = Freeze(new SolidColorBrush(C("#e6e8ec")));
    public static readonly SolidColorBrush Track = Freeze(new SolidColorBrush(C("#2c3039")));

    /// <summary>The brush for <paramref name="name"/>, or <paramref name="fallback"/> when missing or unrecognized.</summary>
    public static SolidColorBrush Brush(string? name, SolidColorBrush? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback ?? Accent;
        if (Cache.TryGetValue(name, out var b)) return b;
        Color? c = Named.TryGetValue(name, out var n) ? n : TryParse(name);
        if (c is null) return fallback ?? Accent;
        var brush = Freeze(new SolidColorBrush(c.Value));
        if (Cache.Count < 512) Cache[name] = brush; // apps choose these strings: don't let them grow the cache forever
        return brush;
    }

    /// <summary>
    /// Only <c>#rgb</c>/<c>#argb</c>/<c>#rrggbb</c>/<c>#aarrggbb</c> and plain color names: WPF's converter
    /// also takes <c>sc#</c> and <c>ContextColor</c> profile paths, which an app must not be able to make us open.
    /// </summary>
    internal static Color? TryParse(string s)
    {
        s = s.Trim();
        bool hex = s.Length is 4 or 5 or 7 or 9 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
        bool word = s.Length is > 0 and <= 32 && s.All(char.IsAsciiLetter);
        if (!hex && !word) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch (Exception) { return null; }
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
