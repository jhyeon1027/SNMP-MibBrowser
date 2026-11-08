using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnmpMibBrowser;

// Polls a set of OIDs at a fixed interval and draws a small graph per OID.
// Runs independently of the main window's single-query rule, like the multi-target window.
public partial class MonitorWindow : Window
{
    private const int MaxItems = MonitorItem.MaxItems;

    private readonly SnmpService _snmp;
    private readonly Func<string, SnmpOptions> _optionsFor;
    private readonly Func<string> _currentTarget;
    private readonly bool _de;
    private readonly ObservableCollection<MonitorItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _colorIndex;

    public MonitorWindow(SnmpService snmp, Func<string, SnmpOptions> optionsFor, Func<string> currentTarget, string language)
    {
        InitializeComponent();
        _snmp = snmp; _optionsFor = optionsFor; _currentTarget = currentTarget; _de = language == "de";
        ItemsList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Localize();
        Activated += (_, _) => AddHintText.Text = Tr($"Ziel: {_currentTarget()} (aus dem Hauptfenster)", $"Target: {_currentTarget()} (from the main window)");
        Closing += (_, _) => _cts?.Cancel();
    }

    private string Tr(string de, string en) => _de ? de : en;
    private TimeSpan Interval => TimeSpan.FromSeconds(int.Parse((string)((ComboBoxItem)IntervalBox.SelectedItem).Tag));
    private TimeSpan History => TimeSpan.FromMinutes(int.Parse((string)((ComboBoxItem)HistoryBox.SelectedItem).Tag));

    /// <summary>Adds an OID on a target with a snapshot of the SNMP settings, so later main-window changes do not affect it.</summary>
    public void AddItem(string target, string oid, string name)
    {
        oid = oid.Trim().TrimStart('.');
        if (target.Length == 0 || !oid.Split('.').All(p => p.Length > 0 && uint.TryParse(p, out _))) { StatusLine.Text = Tr($"Ungültige OID: {oid}", $"Invalid OID: {oid}"); return; }
        if (_items.Any(i => i.Oid == oid && string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase))) { StatusLine.Text = Tr("Bereits in der Liste.", "Already in the list."); return; }
        if (_items.Count >= MaxItems) { StatusLine.Text = Tr($"Höchstens {MaxItems} Einträge.", $"At most {MaxItems} items."); return; }
        _items.Add(new MonitorItem(name.Length > 0 ? name : oid, target, oid, _optionsFor(target), MonitorItem.Palette[_colorIndex++ % MonitorItem.Palette.Length]) { History = History });
        StatusLine.Text = Tr($"Hinzugefügt: {name} ({target})", $"Added: {name} ({target})");
        if (_cts == null) Start();
    }

    private void Add_Click(object sender, RoutedEventArgs e) { AddItem(_currentTarget(), AddOidBox.Text, ""); AddOidBox.Clear(); }
    private void AddOidBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) Add_Click(sender, e); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is MonitorItem item) _items.Remove(item); }
    private void Start_Click(object sender, RoutedEventArgs e) => Start();
    private void Pause_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void ClearHistory_Click(object sender, RoutedEventArgs e) { foreach (var item in _items) item.ClearHistory(); }
    private void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        foreach (var item in _items) { item.History = History; item.Refresh(DateTime.Now); }
    }

    private async void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false; PauseButton.IsEnabled = true;
        StatusLine.Text = Tr("Überwachung läuft…", "Monitoring…");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.Now;
                var items = _items.ToList();
                // Items of one target are polled one after another, targets in parallel:
                // a host that does not answer only delays its own graphs.
                await Task.WhenAll(items.GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase).Select(g => PollAsync(g.ToList(), ct)));
                var now = DateTime.Now;
                foreach (var item in _items) item.Refresh(now);
                StatusLine.Text = Tr($"Letzte Abfrage {now:HH:mm:ss} · {items.Count} Einträge · alle {Interval.TotalSeconds:0} s",
                                     $"Last poll {now:HH:mm:ss} · {items.Count} items · every {Interval.TotalSeconds:0} s");
                var wait = Interval - (DateTime.Now - started);
                await Task.Delay(wait > TimeSpan.FromMilliseconds(200) ? wait : TimeSpan.FromMilliseconds(200), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose(); _cts = null;
            StartButton.IsEnabled = true; PauseButton.IsEnabled = false;
            StatusLine.Text = Tr("Pausiert.", "Paused.");
        }
    }

    private async Task PollAsync(List<MonitorItem> items, CancellationToken ct)
    {
        foreach (var item in items) await item.PollAsync(_snmp, ct);
    }

    private void Localize()
    {
        Title = Tr("Überwachung", "Monitor");
        IntervalLabel.Text = Tr("Intervall", "Interval"); HistoryLabel.Text = Tr("Verlauf", "History");
        StartButton.Content = Tr("▶  Start", "▶  Start"); PauseButton.Content = Tr("⏸  Pause", "⏸  Pause"); ClearHistoryButton.Content = Tr("⌫  Verlauf löschen", "⌫  Clear history");
        AddButton.Content = Tr("+  Hinzufügen", "+  Add");
        EmptyText.Text = Tr("Noch keine Einträge. OID oben eingeben oder in Ergebnissen, WALK-Vergleich, Ressourcen bzw. im MIB-Baum per Rechtsklick „Zur Überwachung hinzufügen“ wählen.",
                            "No items yet. Enter an OID above, or right-click a row in Results, WALK compare, Resources or the MIB tree and choose \"Add to monitor\".");
    }
}

/// <summary>Line graph of one MonitorItem over its history window, scaled to the item's own min/max.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(MonitorItem), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Sparkline)d).OnItemChanged(e)));

    public MonitorItem? Item { get => (MonitorItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    private void OnItemChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MonitorItem old) old.Updated -= InvalidateVisual;
        if (e.NewValue is MonitorItem item) item.Updated += InvalidateVisual;
    }

    private static readonly Dictionary<string, Brush> Brushes_ = [];
    private static Brush BrushFor(string color)
    {
        if (Brushes_.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return Brushes_[color] = brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (Item is not MonitorItem item || w < 20 || h < 20) return;
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        var subtle = TryFindResource("SubtleTextBrush") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(Brushes.Transparent, new Pen(border, 1), new Rect(0.5, 0.5, w - 1, h - 1));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, subtle, dpi);
        var points = item.Points;
        if (points.Count == 0)
        {
            if (item.NonNumeric) dc.DrawText(Label("No graph (not a numeric value)"), new Point(6, h / 2 - 7));
            return;
        }
        var min = points.Min(p => p.Value); var max = points.Max(p => p.Value);
        var lo = min; var hi = max;
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }   // flat line in the middle instead of a division by zero
        // The time axis grows with the data (at least one minute) until the history window is full, then scrolls.
        var end = DateTime.Now;
        var span = Math.Clamp((end - points[0].Time).TotalMilliseconds, 60_000, item.History.TotalMilliseconds);
        Point ToPoint((DateTime Time, double Value) p) =>
            new(Math.Clamp(w - (end - p.Time).TotalMilliseconds / span * w, 0, w), h - 6 - (p.Value - lo) / (hi - lo) * (h - 12));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToPoint(points[0]), false, false);
            foreach (var p in points.Skip(1)) ctx.LineTo(ToPoint(p), true, true);
        }
        geometry.Freeze();
        var brush = BrushFor(item.Color);
        dc.DrawGeometry(null, new Pen(brush, 1.6), geometry);
        if (points.Count == 1) dc.DrawEllipse(brush, null, ToPoint(points[0]), 2.5, 2.5);
        dc.DrawText(Label(item.Format(max)), new Point(4, 2));
        var bottom = Label(item.Format(min)); dc.DrawText(bottom, new Point(4, h - bottom.Height - 1));
    }
}
