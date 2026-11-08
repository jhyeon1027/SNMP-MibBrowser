using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Lextm.SharpSnmpLib;

namespace SnmpMibBrowser;

public partial class MainWindow : Window
{
    private readonly MibLibrary _mib = new();
    private List<MibNode> _nodes => _mib.Nodes;
    private readonly SnmpService _snmp = new();
    private readonly ObservableCollection<QueryResult> _results = [];
    private CancellationTokenSource? _cts;
    private string _language = "en";
    private bool _dark;
    private bool _syncingConnectionControls;
    private string _currentOid = "1.3.6.1.2.1.1.1.0";
    // v1/v2c communities tried in order; the first entry mirrors CommunityBox.
    private List<string> _communities = ["public"];
    // Targets that answered, most recent first (kept across sessions in settings.json).
    private List<string> _recentTargets = [];
    private const int MaxRecentTargets = 20;
    // WALK compare tab: one row per OID, values per round.
    private readonly ObservableCollection<CompareRow> _compareRows = [];
    private readonly Dictionary<string, CompareRow> _compareIndex = [];
    // Target of the last WALK compare run and resources check, used by "Add to monitor".
    private string _compareTarget = "", _resourcesTarget = "";
    private UserSettings _settings = new();
    private readonly SettingsStore _store = new();

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        _snmp.CommunityAccepted += (host, community, index, total) => Dispatcher.Invoke(() =>
            Log(Tr($"{host}: Community \"{community}\" akzeptiert (Versuch {index}/{total}).", $"{host}: community \"{community}\" accepted (attempt {index}/{total}).")));
        ResultsGrid.ItemsSource = _results;
        CompareGrid.ItemsSource = _compareRows;
        WalkCompareTab.GotFocus += (_, _) => { if (CompareOidBox.Text.Length == 0) CompareOidBox.Text = QueryOidBox.Text.Trim(); };
        for (var n = 2; n <= 10; n++) CompareRoundsBox.Items.Add(new ComboBoxItem { Content = n.ToString(), IsSelected = n == 3 });
        LoadSettings();
        _recentTargets = [.. _settings.RecentTargets.Take(MaxRecentTargets)];
        RefreshProfiles();
        RebuildTree(_nodes);
        ApplyTheme();
        ApplyLanguage();
        UpdateMibStatus();
        Log(Tr("Anwendung gestartet.", "Application started."));
        Closing += (_, _) => SaveSettings();
        Loaded += async (_, _) => await LoadDefaultMibFolderAsync();
    }

    // Smaller or square screens: never open larger than the usable desktop area.
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
    }

    private async void LoadMibs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Tr("MIB-Dateien auswählen", "Select MIB files"), Filter = Tr("MIB-Dateien|*.mib;*.my;*.txt;*.*|Alle Dateien|*.*", "MIB files|*.mib;*.my;*.txt;*.*|All files|*.*"), Multiselect = true };
        if (dialog.ShowDialog() == true) await LoadMibsAsync(dialog.FileNames);
    }

    private async void LoadMibFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Tr("MIB-Ordner auswählen", "Select MIB folder"), Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            await LoadMibsAsync(MibLibrary.MibFilesIn(dialog.FolderName));
        }
    }

    private async Task LoadMibsAsync(IEnumerable<string> files)
    {
        var list = files.ToList(); SetBusy(true, Tr($"Lade {list.Count} MIB-Dateien…", $"Loading {list.Count} MIB files…"));
        try
        {
            var (accepted, protectedCount) = await Task.Run(() => _mib.Load(list));
            RebuildTree(_nodes);
            UpdateMibStatus();
            Log(Tr($"{accepted:N0} MIB-Objekte geladen; {protectedCount:N0} öffentliche Definitionen zum Schutz des eingebauten Katalogs übersprungen.", $"{accepted:N0} MIB objects loaded; {protectedCount:N0} public definitions skipped to protect the built-in catalog."));
        }
        catch (Exception ex) { ShowError(Tr("MIB-Dateien konnten nicht geladen werden", "MIB files could not be loaded"), ex); }
        finally { SetBusy(false, Tr("Bereit", "Ready")); }
    }

    private void RebuildTree(IEnumerable<MibNode> nodes, string? focusOid = null)
    {
        MibTree.Items.Clear();
        var items = new Dictionary<string, TreeViewItem>();
        foreach (var (oid, name, model) in _mib.BuildTree(nodes))
        {
            var expandsToFocus = !string.IsNullOrWhiteSpace(focusOid) &&
                (focusOid == oid || focusOid.StartsWith(oid + ".", StringComparison.Ordinal));
            var item = new TreeViewItem { Header = name, Tag = model, ToolTip = oid, IsExpanded = expandsToFocus || MibLibrary.ExpandedByDefault.Contains(oid) };
            items[oid] = item;
            var cut = oid.LastIndexOf('.');
            if (cut > 0 && items.TryGetValue(oid[..cut], out var parent)) parent.Items.Add(item); else MibTree.Items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(focusOid) && items.TryGetValue(focusOid, out var focusedItem))
        {
            focusedItem.IsSelected = true;
            focusedItem.Focus();
            Dispatcher.BeginInvoke(() => focusedItem.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static string OidSortKey(string oid) => OidUtil.SortKey(oid);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_nodes.Count == 0) return;
        var (matches, focus) = _mib.Search(SearchBox.Text);
        RebuildTree(matches, focus);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private void MibTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: MibNode n }) return;
        _currentOid = QueryOidForNode(n);
        SetOidBox.Text = QueryOidForNode(n);
        QueryOidBox.Text = _currentOid;
        DetailsBox.Text = _mib.Details(n, L, "\r\n");
    }

    private async void Get_Click(object sender, RoutedEventArgs e) => await ExecuteGetAsync();
    private async void HostBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _cts != null) return;
        e.Handled = true;
        const string sysDescrOid = "1.3.6.1.2.1.1.1.0";
        _currentOid = sysDescrOid;
        ResultsTab.IsSelected = true;
        await RunQueryAsync("GET", ct => _snmp.GetAsync(Options(), sysDescrOid, false, ct));
    }
    private async void QueryOidBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _cts != null) return;
        e.Handled = true;
        ResultsTab.IsSelected = true;
        await ExecuteGetAsync();
    }
    private async void GetNext_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetQueryOid(out var oid)) return;
        await RunGetNextAsync(oid);
    }
    private async void Walk_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetQueryOid(out var oid)) return;
        await RunWalkAsync(oid);
    }

    // Puts the returned OID into the toolbar so GET NEXT can be pressed repeatedly.
    private async Task RunGetNextAsync(string oid)
    {
        Variable? next = null;
        await RunQueryAsync("GET NEXT", async ct =>
        {
            var answer = await _snmp.GetAsync(Options(), oid, true, ct);
            next = answer.FirstOrDefault();
            return answer;
        });
        if (next == null || next.Data.TypeCode == SnmpType.EndOfMibView) return;
        _currentOid = QueryOidBox.Text = next.Id.ToString();
    }

    private async Task RunWalkAsync(string oid)
    {
        if (!TryGetWalkDepth(out var depth)) return;
        var truncated = 0;
        await RunQueryAsync("WALK", async ct =>
        {
            var answer = await _snmp.WalkAsync(Options(), oid, ct, depth, () => Dispatcher.Invoke(() => LogWalkLimit(oid)));
            var rootArcs = oid.Split('.').Length;
            if (depth > 0) truncated = answer.Count(v => v.Id.ToString().Split('.').Length - rootArcs > depth);
            return answer;
        });
        if (truncated > 0) Log(Tr($"WALK {oid}: {truncated} Teilbäume unterhalb von Tiefe {depth} übersprungen (nur erstes Objekt angezeigt).", $"WALK {oid}: {truncated} subtrees below depth {depth} skipped (first object shown only)."));
    }

    private void LogWalkLimit(string oid) =>
        Log(Tr($"WARNUNG: WALK {oid} nach {SnmpService.MaxWalkSteps:N0} Anfragen abgebrochen; das Ergebnis ist unvollständig.", $"WARNING: WALK {oid} stopped after {SnmpService.MaxWalkSteps:N0} requests; the result is incomplete."));

    private bool TryGetWalkDepth(out int depth)
    {
        var text = WalkDepthBox.Text.Trim();
        if (text.Length == 0) { depth = 0; return true; }
        if (int.TryParse(text, out depth) && depth >= 0) return true;
        var message = Tr($"Ungültige WALK-Tiefe: \"{text}\". Erwartet wird eine Zahl ≥ 0 (0 = unbegrenzt).", $"Invalid WALK depth: \"{text}\". Expected a number ≥ 0 (0 = unlimited).");
        StatusText.Text = message;
        Log($"{Tr("FEHLER", "ERROR")}: {message}");
        return false;
    }

    // Reads the toolbar OID so that objects missing from the MIB tree
    // (e.g. vendor enterprise OIDs) can be queried directly.
    private bool TryGetQueryOid(out string oid)
    {
        oid = QueryOidBox.Text.Trim().TrimStart('.');
        if (RejectWhileBusy()) return false;
        if (!OidUtil.IsValid(oid))
        {
            var message = Tr($"Ungültige OID: \"{QueryOidBox.Text.Trim()}\". Erwartet wird eine numerische OID wie 1.3.6.1.2.1.1.1.0.", $"Invalid OID: \"{QueryOidBox.Text.Trim()}\". Expected a numeric OID such as 1.3.6.1.2.1.1.1.0.");
            StatusText.Text = message;
            Log($"{Tr("FEHLER", "ERROR")}: {message}");
            return false;
        }
        _currentOid = oid;
        QueryOidBox.Text = oid;
        return true;
    }
    private async void ApplySet_Click(object sender, RoutedEventArgs e)
    {
        if (RejectWhileBusy()) return;
        if (MessageBox.Show(Tr("Der SET-Befehl verändert einen Wert auf dem Zielgerät. Fortfahren?", "The SET operation changes a value on the target device. Continue?"), Tr("SNMP SET bestätigen", "Confirm SNMP SET"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunQueryAsync("SET", ct => _snmp.SetAsync(Options(), SetOidBox.Text, Selected(DataTypeBox), SetValueBox.Text, ct));
    }
    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteGetAsync();

    private async Task ExecuteGetAsync()
    {
        if (!TryGetQueryOid(out var input)) return;
        var oid = NormalizeGetOid(input);
        _currentOid = oid;
        QueryOidBox.Text = oid;
        await RunQueryAsync("GET", ct => _snmp.GetAsync(Options(), oid, false, ct));
    }

    private async void MibTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MibTree.SelectedItem is not TreeViewItem { Tag: MibNode node } || RejectWhileBusy()) return;
        if (IsScalar(node)) { QueryOidBox.Text = QueryOidForNode(node); await ExecuteGetAsync(); }
        else { _currentOid = QueryOidBox.Text = node.Oid; await RunWalkAsync(node.Oid); }
        e.Handled = true;
    }

    private MibNode? SelectedMibNode() => (MibTree.SelectedItem as TreeViewItem)?.Tag as MibNode;
    private void MibTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current=e.OriginalSource as DependencyObject;
        while(current!=null && current is not TreeViewItem) current=VisualTreeHelper.GetParent(current);
        if(current is TreeViewItem item){item.IsSelected=true;item.Focus();}
    }
    private async void ContextGet_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n && !RejectWhileBusy()) { QueryOidBox.Text=QueryOidForNode(n); await ExecuteGetAsync(); } }
    private async void ContextNext_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n && !RejectWhileBusy()) { _currentOid=QueryOidBox.Text=n.Oid; await RunGetNextAsync(n.Oid); } }
    private async void ContextWalk_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n && !RejectWhileBusy()) { _currentOid=QueryOidBox.Text=n.Oid; await RunWalkAsync(n.Oid); } }
    private void ContextCopy_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n) CopyToClipboard(n.Oid, 1); }
    private void ContextDetails_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode()!=null) DetailsPanel.IsExpanded=true; }

    private string QueryOidForNode(MibNode node) => _mib.QueryOidFor(node);
    private string NormalizeGetOid(string oid) => _mib.NormalizeGetOid(oid);
    private bool IsScalar(MibNode node) => _mib.IsScalar(node);

    private async void LoadInterfaces_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("Interfaces", DeviceTables.InterfaceRoots);
        if (c == null) return;
        var rows = DeviceTables.Interfaces(c);
        InterfacesGrid.ItemsSource = rows;
        StatusText.Text = _language == "de" ? $"Interfaces: {rows.Count} physische Ports" : $"Interfaces: {rows.Count} physical ports";
    }

    private async void LoadIp_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("IP", DeviceTables.IpRoots);
        if (c == null) return; IpGrid.ItemsSource = DeviceTables.IpAddresses(c);
    }

    private async void LoadRoutes_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("Routing", DeviceTables.RouteRoots);
        if (c == null) return; RoutesGrid.ItemsSource = DeviceTables.Routes(c);
    }

    private async void LoadArp_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("ARP", DeviceTables.ArpRoots);
        if (c == null) return; ArpGrid.ItemsSource = DeviceTables.Arp(c);
    }

    private async void LoadLldp_Click(object sender, RoutedEventArgs e)
    {
        var options = Options();   // the device LoadColumnsAsync queries, read before the await
        var c = await LoadColumnsAsync("LLDP", DeviceTables.LldpRoots);
        if (c == null) return;
        var localPorts = new Dictionary<string, string>();
        try { localPorts = await DeviceTables.LldpLocalPortsAsync(_snmp, options, CancellationToken.None); }
        catch (Exception ex) { Log(Tr($"LLDP-Zuordnung lokaler Ports nicht verfügbar: {ex.Message}", $"LLDP local port mapping unavailable: {ex.Message}")); }
        LldpGrid.ItemsSource = DeviceTables.Lldp(c, localPorts);
    }

    private async Task<TableColumns?> LoadColumnsAsync(string feature, IReadOnlyList<string> roots)
    {
        if (RejectWhileBusy()) return null;
        _cts = new CancellationTokenSource(); SetBusy(true, $"{feature}…");
        try
        {
            var options = Options();   // one device for all columns, even if the target is edited meanwhile
            var columns = await TableColumns.LoadAsync(_snmp, options, roots, _cts.Token, root => Dispatcher.Invoke(() => LogWalkLimit(root)));
            RememberTarget(options.Host);
            StatusText.Text = _language == "de" ? $"{feature}: {columns.Keys().Count()} Einträge" : $"{feature}: {columns.Keys().Count()} entries";
            Log(Tr($"{feature}: Tabelle geladen.", $"{feature}: table loaded.")); return columns;
        }
        catch (OperationCanceledException) { StatusText.Text = _language == "de" ? "Abgebrochen" : "Cancelled"; return null; }
        catch (Exception ex) { ShowError(Tr($"{feature} fehlgeschlagen", $"{feature} failed"), ex); return null; }
        finally { _cts?.Dispose(); _cts = null; SetBusy(false, StatusText.Text); }
    }

    // Only one query may run at a time; _cts is shared with the Cancel button.
    private bool RejectWhileBusy()
    {
        if (_cts == null) return false;
        StatusText.Text = Tr("Es läuft bereits eine Abfrage. Bitte warten oder abbrechen.", "A query is already running. Wait for it or cancel it.");
        return true;
    }

    private async Task RunQueryAsync(string operation, Func<CancellationToken, Task<IList<Variable>>> action)
    {
        if (RejectWhileBusy()) return;
        _cts = new CancellationTokenSource(); SetBusy(true, Tr($"{operation} wird ausgeführt…", $"Running {operation}…"));
        var host = HostBox.Text.Trim();
        var queriedOid = _currentOid;   // the tree or OID box may change while the query runs
        var sw = Stopwatch.StartNew();
        try
        {
            var answer = await action(_cts.Token); sw.Stop();
            var firstNew = _results.Count;
            foreach (var v in answer)
            {
                var oid = v.Id.ToString(); var node = ResolveNode(oid);
                _results.Add(new(DateTime.Now.ToString("HH:mm:ss"), operation, oid, node?.Name ?? "", v.Data.TypeCode.ToString(), DisplayValue(v.Data), $"{sw.ElapsedMilliseconds} ms", host));
            }
            // WALK still fills the WALK table tab, but results are shown in Results,
            // scrolled to the first row of this walk.
            if (operation == "WALK")
            {
                PopulateWalkTable(answer); DataTabs.SelectedItem = ResultsTab;
                if (firstNew < _results.Count) ResultsGrid.ScrollIntoView(_results[firstNew]);
            }
            RememberTarget(host);
            StatusText.Text = Tr($"{answer.Count} Antwort(en) in {sw.ElapsedMilliseconds} ms", $"{answer.Count} response(s) in {sw.ElapsedMilliseconds} ms");
            Log(Tr($"{operation} {queriedOid}: {answer.Count} Antwort(en), {sw.ElapsedMilliseconds} ms.", $"{operation} {queriedOid}: {answer.Count} response(s), {sw.ElapsedMilliseconds} ms."));
        }
        catch (OperationCanceledException) { StatusText.Text = Tr("Abgebrochen", "Cancelled"); Log(Tr($"{operation} abgebrochen.", $"{operation} cancelled.")); }
        catch (Exception ex) { ShowError(Tr($"{operation} fehlgeschlagen", $"{operation} failed"), ex); }
        finally { _cts.Dispose(); _cts = null; SetBusy(false, StatusText.Text); }
    }

    private static string DisplayValue(ISnmpData data) => ValueFormat.Display(data);

    private void RememberTarget(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _recentTargets.RemoveAll(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
        _recentTargets.Insert(0, host);
        if (_recentTargets.Count > MaxRecentTargets) _recentTargets.RemoveRange(MaxRecentTargets, _recentTargets.Count - MaxRecentTargets);
        SaveSettings();
    }

    private void RecentTargets_Click(object sender, RoutedEventArgs e) => ShowRecentTargets();
    private void ToolbarHostBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Down) return;
        e.Handled = true; ShowRecentTargets();
    }
    private void ShowRecentTargets()
    {
        var menu = new ContextMenu { PlacementTarget = ToolbarHostBox, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = ToolbarHostBox.ActualWidth };
        foreach (var target in _recentTargets)
        {
            var item = new MenuItem { Header = target };
            item.Click += (_, _) => { ToolbarHostBox.Text = target; ToolbarHostBox.Focus(); ToolbarHostBox.CaretIndex = target.Length; };
            menu.Items.Add(item);
        }
        if (_recentTargets.Count == 0) menu.Items.Add(new MenuItem { Header = Tr("(noch keine erfolgreichen Ziele)", "(no successful targets yet)"), IsEnabled = false });
        else
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = Tr("Verlauf löschen", "Clear history") };
            clear.Click += (_, _) => { _recentTargets.Clear(); SaveSettings(); };
            menu.Items.Add(clear);
        }
        menu.IsOpen = true;
    }

    // ---- Results copy helpers (context menu, Ctrl+C, double-click)
    // Selected rows in display order. A set keeps this linear for large WALK results (Ctrl+A on 10,000 rows).
    private static List<T> SelectedInOrder<T>(IEnumerable<T> rows, DataGrid grid)
    {
        var selected = new HashSet<object>(grid.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
        return rows.Where(r => selected.Contains(r!)).ToList();
    }
    private List<QueryResult> SelectedResults() => SelectedInOrder(_results, ResultsGrid);
    private string SymbolicOid(string oid) => _mib.Symbolic(oid);
    private bool CopyToClipboard(string text, int count)
    {
        if (text.Length == 0) return false;
        if (!ClipboardText.TrySet(new System.Windows.Interop.WindowInteropHelper(this).Handle, text))
        {
            StatusText.Text = Tr("Zwischenablage ist belegt. Bitte erneut versuchen.", "The clipboard is busy. Please try again.");
            return false;
        }
        StatusText.Text = Tr($"{count} Zeile(n) kopiert", $"Copied {count} row(s)");
        return true;
    }
    private void CopyResults(Func<QueryResult, string> line)
    {
        var rows = SelectedResults();
        CopyToClipboard(string.Join(Environment.NewLine, rows.Select(line)), rows.Count);
    }
    private void CopyResultValue_Click(object sender, RoutedEventArgs e) => CopyResults(r => r.Value);
    private void CopyResultOid_Click(object sender, RoutedEventArgs e) => CopyResults(r => r.Oid);
    private void CopyResultOidValue_Click(object sender, RoutedEventArgs e) => CopyResults(r => $"{SymbolicOid(r.Oid)} = {r.Value}");
    private void CopyResultRows_Click(object sender, RoutedEventArgs e) => CopyResultRowsWithHeader();
    private void CopyResultRowsWithHeader()
    {
        var rows = SelectedResults();
        if (rows.Count == 0) return;
        var header = string.Join("\t", ResultsGrid.Columns.Select(col => col.Header?.ToString() ?? ""));
        CopyToClipboard(header + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(r => string.Join("\t", r.Timestamp, r.Operation, r.Oid, r.Name, r.Type, r.Value, r.Duration))), rows.Count);
    }
    private void ResultsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.C || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true; CopyResultRowsWithHeader();
    }
    private void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow { Item: QueryResult row }) return;
        if (CopyToClipboard(row.Value, 1)) StatusText.Text = Tr($"Wert kopiert: {Shorten(row.Value)}", $"Value copied: {Shorten(row.Value)}");
        e.Handled = true;
    }
    // Right-clicking a row outside the selection makes it the selection, so the copy menu acts on it.
    private void ResultsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is DataGridRow row && !row.IsSelected && sender is DataGrid grid) { grid.SelectedItems.Clear(); row.IsSelected = true; }
    }
    private static string Shorten(string value) => value.Length > 60 ? value[..60] + "…" : value;

    // ---- WALK compare: walk the same OID several times and highlight rows whose value changed.
    private async void CompareStart_Click(object sender, RoutedEventArgs e)
    {
        if (RejectWhileBusy()) return;
        var oid = CompareOidBox.Text.Trim().TrimStart('.');
        if (oid.Length == 0) { oid = QueryOidBox.Text.Trim().TrimStart('.'); CompareOidBox.Text = oid; }
        if (!OidUtil.IsValid(oid))
        {
            CompareStatusText.Text = StatusText.Text = Tr($"Ungültige OID: \"{oid}\"", $"Invalid OID: \"{oid}\"");
            return;
        }
        var depthText = CompareDepthBox.Text.Trim();
        var depth = 0;
        if (depthText.Length > 0 && (!int.TryParse(depthText, out depth) || depth < 0))
        {
            CompareStatusText.Text = StatusText.Text = Tr("Ungültige Tiefe (Zahl ≥ 0).", "Invalid depth (number ≥ 0).");
            return;
        }
        var rounds = int.Parse(((ComboBoxItem)CompareRoundsBox.SelectedItem).Content.ToString()!);
        var interval = int.Parse(((ComboBoxItem)CompareIntervalBox.SelectedItem).Tag.ToString()!);
        // 1.3.6.1.4.1 and shorter cover every vendor; suggest narrowing to the vendor subtree.
        if (oid.Split('.').Length <= 7 && depth == 0 &&
            MessageBox.Show(Tr($"WALK von {oid} kann sehr viele Objekte liefern und lange dauern. Besser auf den Herstellerzweig (z. B. 1.3.6.1.4.1.<Nummer>) eingrenzen. Trotzdem starten?",
                                $"Walking {oid} can return a very large number of objects and take a long time. Narrowing it to the vendor subtree (e.g. 1.3.6.1.4.1.<number>) is recommended. Start anyway?"),
                Tr("WALK-Vergleich", "WALK compare"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _compareRows.Clear(); _compareIndex.Clear();
        BuildCompareColumns(rounds);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, Tr("WALK-Vergleich läuft…", "WALK compare running…"));
        CompareStartButton.IsEnabled = false; CompareStopButton.IsEnabled = true;
        var options = Options();   // same device and settings for every round
        var host = _compareTarget = options.Host;
        var completed = 0;
        Log(Tr($"WALK-Vergleich gestartet: {oid}, {rounds} Runden, alle {interval} s.", $"WALK compare started: {oid}, {rounds} rounds, every {interval} s."));
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                if (round > 0)
                    for (var remaining = interval; remaining > 0; remaining--)
                    {
                        CompareStatusText.Text = Tr($"Runde {round + 1}/{rounds} — nächste in {remaining} s", $"Round {round + 1}/{rounds} — next in {remaining} s");
                        await Task.Delay(1000, ct);
                    }
                CompareStatusText.Text = Tr($"Runde {round + 1}/{rounds} — WALK läuft…", $"Round {round + 1}/{rounds} — walking…");
                var answer = await _snmp.WalkAsync(options, oid, ct, depth, () => Dispatcher.Invoke(() => LogWalkLimit(oid)));
                foreach (var v in answer)
                {
                    var id = v.Id.ToString();
                    if (!_compareIndex.TryGetValue(id, out var row))
                    {
                        row = new CompareRow(id, ResolveNode(id)?.Name ?? "", v.Data.TypeCode.ToString(), rounds);
                        _compareIndex[id] = row; _compareRows.Add(row);
                    }
                    row.Values[round] = DisplayValue(v.Data);
                }
                completed = round + 1;
                foreach (var row in _compareRows) row.Evaluate(completed);
                RefreshCompareFilter();
                RememberTarget(host);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(Tr("WALK-Vergleich fehlgeschlagen", "WALK compare failed"), ex); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            CompareStartButton.IsEnabled = true; CompareStopButton.IsEnabled = false;
            var changed = _compareRows.Count(r => r.Changed);
            var summary = Tr($"{completed}/{rounds} Runden, {_compareRows.Count} OIDs, {changed} geändert", $"{completed}/{rounds} rounds, {_compareRows.Count} OIDs, {changed} changed");
            CompareStatusText.Text = summary;
            Log($"WALK compare {oid}: {summary}.");
            SetBusy(false, summary);
        }
    }

    private void BuildCompareColumns(int rounds)
    {
        CompareGrid.Columns.Clear();
        CompareGrid.Columns.Add(new DataGridTextColumn { Header = "OID", Binding = new System.Windows.Data.Binding(nameof(CompareRow.Oid)), Width = 220, MinWidth = 160 });
        CompareGrid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(CompareRow.Name)), Width = 140, MinWidth = 90 });
        CompareGrid.Columns.Add(new DataGridTextColumn { Header = Tr("Typ", "Type"), Binding = new System.Windows.Data.Binding(nameof(CompareRow.Type)), Width = 95, MinWidth = 80 });
        for (var r = 0; r < rounds; r++)
            CompareGrid.Columns.Add(new DataGridTextColumn { Header = $"#{r + 1}", Binding = new System.Windows.Data.Binding($"Values[{r}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 80 });
        CompareGrid.Columns.Add(new DataGridTextColumn { Header = Tr("Änderung", "Change"), Binding = new System.Windows.Data.Binding(nameof(CompareRow.Change)), Width = 90, MinWidth = 70 });
    }

    private void CompareChangedOnly_Click(object sender, RoutedEventArgs e) => RefreshCompareFilter();
    private void RefreshCompareFilter()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_compareRows);
        view.Filter = CompareChangedOnlyBox.IsChecked == true ? item => ((CompareRow)item).Changed : null;
    }

    private List<CompareRow> SelectedCompareRows() => SelectedInOrder(_compareRows, CompareGrid);
    private void CompareCopyOid_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedCompareRows();
        CopyToClipboard(string.Join(Environment.NewLine, rows.Select(r => r.Oid)), rows.Count);
    }
    private void CompareCopyRows_Click(object sender, RoutedEventArgs e) => CopyCompareRowsWithHeader();
    private void CopyCompareRowsWithHeader()
    {
        var rows = SelectedCompareRows();
        if (rows.Count == 0) return;
        var header = string.Join("\t", CompareGrid.Columns.Select(col => col.Header?.ToString() ?? ""));
        CopyToClipboard(header + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(r => string.Join("\t", new[] { r.Oid, r.Name, r.Type }.Concat(r.Values).Append(r.Change?.ToString() ?? "")))), rows.Count);
    }
    private void CompareGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.C || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true; CopyCompareRowsWithHeader();
    }
    // Double-click copies the OID: the goal of this tab is finding the OID to use.
    private void CompareGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow { Item: CompareRow row }) return;
        if (CopyToClipboard(row.Oid, 1)) StatusText.Text = Tr($"OID kopiert: {row.Oid}", $"OID copied: {row.Oid}");
        e.Handled = true;
    }

    // ---- Resources tab: CPU, memory, disk, load and sensors from standard MIBs (HOST-RESOURCES, UCD-SNMP, ENTITY-SENSOR).
    // Uses LoadColumnsAsync like the other device tabs; MIBs a device does not implement simply return no rows.
    private async void LoadResources_Click(object sender, RoutedEventArgs e)
    {
        var target = HostBox.Text.Trim();   // the device LoadColumnsAsync queries, read before the await
        var c = await LoadColumnsAsync("Resources", ResourceCheck.Roots);
        if (c == null) return;
        _resourcesTarget = target;
        var result = ResourceCheck.Build(c, L);
        ResourcesGrid.ItemsSource = result.Rows;
        ResourcesSummaryText.Text = result.Summary(L);
        StatusText.Text = Tr($"Ressourcen: {result.Rows.Count} Werte", $"Resources: {result.Rows.Count} values");
        Log($"Resources {target}: {ResourcesSummaryText.Text}");
    }

    private List<ResourceRow> SelectedResourceRows() => SelectedInOrder(ResourcesGrid.Items.OfType<ResourceRow>(), ResourcesGrid);
    private void ResourcesCopyOid_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedResourceRows().Where(r => r.Oid.Length > 0).ToList();
        CopyToClipboard(string.Join(Environment.NewLine, rows.Select(r => r.Oid)), rows.Count);
    }
    private void ResourcesCopyRows_Click(object sender, RoutedEventArgs e) => CopyResourceRowsWithHeader();
    private void CopyResourceRowsWithHeader()
    {
        var rows = SelectedResourceRows();
        if (rows.Count == 0) return;
        var header = string.Join("\t", ResourcesGrid.Columns.Select(col => col.Header?.ToString() ?? ""));
        CopyToClipboard(header + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(r => string.Join("\t", r.Category, r.Item, r.Value, r.Source, r.Oid))), rows.Count);
    }
    private void ResourcesGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.C || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true; CopyResourceRowsWithHeader();
    }
    // Double-click copies the instance OID, ready to register in a monitoring system.
    private void ResourcesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow { Item: ResourceRow row }) return;
        if (row.Oid.Length > 0 && CopyToClipboard(row.Oid, 1)) StatusText.Text = Tr($"OID kopiert: {row.Oid}", $"OID copied: {row.Oid}");
        e.Handled = true;
    }

    // Column names are MIB names or raw OIDs; bind through the DataRowView indexer so dots are not read as a property path.
    private void WalkTableGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e) =>
        e.Column = new DataGridTextColumn { Header = e.PropertyName, Binding = new System.Windows.Data.Binding { Path = new PropertyPath("[" + e.PropertyName + "]") } };

    private void PopulateWalkTable(IList<Variable> variables)
    {
        var walk = WalkTable.Build(variables, _mib);
        var table = new DataTable();
        foreach (var name in walk.Columns) table.Columns.Add(name);
        foreach (var cells in walk.Rows) table.Rows.Add(cells.Select(x => (object?)x ?? DBNull.Value).ToArray());
        WalkTableGrid.ItemsSource = table.DefaultView;
    }

    private MibNode? ResolveNode(string oid) => _mib.Resolve(oid);
    private MonitorWindow? _monitorWindow;
    private MonitorWindow ShowMonitor()
    {
        if (_monitorWindow is not { IsLoaded: true })
        {
            _monitorWindow = new MonitorWindow(_snmp, host => Options() with { Host = host }, () => HostBox.Text.Trim(), _language) { Owner = this };
            _monitorWindow.Show();
        }
        _monitorWindow.Activate();
        return _monitorWindow;
    }
    private void Monitor_Click(object sender, RoutedEventArgs e) => ShowMonitor();
    private void AddToMonitor(IEnumerable<(string Target, string Oid, string Name)> items)
    {
        var list = items.Where(i => i.Oid.Length > 0).ToList();
        if (list.Count == 0) return;
        var monitor = ShowMonitor();
        foreach (var (target, oid, name) in list) monitor.AddItem(target.Length > 0 ? target : HostBox.Text.Trim(), oid, name);
    }
    private void ResultsMonitor_Click(object sender, RoutedEventArgs e) =>
        AddToMonitor(SelectedResults().Select(r => (r.Target, r.Oid, SymbolicOid(r.Oid))));
    private void CompareMonitor_Click(object sender, RoutedEventArgs e) =>
        AddToMonitor(SelectedCompareRows().Select(r => (_compareTarget, r.Oid, SymbolicOid(r.Oid))));
    private void ResourcesMonitor_Click(object sender, RoutedEventArgs e) =>
        AddToMonitor(SelectedResourceRows().Select(r => (_resourcesTarget, r.Oid, r.Item)));
    private void ContextMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMibNode() is { } n) AddToMonitor([(HostBox.Text.Trim(), QueryOidForNode(n), n.Name)]);
    }

    private MultiTargetWindow? _multiTargetWindow;
    private void MultiTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_multiTargetWindow is { IsLoaded: true }) { _multiTargetWindow.Activate(); return; }
        _multiTargetWindow = new MultiTargetWindow(_snmp, host => Options() with { Host = host }, SetTargetFromMultiTarget, () => _settings, SaveSettings, _language) { Owner = this };
        _multiTargetWindow.Show();
    }
    private void SetTargetFromMultiTarget(string host)
    {
        ToolbarHostBox.Text = host;
        Activate();
        StatusText.Text = Tr($"Ziel gesetzt: {host}", $"Target set: {host}");
    }

    private SnmpOptions Options()
    {
        _ = int.TryParse(PortBox.Text, out var port);
        var profile = ProfileBox.SelectedItem as SnmpProfile;
        var communities = _communities.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new SnmpOptions(HostBox.Text.Trim(), port > 0 ? port : 161, Selected(VersionBox), CommunityBox.Text,
            UserBox.Text, Selected(AuthBox), AuthPasswordBox.Password, Selected(PrivacyBox), PrivacyPasswordBox.Password, ContextBox.Text,
            profile?.Timeout ?? 3000, profile?.Retries ?? 1, communities);
    }
    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    // MIB loading also uses the busy bar; while a query is running (_cts set) it stays visible and cancellable.
    private void SetBusy(bool busy, string text)
    {
        var active = busy || _cts != null;
        BusyBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = _cts != null; StatusText.Text = text;
    }
    private void Log(string text) { LogBox.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\r\n"); LogBox.ScrollToEnd(); }
    private void ShowError(string title, Exception ex) { StatusText.Text = title; Log($"{Tr("FEHLER", "ERROR")}: {title}: {ex.Message}"); MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    private string Tr(string de, string en) => _language == "de" ? de : en;
    private Lang L => Lang.From(_language);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void ClearResults_Click(object sender, RoutedEventArgs e) => _results.Clear();
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _language) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings = dialog.ResultSettings; _settings.Language = _language; _settings.Dark = _dark;
        SaveSettings(); RefreshProfiles();
    }
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        new UpdateWindow(_settings, _language) { Owner = this }.ShowDialog();
        SaveSettings();
    }
    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ProfileBox.SelectedItem is not SnmpProfile p) return;
        if (!_syncingConnectionControls)
        {
            _syncingConnectionControls = true;
            ToolbarProfileBox.SelectedItem = p;
            _syncingConnectionControls = false;
        }
        _settings.SelectedProfile = p.Name;
        ApplyProfile(p);
        SaveSettings();
    }
    private void ToolbarProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingConnectionControls || ToolbarProfileBox.SelectedItem is not SnmpProfile p) return;
        _syncingConnectionControls = true;
        ProfileBox.SelectedItem = p;
        _syncingConnectionControls = false;
        _settings.SelectedProfile = p.Name;
        ApplyProfile(p);
        SaveSettings();
    }
    private void HostBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingConnectionControls || !IsInitialized) return;
        _syncingConnectionControls = true;
        ToolbarHostBox.Text = HostBox.Text;
        _syncingConnectionControls = false;
    }
    private void ToolbarHostBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingConnectionControls || !IsInitialized) return;
        _syncingConnectionControls = true;
        HostBox.Text = ToolbarHostBox.Text;
        _syncingConnectionControls = false;
    }
    private void RefreshProfiles()
    {
        ProfileBox.ItemsSource = null; ToolbarProfileBox.ItemsSource = null;
        ProfileBox.ItemsSource = _settings.Profiles; ToolbarProfileBox.ItemsSource = _settings.Profiles;
        if (_settings.Profiles.Count == 0) return;
        var selected = _settings.Profiles.FirstOrDefault(x => x.Name == _settings.SelectedProfile) ?? _settings.Profiles[0];
        _syncingConnectionControls = true;
        ProfileBox.SelectedItem = selected; ToolbarProfileBox.SelectedItem = selected;
        _syncingConnectionControls = false;
        ApplyProfile(selected);
    }
    private void ApplyProfile(SnmpProfile p)
    {
        PortBox.Text = p.Port.ToString();
        SelectCombo(VersionBox, p.Version);
        _communities = p.CommunityList();
        CommunityBox.Text = _communities[0];
        UpdateCommunityListButton();
        UserBox.Text = p.User;
        SelectCombo(AuthBox, p.AuthType);
        AuthPasswordBox.Password = p.AuthPassword;
        SelectCombo(PrivacyBox, p.PrivacyType);
        PrivacyPasswordBox.Password = p.PrivacyPassword;
        ContextBox.Text = p.Context;
        UpdateVersionFields();
    }
    private static void SelectCombo(ComboBox box, string value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
    private void CommunityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_communities.Count == 0) _communities.Add(CommunityBox.Text); else _communities[0] = CommunityBox.Text;
        UpdateCommunityListButton();
    }
    private void CommunityList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CommunityListWindow(_communities.Where(x => x.Length > 0), _language) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _communities = dialog.Result.Count > 0 ? dialog.Result : [""];
        CommunityBox.Text = _communities[0];
        UpdateCommunityListButton();
    }
    // Writes the connection settings shown in the configuration panel (not the target,
    // which is never part of a profile) back to the selected profile.
    private void SaveToProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not SnmpProfile p) return;
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = Tr("Ungültiger Port. Profil wurde nicht gespeichert.", "Invalid port. The profile was not saved.");
            return;
        }
        if (MessageBox.Show(Tr($"Aktuelle SNMP-Einstellungen (Port, Version, Community-Liste, v3-Zugangsdaten) im Profil \"{p.Name}\" speichern?", $"Save the current SNMP settings (port, version, community list, v3 credentials) to profile \"{p.Name}\"?"),
                Tr("Profil speichern", "Save profile"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        p.Port = port; p.Version = Selected(VersionBox);
        p.Communities = _communities.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList(); p.Community = p.Communities.FirstOrDefault() ?? "";
        p.User = UserBox.Text; p.AuthType = Selected(AuthBox); p.AuthPassword = AuthPasswordBox.Password;
        p.PrivacyType = Selected(PrivacyBox); p.PrivacyPassword = PrivacyPasswordBox.Password; p.Context = ContextBox.Text;
        SaveSettings();
        StatusText.Text = Tr($"Profil \"{p.Name}\" gespeichert.", $"Profile \"{p.Name}\" saved.");
        Log(StatusText.Text);
    }
    private void UpdateCommunityListButton()
    {
        var count = _communities.Count(x => x.Length > 0);
        CommunityListButton.Content = Tr($"☰  Liste ({count})", $"☰  List ({count})");
        CommunityListButton.ToolTip = Tr("Community-Liste bearbeiten (wird der Reihe nach versucht)", "Edit the community list (tried in order)");
    }
    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVersionFields();
    private void UpdateVersionFields()
    {
        if (!IsInitialized) return;
        var isV3 = Selected(VersionBox) == "v3";
        CommunityLabel.Visibility = CommunityBox.Visibility = CommunityListButton.Visibility = isV3 ? Visibility.Collapsed : Visibility.Visible;
        V3Panel.Visibility = isV3 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(Tr("SNMP MibBrowser\n\nOpen-Source-WPF-Anwendung unter der MIT-Lizenz.\nSNMP-Kommunikation: SharpSnmpLib (MIT).", "SNMP MibBrowser\n\nOpen-source WPF application licensed under MIT.\nSNMP communication: SharpSnmpLib (MIT)."), Tr("Über SNMP MibBrowser", "About SNMP MibBrowser"), MessageBoxButton.OK, MessageBoxImage.Information);

    private void ThemeButton_Click(object sender, RoutedEventArgs e) { _dark = !_dark; ApplyTheme(); SaveSettings(); }
    private void LanguageButton_Click(object sender, RoutedEventArgs e) { _language = _language == "de" ? "en" : "de"; ApplyLanguage(); SaveSettings(); }

    private void ApplyTheme()
    {
        SetBrush("WindowBrush", _dark ? "#171922" : "#F2F5F9"); SetBrush("SurfaceBrush", _dark ? "#222530" : "#FFFFFF");
        SetBrush("SurfaceAltBrush", _dark ? "#2B2F3C" : "#F7F9FC"); SetBrush("TextBrush", _dark ? "#F2F3F7" : "#172033");
        SetBrush("SubtleTextBrush", _dark ? "#AEB5C5" : "#68758A"); SetBrush("BorderBrush", _dark ? "#3A3F4E" : "#DDE3EC");
        SetBrush("InputBrush", _dark ? "#1D2029" : "#FFFFFF"); SetBrush("CompareChangedBrush", _dark ? "#5C2A44" : "#FFD6E5"); SetBrush("SelectionBrush", _dark ? "#493667" : "#E9DFF7");
        Application.Current.Resources[SystemColors.WindowBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_dark ? "#222530" : "#FFFFFF"));
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_dark ? "#F2F3F7" : "#172033"));
        Application.Current.Resources[SystemColors.ControlBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_dark ? "#2B2F3C" : "#F7F9FC"));
        Application.Current.Resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_dark ? "#F2F3F7" : "#172033"));
        Application.Current.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_dark ? "#7350A8" : "#E9DFF7"));
        ThemeButton.Content = _dark ? Tr("☀  Hell", "☀  Light") : Tr("☾  Dunkel", "☾  Dark");
    }

    private static void SetBrush(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private void ApplyLanguage()
    {
        var de = _language == "de";
        SubtitleText.Text = de ? "Netzwerkdiagnose und MIB-Explorer" : "Network diagnostics and MIB explorer";
        LanguageButton.ToolTip = de ? "Zu Englisch wechseln" : "Switch to German"; LoadMibButton.Content = de ? "＋  MIB laden" : "＋  Load MIB";
        LoadFolderButton.Content = de ? "▣  MIB-Ordner" : "▣  MIB folder"; ClearButton.Content = de ? "⌫  Leeren" : "⌫  Clear"; AboutButton.Content = de ? "ⓘ  Info" : "ⓘ  About";
        MibLibraryTitle.Text = de ? "MIB-Bibliothek" : "MIB library"; WorkspaceTitle.Text = de ? "SNMP-Arbeitsbereich" : "SNMP workspace";
        WorkspaceSubtitle.Text = de ? "Geräte abfragen und Managementinformationen untersuchen." : "Query devices and inspect management information.";
        ConnectionGroup.Header = de ? "Verbindung" : "Connection"; DataTypeLabel.Text = de ? "Datentyp" : "Data type"; ValueLabel.Text = de ? "Wert" : "Value";
        SetTab.Header = "SNMP SET"; ApplySetButton.Content = de ? "✎  Wert schreiben" : "✎  Write value";
        WorkspacePanel.Header = de ? "SNMP-Konfiguration" : "SNMP configuration"; ToolbarTargetLabel.Text = ToolbarTargetLabel2.Text = de ? "Ziel" : "Target";
        CancelButton.Content = de ? "Abbrechen" : "Cancel";
        ResultsTab.Header = de ? "Ergebnisse" : "Results"; DetailsPanel.Header = de ? "MIB-Details" : "MIB details"; WalkTableTab.Header = de ? "WALK-Tabelle" : "WALK table"; LogTab.Header = de ? "Protokoll" : "Log";
        InterfacesTab.Header = "Interfaces"; IpTab.Header = de ? "IP-Adressen" : "IP addresses"; RoutesTab.Header = de ? "Routing" : "Routing"; ArpTab.Header = "ARP"; LldpTab.Header = de ? "LLDP-Nachbarn" : "LLDP neighbors";
        LoadInterfacesButton.Content = de ? "↻  Interfaces laden" : "↻  Load interfaces"; LoadIpButton.Content = de ? "↻  IP-Adressen laden" : "↻  Load IP addresses";
        LoadRoutesButton.Content = de ? "↻  Routen laden" : "↻  Load routes"; LoadArpButton.Content = de ? "↻  ARP-Tabelle laden" : "↻  Load ARP table"; LoadLldpButton.Content = de ? "↻  LLDP-Nachbarn laden" : "↻  Load LLDP neighbors";
        ProfileLabel.Text = ToolbarProfileLabel.Text = de ? "Profil" : "Profile"; SettingsButton.Content = de ? "⚙  Optionen" : "⚙  Options"; UpdateButton.Content = de ? "↻  Updates" : "↻  Updates";
        SearchLabel.Text = de ? "MIB-Baum durchsuchen" : "Search MIB tree"; ClearSearchButton.ToolTip = de ? "Suche löschen" : "Clear search"; ContextCopyItem.Header = de ? "OID kopieren" : "Copy OID"; ContextDetailsItem.Header = de ? "Details anzeigen" : "Show details";
        SearchBox.ToolTip = de ? "Nach Name, OID oder Beschreibung suchen" : "Search by name, OID or description";
        WalkDepthLabel.Text = de ? "Tiefe" : "Depth"; WalkDepthBox.ToolTip = de ? "Maximale WALK-Tiefe unterhalb der OID (0 = unbegrenzt)" : "Maximum WALK depth below the OID (0 = unlimited)";
        UpdateCommunityListButton();
        MonitorButton.Content = de ? "📈  Überwachung" : "📈  Monitor"; MonitorButton.ToolTip = de ? "OIDs über die Zeit beobachten" : "Watch OIDs over time";
        ResultsMonitorItem.Header = CompareMonitorItem.Header = ResourcesMonitorItem.Header = ContextMonitorItem.Header = de ? "Zur Überwachung hinzufügen" : "Add to monitor";
        MultiTargetButton.Content = de ? "▦  Mehrere Ziele" : "▦  Multi-target"; MultiTargetButton.ToolTip = de ? "Viele Ziele gleichzeitig abfragen" : "Query many targets at once";
        ResourcesTab.Header = de ? "Ressourcen" : "Resources"; LoadResourcesButton.Content = de ? "↻  Ressourcen prüfen" : "↻  Check resources";
        ResourcesGrid.Columns[0].Header = de ? "Kategorie" : "Category"; ResourcesGrid.Columns[1].Header = de ? "Element" : "Item"; ResourcesGrid.Columns[2].Header = de ? "Wert" : "Value";
        ResourcesGrid.Columns[3].Header = de ? "Quelle" : "Source"; ResourcesGrid.Columns[4].Header = "OID";
        ResourcesCopyOidItem.Header = de ? "OID kopieren" : "Copy OID"; ResourcesCopyRowsItem.Header = de ? "Zeilen kopieren (Excel)" : "Copy rows (Excel)";
        WalkCompareTab.Header = de ? "WALK-Vergleich" : "WALK compare"; CompareIntervalLabel.Text = de ? "Intervall" : "Interval"; CompareRoundsLabel.Text = de ? "Runden" : "Rounds";
        CompareDepthLabel.Text = de ? "Tiefe" : "Depth"; CompareStartButton.Content = de ? "▶  Start" : "▶  Start"; CompareStopButton.Content = de ? "■  Stopp" : "■  Stop";
        CompareChangedOnlyBox.Content = de ? "Nur geänderte" : "Changed only"; CompareCopyOidItem.Header = de ? "OID kopieren" : "Copy OID"; CompareCopyRowsItem.Header = de ? "Zeilen kopieren (Excel)" : "Copy rows (Excel)";
        CompareOidBox.ToolTip = de ? "Leer lassen, um die OID aus der Symbolleiste zu verwenden" : "Leave empty to use the toolbar OID";
        RecentTargetsButton.ToolTip = de ? "Zuletzt verwendete Ziele (↓)" : "Recent targets (↓)";
        CopyValueItem.Header = de ? "Wert kopieren" : "Copy value"; CopyOidItem.Header = de ? "OID kopieren" : "Copy OID";
        CopyOidValueItem.Header = de ? "OID = Wert kopieren" : "Copy OID = value"; CopyRowsItem.Header = de ? "Zeilen kopieren (Excel)" : "Copy rows (Excel)";
        SaveToProfileButton.Content = de ? "💾  Im Profil speichern" : "💾  Save to profile"; SaveToProfileButton.ToolTip = de ? "Diese SNMP-Einstellungen im ausgewählten Profil speichern" : "Save these SNMP settings to the selected profile";
        QueryOidLabel.Text = "OID"; QueryOidBox.ToolTip = de ? "Numerische OID (z. B. 1.3.6.1.4.1.9999.1.1.0)" : "Numeric OID (e.g. 1.3.6.1.4.1.9999.1.1.0)";
        ThemeButton.Content = _dark ? (de ? "☀  Hell" : "☀  Light") : (de ? "☾  Dunkel" : "☾  Dark");
        ResultsGrid.Columns[0].Header = de ? "Zeit" : "Time"; ResultsGrid.Columns[1].Header = de ? "Vorgang" : "Operation"; ResultsGrid.Columns[2].Header = "OID"; ResultsGrid.Columns[3].Header = "Name"; ResultsGrid.Columns[4].Header = de ? "Typ" : "Type"; ResultsGrid.Columns[5].Header = de ? "Wert" : "Value"; ResultsGrid.Columns[6].Header = de ? "Dauer" : "Duration";
        InterfacesGrid.Columns[1].Header = "Name"; InterfacesGrid.Columns[2].Header = de ? "Typ" : "Type"; InterfacesGrid.Columns[4].Header = de ? "Geschwindigkeit" : "Speed"; InterfacesGrid.Columns[6].Header = de ? "Administrativ" : "Admin"; InterfacesGrid.Columns[7].Header = de ? "Betriebsstatus" : "Operational";
        IpGrid.Columns[0].Header = de ? "IP-Adresse" : "IP address"; IpGrid.Columns[1].Header = de ? "Schnittstelle" : "Interface"; IpGrid.Columns[2].Header = de ? "Netzmaske" : "Netmask"; IpGrid.Columns[3].Header = "Broadcast";
        RoutesGrid.Columns[0].Header = de ? "Ziel" : "Destination"; RoutesGrid.Columns[1].Header = de ? "Netzmaske" : "Netmask"; RoutesGrid.Columns[2].Header = de ? "Nächster Hop" : "Next hop"; RoutesGrid.Columns[3].Header = de ? "Schnittstelle" : "Interface"; RoutesGrid.Columns[5].Header = de ? "Typ" : "Type"; RoutesGrid.Columns[6].Header = de ? "Protokoll" : "Protocol";
        ArpGrid.Columns[0].Header = de ? "IP-Adresse" : "IP address"; ArpGrid.Columns[2].Header = de ? "Schnittstelle" : "Interface"; ArpGrid.Columns[3].Header = de ? "Typ" : "Type";
        LldpGrid.Columns[0].Header = de ? "Lokaler Port" : "Local port"; LldpGrid.Columns[1].Header = "Chassis ID"; LldpGrid.Columns[2].Header = de ? "Remote-Port" : "Remote port"; LldpGrid.Columns[3].Header = de ? "Portbeschreibung" : "Port description"; LldpGrid.Columns[4].Header = de ? "Systemname" : "System name"; LldpGrid.Columns[5].Header = de ? "Systembeschreibung" : "System description";
        UpdateMibStatus();
        StatusText.Text = de ? "Bereit" : "Ready";
    }

    private void UpdateMibStatus()
    {
        MibStatus.Text = _mib.LoadedFileCount is int files
            ? Tr($"{_nodes.Count:N0} Objekte aus {files:N0} Datei(en)", $"{_nodes.Count:N0} objects from {files:N0} file(s)")
            : Tr($"{_nodes.Count:N0} Standardobjekte automatisch geladen", $"{_nodes.Count:N0} standard objects loaded automatically");
    }

    private void LoadSettings()
    {
        _settings = _store.Load(out var warning);
        if (warning != null)
            Dispatcher.BeginInvoke(() => Log(Tr($"WARNUNG: settings.json konnte nicht gelesen werden ({warning}); Kopie unter settings.json.bak.", $"WARNING: settings.json could not be read ({warning}); a copy was kept as settings.json.bak.")));
        _language = _settings.Language; _dark = _settings.Dark;
    }

    private void SaveSettings()
    {
        _settings.Language = _language; _settings.Dark = _dark; _settings.RecentTargets = [.. _recentTargets];
        _store.Save(_settings);
    }

    private async Task LoadDefaultMibFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.MibFolder) || !Directory.Exists(_settings.MibFolder)) return;
        await LoadMibsAsync(MibLibrary.MibFilesIn(_settings.MibFolder));
    }
}
