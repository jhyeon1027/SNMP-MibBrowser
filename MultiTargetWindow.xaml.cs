using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Lextm.SharpSnmpLib.Messaging;

namespace SnmpMibBrowser;

// GETs the same OIDs from many targets in parallel, using the SNMP settings of the main window.
public partial class MultiTargetWindow : Window
{
    private const int MaxParallel = 10;
    private const int MaxTargets = 2000;
    private static readonly (string Name, string Oid, bool Checked)[] Presets =
    [
        ("sysName", "1.3.6.1.2.1.1.5.0", true), ("sysDescr", "1.3.6.1.2.1.1.1.0", true), ("sysUpTime", "1.3.6.1.2.1.1.3.0", false),
        ("sysObjectID", "1.3.6.1.2.1.1.2.0", false), ("sysLocation", "1.3.6.1.2.1.1.6.0", false), ("sysContact", "1.3.6.1.2.1.1.4.0", false)
    ];
    private static readonly Regex RangeRx = new(@"^(\d{1,3}\.\d{1,3}\.\d{1,3}\.)(\d{1,3})-(\d{1,3})$", RegexOptions.Compiled);

    private readonly SnmpService _snmp;
    private readonly Func<string, SnmpOptions> _optionsFor;
    private List<string> _communities = ["public"];
    private readonly Action<string> _setMainTarget;
    private readonly bool _de;
    private readonly ObservableCollection<MultiTargetRow> _rows = [];
    private CancellationTokenSource? _cts;
    private readonly Func<UserSettings> _settings;
    private readonly Action _saveSettings;
    // OIDs of the last run; Re-run failed keeps the same columns.
    private List<(string Header, string Oid)>? _lastOids;

    public MultiTargetWindow(SnmpService snmp, Func<string, SnmpOptions> optionsFor, Action<string> setMainTarget, Func<UserSettings> settings, Action saveSettings, string language)
    {
        InitializeComponent();
        _snmp = snmp; _optionsFor = optionsFor; _setMainTarget = setMainTarget; _settings = settings; _saveSettings = saveSettings; _de = language == "de";
        var saved = _settings();
        foreach (var (name, oid, isChecked) in Presets)
            PresetPanel.Children.Add(new CheckBox { Content = name, Tag = oid, IsChecked = saved.MultiPresetOids?.Contains(oid) ?? isChecked, ToolTip = oid, Margin = new Thickness(4, 2, 10, 2), Foreground = (Brush)FindResource("TextBrush") });
        TargetsBox.Text = saved.MultiTargets;
        CustomOidsBox.Text = saved.MultiCustomOids;
        ResultGrid.ItemsSource = _rows;
        // Version and communities start from the main window and can be changed here.
        var main = _optionsFor("");
        _communities = main.Communities is { Count: > 0 } list ? [.. list] : [main.Community];
        CommunityBox.Text = _communities[0];
        VersionBox.SelectedItem = VersionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == main.Version) ?? VersionBox.Items[1];
        Localize();
        TargetsBox_TextChanged(this, null!);
        // The main-window settings can change while this window is open.
        Activated += (_, _) => UpdateSummary();
        Closing += (_, _) => { _cts?.Cancel(); SaveInputs(); };
    }

    private string Tr(string de, string en) => _de ? de : en;

    private void SaveInputs()
    {
        var s = _settings();
        s.MultiTargets = TargetsBox.Text;
        s.MultiPresetOids = PresetPanel.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => x.Tag.ToString()!).ToList();
        s.MultiCustomOids = CustomOidsBox.Text;
        _saveSettings();
    }

    private string SelectedVersion => (VersionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "v2c";
    private SnmpOptions OptionsFor(string host)
    {
        var communities = _communities.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return _optionsFor(host) with { Version = SelectedVersion, Community = communities.FirstOrDefault() ?? "", Communities = communities };
    }
    // Port, timeout, retries and the v3 credentials always come from the main window.
    private void UpdateSummary()
    {
        if (!IsInitialized) return;
        var o = _optionsFor("");
        var v3 = SelectedVersion == "v3" ? $" · v3 user {o.User} ({o.AuthType}/{o.PrivacyType})" : "";
        SummaryText.Text = Tr("Aus dem Hauptfenster: ", "From the main window: ") + $"port {o.Port} · timeout {o.Timeout} ms · retries {o.Retries}{v3}";
    }
    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var v3 = SelectedVersion == "v3";
        CommunityLabel.Visibility = CommunityBox.Visibility = CommunityListButton.Visibility = v3 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSummary();
    }
    private void CommunityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_communities.Count == 0) _communities.Add(CommunityBox.Text); else _communities[0] = CommunityBox.Text;
        UpdateCommunityButton();
    }
    private void CommunityList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CommunityListWindow(_communities.Where(x => x.Length > 0), _de ? "de" : "en") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _communities = dialog.Result.Count > 0 ? dialog.Result : [""];
        CommunityBox.Text = _communities[0];
        UpdateCommunityButton();
    }
    private void UpdateCommunityButton() =>
        CommunityListButton.Content = Tr($"☰  Liste ({_communities.Count(x => x.Length > 0)})", $"☰  List ({_communities.Count(x => x.Length > 0)})");

    public static List<string> ParseTargets(string text)
    {
        var targets = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = RangeRx.Match(line);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var from) && int.TryParse(m.Groups[3].Value, out var to) && from <= to && to <= 255)
                for (var n = from; n <= to; n++) targets.Add(m.Groups[1].Value + n);
            else targets.Add(line);
        }
        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void TargetsBox_TextChanged(object sender, TextChangedEventArgs e) =>
        TargetCountText.Text = Tr($"{ParseTargets(TargetsBox.Text).Count} Ziele", $"{ParseTargets(TargetsBox.Text).Count} targets");

    private List<(string Header, string Oid)>? ReadOids()
    {
        var oids = PresetPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (c.Content.ToString()!, c.Tag.ToString()!)).ToList();
        foreach (var raw in CustomOidsBox.Text.Split('\n'))
        {
            var oid = raw.Trim().TrimStart('.');
            if (oid.Length == 0) continue;
            if (!oid.Split('.').All(part => part.Length > 0 && uint.TryParse(part, out _)))
            {
                ProgressText.Text = Tr($"Ungültige OID: {oid}", $"Invalid OID: {oid}");
                return null;
            }
            oids.Add((oid, oid));
        }
        return oids.DistinctBy(x => x.Item2).ToList();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var targets = ParseTargets(TargetsBox.Text);
        var oids = ReadOids();
        if (oids == null) return;
        if (targets.Count == 0 || oids.Count == 0) { ProgressText.Text = Tr("Bitte Ziele und mindestens eine OID angeben.", "Enter targets and at least one OID."); return; }
        if (targets.Count > MaxTargets) { ProgressText.Text = Tr($"Höchstens {MaxTargets} Ziele.", $"At most {MaxTargets} targets."); return; }
        SaveInputs();
        BuildColumns(oids);
        _lastOids = oids;
        _rows.Clear();
        foreach (var target in targets) _rows.Add(new MultiTargetRow(target, oids.Count));
        await RunRowsAsync([.. _rows], oids);
    }

    // Queries only the failed rows again with the current version and communities; successful rows stay.
    private async void RerunFailed_Click(object sender, RoutedEventArgs e)
    {
        var failed = _rows.Where(r => r.Failed).ToList();
        if (_lastOids == null || failed.Count == 0) { ProgressText.Text = Tr("Keine fehlgeschlagenen Ziele.", "No failed targets."); return; }
        foreach (var row in failed) row.Reset();
        await RunRowsAsync(failed, _lastOids);
    }

    private async Task RunRowsAsync(List<MultiTargetRow> rows, List<(string Header, string Oid)> oids)
    {
        UpdateSummary();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RunButton.IsEnabled = false; StopButton.IsEnabled = true; ClearButton.IsEnabled = false; RerunButton.IsEnabled = false;
        RunProgress.Visibility = Visibility.Visible; RunProgress.Maximum = rows.Count; RunProgress.Value = 0;
        var done = 0;
        var gate = new SemaphoreSlim(MaxParallel);
        async Task RunOne(MultiTargetRow row)
        {
            await gate.WaitAsync(ct);
            try { await QueryAsync(row, oids, ct); }
            finally
            {
                gate.Release();
                done++; RunProgress.Value = done;
                ProgressText.Text = Tr($"{done}/{rows.Count} erledigt", $"{done}/{rows.Count} done");
            }
        }
        try { await Task.WhenAll(rows.Select(RunOne)); }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var row in rows.Where(r => r.Status.Length == 0)) row.SetResult(Tr("Abgebrochen", "Cancelled"), true, "", "");
            _cts.Dispose(); _cts = null;
            RunProgress.Visibility = Visibility.Collapsed;
            var failed = _rows.Count(r => r.Failed);
            RunButton.IsEnabled = true; StopButton.IsEnabled = false; ClearButton.IsEnabled = true; RerunButton.IsEnabled = failed > 0;
            ProgressText.Text = Tr($"{_rows.Count - failed} OK, {failed} fehlgeschlagen", $"{_rows.Count - failed} OK, {failed} failed");
            RefreshFilter();
        }
    }

    private async Task QueryAsync(MultiTargetRow row, List<(string Header, string Oid)> oids, CancellationToken ct)
    {
        var options = OptionsFor(row.Target);
        var sw = Stopwatch.StartNew();
        try
        {
            // One GET per OID: a single unknown OID then only affects its own column (SNMPv1 fails the whole request otherwise).
            // The first GET finds the community; later ones reuse it from the service cache.
            for (var i = 0; i < oids.Count; i++)
            {
                try
                {
                    var answer = await _snmp.GetAsync(options, oids[i].Oid, false, ct);
                    row.Values[i] = answer.Count > 0 ? MainWindow.DisplayValue(answer[0].Data) : "";
                }
                catch (ErrorException ex) { row.Values[i] = "error " + (ex.Body?.Pdu().ErrorStatus.ToInt32() is 2 ? "noSuchName" : ex.Body?.Pdu().ErrorStatus.ToString()); }
            }
            row.SetResult("OK", false, _snmp.CommunityUsed(options) ?? "", $"{sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { row.SetResult(Tr("Abgebrochen", "Cancelled"), true, "", ""); throw; }
        catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or System.TimeoutException) { await SetNoResponseAsync(row, ct); }
        catch (SnmpV3ReportException ex) { row.SetResult("v3: " + ex.Message.Replace("SNMPv3: ", "").TrimEnd('.'), true, "", "", ex.Message); }
        catch (System.Net.Sockets.SocketException ex)
        {
            var (status, detail) = ex.SocketErrorCode switch
            {
                // ICMP "port unreachable": the host is up but nothing listens on the SNMP port.
                System.Net.Sockets.SocketError.ConnectionReset => (Tr("Port geschlossen", "Port closed"), Tr("Host erreichbar, aber kein SNMP-Dienst auf dem Port.", "The host is up but no SNMP service listens on the port.")),
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain => (Tr("Name nicht auflösbar", "Name not resolved"), ex.Message),
                System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable => (Tr("Netz nicht erreichbar", "Network unreachable"), ex.Message),
                _ => (Tr("Netzwerkfehler", "Network error"), ex.Message)
            };
            row.SetResult(status, true, "", "", detail);
        }
        catch (Exception ex) { row.SetResult(Tr("Fehler", "Error"), true, "", "", ex.Message); }
    }

    // No SNMP answer: a ping tells "SNMP not answering" (wrong community/credentials, SNMP off, ACL) from "host not reachable".
    private async Task SetNoResponseAsync(MultiTargetRow row, CancellationToken ct)
    {
        var alive = false;
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            alive = (await ping.SendPingAsync(row.Target, TimeSpan.FromMilliseconds(1000), cancellationToken: ct)).Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
        if (alive)
            row.SetResult(Tr("Kein SNMP (Host aktiv)", "No SNMP (host up)"), true, "", "",
                Tr("Host antwortet auf Ping, aber nicht auf SNMP: falsche Community/v3-Zugangsdaten, SNMP deaktiviert oder ACL blockiert diesen PC.",
                   "The host answers ping but not SNMP: wrong community / v3 credentials, SNMP disabled, or an ACL blocks this PC."));
        else
            row.SetResult(Tr("Keine Antwort (Host aus?)", "No response (host down?)"), true, "", "",
                Tr("Weder SNMP- noch Ping-Antwort: Host aus, falsche IP oder ICMP unterwegs blockiert.",
                   "No reply to SNMP or ping: host down, wrong IP, or ICMP blocked on the way."));
    }

    private void BuildColumns(List<(string Header, string Oid)> oids)
    {
        ResultGrid.Columns.Clear();
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = Tr("Ziel", "Target"), Binding = new Binding(nameof(MultiTargetRow.Target)), SortMemberPath = nameof(MultiTargetRow.SortKey), Width = 120, MinWidth = 100 });
        var statusStyle = new Style(typeof(TextBlock));
        statusStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(MultiTargetRow.Detail))));
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(MultiTargetRow.Status)), ElementStyle = statusStyle, Width = 170, MinWidth = 110 });
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = "Community", Binding = new Binding(nameof(MultiTargetRow.Community)), Width = 100, MinWidth = 80 });
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = Tr("Dauer", "Duration"), Binding = new Binding(nameof(MultiTargetRow.Duration)), Width = 75, MinWidth = 65 });
        for (var i = 0; i < oids.Count; i++)
            ResultGrid.Columns.Add(new DataGridTextColumn { Header = oids[i].Header, Binding = new Binding($"Values[{i}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120 });
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    // Clears the result table only; targets, OIDs and communities stay for the next run.
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _rows.Clear(); ResultGrid.Columns.Clear(); ProgressText.Text = ""; _lastOids = null; RerunButton.IsEnabled = false;
    }
    private void FailedOnly_Changed(object sender, RoutedEventArgs e) => RefreshFilter();
    private void RefreshFilter() =>
        CollectionViewSource.GetDefaultView(_rows).Filter = FailedOnlyBox.IsChecked == true ? item => ((MultiTargetRow)item).Failed : null;

    // ---- copy and hand-over to the main window
    private List<MultiTargetRow> SelectedRows()
    {
        var selected = new HashSet<object>(ResultGrid.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
        return _rows.Where(selected.Contains).ToList();
    }
    private void Copy(string text, int count)
    {
        if (text.Length == 0) return;
        ProgressText.Text = ClipboardText.TrySet(new WindowInteropHelper(this).Handle, text)
            ? Tr($"{count} Zeile(n) kopiert", $"Copied {count} row(s)")
            : Tr("Zwischenablage ist belegt. Bitte erneut versuchen.", "The clipboard is busy. Please try again.");
    }
    private void CopyTargets_Click(object sender, RoutedEventArgs e) { var rows = SelectedRows(); Copy(string.Join(Environment.NewLine, rows.Select(r => r.Target)), rows.Count); }
    private void CopyRows_Click(object sender, RoutedEventArgs e) => CopyRowsWithHeader();
    private void CopyRowsWithHeader()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var header = string.Join("\t", ResultGrid.Columns.Select(c => c.Header?.ToString() ?? ""));
        Copy(header + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(r => string.Join("\t", new[] { r.Target, r.Status, r.Community, r.Duration }.Concat(r.Values)))), rows.Count);
    }
    private void ResultGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.C || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true; CopyRowsWithHeader();
    }
    private void ResultGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow { Item: MultiTargetRow row }) return;
        _setMainTarget(row.Target);
        e.Handled = true;
    }
    private void OpenInMain_Click(object sender, RoutedEventArgs e) { if (SelectedRows().FirstOrDefault() is { } row) _setMainTarget(row.Target); }

    private void Localize()
    {
        Title = Tr("Mehrere Ziele abfragen", "Multi-target query");
        TargetsLabel.Text = Tr("Ziele (eine pro Zeile, Bereiche wie 10.1.1.10-20)", "Targets (one per line, ranges like 10.1.1.10-20)");
        OidsLabel.Text = Tr("Abzufragende OIDs (GET)", "OIDs to GET"); CustomLabel.Text = Tr("Eigene OIDs (eine pro Zeile)", "Custom OIDs (one per line)");
        RunButton.Content = Tr("▶  Starten", "▶  Run"); ClearButton.Content = Tr("⌫  Leeren", "⌫  Clear");
        RerunButton.Content = Tr("↻  Fehlgeschlagene erneut", "↻  Re-run failed");
        RerunButton.ToolTip = Tr("Nur fehlgeschlagene Ziele erneut abfragen (aktuelle Version und Communities, gleiche OIDs)", "Query only the failed targets again (current version and communities, same OIDs)");
        ClearButton.ToolTip = Tr("Ergebnisse löschen (Ziele und OIDs bleiben erhalten)", "Clear the results (targets and OIDs are kept)"); StopButton.Content = Tr("■  Stopp", "■  Stop"); FailedOnlyBox.Content = Tr("Nur fehlgeschlagene", "Failed only");
        CopyTargetsItem.Header = Tr("Ziele kopieren", "Copy targets"); CopyRowsItem.Header = Tr("Zeilen kopieren (Excel)", "Copy rows (Excel)");
        OpenInMainItem.Header = Tr("Als Ziel im Hauptfenster verwenden", "Use as target in main window");
        VersionLabel.Text = "Version"; CommunityLabel.Text = "Community";
        UpdateCommunityButton(); UpdateSummary();
    }
}
