using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SnmpMibBrowser.Tui;

// WALK compare: walk the same OID several times and highlight rows whose value changed (one colour, any change).
public sealed partial class MainView
{
    private static readonly (string Label, int Seconds)[] CompareIntervals = [("15 s", 15), ("30 s", 30), ("1 min", 60), ("2 min", 120), ("5 min", 300)];

    private View _compareTab = null!;
    private TextField _compareOid = null!, _compareDepth = null!;
    private DropDownList _compareRounds = null!, _compareInterval = null!;
    private CheckBox _compareChangedOnly = null!;
    private Label _compareStatus = null!;
    private TableView _compareTable = null!;
    private readonly List<CompareRow> _compareRows = [];
    private List<CompareRow> _compareVisible = [];
    private int _compareRoundCount = 3;
    private string _compareTarget = "";
    private Scheme? _changedScheme;

    private void BuildCompareTab()
    {
        _compareTab = new View { Title = "Compare", Width = Dim.Fill(), Height = Dim.Fill() };
        var oidLabel = new Label { Text = "OID", X = 0, Y = 0 };
        _compareOid = new TextField { X = Pos.Right(oidLabel) + 1, Y = 0, Width = 26 };
        var depthLabel = new Label { Text = "Depth", X = Pos.Right(_compareOid) + 1, Y = 0 };
        _compareDepth = new TextField { X = Pos.Right(depthLabel) + 1, Y = 0, Width = 3, Text = "0" };
        var roundsLabel = new Label { Text = "Rounds", X = Pos.Right(_compareDepth) + 2, Y = 0 };
        _compareRounds = Dialogs.Choice(Enumerable.Range(2, 9).Select(n => n.ToString()), "3", 5);
        _compareRounds.X = Pos.Right(roundsLabel) + 1; _compareRounds.Y = 0;
        var everyLabel = new Label { Text = "Every", X = Pos.Right(_compareRounds) + 2, Y = 0 };
        _compareInterval = Dialogs.Choice(CompareIntervals.Select(i => i.Label), "30 s", 8);
        _compareInterval.X = Pos.Right(everyLabel) + 1; _compareInterval.Y = 0;

        var start = new Button { Text = "Start", X = 0, Y = 1, ShadowStyle = null };
        var stop = new Button { Text = "Stop", X = Pos.Right(start), Y = 1, ShadowStyle = null };
        _compareChangedOnly = new CheckBox { Text = "Changed only", X = Pos.Right(stop) + 1, Y = 1 };
        _compareStatus = new Label { X = Pos.Right(_compareChangedOnly) + 2, Y = 1, Width = Dim.Fill(), Text = "Empty OID = the toolbar OID." };
        _compareTable = new TableView
        {
            X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, MultiSelect = true, MaxCellWidth = 30,
            Table = new GridTableSource(["OID", "Name", "Type", "#1", "#2", "#3", "Change"], [])
        };
        // Rows whose value differs between rounds get one highlight colour.
        _compareTable.Style.RowColorGetter = args =>
            args.RowIndex < _compareVisible.Count && _compareVisible[args.RowIndex].Changed ? ChangedScheme() : null;
        _compareTab.Add(oidLabel, _compareOid, depthLabel, _compareDepth, roundsLabel, _compareRounds, everyLabel, _compareInterval,
            start, stop, _compareChangedOnly, _compareStatus, _compareTable);

        start.Accepting += (_, e) => { e.Handled = true; _ = CompareAsync(); };
        stop.Accepting += (_, e) => { e.Handled = true; CancelQuery(); };
        _compareOid.Accepting += (_, e) => { e.Handled = true; _ = CompareAsync(); };
        _compareChangedOnly.ValueChanged += (_, _) => RefreshCompare();
        _compareTable.Accepting += (_, e) => { e.Handled = true; ShowGridRow(_compareTable); };
    }

    private Scheme ChangedScheme() =>
        // Pink for changed rows; a stronger pink where the cursor is, so the highlight stays visible on the selected row.
        _changedScheme ??= new Scheme(_compareTable.GetScheme())
        {
            Normal = new Attribute(new Color(0, 0, 0), new Color(255, 190, 215)),
            Focus = new Attribute(new Color(0, 0, 0), new Color(240, 110, 160))
        };

    private async Task CompareAsync()
    {
        if (RejectWhileBusy()) return;
        var oid = OidUtil.Normalize(_compareOid.Text);
        if (oid.Length == 0) { oid = OidUtil.Normalize(_oid.Text); _compareOid.Text = oid; }
        if (string.IsNullOrWhiteSpace(_host.Text)) { Fail("Enter a target host."); return; }
        if (!OidUtil.IsValid(oid)) { _compareStatus.Text = $"Invalid OID: \"{oid}\""; return; }
        var depthText = _compareDepth.Text.Trim();
        var depth = 0;
        if (depthText.Length > 0 && (!int.TryParse(depthText, out depth) || depth < 0)) { _compareStatus.Text = "Invalid depth (number ≥ 0)."; return; }
        var rounds = int.TryParse(_compareRounds.Text, out var r) && r is >= 2 and <= 10 ? r : 3;
        var interval = CompareIntervals.FirstOrDefault(i => i.Label == _compareInterval.Text, CompareIntervals[1]).Seconds;
        // 1.3.6.1.4.1 and shorter cover every vendor; suggest narrowing to the vendor subtree.
        if (oid.Split('.').Length <= 7 && depth == 0 &&
            !Dialogs.Confirm(App!, "WALK compare", $"Walking {oid} can return a very large number of objects and take a long time.\nNarrowing it to the vendor subtree (e.g. 1.3.6.1.4.1.<number>) is recommended.\n\nStart anyway?"))
            return;

        _compareRows.Clear();
        _compareRoundCount = rounds;
        var options = Options();   // same device and settings for every round
        _compareTarget = options.Host;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var index = new Dictionary<string, CompareRow>();
        var completed = 0;
        RefreshCompare();
        _tabs.Value = _compareTab;
        Log($"WALK compare started: {oid} on {options.Host}, {rounds} rounds, every {interval} s.");
        SetStatus("WALK compare running…  (Esc or Stop cancels)");
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                if (round > 0)
                    for (var remaining = interval; remaining > 0; remaining--)
                    {
                        _compareStatus.Text = $"Round {round + 1}/{rounds} — next in {remaining} s";
                        await Task.Delay(1000, ct);
                    }
                _compareStatus.Text = $"Round {round + 1}/{rounds} — walking…";
                var answer = await _snmp.WalkAsync(options, oid, ct, depth, () => App?.Invoke(() => LogWalkLimit(oid)));
                foreach (var v in answer)
                {
                    var id = v.Id.ToString();
                    if (!index.TryGetValue(id, out var row))
                    {
                        row = new CompareRow(id, _mib.Resolve(id)?.Name ?? "", v.Data.TypeCode.ToString(), rounds);
                        index[id] = row; _compareRows.Add(row);
                    }
                    row.Values[round] = ValueFormat.Display(v.Data);
                }
                completed = round + 1;
                foreach (var row in _compareRows) row.Evaluate(completed);
                RefreshCompare();
                RememberTarget(options.Host);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail($"WALK compare failed: {ex.Message}"); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            var changed = _compareRows.Count(x => x.Changed);
            var summary = $"{completed}/{rounds} rounds, {_compareRows.Count} OIDs, {changed} changed";
            _compareStatus.Text = summary;
            SetStatus(summary);
            Log($"WALK compare {oid}: {summary}.");
        }
    }

    private void RefreshCompare()
    {
        _compareVisible = _compareChangedOnly.Value == CheckState.Checked ? _compareRows.Where(r => r.Changed).ToList() : [.. _compareRows];
        var rounds = _compareRoundCount;
        var columns = new List<(string, Func<CompareRow, string>)> { ("OID", r => r.Oid), ("Name", r => r.Name), ("Type", r => ShortType(r.Type)) };
        for (var i = 0; i < rounds; i++)
        {
            var round = i;
            columns.Add(($"#{round + 1}", r => r.Values[round]));
        }
        columns.Add(("Change", r => r.Change?.ToString("N0") ?? ""));
        _compareTable.Table = new RowTableSource<CompareRow>(_compareVisible, [.. columns]);
        _compareTable.Update();
    }
}
