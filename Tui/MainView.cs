#pragma warning disable CS0618 // TextView (log and details) is obsolete in Terminal.Gui 2.5 but still the built-in read-only text view.
using System.Diagnostics;
using Lextm.SharpSnmpLib;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SnmpMibBrowser.Tui;

/// <summary>Main screen: connection bar, OID bar, MIB tree on the left, result tabs on the right.</summary>
public sealed class MainView : Runnable
{
    private const int MaxRecentTargets = 20;

    private readonly SettingsStore _store;
    private readonly UserSettings _settings;
    private readonly SnmpService _snmp = new();
    private readonly MibLibrary _mib = new();
    private readonly List<QueryResult> _results = [];
    private CancellationTokenSource? _cts;
    private bool _quitting;
    // Session connection settings: a copy of the selected profile, changed by the toolbar and F8.
    private SnmpProfile _conn;
    private string _profileName;
    private readonly List<string> _recentTargets;
    private object? _searchTimer;
    private Dictionary<string, MibTreeItem> _treeItems = [];

    private readonly TextField _host, _community, _port, _oid, _depth, _search;
    private readonly DropDownList _version;
    private readonly Label _communityExtra, _profileLabel, _status, _mibStatus;
    private readonly TreeView<MibTreeItem> _tree;
    private readonly TableView _resultsTable;
    private readonly TextView _details, _log;
    private readonly Tabs _tabs;
    private readonly FrameView _mibFrame;
    private bool _mibVisible = true;
    private readonly View _resultsTab, _detailsTab, _logTab;

    public MainView(SettingsStore store, string? host)
    {
        _store = store;
        _settings = store.Load(out var warning);
        _recentTargets = [.. _settings.RecentTargets.Take(MaxRecentTargets)];
        var profile = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.SelectedProfile) ?? _settings.Profiles[0];
        _profileName = profile.Name;
        _conn = Copy(profile);

        Title = "SNMP MibBrowser";
        Width = Dim.Fill(); Height = Dim.Fill();

        // ---- menu
        var menu = new MenuBar();
        menu.Add(new MenuBarItem("_File",
        [
            new MenuItem("Load MIB _files…", "", LoadMibFiles),
            new MenuItem("Load MIB f_older…", "", LoadMibFolder),
            new MenuItem("_Connection settings…", "F8", EditConnection),
            new Line(),
            new MenuItem("_Quit", "Ctrl+Q", RequestQuit)
        ]));
        menu.Add(new MenuBarItem("_Query",
        [
            new MenuItem("_GET", "F2", () => _ = GetAsync()),
            new MenuItem("GET _NEXT", "F3", () => _ = GetNextAsync()),
            new MenuItem("_WALK", "F4", () => _ = WalkAsync()),
            new MenuItem("_SET…", "F5", () => _ = SetAsync()),
            new MenuItem("_Cancel query", "Esc", CancelQuery)
        ]));
        menu.Add(new MenuBarItem("_Results",
        [
            new MenuItem("Show/hide MIB _tree", "Ctrl+T", ToggleMibTree),
            new MenuItem("_Copy selected rows", "F6", CopySelected),
            new MenuItem("Show as _text", "F7", ShowResultsText),
            new MenuItem("C_lear results", "", ClearResults)
        ]));
        menu.Add(new MenuBarItem("_Help",
        [
            new MenuItem("_Keys", "F1", ShowKeys),
            new MenuItem("_About", "", ShowAbout)
        ]));

        // ---- connection bar
        var hostLabel = new Label { Text = "Hos_t", X = 0, Y = 1 };
        _host = new TextField { X = Pos.Right(hostLabel) + 1, Y = 1, Width = Dim.Func(_ => Viewport.Width >= 100 ? 22 : 16), Text = host ?? "" };
        var recent = new Button { Text = "▾", X = Pos.Right(_host), Y = 1, NoDecorations = true, NoPadding = true, ShadowStyle = null };
        var versionLabel = new Label { Text = "_Version", X = Pos.Right(recent) + 2, Y = 1 };
        _version = Dialogs.Choice(ConnectionDialog.Versions, _conn.Version, 6);
        _version.X = Pos.Right(versionLabel) + 1; _version.Y = 1;
        var communityLabel = new Label { Text = "Co_mmunity", X = Pos.Right(_version) + 2, Y = 1 };
        _community = new TextField { X = Pos.Right(communityLabel) + 1, Y = 1, Width = Dim.Func(_ => Viewport.Width >= 100 ? 16 : 10) };
        _communityExtra = new Label { X = Pos.Right(_community) + 1, Y = 1, Width = 4 };
        var portLabel = new Label { Text = "_Port", X = Pos.Right(_communityExtra) + 1, Y = 1 };
        _port = new TextField { X = Pos.Right(portLabel) + 1, Y = 1, Width = 6 };

        // ---- OID bar
        var oidLabel = new Label { Text = "_OID", X = 0, Y = 2 };
        _oid = new TextField { X = Pos.Left(_host), Y = 2, Width = Dim.Fill(57), Text = "1.3.6.1.2.1.1.1.0" };
        var depthLabel = new Label { Text = "_Depth", X = Pos.Right(_oid) + 1, Y = 2 };
        _depth = new TextField { X = Pos.Right(depthLabel) + 1, Y = 2, Width = 4, Text = "2" };
        var get = new Button { Text = "_GET", X = Pos.Right(_depth) + 1, Y = 2, ShadowStyle = null };
        var next = new Button { Text = "_NEXT", X = Pos.Right(get), Y = 2, ShadowStyle = null };
        var walk = new Button { Text = "_WALK", X = Pos.Right(next), Y = 2, ShadowStyle = null };
        var set = new Button { Text = "S_ET", X = Pos.Right(walk), Y = 2, ShadowStyle = null };
        var clear = new Button { Text = "C_lear", X = Pos.Right(set), Y = 2, ShadowStyle = null };

        // ---- MIB tree
        var mibFrame = _mibFrame = new FrameView { Title = "MIB", X = 0, Y = 3, Width = Dim.Func(_ => !_mibVisible ? 0 : Viewport.Width >= 120 ? 38 : Viewport.Width >= 100 ? 32 : 26), Height = Dim.Fill(2) };
        var searchHint = new Label { Text = "_Search (name, OID, text)", X = 0, Y = 0, Width = Dim.Fill() };
        _search = new TextField { X = 0, Y = 1, Width = Dim.Fill() };
        _tree = new TreeView<MibTreeItem>
        {
            X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1),
            TreeBuilder = new DelegateTreeBuilder<MibTreeItem>(i => i.Children, i => i.Children.Count > 0),
            AspectGetter = i => i.Label
        };
        _mibStatus = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
        mibFrame.Add(searchHint, _search, _tree, _mibStatus);

        // ---- result tabs
        _resultsTable = new TableView
        {
            Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, MultiSelect = true,
            Table = new RowTableSource<QueryResult>(_results,
                ("Time", r => r.Timestamp), ("Op", r => r.Operation), ("OID", r => r.Oid), ("Name", r => r.Name),
                ("Type", r => ShortType(r.Type)), ("ms", r => r.Duration.Replace(" ms", "")), ("Value", r => r.Value))
        };
        _resultsTable.FrameChanged += (_, _) => FitResultColumns();
        _resultsTab = new View { Title = "Results", Width = Dim.Fill(), Height = Dim.Fill() };
        _resultsTab.Add(_resultsTable);
        _details = new TextView { Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        _detailsTab = new View { Title = "MIB details", Width = Dim.Fill(), Height = Dim.Fill() };
        _detailsTab.Add(_details);
        _log = new TextView { Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        _logTab = new View { Title = "Log", Width = Dim.Fill(), Height = Dim.Fill() };
        _logTab.Add(_log);
        _tabs = new Tabs { X = Pos.Right(mibFrame), Y = 3, Width = Dim.Fill(), Height = Dim.Fill(2) };
        _tabs.Add(_resultsTab, _detailsTab, _logTab);
        _tabs.Value = _resultsTab;

        // ---- status line and key bar
        _profileLabel = new Label { X = Pos.AnchorEnd(), Y = Pos.AnchorEnd(2) };
        _status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = "Ready" };
        var keys = new StatusBar();
        keys.Add(
            Key_(Key.F1, "Help", ShowKeys), Key_(Key.F2, "GET", () => _ = GetAsync()), Key_(Key.F3, "NEXT", () => _ = GetNextAsync()),
            Key_(Key.F4, "WALK", () => _ = WalkAsync()), Key_(Key.F5, "SET", () => _ = SetAsync()), Key_(Key.F6, "Copy", CopySelected),
            Key_(Key.F7, "Text", ShowResultsText), Key_(Key.F8, "Conn", EditConnection), Key_(Key.T.WithCtrl, "Tree", ToggleMibTree), Key_(Key.Q.WithCtrl, "Quit", RequestQuit));

        Add(menu, hostLabel, _host, recent, versionLabel, _version, communityLabel, _community, _communityExtra, portLabel, _port,
            oidLabel, _oid, depthLabel, _depth, get, next, walk, set, clear, mibFrame, _tabs, _status, _profileLabel, keys);

        // ---- events
        get.Accepting += (_, e) => { e.Handled = true; _ = GetAsync(); };
        next.Accepting += (_, e) => { e.Handled = true; _ = GetNextAsync(); };
        walk.Accepting += (_, e) => { e.Handled = true; _ = WalkAsync(); };
        set.Accepting += (_, e) => { e.Handled = true; _ = SetAsync(); };
        clear.Accepting += (_, e) => { e.Handled = true; ClearResults(); };
        recent.Accepting += (_, e) => { e.Handled = true; ShowRecentTargets(); };
        // Enter in the host field reads sysDescr, Enter in the OID field runs GET (as in the Windows app).
        _host.Accepting += (_, e) => { e.Handled = true; _oid.Text = "1.3.6.1.2.1.1.1.0"; _ = GetAsync(); };
        _host.KeyDown += (_, key) => { if (key == Key.CursorDown) { key.Handled = true; ShowRecentTargets(); } };
        _oid.Accepting += (_, e) => { e.Handled = true; _ = GetAsync(); };
        _depth.Accepting += (_, e) => { e.Handled = true; _ = WalkAsync(); };
        _version.TextChanged += (_, _) => { _conn.Version = _version.Text; UpdateConnectionBar(); };
        _community.TextChanged += (_, _) =>
        {
            var list = _conn.CommunityList();
            list[0] = _community.Text;
            _conn.Communities = list; _conn.Community = list[0];
            UpdateConnectionBar();
        };
        _search.TextChanged += (_, _) => ScheduleSearch();
        _search.Accepting += (_, e) => { e.Handled = true; ApplySearch(); _tree.SetFocus(); };
        _tree.SelectionChanged += (_, e) => { if (e.NewValue is { } item) ShowNode(item.Node); };
        _tree.Accepting += (_, e) => { e.Handled = true; ActivateNode(); };
        _resultsTable.Accepting += (_, e) => { e.Handled = true; ShowResultRow(); };
        _snmp.CommunityAccepted += (h, community, index, total) =>
            App?.Invoke(() => Log($"{h}: community \"{community}\" accepted (attempt {index}/{total})."));
        // Esc cancels a running query instead of closing the program; Ctrl+Q quits.
        IsRunningChanging += (_, e) =>
        {
            if (e.NewValue || _quitting) return;
            e.Cancel = true;
            if (_cts != null) CancelQuery();
            else SetStatus("Ctrl+Q quits the program.");
        };

        ApplyConnection();
        RebuildTree(_mib.Nodes);
        UpdateMibStatus();
        Log($"SNMP MibBrowser (terminal) {GetType().Assembly.GetName().Version} started. Settings: {_store.FilePath}");
        if (warning != null) Log($"WARNING: settings.json could not be read ({warning}); a copy was kept as settings.json.bak.");
        if (!string.IsNullOrWhiteSpace(_settings.MibFolder) && Directory.Exists(_settings.MibFolder))
            LoadMibs(MibLibrary.MibFilesIn(_settings.MibFolder).ToList());
        _host.SetFocus();
    }

    private static Shortcut Key_(Key key, string title, Action action) =>
        new() { Key = key, Title = title, BindKeyToApplication = true, Action = action };

    public void ShowUnexpectedError(Exception ex)
    {
        Log($"ERROR: {ex.Message}");
        Dialogs.Error(App!, "Unexpected error", $"{ex.Message}\n\nDetails: {_store.ErrorLogPath}");
    }

    // ================= connection

    private static SnmpProfile Copy(SnmpProfile p) => new()
    {
        Name = p.Name, Port = p.Port, Version = p.Version, Community = p.Community, Communities = [.. p.CommunityList()],
        User = p.User, AuthType = p.AuthType, AuthPassword = p.AuthPassword, PrivacyType = p.PrivacyType, PrivacyPassword = p.PrivacyPassword,
        Context = p.Context, Timeout = p.Timeout, Retries = p.Retries
    };

    private void ApplyConnection()
    {
        _version.Text = _conn.Version;
        _community.Text = _conn.CommunityList()[0];
        _port.Text = _conn.Port.ToString();
        UpdateConnectionBar();
    }

    private void UpdateConnectionBar()
    {
        var v3 = _conn.Version == "v3";
        _community.Enabled = !v3;
        var extra = _conn.CommunityList().Count(c => c.Length > 0) - 1;
        _communityExtra.Text = v3 ? "" : extra > 0 ? $"+{extra}" : "";
        _profileLabel.Text = v3 ? $"Profile: {_profileName} · v3 {(_conn.User.Length > 0 ? _conn.User : "?")} ({_conn.AuthType}/{_conn.PrivacyType})" : $"Profile: {_profileName}";
        // The status text uses the rest of the line.
        _status.Width = Dim.Fill(_profileLabel.Text.Length + 2);
    }

    private SnmpOptions Options()
    {
        _ = int.TryParse(_port.Text, out var port);
        var communities = _conn.CommunityList().Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new SnmpOptions(_host.Text.Trim(), port is > 0 and < 65536 ? port : 161, _version.Text, communities.FirstOrDefault() ?? "",
            _conn.User, _conn.AuthType, _conn.AuthPassword, _conn.PrivacyType, _conn.PrivacyPassword, _conn.Context,
            _conn.Timeout, _conn.Retries, communities);
    }

    private void EditConnection()
    {
        if (int.TryParse(_port.Text, out var port)) _conn.Port = port;
        using var dialog = new ConnectionDialog(_settings, _profileName, _conn);
        while (true)
        {
            App!.Run(dialog);
            if (dialog.Canceled) { SaveSettings(); return; }   // profiles may have been added or deleted
            if (dialog.TryRead(out var error)) break;
            Dialogs.Error(App!, "Connection settings", error);
        }
        _conn = dialog.Connection;
        _profileName = dialog.ProfileName;
        _settings.SelectedProfile = _profileName;
        if (dialog.SaveToProfile && _settings.Profiles.FirstOrDefault(p => p.Name == _profileName) is { } target)
        {
            var index = _settings.Profiles.IndexOf(target);
            _settings.Profiles[index] = Copy(_conn);
            _settings.Profiles[index].Name = _profileName;
            Log($"Profile \"{_profileName}\" saved.");
        }
        SaveSettings();
        ApplyConnection();
        SetStatus($"Connection: {_conn.Version}, port {_conn.Port}, timeout {_conn.Timeout} ms, retries {_conn.Retries}");
    }

    private void ShowRecentTargets()
    {
        var choice = Dialogs.Pick(App!, "Recent targets", _recentTargets, _recentTargets.Count > 0 ? "Clear history" : null);
        if (choice == null) { if (_recentTargets.Count == 0) SetStatus("No successful targets yet."); return; }
        if (choice == "\0Clear history") { _recentTargets.Clear(); SaveSettings(); SetStatus("Recent targets cleared."); return; }
        _host.Text = choice;
        _host.SetFocus();
    }

    private void RememberTarget(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _recentTargets.RemoveAll(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
        _recentTargets.Insert(0, host);
        if (_recentTargets.Count > MaxRecentTargets) _recentTargets.RemoveRange(MaxRecentTargets, _recentTargets.Count - MaxRecentTargets);
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.RecentTargets = [.. _recentTargets];
        _settings.SelectedProfile = _profileName;
        if (!_store.Save(_settings)) Log($"WARNING: settings could not be saved to {_store.FilePath}.");
    }

    // ================= queries

    private bool RejectWhileBusy()
    {
        if (_cts == null) return false;
        SetStatus("A query is already running. Wait for it or press Esc to cancel.");
        return true;
    }

    private bool TryGetOid(out string oid)
    {
        oid = OidUtil.Normalize(_oid.Text);
        if (RejectWhileBusy()) return false;
        if (string.IsNullOrWhiteSpace(_host.Text)) { Fail("Enter a target host."); _host.SetFocus(); return false; }
        if (!OidUtil.IsValid(oid)) { Fail($"Invalid OID: \"{_oid.Text.Trim()}\". Expected a numeric OID such as 1.3.6.1.2.1.1.1.0."); return false; }
        _oid.Text = oid;
        return true;
    }

    private async Task GetAsync()
    {
        if (!TryGetOid(out var input)) return;
        var oid = _mib.NormalizeGetOid(input);
        _oid.Text = oid;
        await RunQueryAsync("GET", oid, (o, ct) => _snmp.GetAsync(o, oid, false, ct));
    }

    // The returned OID goes into the OID field so NEXT can be pressed repeatedly.
    private async Task GetNextAsync()
    {
        if (!TryGetOid(out var oid)) return;
        Variable? next = null;
        await RunQueryAsync("GET NEXT", oid, async (o, ct) =>
        {
            var answer = await _snmp.GetAsync(o, oid, true, ct);
            next = answer.FirstOrDefault();
            return answer;
        });
        if (next != null && next.Data.TypeCode != SnmpType.EndOfMibView) _oid.Text = next.Id.ToString();
    }

    private async Task WalkAsync()
    {
        if (!TryGetOid(out var oid)) return;
        var text = _depth.Text.Trim();
        var depth = 0;
        if (text.Length > 0 && (!int.TryParse(text, out depth) || depth < 0)) { Fail($"Invalid WALK depth: \"{text}\". Expected a number ≥ 0 (0 = unlimited)."); return; }
        var truncated = 0;
        await RunQueryAsync("WALK", oid, async (o, ct) =>
        {
            var answer = await _snmp.WalkAsync(o, oid, ct, depth, () => App?.Invoke(() => LogWalkLimit(oid)));
            var rootArcs = oid.Split('.').Length;
            if (depth > 0) truncated = answer.Count(v => v.Id.ToString().Split('.').Length - rootArcs > depth);
            return answer;
        });
        if (truncated > 0) Log($"WALK {oid}: {truncated} subtrees below depth {depth} skipped (first object shown only).");
    }

    private void LogWalkLimit(string oid) =>
        Log($"WARNING: WALK {oid} stopped after {SnmpService.MaxWalkSteps:N0} requests; the result is incomplete.");

    private async Task SetAsync()
    {
        if (RejectWhileBusy()) return;
        using var dialog = new SetDialog(OidUtil.Normalize(_oid.Text));
        App!.Run(dialog);
        if (dialog.Canceled) return;
        if (!OidUtil.IsValid(dialog.Oid)) { Fail($"Invalid OID: \"{dialog.Oid}\"."); return; }
        if (!Dialogs.Confirm(App!, "Confirm SNMP SET", $"Write {dialog.Type} \"{dialog.Value}\" to {dialog.Oid} on {_host.Text.Trim()}?")) return;
        string oid = dialog.Oid, type = dialog.Type, value = dialog.Value;
        await RunQueryAsync("SET", oid, (o, ct) => _snmp.SetAsync(o, oid, type, value, ct));
    }

    private async Task RunQueryAsync(string operation, string oid, Func<SnmpOptions, CancellationToken, Task<IList<Variable>>> action)
    {
        if (RejectWhileBusy()) return;
        var options = Options();   // one snapshot: the fields may change while the query runs
        _cts = new CancellationTokenSource();
        SetStatus($"Running {operation} on {options.Host}…  (Esc cancels)");
        var sw = Stopwatch.StartNew();
        try
        {
            var answer = await action(options, _cts.Token);
            sw.Stop();
            var firstNew = _results.Count;
            foreach (var v in answer)
            {
                var id = v.Id.ToString();
                _results.Add(new QueryResult(DateTime.Now.ToString("HH:mm:ss"), operation, id, _mib.Resolve(id)?.Name ?? "", v.Data.TypeCode.ToString(),
                    ValueFormat.Display(v.Data), $"{sw.ElapsedMilliseconds} ms", options.Host));
            }
            _resultsTable.Update();
            if (answer.Count > 0)
            {
                _tabs.Value = _resultsTab;
                // Scroll to the first row of this query.
                _resultsTable.SetSelection(0, firstNew, false);
                _resultsTable.EnsureCursorIsVisible();
            }
            RememberTarget(options.Host);
            SetStatus($"{answer.Count} response(s) in {sw.ElapsedMilliseconds} ms");
            Log($"{operation} {oid}: {answer.Count} response(s), {sw.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException) { SetStatus("Cancelled"); Log($"{operation} cancelled."); }
        catch (Exception ex) { Fail($"{operation} failed: {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; }
    }

    private void CancelQuery() { if (_cts != null) { _cts.Cancel(); SetStatus("Cancelling…"); } }

    // ================= MIB tree

    private void RebuildTree(IEnumerable<MibNode> nodes, string? focusOid = null)
    {
        var items = _treeItems = new Dictionary<string, MibTreeItem>();
        var roots = new List<MibTreeItem>();
        foreach (var entry in _mib.BuildTree(nodes))
        {
            var item = new MibTreeItem(entry.Label, entry.Node);
            items[entry.Oid] = item;
            if (items.TryGetValue(OidUtil.Parent(entry.Oid), out var parent)) parent.Children.Add(item); else roots.Add(item);
        }
        _tree.ClearObjects();
        _tree.AddObjects(roots);
        foreach (var (oid, item) in items)
            if (MibLibrary.ExpandedByDefault.Contains(oid) || (focusOid != null && (focusOid == oid || focusOid.StartsWith(oid + ".", StringComparison.Ordinal))))
                _tree.Expand(item);
        if (focusOid != null && items.TryGetValue(focusOid, out var focus)) _tree.GoTo(focus);
    }

    private void ScheduleSearch()
    {
        if (_searchTimer != null) App?.RemoveTimeout(_searchTimer);
        _searchTimer = App?.AddTimeout(TimeSpan.FromMilliseconds(300), () => { _searchTimer = null; ApplySearch(); return false; });
    }

    private void ApplySearch()
    {
        var (matches, focus) = _mib.Search(_search.Text);
        var searching = _search.Text.Trim().Length > 0;
        // A text search selects its best match (exact name, then name prefix, name part, description), so Enter acts on it.
        if (searching && focus == null && matches.Count > 0)
        {
            var q = _search.Text.Trim();
            focus = matches.OrderBy(m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                .ThenBy(m => OidUtil.SortKey(m.Oid)).First().Oid;
        }
        RebuildTree(matches, focus);
        if (searching && matches.Count is > 0 and <= 50) { _tree.ExpandAll(); if (focus != null && _treeItems.TryGetValue(focus, out var item)) _tree.GoTo(item); }
    }

    private void ShowNode(MibNode node)
    {
        _oid.Text = _mib.QueryOidFor(node);
        _details.Text = _mib.Details(node, Lang.English);
    }

    // Enter on a tree node: GET for scalars, WALK for tables and branches (double-click in the Windows app).
    private void ActivateNode()
    {
        if (_tree.SelectedObject is not { } item || RejectWhileBusy()) return;
        if (_mib.IsScalar(item.Node)) { _oid.Text = _mib.QueryOidFor(item.Node); _ = GetAsync(); }
        else { _oid.Text = item.Node.Oid; _ = WalkAsync(); }
    }

    private void LoadMibFiles()
    {
        using var dialog = new OpenDialog { Title = "Load MIB files", AllowsMultipleSelection = true };
        App!.Run(dialog);
        if (!dialog.Canceled && dialog.FilePaths.Count > 0) LoadMibs([.. dialog.FilePaths]);
    }

    private void LoadMibFolder()
    {
        var folder = Dialogs.Input(App!, "Load MIB folder", "Folder with MIB files (searched recursively):", _settings.MibFolder.Length > 0 ? _settings.MibFolder : Environment.CurrentDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!Directory.Exists(folder)) { Fail($"Folder not found: {folder}"); return; }
        LoadMibs(MibLibrary.MibFilesIn(folder).ToList());
        if (Dialogs.Confirm(App!, "MIB folder", $"Load this folder automatically at every start?\n{folder}")) { _settings.MibFolder = folder; SaveSettings(); }
    }

    private void LoadMibs(List<string> files)
    {
        try
        {
            SetStatus($"Loading {files.Count} MIB files…");
            var (accepted, skipped) = _mib.Load(files);
            RebuildTree(_mib.Nodes);
            UpdateMibStatus();
            Log($"{accepted:N0} MIB objects loaded from {files.Count} file(s); {skipped:N0} public definitions skipped to protect the built-in catalog.");
            SetStatus("Ready");
        }
        catch (Exception ex) { Fail($"MIB files could not be loaded: {ex.Message}"); }
    }

    private void UpdateMibStatus() =>
        _mibStatus.Text = _mib.LoadedFileCount is int files ? $"{_mib.Nodes.Count:N0} objects, {files:N0} file(s)" : $"{_mib.Nodes.Count:N0} standard objects";

    // ================= results

    private void CopySelected()
    {
        var rows = _resultsTable.SelectedRows();
        if (rows.Count == 0) { SetStatus("No rows to copy."); return; }
        TerminalClipboard.Copy(App!, _resultsTable.RowsAsText(rows));
        SetStatus($"Sent {rows.Count} row(s) to the terminal clipboard (OSC 52). If nothing arrives, use F7 (Text).");
    }

    private void ShowResultsText()
    {
        var rows = _resultsTable.SelectedRows();
        if (rows.Count <= 1) rows = Enumerable.Range(0, _results.Count).ToList();
        if (rows.Count == 0) { SetStatus("No results yet."); return; }
        Dialogs.ShowText(App!, $"Results ({rows.Count} rows)", string.Join("\n", rows.Select(r => _results[r]).Select(r => $"{_mib.Symbolic(r.Oid)} = {r.Type}: {r.Value}")),
            "Select with the mouse (Shift+drag, or start with --no-mouse) and copy with the terminal.");
    }

    private void ClearResults()
    {
        _results.Clear();
        _resultsTable.SetSelection(0, 0, false);
        _resultsTable.Update();
        SetStatus("Results cleared.");
    }

    // Time, Op, OID, Name, Type, ms, Value. The widths always add up to the visible width, so the table never
    // scrolls sideways (it has no horizontal scroll bar); long values are shown in full with Enter.
    private void FitResultColumns()
    {
        var width = _resultsTable.Viewport.Width;
        if (width <= 0) return;
        var timeVisible = width >= 70;
        var available = width - (timeVisible ? 8 : 7) - (timeVisible ? 8 : 0) - 8 - 5;   // separators, Time, Op, ms
        var flex = Math.Max(0, available - 16);                                             // OID + Name + Type, Value keeps at least 16
        // The OID matters most, then the name; the type is short anyway.
        var oid = Math.Min(32, Math.Max(10, flex * 65 / 100));
        var name = Math.Min(20, Math.Max(4, (flex - oid) * 60 / 100));
        var type = Math.Min(10, Math.Max(4, flex - oid - name));
        var value = Math.Max(10, available - oid - name - type);
        int[] widths = [8, 8, oid, name, type, 5, value];
        for (var c = 0; c < widths.Length; c++)
        {
            var style = _resultsTable.Style.GetOrCreateColumnStyle(c);
            style.MaxWidth = widths[c];
            style.Visible = c != 0 || timeVisible;
        }
        _resultsTable.MaxCellWidth = widths.Max();
        _resultsTable.ColumnOffset = 0;
        _resultsTable.Update();
    }

    private void ToggleMibTree()
    {
        _mibVisible = !_mibVisible;
        _mibFrame.Visible = _mibVisible;
        SetNeedsLayout();
        SetStatus(_mibVisible ? "MIB tree shown." : "MIB tree hidden (Ctrl+T shows it again).");
    }

    /// <summary>Short type names for the results table; the full name is in the row view (Enter).</summary>
    private static string ShortType(string type) => type switch
    {
        "OctetString" => "String", "ObjectIdentifier" => "OID", "Integer32" => "Int", "Counter32" => "Cnt32", "Counter64" => "Cnt64",
        "Gauge32" => "Gauge", "TimeTicks" => "Ticks", "IPAddress" or "IpAddress" => "IP", "NoSuchObject" => "noSuchObj",
        "NoSuchInstance" => "noSuchInst", "EndOfMibView" => "endOfMib", _ => type
    };

    // Enter / double-click: the whole row, with values that do not fit in the table.
    private void ShowResultRow()
    {
        if (_resultsTable.SelectedRows() is not [var index, ..] || index >= _results.Count) return;
        var r = _results[index];
        Dialogs.ShowText(App!, "Result", $"Time:      {r.Timestamp}\nOperation: {r.Operation}\nTarget:    {r.Target}\nOID:       {r.Oid}\nName:      {_mib.Symbolic(r.Oid)}\nType:      {r.Type}\nDuration:  {r.Duration}\n\nValue:\n{r.Value}",
            "F6 copies the selected rows; select text with the terminal (Shift+drag or --no-mouse).");
    }

    // ================= misc

    /// <summary>Ctrl+Q, menu, or SIGTERM/SIGHUP: ends the program normally so the terminal is restored.</summary>
    public void RequestQuit() { _quitting = true; _cts?.Cancel(); SaveSettings(); App?.RequestStop(); }

    private void ShowKeys() => Dialogs.ShowText(App!, "Keys", """
        F2  GET            Enter in OID field = GET, in Host field = sysDescr
        F3  GET NEXT       the returned OID is put into the OID field
        F4  WALK           Depth limits the levels below the OID (0 = unlimited)
        F5  SET            asks for type and value, then for confirmation
        F6  Copy selected result rows to your PC (OSC 52, if the terminal supports it)
        Enter on a result row: show the whole row (long values)   Alt+L  clear results
        F7  Show results as plain text (select and copy with the terminal)
        F8  Connection settings: profile, version, communities, SNMPv3, timeout
        Esc Cancel a running query          Ctrl+Q  Quit
        Alt+letter  underlined items: Alt+T host, Alt+O OID, Alt+M community, Alt+P port,
                    Alt+D depth, Alt+V version, Alt+S search, Alt+G/N/W/E buttons
        Down in Host field                  recent targets
        Tree: Enter = GET (scalar) or WALK (table/branch), arrows to move/expand
        Ctrl+T  show/hide the MIB tree (more room for the results)
        Tab / Shift+Tab move between fields; F9 or Alt opens the menu.

        Start with --no-mouse to select text with the terminal's own mouse selection.
        """, "");

    private void ShowAbout() => MessageBox.Query(App!, "About", $"SNMP MibBrowser (terminal) {GetType().Assembly.GetName().Version}\n\nOpen source, MIT license.\nSNMP: SharpSnmpLib (MIT). UI: Terminal.Gui (MIT).\n\nSettings: {_store.FilePath}", "OK");

    private void SetStatus(string text) => _status.Text = text;
    private void Fail(string message) { SetStatus(message); Log($"ERROR: {message}"); }

    private void Log(string text)
    {
        _log.Text += $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n";
        _log.MoveEnd();
    }
}

public sealed class MibTreeItem(string label, MibNode node)
{
    public string Label { get; } = label;
    public MibNode Node { get; } = node;
    public List<MibTreeItem> Children { get; } = [];
    public override string ToString() => Label;
}
