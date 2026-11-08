using Lextm.SharpSnmpLib;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SnmpMibBrowser.Tui;

// WALK table, Devices (Interfaces, IP, Routing, ARP, LLDP) and Resources tabs.
// They use the same Core code as the Windows app and the same single-query rule as GET/WALK.
public sealed partial class MainView
{
    private TableView _walkTable = null!, _deviceTable = null!, _resourcesTable = null!;
    private Label _walkInfo = null!, _deviceInfo = null!, _resourcesInfo = null!;
    private View _walkTab = null!, _devicesTab = null!, _resourcesTab = null!;
    private List<ResourceRow> _resourceRows = [];
    private string _resourcesTarget = "";

    private const string CopyHint = "Termius: drag, then Ctrl+Shift+C   ·   MobaXterm: drag, then right-click > Copy   ·   Esc closes";

    private static TableView NewGrid() => new()
    {
        X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, MultiSelect = true, MaxCellWidth = 40,
        Table = new GridTableSource(["(empty)"], [])
    };

    private void BuildTableTabs()
    {
        // ---- WALK table: the last WALK pivoted into rows (index) and columns (object)
        _walkInfo = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Shows the last WALK as a table: one row per index, one column per object." };
        _walkTable = NewGrid();
        _walkTab = new View { Title = "WALK table", Width = Dim.Fill(), Height = Dim.Fill() };
        _walkTab.Add(_walkInfo, _walkTable);

        // ---- Devices: standard tables, one at a time
        _devicesTab = new View { Title = "Devices", Width = Dim.Fill(), Height = Dim.Fill() };
        View? previous = null;
        foreach (var (title, load) in new (string, Func<Task>)[]
                 {
                     ("Interfaces", LoadInterfacesAsync), ("IP addresses", LoadIpAsync), ("Routes", LoadRoutesAsync), ("ARP", LoadArpAsync), ("LLDP", LoadLldpAsync)
                 })
        {
            var button = new Button { Text = title, X = previous == null ? 0 : Pos.Right(previous), Y = 0, ShadowStyle = null, NoDecorations = false };
            button.Accepting += (_, e) => { e.Handled = true; _ = load(); };
            _devicesTab.Add(button);
            previous = button;
        }
        _deviceInfo = new Label { X = Pos.Right(previous!) + 1, Y = 0, Width = Dim.Fill(), Text = "← choose a table" };
        _deviceTable = NewGrid();
        _devicesTab.Add(_deviceInfo, _deviceTable);

        // ---- Resources: CPU, memory, disk, load, sensors from standard MIBs
        _resourcesTab = new View { Title = "Resources", Width = Dim.Fill(), Height = Dim.Fill() };
        var check = new Button { Text = "_Check resources", X = 0, Y = 0, ShadowStyle = null };
        check.Accepting += (_, e) => { e.Handled = true; _ = LoadResourcesAsync(); };
        _resourcesInfo = new Label { X = Pos.Right(check) + 1, Y = 0, Width = Dim.Fill(), Text = "HOST-RESOURCES, UCD-SNMP and ENTITY-SENSOR" };
        _resourcesTable = NewGrid();
        _resourcesTab.Add(check, _resourcesInfo, _resourcesTable);

        foreach (var table in new[] { _walkTable, _deviceTable, _resourcesTable })
            table.Accepting += (_, e) => { e.Handled = true; ShowGridRow(table); };
    }

    // ================= WALK table

    private void PopulateWalkTable(string oid, IList<Variable> variables)
    {
        var walk = WalkTable.Build(variables, _mib);
        _walkTable.Table = new GridTableSource([.. walk.Columns], walk.Rows);
        _walkTable.SetSelection(0, 0, false);
        _walkTable.Update();
        _walkInfo.Text = $"WALK {oid}: {walk.Rows.Count} rows × {walk.Columns.Count - 1} columns   (←/→ scroll, Enter shows a row)";
    }

    // ================= Devices

    private async Task<TableColumns?> LoadColumnsAsync(string feature, IReadOnlyList<string> roots, SnmpOptions? options = null)
    {
        if (RejectWhileBusy()) return null;
        if (string.IsNullOrWhiteSpace(_host.Text)) { Fail("Enter a target host."); _host.SetFocus(); return null; }
        options ??= Options();   // one device for all columns, even if the target is edited meanwhile
        _cts = new CancellationTokenSource();
        SetStatus($"{feature} from {options.Host}…  (Esc cancels)");
        try
        {
            var columns = await TableColumns.LoadAsync(_snmp, options, roots, _cts.Token, root => App?.Invoke(() => LogWalkLimit(root)));
            RememberTarget(options.Host);
            SetStatus($"{feature}: {columns.Keys().Count()} entries");
            Log($"{feature} {options.Host}: table loaded.");
            return columns;
        }
        catch (OperationCanceledException) { SetStatus("Cancelled"); return null; }
        catch (Exception ex) { Fail($"{feature} failed: {ex.Message}"); return null; }
        finally { _cts?.Dispose(); _cts = null; }
    }

    private void ShowDeviceTable<T>(string title, List<T> rows, params (string Name, Func<T, string> Get)[] columns)
    {
        _deviceTable.Table = new GridTableSource(columns.Select(c => c.Name).ToArray(), rows.Select(r => columns.Select(c => (string?)c.Get(r)).ToArray()).ToList());
        _deviceTable.SetSelection(0, 0, false);
        _deviceTable.Update();
        _deviceInfo.Text = $"{title}: {rows.Count} rows";
        _tabs.Value = _devicesTab;
        _deviceTable.SetFocus();   // arrows and Enter act on the rows right away
    }

    private async Task LoadInterfacesAsync()
    {
        if (await LoadColumnsAsync("Interfaces", DeviceTables.InterfaceRoots) is not { } c) return;
        ShowDeviceTable("Interfaces (physical ports)", DeviceTables.Interfaces(c),
            ("Index", r => r.Index), ("Name", r => r.Name), ("Type", r => r.Type), ("MTU", r => r.Mtu), ("Speed", r => r.Speed),
            ("MAC", r => r.Mac), ("Admin", r => r.Admin), ("Oper", r => r.Operational));
    }

    private async Task LoadIpAsync()
    {
        if (await LoadColumnsAsync("IP addresses", DeviceTables.IpRoots) is not { } c) return;
        ShowDeviceTable("IP addresses", DeviceTables.IpAddresses(c),
            ("IP address", r => r.Address), ("Interface", r => r.Interface), ("Netmask", r => r.Netmask), ("Broadcast", r => r.Broadcast));
    }

    private async Task LoadRoutesAsync()
    {
        if (await LoadColumnsAsync("Routing", DeviceTables.RouteRoots) is not { } c) return;
        ShowDeviceTable("Routing", DeviceTables.Routes(c),
            ("Destination", r => r.Destination), ("Netmask", r => r.Netmask), ("Next hop", r => r.NextHop), ("Interface", r => r.Interface),
            ("Metric", r => r.Metric), ("Type", r => r.Type), ("Protocol", r => r.Protocol));
    }

    private async Task LoadArpAsync()
    {
        if (await LoadColumnsAsync("ARP", DeviceTables.ArpRoots) is not { } c) return;
        ShowDeviceTable("ARP", DeviceTables.Arp(c), ("IP address", r => r.Address), ("MAC", r => r.Mac), ("Interface", r => r.Interface), ("Type", r => r.Type));
    }

    private async Task LoadLldpAsync()
    {
        var options = Options();
        if (await LoadColumnsAsync("LLDP", DeviceTables.LldpRoots, options) is not { } c) return;
        var localPorts = new Dictionary<string, string>();
        try { localPorts = await DeviceTables.LldpLocalPortsAsync(_snmp, options, CancellationToken.None); }
        catch (Exception ex) { Log($"LLDP local port mapping unavailable: {ex.Message}"); }
        ShowDeviceTable("LLDP neighbors", DeviceTables.Lldp(c, localPorts),
            ("Local port", r => r.LocalPort), ("Chassis ID", r => r.ChassisId), ("Remote port", r => r.PortId), ("Port description", r => r.PortDescription),
            ("System name", r => r.SystemName), ("System description", r => r.SystemDescription));
    }

    // ================= Resources

    private async Task LoadResourcesAsync()
    {
        var options = Options();
        if (await LoadColumnsAsync("Resources", ResourceCheck.Roots, options) is not { } c) return;
        var result = ResourceCheck.Build(c, Lang.English);
        _resourceRows = result.Rows;
        _resourcesTarget = options.Host;
        _resourcesTable.Table = new GridTableSource(["Category", "Item", "Value", "Source", "OID"],
            result.Rows.Select(r => new string?[] { r.Category, r.Item, r.Value, r.Source, r.Oid }).ToList());
        _resourcesTable.SetSelection(0, 0, false);
        _resourcesTable.Update();
        _resourcesInfo.Text = result.Summary(Lang.English);
        _tabs.Value = _resourcesTab;
        _resourcesTable.SetFocus();
        SetStatus($"Resources {options.Host}: {result.Rows.Count} values");
        Log($"Resources {options.Host}: {_resourcesInfo.Text}");
    }

    // ================= shared

    /// <summary>Enter on a table row: all columns of the row, with the full values (and how a computed resource value is derived).</summary>
    private void ShowGridRow(TableView table)
    {
        if (table.Table is not { Rows: > 0 } source || table.SelectedRows() is not [var row, ..]) return;
        var width = source.ColumnNames.Max(n => n.Length) + 2;
        var lines = Enumerable.Range(0, source.Columns).Select(c => $"{source.ColumnNames[c].PadRight(width)}{source[row, c]}").ToList();
        if (table == _resourcesTable && row < _resourceRows.Count && _resourceRows[row].Detail.Length > 0)
            lines.AddRange(["", _resourceRows[row].Detail]);
        if (table == _resourcesTable) lines.AddRange(["", $"Target: {_resourcesTarget}"]);
        Dialogs.ShowText(App!, "Row", string.Join("\n", lines), CopyHint);
    }

    /// <summary>The table on the visible tab (F6 / F7 act on it).</summary>
    private TableView? CurrentTable() =>
        _tabs.Value == _resultsTab ? _resultsTable : _tabs.Value == _walkTab ? _walkTable : _tabs.Value == _compareTab ? _compareTable : _tabs.Value == _devicesTab ? _deviceTable :
        _tabs.Value == _resourcesTab ? _resourcesTable : null;

    // Alt+1..7 switch tabs (only on the main screen, not while a dialog is open).
    private void OnAppKeyDown(object? sender, Key key)
    {
        if (App?.TopRunnableView != this || !key.IsAlt) return;
        View[] tabs = [_resultsTab, _walkTab, _compareTab, _devicesTab, _resourcesTab, _detailsTab, _logTab];
        var index = (int)key.NoAlt.NoShift.NoCtrl.KeyCode - (int)Terminal.Gui.Drivers.KeyCode.D1;
        if (index < 0 || index >= tabs.Length) return;
        _tabs.Value = tabs[index];
        key.Handled = true;
    }
}
