using System.Diagnostics;
using System.Text.RegularExpressions;
using Lextm.SharpSnmpLib.Messaging;

namespace SnmpMibBrowser;

/// <summary>GETs the same OIDs from many targets and classifies why a target failed.</summary>
public static class MultiTargetQuery
{
    public const int MaxParallel = 10;
    public const int MaxTargets = 2000;

    public static readonly (string Name, string Oid, bool Checked)[] Presets =
    [
        ("sysName", "1.3.6.1.2.1.1.5.0", true), ("sysDescr", "1.3.6.1.2.1.1.1.0", true), ("sysUpTime", "1.3.6.1.2.1.1.3.0", false),
        ("sysObjectID", "1.3.6.1.2.1.1.2.0", false), ("sysLocation", "1.3.6.1.2.1.1.6.0", false), ("sysContact", "1.3.6.1.2.1.1.4.0", false)
    ];

    private static readonly Regex RangeRx = new(@"^(\d{1,3}\.\d{1,3}\.\d{1,3}\.)(\d{1,3})-(\d{1,3})$", RegexOptions.Compiled);

    /// <summary>One target per line; "a.b.c.x-y" expands to a range, "#" starts a comment line.</summary>
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

    /// <summary>
    /// Queries one target and stores values and status in the row. One GET per OID: a single unknown OID then
    /// only affects its own column (SNMPv1 fails the whole request otherwise). The first GET finds the community;
    /// later ones reuse it from the service cache. Throws OperationCanceledException when cancelled.
    /// </summary>
    public static async Task QueryAsync(SnmpService snmp, SnmpOptions options, MultiTargetRow row, IReadOnlyList<string> oids, Lang lang, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < oids.Count; i++)
            {
                try
                {
                    var answer = await snmp.GetAsync(options, oids[i], false, ct);
                    row.Values[i] = answer.Count > 0 ? ValueFormat.Display(answer[0].Data) : "";
                }
                catch (ErrorException ex) { row.Values[i] = "error " + (ex.Body?.Pdu().ErrorStatus.ToInt32() is 2 ? "noSuchName" : ex.Body?.Pdu().ErrorStatus.ToString()); }
            }
            row.SetResult("OK", false, snmp.CommunityUsed(options) ?? "", $"{sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { row.SetResult(lang.T("Abgebrochen", "Cancelled"), true, "", ""); throw; }
        catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or System.TimeoutException) { await SetNoResponseAsync(row, lang, ct); }
        catch (SnmpV3ReportException ex) { row.SetResult("v3: " + ex.Message.Replace("SNMPv3: ", "").TrimEnd('.'), true, "", "", ex.Message); }
        catch (System.Net.Sockets.SocketException ex)
        {
            var (status, detail) = ex.SocketErrorCode switch
            {
                // ICMP "port unreachable": the host is up but nothing listens on the SNMP port.
                System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionRefused =>
                    (lang.T("Port geschlossen", "Port closed"), lang.T("Host erreichbar, aber kein SNMP-Dienst auf dem Port.", "The host is up but no SNMP service listens on the port.")),
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain => (lang.T("Name nicht auflösbar", "Name not resolved"), ex.Message),
                System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable => (lang.T("Netz nicht erreichbar", "Network unreachable"), ex.Message),
                _ => (lang.T("Netzwerkfehler", "Network error"), ex.Message)
            };
            row.SetResult(status, true, "", "", detail);
        }
        catch (Exception ex) { row.SetResult(lang.T("Fehler", "Error"), true, "", "", ex.Message); }
    }

    // No SNMP answer: a ping tells "SNMP not answering" (wrong community/credentials, SNMP off, ACL) from "host not reachable".
    private static async Task SetNoResponseAsync(MultiTargetRow row, Lang lang, CancellationToken ct)
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
            row.SetResult(lang.T("Kein SNMP (Host aktiv)", "No SNMP (host up)"), true, "", "",
                lang.T("Host antwortet auf Ping, aber nicht auf SNMP: falsche Community/v3-Zugangsdaten, SNMP deaktiviert oder ACL blockiert diesen PC.",
                   "The host answers ping but not SNMP: wrong community / v3 credentials, SNMP disabled, or an ACL blocks this PC."));
        else
            row.SetResult(lang.T("Keine Antwort (Host aus?)", "No response (host down?)"), true, "", "",
                lang.T("Weder SNMP- noch Ping-Antwort: Host aus, falsche IP oder ICMP unterwegs blockiert.",
                   "No reply to SNMP or ping: host down, wrong IP, or ICMP blocked on the way."));
    }
}
