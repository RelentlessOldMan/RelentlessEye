using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Ui.Panels;

/// <summary>Draws one app's panel from the layout the app sent: sections of widgets on a grid.</summary>
internal sealed class AppPanel : StackPanel
{
    private readonly List<WidgetView> _widgets = [];
    private string _layoutJson = "";
    private bool? _online;

    public AppPanel(string key, ActionSender send)
    {
        Key = key;
        Send = send;
        Margin = new Thickness(16, 8, 16, 16);
    }

    public string Key { get; }
    private ActionSender Send { get; }

    private static string LayoutJson(LayoutDef layout)
    {
        // Layouts that came over the wire always serialize; this guards one set directly in code.
        try { return JsonSerializer.Serialize(LayoutDef.Clean(layout), Protocol.Json); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException) { return ""; }
    }

    public void Render(AppState state)
    {
        var json = state.Layout is null ? "" : LayoutJson(state.Layout);
        if (json != _layoutJson)
        {
            _layoutJson = json;
            Build(state.Layout);
            _online = null;
        }
        if (_online != state.Connected)
        {
            _online = state.Connected;
            foreach (var w in _widgets) w.SetOnline(state.Connected);
        }
        foreach (var w in _widgets)
        {
            // One widget choking on an odd value must not blank the rest of the panel.
            try { w.Render(state); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"render {Key}: {ex}"); }
        }
    }

    private void Build(LayoutDef? layout)
    {
        Children.Clear();
        _widgets.Clear();
        if (layout is null || layout.Sections is not { Count: > 0 })
        {
            Children.Add(new TextBlock { Text = "This app hasn't sent a layout.", Foreground = Palette.Dim, Margin = new Thickness(0, 12, 0, 0) });
            return;
        }
        foreach (var section in layout.Sections) if (section is not null) Children.Add(BuildSection(section));
    }

    private Border BuildSection(SectionDef section)
    {
        var body = new StackPanel();
        if (!string.IsNullOrEmpty(section.Title))
            body.Children.Add(new TextBlock { Text = section.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });

        int columns = Math.Clamp(section.Columns ?? 2, 1, 12);
        var grid = new Grid();
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Flow left to right, wrapping when a widget's span doesn't fit the rest of the row.
        int row = -1, col = columns;
        foreach (var def in section.Widgets ?? [])
        {
            if (def is null) continue;
            int span = Math.Clamp(def.Span ?? (WidgetKinds.IsStream(def.Kind) && columns > 1 ? columns : 1), 1, columns);
            if (col + span > columns)
            {
                row++;
                col = 0;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var view = WidgetView.Create(def, Send);
            Grid.SetRow(view, row);
            Grid.SetColumn(view, col);
            Grid.SetColumnSpan(view, span);
            grid.Children.Add(view);
            _widgets.Add(view);
            col += span;
        }
        body.Children.Add(grid);

        return new Border
        {
            Child = body,
            Background = (Brush)Application.Current.Resources["Card"],
            BorderBrush = (Brush)Application.Current.Resources["CardEdge"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 0, 2),
            Margin = new Thickness(0, 0, 0, 12),
        };
    }
}
