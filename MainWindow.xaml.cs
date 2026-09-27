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
    private readonly MibParser _parser = new();
    private readonly SnmpService _snmp = new();
    private readonly ObservableCollection<QueryResult> _results = [];
    private List<MibNode> _nodes = [];
    private CancellationTokenSource? _cts;
    private string _language = "de";
    private bool _dark;
    private bool _syncingConnectionControls;
    private int? _loadedMibFileCount;
    private string _currentOid = "1.3.6.1.2.1.1.1.0";
    private UserSettings _settings = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SNMP-MibBrowser", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        LoadSettings();
        RefreshProfiles();
        _nodes = StandardMibCatalog.Load().ToList();
        RebuildTree(_nodes);
        ApplyTheme();
        ApplyLanguage();
        UpdateMibStatus();
        Log(Tr("Anwendung gestartet.", "Application started."));
        Closing += (_, _) => SaveSettings();
        Loaded += async (_, _) => await LoadDefaultMibFolderAsync();
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
            var files = Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.AllDirectories)
                .Where(x => new[] { ".mib", ".my", ".txt", "" }.Contains(Path.GetExtension(x).ToLowerInvariant()));
            await LoadMibsAsync(files);
        }
    }

    private async Task LoadMibsAsync(IEnumerable<string> files)
    {
        var list = files.ToList(); SetBusy(true, Tr($"Lade {list.Count} MIB-Dateien…", $"Loading {list.Count} MIB files…"));
        try
        {
            var parsed = await Task.Run(() => _parser.ParseFiles(list));
            var existingOids = _nodes.Select(x => x.Oid).ToHashSet(StringComparer.Ordinal);
            var accepted = parsed.Where(x => existingOids.Contains(x.Oid) || IsImportableOid(x.Oid)).ToList();
            var protectedCount = parsed.Count - accepted.Count;
            // Existing/built-in definitions win. This prevents imported standard MIBs
            // from replacing public catalog entries or appearing twice.
            _nodes = _nodes.Concat(accepted).Where(x => !string.IsNullOrWhiteSpace(x.Oid)).GroupBy(x => x.Oid).Select(x => x.First()).OrderBy(x => x.Oid).ToList();
            RebuildTree(_nodes);
            _loadedMibFileCount = list.Count;
            UpdateMibStatus();
            Log(Tr($"{accepted.Count:N0} MIB-Objekte geladen; {protectedCount:N0} öffentliche Definitionen zum Schutz des eingebauten Katalogs übersprungen.", $"{accepted.Count:N0} MIB objects loaded; {protectedCount:N0} public definitions skipped to protect the built-in catalog."));
        }
        catch (Exception ex) { ShowError(Tr("MIB-Dateien konnten nicht geladen werden", "MIB files could not be loaded"), ex); }
        finally { SetBusy(false, Tr("Bereit", "Ready")); }
    }

    private static bool IsImportableOid(string oid) =>
        oid == "1.3.6.1.4.1" || oid.StartsWith("1.3.6.1.4.1.", StringComparison.Ordinal) ||
        oid == "1.3.6.1.3" || oid.StartsWith("1.3.6.1.3.", StringComparison.Ordinal);

    private void RebuildTree(IEnumerable<MibNode> nodes, string? focusOid = null)
    {
        MibTree.Items.Clear();
        var expandedByDefault = new HashSet<string> { "1", "1.3", "1.3.6", "1.3.6.1", "1.3.6.1.2", "1.3.6.1.2.1", "1.3.6.1.2.1.1" };
        var nodeList = nodes.ToList();
        // Resolve labels against the complete MIB catalog, not only the filtered
        // result set. Otherwise parents of a search result fall back to numeric arcs.
        var exact = _nodes.Concat(nodeList)
            .Where(x => !string.IsNullOrWhiteSpace(x.Oid))
            .GroupBy(x => x.Oid)
            .ToDictionary(x => x.Key, x => x.First());
        var allOids = new HashSet<string>();
        foreach (var node in nodeList)
        {
            var parts = node.Oid.Split('.');
            for (var i = 1; i <= parts.Length; i++) allOids.Add(string.Join('.', parts.Take(i)));
        }
        var labels = OidRootLabels();
        var items = new Dictionary<string, TreeViewItem>();
        foreach (var oid in allOids
            .OrderBy(x => x.Count(c => c == '.'))
            .ThenBy(ParentOid, StringComparer.Ordinal)
            .ThenBy(x => labels.TryGetValue(x, out var canonical) ? canonical : exact.TryGetValue(x, out var node) ? node.Name : x.Split('.').Last(), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(OidSortKey))
        {
            exact.TryGetValue(oid, out var node);
            // Canonical public-tree labels are immutable. Imported aliases may
            // enrich details, but cannot rename the standard OID hierarchy.
            var name = labels.TryGetValue(oid, out var canonical) ? canonical : node?.Name ?? oid.Split('.').Last();
            var model = node ?? new MibNode { Name = name, Oid = oid, Module = "OID tree", Description = "Struktureller OID-Knoten", DescriptionEnglish = "Structural OID node" };
            var expandsToFocus = !string.IsNullOrWhiteSpace(focusOid) &&
                (focusOid == oid || focusOid.StartsWith(oid + ".", StringComparison.Ordinal));
            var item = new TreeViewItem { Header = name, Tag = model, ToolTip = oid, IsExpanded = expandsToFocus || expandedByDefault.Contains(oid) };
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

    private static Dictionary<string, string> OidRootLabels() => new()
    {
        ["1"]="iso", ["1.0"]="standard", ["1.3"]="org", ["1.3.6"]="dod", ["1.3.6.1"]="internet",
        ["1.3.6.1.1"]="directory", ["1.3.6.1.2"]="mgmt", ["1.3.6.1.2.1"]="mib-2", ["1.3.6.1.2.1.10"]="transmission",
        ["1.3.6.1.2.1.2"]="interfaces", ["1.3.6.1.2.1.4"]="ip", ["1.3.6.1.2.1.6"]="tcp", ["1.3.6.1.2.1.7"]="udp",
        ["1.3.6.1.2.1.17"]="dot1dBridge", ["1.3.6.1.2.1.25"]="host", ["1.3.6.1.2.1.47"]="entityMIB",
        ["1.3.6.1.3"]="experimental", ["1.3.6.1.4"]="private", ["1.3.6.1.4.1"]="enterprises",
        ["1.0.8802"]="ieee", ["1.0.8802.1"]="standards", ["1.0.8802.1.1"]="ieee802dot1", ["1.0.8802.1.1.2"]="lldpMIB"
    };

    private static string OidSortKey(string oid) => string.Join('.', oid.Split('.').Select(x => int.Parse(x).ToString("D10")));
    private static string ParentOid(string oid)
    {
        var cut = oid.LastIndexOf('.');
        return cut > 0 ? oid[..cut] : string.Empty;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_nodes.Count == 0) return;
        var q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            RebuildTree(_nodes);
            return;
        }

        var normalizedOid = q.TrimStart('.');
        if (IsNumericOid(normalizedOid))
        {
            // An entered OID may include a scalar or table index that has no MIB
            // node of its own. Select the most specific known object in that case.
            var match = _nodes
                .Where(x => normalizedOid == x.Oid || normalizedOid.StartsWith(x.Oid + ".", StringComparison.Ordinal))
                .OrderByDescending(x => x.Oid.Split('.').Length)
                .FirstOrDefault();
            if (match != null)
            {
                RebuildTree([match], match.Oid);
                return;
            }
        }

        RebuildTree(_nodes.Where(x =>
            x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.Oid.Contains(normalizedOid, StringComparison.Ordinal) ||
            x.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.DescriptionEnglish.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsNumericOid(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('.').All(part => part.Length > 0 && part.All(char.IsDigit));
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private void MibTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: MibNode n }) return;
        _currentOid = QueryOidForNode(n);
        SetOidBox.Text = QueryOidForNode(n);
        var description = _language == "en" && !string.IsNullOrWhiteSpace(n.DescriptionEnglish) ? n.DescriptionEnglish : n.Description;
        DetailsBox.Text = _language == "de"
            ? $"Name:         {n.Name}\r\nOID:          {n.Oid}\r\nModul:        {n.Module}\r\nSyntax:       {n.Syntax}\r\nZugriff:      {n.Access}\r\n\r\nBeschreibung:\r\n{description}"
            : $"Name:         {n.Name}\r\nOID:          {n.Oid}\r\nModule:       {n.Module}\r\nSyntax:       {n.Syntax}\r\nAccess:       {n.Access}\r\n\r\nDescription:\r\n{description}";
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
    private async void GetNext_Click(object sender, RoutedEventArgs e) => await RunQueryAsync("GET NEXT", ct => _snmp.GetAsync(Options(), _currentOid, true, ct));
    private async void Walk_Click(object sender, RoutedEventArgs e) => await RunQueryAsync("WALK", ct => _snmp.WalkAsync(Options(), _currentOid, ct));
    private async void ApplySet_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Tr("Der SET-Befehl verändert einen Wert auf dem Zielgerät. Fortfahren?", "The SET operation changes a value on the target device. Continue?"), Tr("SNMP SET bestätigen", "Confirm SNMP SET"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunQueryAsync("SET", ct => _snmp.SetAsync(Options(), SetOidBox.Text, Selected(DataTypeBox), SetValueBox.Text, ct));
    }
    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteGetAsync();

    private async Task ExecuteGetAsync()
    {
        var oid = NormalizeGetOid(_currentOid);
        _currentOid = oid;
        await RunQueryAsync("GET", ct => _snmp.GetAsync(Options(), oid, false, ct));
    }

    private async void MibTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MibTree.SelectedItem is not TreeViewItem { Tag: MibNode node }) return;
        _currentOid = QueryOidForNode(node);
        if (IsScalar(node)) await ExecuteGetAsync();
        else await RunQueryAsync("WALK", ct => _snmp.WalkAsync(Options(), node.Oid, ct));
        e.Handled = true;
    }

    private MibNode? SelectedMibNode() => (MibTree.SelectedItem as TreeViewItem)?.Tag as MibNode;
    private void MibTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current=e.OriginalSource as DependencyObject;
        while(current!=null && current is not TreeViewItem) current=VisualTreeHelper.GetParent(current);
        if(current is TreeViewItem item){item.IsSelected=true;item.Focus();}
    }
    private async void ContextGet_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n) { _currentOid=QueryOidForNode(n); await ExecuteGetAsync(); } }
    private async void ContextNext_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n) { _currentOid=n.Oid; await RunQueryAsync("GET NEXT",ct=>_snmp.GetAsync(Options(),n.Oid,true,ct)); } }
    private async void ContextWalk_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n) { _currentOid=n.Oid; await RunQueryAsync("WALK",ct=>_snmp.WalkAsync(Options(),n.Oid,ct)); } }
    private void ContextCopy_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode() is { } n) Clipboard.SetText(n.Oid); }
    private void ContextDetails_Click(object sender, RoutedEventArgs e) { if (SelectedMibNode()!=null) DetailsPanel.IsExpanded=true; }

    private string QueryOidForNode(MibNode node) => IsScalar(node) ? node.Oid + ".0" : node.Oid;

    private string NormalizeGetOid(string oid)
    {
        oid = oid.Trim().TrimStart('.');
        if (oid.EndsWith(".0", StringComparison.Ordinal)) return oid;
        var exact = _nodes.FirstOrDefault(n => n.Oid == oid);
        return exact != null && IsScalar(exact) ? oid + ".0" : oid;
    }

    private bool IsScalar(MibNode node)
    {
        if (!node.Access.StartsWith("read", StringComparison.OrdinalIgnoreCase)) return false;
        return !_nodes.Any(parent => node.Oid.StartsWith(parent.Oid + ".", StringComparison.Ordinal) && parent.Syntax.Contains("SEQUENCE", StringComparison.OrdinalIgnoreCase));
    }

    private async void LoadInterfaces_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("Interfaces", new[] { "1.3.6.1.2.1.2.2.1.2", "1.3.6.1.2.1.2.2.1.3", "1.3.6.1.2.1.2.2.1.4", "1.3.6.1.2.1.2.2.1.5", "1.3.6.1.2.1.2.2.1.6", "1.3.6.1.2.1.2.2.1.7", "1.3.6.1.2.1.2.2.1.8", "1.3.6.1.2.1.31.1.1.1.1", "1.3.6.1.2.1.31.1.1.1.15", "1.3.6.1.2.1.31.1.1.1.17" });
        if (c == null) return;
        var hasConnectorData = c[9].Count > 0;
        var rows = Keys(c).Where(k => hasConnectorData ? Val(c,9,k) == "1" : IsPhysicalInterfaceType(Val(c,1,k))).Select(k => new InterfaceRow(k,
            string.IsNullOrWhiteSpace(Val(c,7,k)) ? Val(c,0,k) : Val(c,7,k), InterfaceType(Val(c,1,k)), Val(c,2,k),
            FormatInterfaceSpeed(Val(c,3,k), Val(c,8,k)), Mac(c[4].GetValueOrDefault(k)), Status(Val(c,5,k)), Status(Val(c,6,k)))).ToList();
        InterfacesGrid.ItemsSource = rows;
        StatusText.Text = _language == "de" ? $"Interfaces: {rows.Count} physische Ports" : $"Interfaces: {rows.Count} physical ports";
    }

    private async void LoadIp_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("IP", new[] { "1.3.6.1.2.1.4.20.1.1", "1.3.6.1.2.1.4.20.1.2", "1.3.6.1.2.1.4.20.1.3", "1.3.6.1.2.1.4.20.1.4" });
        if (c == null) return; IpGrid.ItemsSource = Keys(c).Select(k => new IpAddressRow(Val(c,0,k), Val(c,1,k), Val(c,2,k), BroadcastAddress(Val(c,0,k), Val(c,2,k)))).ToList();
    }

    private async void LoadRoutes_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("Routing", new[] { "1.3.6.1.2.1.4.21.1.1", "1.3.6.1.2.1.4.21.1.11", "1.3.6.1.2.1.4.21.1.7", "1.3.6.1.2.1.4.21.1.2", "1.3.6.1.2.1.4.21.1.3", "1.3.6.1.2.1.4.21.1.8", "1.3.6.1.2.1.4.21.1.9" });
        if (c == null) return; RoutesGrid.ItemsSource = Keys(c).Select(k => new RouteRow(Val(c,0,k), Val(c,1,k), Val(c,2,k), Val(c,3,k), Val(c,4,k), RouteType(Val(c,5,k)), RouteProtocol(Val(c,6,k)))).ToList();
    }

    private async void LoadArp_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("ARP", new[] { "1.3.6.1.2.1.4.22.1.3", "1.3.6.1.2.1.4.22.1.2", "1.3.6.1.2.1.4.22.1.1", "1.3.6.1.2.1.4.22.1.4" });
        if (c == null) return; ArpGrid.ItemsSource = Keys(c).Select(k => new ArpRow(Val(c,0,k), Mac(c[1].GetValueOrDefault(k)), Val(c,2,k), ArpType(Val(c,3,k)))).ToList();
    }

    private async void LoadLldp_Click(object sender, RoutedEventArgs e)
    {
        var c = await LoadColumnsAsync("LLDP", new[] { "1.0.8802.1.1.2.1.4.1.1.4", "1.0.8802.1.1.2.1.4.1.1.5", "1.0.8802.1.1.2.1.4.1.1.6", "1.0.8802.1.1.2.1.4.1.1.7", "1.0.8802.1.1.2.1.4.1.1.8", "1.0.8802.1.1.2.1.4.1.1.9", "1.0.8802.1.1.2.1.4.1.1.10" });
        if (c == null) return;
        const string localPortRoot = "1.0.8802.1.1.2.1.3.7.1.3";
        var localPorts = new Dictionary<string, string>();
        try
        {
            var localPortValues = await _snmp.WalkAsync(Options(), localPortRoot, CancellationToken.None);
            localPorts = localPortValues.ToDictionary(v => v.Id.ToString()[(localPortRoot.Length + 1)..], v => v.Data.ToString());
        }
        catch (Exception ex) { Log(Tr($"LLDP-Zuordnung lokaler Ports nicht verfügbar: {ex.Message}", $"LLDP local port mapping unavailable: {ex.Message}")); }
        LldpGrid.ItemsSource = Keys(c).Select(k => new LldpRow(LocalPortName(k, localPorts), LldpChassisId(c[1].GetValueOrDefault(k), Val(c,0,k)), LldpPortId(c[3].GetValueOrDefault(k), Val(c,2,k)), Val(c,4,k), Val(c,5,k), Val(c,6,k))).ToList();
    }

    private async Task<List<Dictionary<string,ISnmpData>>?> LoadColumnsAsync(string feature, IReadOnlyList<string> roots)
    {
        _cts = new CancellationTokenSource(); SetBusy(true, $"{feature}…");
        try
        {
            var columns = new List<Dictionary<string,ISnmpData>>();
            foreach (var root in roots)
            {
                var values = await _snmp.WalkAsync(Options(), root, _cts.Token);
                columns.Add(values.ToDictionary(v => v.Id.ToString()[(root.Length + 1)..], v => v.Data));
            }
            StatusText.Text = _language == "de" ? $"{feature}: {Keys(columns).Count()} Einträge" : $"{feature}: {Keys(columns).Count()} entries";
            Log(Tr($"{feature}: Tabelle geladen.", $"{feature}: table loaded.")); return columns;
        }
        catch (OperationCanceledException) { StatusText.Text = _language == "de" ? "Abgebrochen" : "Cancelled"; return null; }
        catch (Exception ex) { ShowError(Tr($"{feature} fehlgeschlagen", $"{feature} failed"), ex); return null; }
        finally { _cts?.Dispose(); _cts = null; SetBusy(false, StatusText.Text); }
    }

    private static IEnumerable<string> Keys(List<Dictionary<string,ISnmpData>> c) => c.SelectMany(x => x.Keys).Distinct().OrderBy(OidSortKey);
    private static string Val(List<Dictionary<string,ISnmpData>> c, int column, string key) => c[column].TryGetValue(key, out var value) ? value.ToString() : "";
    private static string Status(string value) => value switch { "1" => "up", "2" => "down", "3" => "testing", _ => value };
    private static string FormatSpeed(string value) => ulong.TryParse(value, out var n) ? n >= 1_000_000_000 ? $"{n/1_000_000_000d:0.##} Gbit/s" : n >= 1_000_000 ? $"{n/1_000_000d:0.##} Mbit/s" : n >= 1000 ? $"{n/1000d:0.##} kbit/s" : $"{n} bit/s" : value;
    private static string FormatInterfaceSpeed(string ifSpeed, string highSpeed) =>
        ulong.TryParse(highSpeed, out var mbps) && mbps > 0 ? mbps >= 1000 ? $"{mbps / 1000d:0.##} Gbit/s" : $"{mbps} Mbit/s" : FormatSpeed(ifSpeed);
    private static string Mac(ISnmpData? value)
    {
        if (value is not OctetString octets) return value?.ToString() ?? "";
        var bytes = octets.GetRaw();
        return bytes.Length == 0 ? "" : string.Join(":", bytes.Select(x => x.ToString("X2")));
    }
    private static string InterfaceType(string value) => value switch
    {
        "6" => "ethernetCsmacd", "24" => "softwareLoopback", "53" => "propVirtual", "62" => "fastEther",
        "117" => "gigabitEthernet", "135" => "l2vlan", "161" => "ieee8023adLag", _ => value
    };
    private static bool IsPhysicalInterfaceType(string value) => value is "6" or "62" or "69" or "117";
    private static string BroadcastAddress(string address, string mask)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip) || !System.Net.IPAddress.TryParse(mask, out var netmask)) return "";
        var a = ip.GetAddressBytes(); var m = netmask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return "";
        return new System.Net.IPAddress(a.Zip(m, (x, y) => (byte)(x | ~y)).ToArray()).ToString();
    }
    private static string RouteType(string value) => value switch { "1" => "other", "2" => "invalid", "3" => "direct", "4" => "indirect", _ => value };
    private static string RouteProtocol(string value) => value switch { "1" => "other", "2" => "local", "3" => "netmgmt", "4" => "icmp", "8" => "rip", "13" => "ospf", "14" => "bgp", _ => value };
    private static string ArpType(string value) => value switch { "1" => "other", "2" => "invalid", "3" => "dynamic", "4" => "static", _ => value };
    private static string LldpChassisId(ISnmpData? value, string subtype) => subtype switch { "4" => Mac(value), "5" => NetworkAddress(value), _ => TextOrHex(value) };
    private static string LldpPortId(ISnmpData? value, string subtype) => subtype switch { "3" => Mac(value), "4" => NetworkAddress(value), _ => TextOrHex(value) };
    private static string LocalPortName(string remoteIndex, IReadOnlyDictionary<string, string> localPorts)
    {
        var parts = remoteIndex.Split('.');
        var localPortNumber = parts.Length >= 2 ? parts[^2] : remoteIndex;
        return localPorts.GetValueOrDefault(localPortNumber, localPortNumber);
    }
    private static string NetworkAddress(ISnmpData? value)
    {
        if (value is not OctetString octets) return value?.ToString() ?? "";
        var bytes = octets.GetRaw();
        if (bytes.Length == 5 && bytes[0] == 1) return new System.Net.IPAddress(bytes[1..]).ToString();
        if (bytes.Length == 17 && bytes[0] == 2) return new System.Net.IPAddress(bytes[1..]).ToString();
        return Convert.ToHexString(bytes);
    }
    private static string TextOrHex(ISnmpData? value)
    {
        if (value is not OctetString octets) return value?.ToString() ?? "";
        var bytes = octets.GetRaw();
        return bytes.All(x => x is >= 32 and <= 126) ? System.Text.Encoding.UTF8.GetString(bytes) : Mac(value);
    }

    private async Task RunQueryAsync(string operation, Func<CancellationToken, Task<IList<Variable>>> action)
    {
        _cts = new CancellationTokenSource(); SetBusy(true, Tr($"{operation} wird ausgeführt…", $"Running {operation}…"));
        var sw = Stopwatch.StartNew();
        try
        {
            var answer = await action(_cts.Token); sw.Stop();
            foreach (var v in answer)
            {
                var oid = v.Id.ToString(); var node = ResolveNode(oid);
                _results.Add(new(DateTime.Now.ToString("HH:mm:ss"), operation, oid, node?.Name ?? "", v.Data.TypeCode.ToString(), v.Data.ToString(), $"{sw.ElapsedMilliseconds} ms"));
            }
            if (operation == "WALK") { PopulateWalkTable(answer); DataTabs.SelectedItem = WalkTableTab; }
            StatusText.Text = Tr($"{answer.Count} Antwort(en) in {sw.ElapsedMilliseconds} ms", $"{answer.Count} response(s) in {sw.ElapsedMilliseconds} ms");
            Log(Tr($"{operation} {_currentOid}: {answer.Count} Antwort(en), {sw.ElapsedMilliseconds} ms.", $"{operation} {_currentOid}: {answer.Count} response(s), {sw.ElapsedMilliseconds} ms."));
        }
        catch (OperationCanceledException) { StatusText.Text = Tr("Abgebrochen", "Cancelled"); Log(Tr($"{operation} abgebrochen.", $"{operation} cancelled.")); }
        catch (Exception ex) { ShowError(Tr($"{operation} fehlgeschlagen", $"{operation} failed"), ex); }
        finally { _cts.Dispose(); _cts = null; SetBusy(false, StatusText.Text); }
    }

    private void PopulateWalkTable(IList<Variable> variables)
    {
        var cells = new List<(string Index,string Column,string Value)>();
        foreach (var variable in variables)
        {
            var oid=variable.Id.ToString(); var node=ResolveNode(oid);
            var column=node?.Name ?? oid; var index=node != null && oid.Length>node.Oid.Length ? oid[(node.Oid.Length+1)..] : "0";
            cells.Add((index,column,variable.Data.ToString()));
        }
        var table=new DataTable(); table.Columns.Add(_language=="de"?"Index":"Index");
        foreach(var name in cells.Select(x=>x.Column).Distinct()) table.Columns.Add(name);
        foreach(var group in cells.GroupBy(x=>x.Index).OrderBy(x=>OidSortKey(string.IsNullOrWhiteSpace(x.Key)?"0":x.Key)))
        {
            var row=table.NewRow(); row[0]=group.Key;
            foreach(var cell in group) row[cell.Column]=cell.Value;
            table.Rows.Add(row);
        }
        WalkTableGrid.ItemsSource=table.DefaultView;
    }

    private MibNode? ResolveNode(string oid) => _nodes.Where(n => oid == n.Oid || oid.StartsWith(n.Oid + ".", StringComparison.Ordinal)).OrderByDescending(n => n.Oid.Length).FirstOrDefault();
    private SnmpOptions Options()
    {
        _ = int.TryParse(PortBox.Text, out var port);
        return new SnmpOptions(HostBox.Text.Trim(), port > 0 ? port : 161, Selected(VersionBox), CommunityBox.Text,
            UserBox.Text, Selected(AuthBox), AuthPasswordBox.Password, Selected(PrivacyBox), PrivacyPasswordBox.Password, ContextBox.Text);
    }
    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    private void SetBusy(bool busy, string text) { BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = busy; StatusText.Text = text; }
    private void Log(string text) { LogBox.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\r\n"); LogBox.ScrollToEnd(); }
    private void ShowError(string title, Exception ex) { StatusText.Text = title; Log($"{Tr("FEHLER", "ERROR")}: {title}: {ex.Message}"); MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    private string Tr(string de, string en) => _language == "de" ? de : en;
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
        CommunityBox.Text = p.Community;
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
    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVersionFields();
    private void UpdateVersionFields()
    {
        if (!IsInitialized) return;
        var isV3 = Selected(VersionBox) == "v3";
        CommunityLabel.Visibility = CommunityBox.Visibility = isV3 ? Visibility.Collapsed : Visibility.Visible;
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
        SetBrush("InputBrush", _dark ? "#1D2029" : "#FFFFFF"); SetBrush("SelectionBrush", _dark ? "#493667" : "#E9DFF7");
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
        MibStatus.Text = _loadedMibFileCount is int files
            ? Tr($"{_nodes.Count:N0} Objekte aus {files:N0} Datei(en)", $"{_nodes.Count:N0} objects from {files:N0} file(s)")
            : Tr($"{_nodes.Count:N0} Standardobjekte automatisch geladen", $"{_nodes.Count:N0} standard objects loaded automatically");
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath));
                if (s != null) _settings = s;
            }
        }
        catch { }
        if (_settings.Profiles.Count == 0)
        {
            _settings.Profiles.Add(new SnmpProfile { Name = "Default" });
            _settings.SelectedProfile = "Default";
        }
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepository)) _settings.GitHubRepository = "https://github.com/marlon82/SNMP-MibBrowser";
        _language = _settings.Language; _dark = _settings.Dark;
    }

    private void SaveSettings()
    {
        try { _settings.Language=_language; _settings.Dark=_dark; Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!); File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings,new JsonSerializerOptions{WriteIndented=true})); } catch { }
    }

    private async Task LoadDefaultMibFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.MibFolder) || !Directory.Exists(_settings.MibFolder)) return;
        var files=Directory.EnumerateFiles(_settings.MibFolder,"*",SearchOption.AllDirectories).Where(x=>new[]{".mib",".my",".txt",""}.Contains(Path.GetExtension(x).ToLowerInvariant()));
        await LoadMibsAsync(files);
    }
}
