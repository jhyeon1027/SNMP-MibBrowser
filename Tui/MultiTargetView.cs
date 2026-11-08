#pragma warning disable CS0618 // TextView (multi-line inputs)
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SnmpMibBrowser.Tui;

/// <summary>
/// GETs the same OIDs from many targets in parallel (F9). Port, timeout, retries and SNMPv3 credentials come from the
/// main screen; version and community can be changed here. A run keeps going when the screen is closed (Esc).
/// </summary>
public sealed class MultiTargetView : Runnable
{
    private readonly SnmpService _snmp;
    private readonly Func<SnmpOptions> _mainOptions;
    private readonly UserSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action<string> _log;
    private readonly TextView _targets, _customOids;
    private readonly CheckBox[] _presets;
    private readonly DropDownList _version;
    private readonly TextField _community;
    private readonly Label _communityExtra, _progress, _summary;
    private readonly CheckBox _failedOnly;
    private readonly TableView _table;
    private readonly List<MultiTargetRow> _rows = [];
    private List<MultiTargetRow> _visible = [];
    private List<(string Header, string Oid)>? _lastOids;
    private List<string> _communities;
    private CancellationTokenSource? _cts;
    private bool _sortByIp;

    /// <summary>Set when the user picked "Use as target"; the main screen reads it after the screen closes.</summary>
    public string? SelectedTarget { get; set; }

    public MultiTargetView(SnmpService snmp, Func<SnmpOptions> mainOptions, UserSettings settings, Action saveSettings, Action<string> log)
    {
        _snmp = snmp; _mainOptions = mainOptions; _settings = settings; _saveSettings = saveSettings; _log = log;
        Title = "Multi-target query";
        Width = Dim.Fill(); Height = Dim.Fill();

        var targetsLabel = new Label { Text = "_Targets (one per line, ranges like 10.1.1.10-20, # = comment)", X = 0, Y = 0 };
        _targets = new TextView { X = 0, Y = 1, Width = Dim.Percent(50), Height = 7, Text = settings.MultiTargets };
        var oidsLabel = new Label { Text = "OIDs to GET", X = Pos.Right(_targets) + 2, Y = 0 };
        _presets = MultiTargetQuery.Presets.Select((p, i) => new CheckBox
        {
            Text = p.Name, Data = p.Oid, X = Pos.Right(_targets) + 2 + (i % 3) * 15, Y = 1 + i / 3,
            Value = (settings.MultiPresetOids?.Contains(p.Oid) ?? p.Checked) ? CheckState.Checked : CheckState.UnChecked
        }).ToArray();
        var customLabel = new Label { Text = "C_ustom OIDs (one per line)", X = Pos.Right(_targets) + 2, Y = 3 };
        _customOids = new TextView { X = Pos.Right(_targets) + 2, Y = 4, Width = Dim.Fill(), Height = 4, Text = settings.MultiCustomOids };

        var main = mainOptions();
        _communities = main.Communities is { Count: > 0 } list ? [.. list] : [main.Community];
        var versionLabel = new Label { Text = "Version", X = 0, Y = 9 };
        _version = Dialogs.Choice(ConnectionDialog.Versions, main.Version, 6);
        _version.X = Pos.Right(versionLabel) + 1; _version.Y = 9;
        var communityLabel = new Label { Text = "Community", X = Pos.Right(_version) + 2, Y = 9 };
        _community = new TextField { X = Pos.Right(communityLabel) + 1, Y = 9, Width = 14, Text = _communities[0] };
        _communityExtra = new Label { X = Pos.Right(_community) + 1, Y = 9, Width = 4 };
        var run = new Button { Text = "_Run", X = Pos.Right(_communityExtra) + 1, Y = 9, ShadowStyle = null };
        var stop = new Button { Text = "Sto_p", X = Pos.Right(run), Y = 9, ShadowStyle = null };
        var rerun = new Button { Text = "Re-run _failed", X = Pos.Right(stop), Y = 9, ShadowStyle = null };
        var clear = new Button { Text = "C_lear", X = Pos.Right(rerun), Y = 9, ShadowStyle = null };
        var sort = new Button { Text = "Sort by _IP", X = Pos.Right(clear), Y = 9, ShadowStyle = null };
        _failedOnly = new CheckBox { Text = "Failed _only", X = Pos.Right(sort) + 1, Y = 9 };
        _summary = new Label { X = 0, Y = 10, Width = Dim.Fill() };
        _progress = new Label { X = 0, Y = 11, Width = Dim.Fill(), Text = "Enter targets and OIDs, then Run (Alt+R). Enter on a row: details / use as target. Esc: back." };
        _table = new TableView
        {
            X = 0, Y = 12, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, MultiSelect = true, MaxCellWidth = 40,
            Table = new GridTableSource(["Target", "Status"], [])
        };

        Add(targetsLabel, _targets, oidsLabel);
        Add(_presets);
        Add(customLabel, _customOids, versionLabel, _version, communityLabel, _community, _communityExtra, run, stop, rerun, clear, sort, _failedOnly, _summary, _progress, _table);

        run.Accepting += (_, e) => { e.Handled = true; _ = RunAsync(false); };
        stop.Accepting += (_, e) => { e.Handled = true; _cts?.Cancel(); };
        rerun.Accepting += (_, e) => { e.Handled = true; _ = RunAsync(true); };
        clear.Accepting += (_, e) => { e.Handled = true; Clear(); };
        sort.Accepting += (_, e) => { e.Handled = true; _sortByIp = !_sortByIp; Refresh(); };
        _failedOnly.ValueChanged += (_, _) => Refresh();
        _community.TextChanged += (_, _) => { _communities[0] = _community.Text; UpdateCommunity(); };
        _version.TextChanged += (_, _) => UpdateCommunity();
        _table.Accepting += (_, e) => { e.Handled = true; ShowRow(); };
        IsRunningChanged += (_, e) => { if (e.Value) UpdateCommunity(); else SaveInputs(); };
        UpdateCommunity();
    }

    private void UpdateCommunity()
    {
        var v3 = _version.Text == "v3";
        _community.Enabled = !v3;
        var extra = _communities.Count(c => c.Length > 0) - 1;
        _communityExtra.Text = v3 || extra <= 0 ? "" : $"+{extra}";
        var o = _mainOptions();
        _summary.Text = $"From the main screen: port {o.Port} · timeout {o.Timeout} ms · retries {o.Retries}" +
                        (v3 ? $" · v3 user {o.User} ({o.AuthType}/{o.PrivacyType})" : "") + $" · {MultiTargetQuery.MaxParallel} targets in parallel";
    }

    private SnmpOptions OptionsFor(string host)
    {
        var communities = _communities.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return _mainOptions() with { Host = host, Version = _version.Text, Community = communities.FirstOrDefault() ?? "", Communities = communities };
    }

    private void SaveInputs()
    {
        _settings.MultiTargets = _targets.Text;
        _settings.MultiPresetOids = _presets.Where(p => p.Value == CheckState.Checked).Select(p => (string)p.Data!).ToList();
        _settings.MultiCustomOids = _customOids.Text;
        _saveSettings();
    }

    private List<(string Header, string Oid)>? ReadOids()
    {
        var oids = _presets.Where(p => p.Value == CheckState.Checked).Select(p => (p.Text, (string)p.Data!)).ToList();
        foreach (var raw in _customOids.Text.Split('\n'))
        {
            var oid = OidUtil.Normalize(raw);
            if (oid.Length == 0) continue;
            if (!OidUtil.IsValid(oid)) { _progress.Text = $"Invalid OID: {oid}"; return null; }
            oids.Add((oid, oid));
        }
        return oids.DistinctBy(x => x.Item2).ToList();
    }

    private async Task RunAsync(bool failedOnly)
    {
        if (_cts != null) { _progress.Text = "A run is in progress. Stop it first (Alt+P)."; return; }
        List<MultiTargetRow> rows;
        List<(string Header, string Oid)> oids;
        if (failedOnly)
        {
            rows = _rows.Where(r => r.Failed).ToList();
            if (_lastOids == null || rows.Count == 0) { _progress.Text = "No failed targets."; return; }
            oids = _lastOids;
            foreach (var row in rows) row.Reset();
        }
        else
        {
            var targets = MultiTargetQuery.ParseTargets(_targets.Text.Replace("\r", ""));
            if (ReadOids() is not { } read) return;
            oids = read;
            if (targets.Count == 0 || oids.Count == 0) { _progress.Text = "Enter targets and at least one OID."; return; }
            if (targets.Count > MultiTargetQuery.MaxTargets) { _progress.Text = $"At most {MultiTargetQuery.MaxTargets} targets."; return; }
            SaveInputs();
            _lastOids = oids;
            _rows.Clear();
            _rows.AddRange(targets.Select(t => new MultiTargetRow(t, oids.Count)));
            rows = [.. _rows];
        }
        Refresh();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var done = 0;
        var gate = new SemaphoreSlim(MultiTargetQuery.MaxParallel);
        // Rows change from many queries; redraw a few times per second instead of per value.
        var timer = App!.AddTimeout(TimeSpan.FromMilliseconds(300), () => { _table.Update(); _progress.Text = $"{done}/{rows.Count} done…  (Alt+P stops)"; return true; });
        var oidList = oids.Select(o => o.Oid).ToList();
        async Task RunOne(MultiTargetRow row)
        {
            await gate.WaitAsync(ct);
            try { await MultiTargetQuery.QueryAsync(_snmp, OptionsFor(row.Target), row, oidList, Lang.English, ct); }
            finally { gate.Release(); done++; }
        }
        _log($"Multi-target: {rows.Count} targets, {oids.Count} OIDs{(failedOnly ? " (re-run failed)" : "")}.");
        try { await Task.WhenAll(rows.Select(RunOne)); }
        catch (OperationCanceledException) { }
        finally
        {
            App?.RemoveTimeout(timer);
            foreach (var row in rows.Where(r => r.Status.Length == 0)) row.SetResult("Cancelled", true, "", "");
            _cts.Dispose(); _cts = null;
            var failed = _rows.Count(r => r.Failed);
            _progress.Text = $"{_rows.Count - failed} OK, {failed} failed.  Enter on a row: details / use as target.";
            _log($"Multi-target finished: {_rows.Count - failed} OK, {failed} failed.");
            Refresh();
        }
    }

    private void Clear()
    {
        if (_cts != null) { _progress.Text = "Stop the run first."; return; }
        _rows.Clear(); _lastOids = null;
        _progress.Text = "Results cleared (targets and OIDs are kept).";
        Refresh();
    }

    private void Refresh()
    {
        IEnumerable<MultiTargetRow> rows = _rows;
        if (_failedOnly.Value == CheckState.Checked) rows = rows.Where(r => r.Failed);
        if (_sortByIp) rows = rows.OrderBy(r => r.SortKey, StringComparer.Ordinal);
        _visible = rows.ToList();
        var columns = new List<(string, Func<MultiTargetRow, string>)> { ("Target", r => r.Target), ("Status", r => r.Status), ("Community", r => r.Community), ("ms", r => r.Duration.Replace(" ms", "")) };
        if (_lastOids != null)
            for (var i = 0; i < _lastOids.Count; i++)
            {
                var index = i;
                columns.Add((_lastOids[index].Header, r => r.Values[index]));
            }
        _table.Table = new RowTableSource<MultiTargetRow>(_visible, [.. columns]);
        _table.Update();
    }

    private void ShowRow()
    {
        if (_table.SelectedRows() is not [var i, ..] || i >= _visible.Count) return;
        var row = _visible[i];
        var lines = new List<string> { $"Target:     {row.Target}", $"Status:     {row.Status}", $"Community:  {row.Community}", $"Duration:   {row.Duration}" };
        if (row.Detail.Length > 0) lines.AddRange(["", row.Detail]);
        if (_lastOids != null) { lines.Add(""); lines.AddRange(_lastOids.Select((o, n) => $"{o.Header} = {row.Values[n]}")); }
        var text = new TextView { X = 0, Y = 0, Width = 90, Height = Math.Min(lines.Count + 1, 20), ReadOnly = true, WordWrap = true, Text = string.Join("\n", lines) };
        using var dialog = new Dialog { Title = row.Target };
        dialog.Add(text);
        dialog.AddButton(new Button { Text = "Close" });
        dialog.AddButton(new Button { Text = "_Use as target" });
        App!.Run(dialog);
        if (dialog.Result == 1) { SelectedTarget = row.Target; App.RequestStop(); }
    }
}
