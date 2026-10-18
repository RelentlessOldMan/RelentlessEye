using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RelentlessMonitor.Ui.Panels;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Ui;

/// <summary>
/// The app list on the left, the selected app's panel on the right. Everything shown comes from
/// the apps' own layouts and values via the hub; this window knows nothing about any app.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>One app as the window knows it.</summary>
    private sealed class Entry(AppState state)
    {
        public AppState State = state;
        public AppPanel? Panel;
        public readonly ListBoxItem Item = new();
        public readonly Ellipse Dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        public readonly TextBlock Name = new() { FontSize = 13 };
        public readonly TextBlock Sub = new() { FontSize = 11, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis };
        public bool Dirty = true;
    }

    private readonly string _settingsPath = MonitorSettings.DefaultPath;
    private readonly MonitorSettings _settings;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _drain;
    private readonly DispatcherTimer _clock;

    // Viewer events arrive on a background thread; they land here and the UI drains them ~10x a second,
    // so a chatty app costs one redraw per tick rather than one per message.
    private readonly object _inLock = new();
    private readonly Dictionary<string, AppState?> _incoming = new(StringComparer.Ordinal); // null = removed
    private readonly HashSet<string> _wentOffline = new(StringComparer.Ordinal);
    private bool _reset;

    private volatile MonitorViewer? _viewer;
    private bool _hubUp;

    public MainWindow()
    {
        InitializeComponent();
        _settings = MonitorSettings.Load(_settingsPath);
        ApplyCommandLine(_settings);
        if (_settings.Width is > 300) Width = _settings.Width.Value;
        if (_settings.Height is > 200) Height = _settings.Height.Value;

        _drain = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateHeader(), Dispatcher);
        Loaded += (_, _) => { Connect(); _drain.Start(); _clock.Start(); };
        Closing += async (_, _) =>
        {
            _settings.Width = ActualWidth;
            _settings.Height = ActualHeight;
            _settings.Save(_settingsPath);
            if (_viewer is { } v) { _viewer = null; await v.DisposeAsync(); }
        };
        ShowEmpty();
    }

    /// <summary><c>--hub host[:port]</c> and <c>--token X</c> override the saved settings for this run.</summary>
    private static void ApplyCommandLine(MonitorSettings s)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] == "--hub") s.Hub = args[++i];
            else if (args[i] == "--token") s.Token = args[++i];
        }
    }

    // ---- hub connection ----

    private void Connect()
    {
        var old = _viewer;
        _viewer = null;
        if (old is not null) _ = old.DisposeAsync().AsTask();

        lock (_inLock) { _incoming.Clear(); _wentOffline.Clear(); _reset = true; }

        var v = new MonitorViewer(new HubOptions
        {
            Hub = string.IsNullOrWhiteSpace(_settings.Hub) ? null : _settings.Hub.Trim(),
            Token = string.IsNullOrWhiteSpace(_settings.Token) ? null : _settings.Token.Trim(),
            RetryInterval = TimeSpan.FromSeconds(2),
        }, "RelentlessMonitor");
        // A replaced viewer can still be delivering its last events; only the current one's count.
        bool Mine() => ReferenceEquals(_viewer, v);
        v.Reset += () => { if (Mine()) lock (_inLock) { _incoming.Clear(); _wentOffline.Clear(); _reset = true; } };
        v.AppChanged += s => { if (Mine()) Arrived(s); };
        v.Updated += (s, _) => { if (Mine()) Arrived(s); };
        v.AppGone += (key, removed) =>
        {
            if (!Mine()) return;
            lock (_inLock)
            {
                if (removed) { _incoming[key] = null; _wentOffline.Remove(key); }
                else if (_incoming.TryGetValue(key, out var s) && s is not null) s.Connected = false;
                else _wentOffline.Add(key);
            }
        };
        v.ConnectionChanged += up => Dispatcher.BeginInvoke(() => { if (_viewer == v) SetHub(up); });
        _viewer = v;
        SetHub(false);
        v.Start();
    }

    /// <summary>Newer than any "went offline" seen this tick: an app that dropped and came back shows online.</summary>
    private void Arrived(AppState s)
    {
        lock (_inLock) { _incoming[s.Key] = s; _wentOffline.Remove(s.Key); }
    }

    private string HubName => string.IsNullOrWhiteSpace(_settings.Hub)
        ? Environment.GetEnvironmentVariable(Protocol.HubEnvVar) is { Length: > 0 } env ? env : $"this machine (port {Protocol.DefaultPort})"
        : _settings.Hub!;

    private void SetHub(bool up)
    {
        _hubUp = up;
        HubDot.Fill = up ? Palette.Brush("ok") : Palette.Brush("warn");
        if (up) HubText.Text = $"hub: {HubName}";
        else
        {
            var why = _viewer?.LastRejection;
            HubText.Text = why is null ? $"hub not reachable at {HubName} — retrying" : $"hub refused: {why}";
        }
        // Panels stay up while the hub is away, but nothing on them can be clicked.
        foreach (var e in _entries.Values) e.Dirty = true;
        ShowEmpty();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HubBox.Text = _settings.Hub ?? "";
        TokenBox.Password = _settings.Token ?? "";
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsPanel.Visibility == Visibility.Visible) HubBox.Focus();
    }

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.Hub = HubBox.Text.Trim();
        _settings.Token = TokenBox.Password.Trim();
        _settings.Save(_settingsPath);
        SettingsPanel.Visibility = Visibility.Collapsed;
        Connect();
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    // ---- applying what the hub sent ----

    private void Drain()
    {
        // One bad message must not stop the timer (an exception here would end it for good).
        try { DrainCore(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"drain: {ex}"); }
    }

    private void DrainCore()
    {
        bool reset;
        List<KeyValuePair<string, AppState?>> changes;
        List<string> offline;
        lock (_inLock)
        {
            reset = _reset;
            _reset = false;
            changes = [.. _incoming];
            offline = [.. _wentOffline];
            _incoming.Clear();
            _wentOffline.Clear();
        }

        bool listChanged = false;
        if (reset)
        {
            // Keep the panels the user can see, but drop apps the fresh hub doesn't send back.
            var keep = changes.Where(c => c.Value is not null).Select(c => c.Key).ToHashSet();
            foreach (var key in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
                listChanged |= Remove(key);
        }
        foreach (var (key, state) in changes)
        {
            if (state is null) { listChanged |= Remove(key); continue; }
            if (_entries.TryGetValue(key, out var e)) { e.State = state; e.Dirty = true; }
            else
            {
                _entries[key] = e = new Entry(state);
                BuildItem(e);
                listChanged = true;
            }
        }
        foreach (var key in offline)
            if (_entries.TryGetValue(key, out var e)) { e.State.Connected = false; e.Dirty = true; }

        if (listChanged) SortList();
        foreach (var e in _entries.Values.Where(e => e.Dirty).ToList())
        {
            try { Refresh(e); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"refresh {e.State.Key}: {ex}"); }
        }
    }

    private bool Remove(string key)
    {
        if (!_entries.Remove(key, out var e)) return false;
        AppList.Items.Remove(e.Item);
        if (PanelHost.Content == e.Panel) PanelHost.Content = null;
        return true;
    }

    private void BuildItem(Entry e)
    {
        var text = new StackPanel();
        text.Children.Add(e.Name);
        text.Children.Add(e.Sub);
        var row = new DockPanel();
        DockPanel.SetDock(e.Dot, Dock.Left);
        row.Children.Add(e.Dot);
        row.Children.Add(text);
        e.Item.Content = row;
        e.Item.Tag = e;
        AppList.Items.Add(e.Item);
        if (AppList.SelectedItem is null && (_settings.Selected is null || _settings.Selected == e.State.Key))
            AppList.SelectedItem = e.Item;
    }

    private void SortList()
    {
        var selected = AppList.SelectedItem;
        var ordered = _entries.Values
            .OrderBy(e => e.State.Layout?.Title ?? e.State.App, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.State.Key, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Item).ToList();
        if (ordered.SequenceEqual(AppList.Items.Cast<ListBoxItem>())) return;
        AppList.SelectionChanged -= AppList_SelectionChanged;
        AppList.Items.Clear();
        foreach (var item in ordered) AppList.Items.Add(item);
        AppList.SelectedItem = selected;
        AppList.SelectionChanged += AppList_SelectionChanged;
        if (AppList.SelectedItem is null && AppList.Items.Count > 0) AppList.SelectedIndex = 0;
    }

    private void Refresh(Entry e)
    {
        e.Dirty = false;
        var s = e.State;
        bool live = s.Connected && _hubUp;
        e.Dot.Fill = live ? Palette.Brush("ok") : Palette.Track;
        e.Name.Text = s.Layout?.Title ?? s.App;
        e.Name.Foreground = live ? Palette.Fg : Palette.Dim;
        e.Sub.Text = string.Join(" · ", new[] { s.Machine, s.Instance, s.Connected ? null : "offline" }.Where(x => !string.IsNullOrEmpty(x)));
        e.Item.ToolTip = s.Key;

        if (Selected == e)
        {
            e.Panel ??= new AppPanel(s.Key, (widget, value) => SendAction(s.Key, widget, value));
            // While the hub is down every button would fail, so show the app as offline.
            e.Panel.Render(live ? s : WithConnected(s, false));
            if (PanelHost.Content != e.Panel) PanelHost.Content = e.Panel;
            UpdateHeader();
        }
        ShowEmpty();
    }

    private static AppState WithConnected(AppState s, bool connected) => new()
    {
        Key = s.Key, App = s.App, Instance = s.Instance, Machine = s.Machine, Version = s.Version,
        Connected = connected, ConnectedAt = s.ConnectedAt, LastSeen = s.LastSeen,
        Layout = s.Layout, Provides = s.Provides, Values = s.Values, Streams = s.Streams,
    };

    private Task<HubResponse> SendAction(string key, string widget, object? value)
        => _viewer is { } v ? v.ActionAsync(key, widget, value) : Task.FromResult(HubResponse.Fail("not connected to a hub"));

    // ---- selection and header ----

    private Entry? Selected => (AppList.SelectedItem as ListBoxItem)?.Tag as Entry;

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } sel)
        {
            _settings.Selected = sel.State.Key;
            PanelHost.ScrollToTop();
            Refresh(sel);
        }
        else
        {
            PanelHost.Content = null;
            UpdateHeader();
            ShowEmpty();
        }
    }

    private void UpdateHeader()
    {
        if (Selected is not { } e) { AppHeader.Visibility = Visibility.Collapsed; return; }
        var s = e.State;
        AppHeader.Visibility = Visibility.Visible;
        AppTitle.Text = s.Layout?.Title ?? s.App;
        OfflineBadge.Visibility = s.Connected ? Visibility.Collapsed : Visibility.Visible;
        ForgetButton.Visibility = s.Connected || !_hubUp ? Visibility.Collapsed : Visibility.Visible;

        var parts = new List<string> { s.Key };
        if (!string.IsNullOrEmpty(s.Version)) parts.Add("v" + s.Version);
        parts.Add(s.Connected ? $"connected {Ago(s.ConnectedAt)}" : $"last seen {Ago(s.LastSeen)}");
        if (s.Provides is { Count: > 0 } p) parts.Add("provides " + string.Join(", ", p.Select(c => c.Name)));
        AppSub.Text = string.Join("   ·   ", parts);
    }

    private static string Ago(DateTimeOffset t)
    {
        if (t == default) return "";
        var d = DateTimeOffset.Now - t;
        if (d < TimeSpan.FromSeconds(5)) return "just now";
        if (d < TimeSpan.FromMinutes(1)) return $"{d.TotalSeconds:0}s ago";
        if (d < TimeSpan.FromHours(1)) return $"{d.TotalMinutes:0}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h {d.Minutes}m ago";
        return t.LocalDateTime.ToString("g");
    }

    private void ShowEmpty()
    {
        AppCount.Text = _entries.Count switch
        {
            0 => "NO APPS",
            var n => $"{n} APP{(n == 1 ? "" : "S")} · {_entries.Values.Count(x => x.State.Connected)} ONLINE",
        };
        if (_entries.Count > 0 && Selected is not null) { EmptyText.Visibility = Visibility.Collapsed; return; }
        EmptyText.Visibility = Visibility.Visible;
        EmptyText.Text = !_hubUp
            ? "Waiting for the hub.\n\nStart RelentlessMonitor.Hub on this machine, or point Hub settings at another one."
            : _entries.Count == 0
                ? "Connected. No apps yet.\n\nApps appear here when they connect with the RelentlessMonitor client."
                : "Pick an app.";
    }

    private async void ForgetButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel || _viewer is not { } v) return;
        var r = await v.ForgetAsync(sel.State.Key);
        if (!r.Ok) MessageBox.Show(this, r.Error, "Forget", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
