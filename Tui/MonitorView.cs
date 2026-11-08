using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SnmpMibBrowser.Tui;

/// <summary>
/// Polls the monitored OIDs (F11 screen). Owned by the main screen, so polling and history go on while the
/// monitor screen is closed. Items of one target are polled one after another, targets in parallel.
/// </summary>
public sealed class MonitorController(SnmpService snmp)
{
    public static readonly (string Label, int Seconds)[] Intervals = [("1 s", 1), ("2 s", 2), ("5 s", 5), ("10 s", 10), ("30 s", 30)];
    public static readonly (string Label, int Minutes)[] Histories = [("10 min", 10), ("30 min", 30), ("60 min", 60)];

    private CancellationTokenSource? _cts;
    private int _colorIndex;

    public List<MonitorItem> Items { get; } = [];
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan History { get; private set; } = TimeSpan.FromMinutes(10);
    public bool Running => _cts != null;
    public string Status { get; private set; } = "No items yet.";
    /// <summary>Raised on the UI thread after each poll and on every change.</summary>
    public event Action? Changed;

    public void SetHistory(TimeSpan history)
    {
        History = history;
        foreach (var item in Items) { item.History = history; item.Refresh(DateTime.Now); }
        Changed?.Invoke();
    }

    /// <summary>Adds an OID on a target with a snapshot of the SNMP settings. Returns a message for the status line.</summary>
    public string Add(string target, string oid, string name, SnmpOptions options)
    {
        oid = OidUtil.Normalize(oid);
        if (target.Length == 0) return "Enter a target host on the main screen first.";
        if (!OidUtil.IsValid(oid)) return $"Invalid OID: {oid}";
        if (Items.Any(i => i.Oid == oid && string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase))) return $"Already monitored: {oid} on {target}.";
        if (Items.Count >= MonitorItem.MaxItems) return $"At most {MonitorItem.MaxItems} items.";
        Items.Add(new MonitorItem(name.Length > 0 ? name : oid, target, oid, options, MonitorItem.Palette[_colorIndex++ % MonitorItem.Palette.Length]) { History = History });
        if (!Running) _ = RunAsync();
        Changed?.Invoke();
        return $"Monitoring {(name.Length > 0 ? name : oid)} on {target}.";
    }

    public void Remove(MonitorItem item) { Items.Remove(item); if (Items.Count == 0) Pause(); Changed?.Invoke(); }
    public void ClearHistory() { foreach (var item in Items) item.ClearHistory(); Changed?.Invoke(); }
    public void Pause() { _cts?.Cancel(); }
    public void Start() { if (!Running && Items.Count > 0) _ = RunAsync(); }

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Status = "Monitoring…";
        Changed?.Invoke();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.Now;
                var items = Items.ToList();
                await Task.WhenAll(items.GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase).Select(async g =>
                {
                    foreach (var item in g) await item.PollAsync(snmp, ct);
                }));
                var now = DateTime.Now;
                foreach (var item in Items) item.Refresh(now);
                Status = $"Last poll {now:HH:mm:ss} · {items.Count} items · every {Interval.TotalSeconds:0} s · history {History.TotalMinutes:0} min";
                Changed?.Invoke();
                var wait = Interval - (DateTime.Now - started);
                await Task.Delay(wait > TimeSpan.FromMilliseconds(200) ? wait : TimeSpan.FromMilliseconds(200), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose(); _cts = null;
            Status = Items.Count == 0 ? "No items yet." : "Paused.";
            Changed?.Invoke();
        }
    }
}

/// <summary>The monitor screen: add/remove items, interval and history, and a text graph per item.</summary>
public sealed class MonitorView : Runnable
{
    private readonly MonitorController _monitor;
    private readonly MonitorPanel _panel;
    private readonly Label _status;
    private readonly Action _changed;

    public MonitorView(MonitorController monitor, Func<string> currentTarget, Func<string, SnmpOptions> optionsFor, Func<string, string> nameFor)
    {
        _monitor = monitor;
        Title = "Monitor";
        Width = Dim.Fill(); Height = Dim.Fill();

        var oidLabel = new Label { Text = "_OID", X = 0, Y = 0 };
        var oid = new TextField { X = Pos.Right(oidLabel) + 1, Y = 0, Width = 28 };
        var add = new Button { Text = "_Add", X = Pos.Right(oid), Y = 0, ShadowStyle = null };
        var intervalLabel = new Label { Text = "Interval", X = Pos.Right(add) + 1, Y = 0 };
        var interval = Dialogs.Choice(MonitorController.Intervals.Select(i => i.Label), MonitorController.Intervals.First(i => i.Seconds == (int)monitor.Interval.TotalSeconds).Label, 6);
        interval.X = Pos.Right(intervalLabel) + 1; interval.Y = 0;
        var historyLabel = new Label { Text = "History", X = Pos.Right(interval) + 2, Y = 0 };
        var history = Dialogs.Choice(MonitorController.Histories.Select(h => h.Label), MonitorController.Histories.First(h => h.Minutes == (int)monitor.History.TotalMinutes).Label, 8);
        history.X = Pos.Right(historyLabel) + 1; history.Y = 0;
        var start = new Button { Text = "_Start", X = 0, Y = 1, ShadowStyle = null };
        var pause = new Button { Text = "_Pause", X = Pos.Right(start), Y = 1, ShadowStyle = null };
        var clear = new Button { Text = "_Clear history", X = Pos.Right(pause), Y = 1, ShadowStyle = null };
        var remove = new Button { Text = "_Remove", X = Pos.Right(clear), Y = 1, ShadowStyle = null };
        var hint = new Label { X = Pos.Right(remove) + 1, Y = 1, Width = Dim.Fill() };
        _status = new Label { X = 0, Y = 2, Width = Dim.Fill() };
        _panel = new MonitorPanel(monitor) { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        Add(oidLabel, oid, add, intervalLabel, interval, historyLabel, history, start, pause, clear, remove, hint, _status, _panel);

        void AddOid()
        {
            var target = currentTarget();
            _status.Text = monitor.Add(target, oid.Text, nameFor(OidUtil.Normalize(oid.Text)), optionsFor(target));
            oid.Text = "";
        }
        add.Accepting += (_, e) => { e.Handled = true; AddOid(); };
        oid.Accepting += (_, e) => { e.Handled = true; AddOid(); };
        start.Accepting += (_, e) => { e.Handled = true; monitor.Start(); };
        pause.Accepting += (_, e) => { e.Handled = true; monitor.Pause(); };
        clear.Accepting += (_, e) => { e.Handled = true; monitor.ClearHistory(); };
        remove.Accepting += (_, e) => { e.Handled = true; _panel.RemoveSelected(); };
        interval.TextChanged += (_, _) => monitor.Interval = TimeSpan.FromSeconds(MonitorController.Intervals.First(i => i.Label == interval.Text).Seconds);
        history.TextChanged += (_, _) => monitor.SetHistory(TimeSpan.FromMinutes(MonitorController.Histories.First(h => h.Label == history.Text).Minutes));

        _changed = () => { _status.Text = monitor.Status; _panel.SetNeedsDraw(); };
        IsRunningChanged += (_, e) =>
        {
            if (e.Value)
            {
                hint.Text = $"Target for new OIDs: {currentTarget()} (main screen)  ·  ↑↓ select, Del removes, Esc back";
                monitor.Changed += _changed;
                _changed();
            }
            else monitor.Changed -= _changed;
        };
    }
}

/// <summary>Three lines per item: name and OID, current value and statistics, and a block graph over the history window.</summary>
public sealed class MonitorPanel : View
{
    private const string Blocks = "▁▂▃▄▅▆▇█";
    private readonly MonitorController _monitor;
    private int _selected, _top;

    public MonitorPanel(MonitorController monitor)
    {
        _monitor = monitor;
        KeyDown += (_, key) =>
        {
            if (key == Key.CursorUp) { _selected = Math.Max(0, _selected - 1); SetNeedsDraw(); key.Handled = true; }
            else if (key == Key.CursorDown) { _selected = Math.Min(Math.Max(0, _monitor.Items.Count - 1), _selected + 1); SetNeedsDraw(); key.Handled = true; }
            else if (key == Key.Delete || key == Key.Backspace) { RemoveSelected(); key.Handled = true; }
        };
    }

    public void RemoveSelected()
    {
        if (_selected < 0 || _selected >= _monitor.Items.Count) return;
        _monitor.Remove(_monitor.Items[_selected]);
        _selected = Math.Max(0, Math.Min(_selected, _monitor.Items.Count - 1));
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetScheme().Normal;
        var width = Viewport.Width;
        var rowsVisible = Math.Max(1, Viewport.Height / 4);
        var items = _monitor.Items;
        if (items.Count == 0)
        {
            SetAttribute(normal);
            AddStr(0, 0, "No items yet. Enter an OID above (target = main screen host),");
            AddStr(0, 1, "or select rows in Results, Compare or Resources (or a tree node) and press Ctrl+W on the main screen.");
            return true;
        }
        _selected = Math.Min(_selected, items.Count - 1);
        if (_selected < _top) _top = _selected;
        if (_selected >= _top + rowsVisible) _top = _selected - rowsVisible + 1;
        for (var n = 0; n < rowsVisible && _top + n < items.Count; n++)
        {
            var item = items[_top + n];
            var y = n * 4;
            var color = new Attribute(new Color(item.Color), normal.Background);
            var mark = _top + n == _selected ? (HasFocus ? "▶ " : "> ") : "  ";
            SetAttribute(normal); AddStr(0, y, mark);
            SetAttribute(color); AddStr(2, y, "● ");
            SetAttribute(normal); AddStr(4, y, Fit($"{item.Name}   {item.Target}   {item.Oid}", width - 4));
            AddStr(4, y + 1, Fit($"{item.Current}   {item.CurrentDetail}   {item.Stats}   {item.Status}", width - 4));
            SetAttribute(color);
            AddStr(4, y + 2, Fit(Graph(item, width - 6), width - 4));
            SetAttribute(normal);
        }
        if (items.Count > rowsVisible) AddStr(Math.Max(0, width - 14), 0, $"[{_top + 1}-{Math.Min(items.Count, _top + rowsVisible)}/{items.Count}]");
        return true;
    }

    private static string Fit(string text, int width) => width <= 0 ? "" : text.Length > width ? text[..(width - 1)] + "…" : text.PadRight(width);

    // One column per time slot; the time axis grows with the data (at least one minute) until the history window is full.
    private static string Graph(MonitorItem item, int width)
    {
        if (width < 10) return "";
        var points = item.Points;
        if (points.Count == 0) return item.NonNumeric ? "No graph (not a numeric value)" : "…";
        var end = DateTime.Now;
        var span = Math.Clamp((end - points[0].Time).TotalMilliseconds, 60_000, item.History.TotalMilliseconds);
        var slots = new List<double>?[width];
        foreach (var (time, value) in points)
        {
            var x = (int)Math.Clamp(width - 1 - (end - time).TotalMilliseconds / span * (width - 1), 0, width - 1);
            (slots[x] ??= []).Add(value);
        }
        double lo = points.Min(p => p.Value), hi = points.Max(p => p.Value);
        var range = $" {item.Format(lo)}..{item.Format(hi)}";
        width = Math.Max(10, width - range.Length);
        if (slots.Length > width)
        {
            // Recompute the slots for the narrower graph.
            slots = new List<double>?[width];
            foreach (var (time, value) in points)
            {
                var x = (int)Math.Clamp(width - 1 - (end - time).TotalMilliseconds / span * (width - 1), 0, width - 1);
                (slots[x] ??= []).Add(value);
            }
        }
        // Between two samples the previous value is kept, so the graph is a continuous step line.
        List<double>? last = null;
        var firstSlot = Array.FindIndex(slots, s => s != null);
        for (var x = Math.Max(0, firstSlot); x < width && firstSlot >= 0; x++)
            if (slots[x] == null) slots[x] = last; else last = slots[x];
        var chars = new char[width];
        for (var x = 0; x < width; x++)
        {
            if (slots[x] is not { } values) { chars[x] = ' '; continue; }
            var v = values.Average();
            var level = hi - lo < 1e-9 ? 3 : (int)Math.Round((v - lo) / (hi - lo) * (Blocks.Length - 1));
            chars[x] = Blocks[Math.Clamp(level, 0, Blocks.Length - 1)];
        }
        return new string(chars) + range;
    }
}
