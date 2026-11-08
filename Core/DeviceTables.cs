using Lextm.SharpSnmpLib;

namespace SnmpMibBrowser;

/// <summary>Column values of an SNMP table: one dictionary per walked column, keyed by row index.</summary>
public sealed class TableColumns(List<Dictionary<string, ISnmpData>> columns)
{
    public List<Dictionary<string, ISnmpData>> Columns { get; } = columns;
    public Dictionary<string, ISnmpData> this[int column] => Columns[column];
    public IEnumerable<string> Keys() => Columns.SelectMany(x => x.Keys).Distinct().OrderBy(OidUtil.SortKey);
    public string Val(int column, string key) => Columns[column].TryGetValue(key, out var value) ? value.ToString() : "";
    public ISnmpData? Raw(int column, string key) => Columns[column].GetValueOrDefault(key);

    /// <summary>Walks each root on one device; limitReached gets the root whose walk hit the step limit.</summary>
    public static async Task<TableColumns> LoadAsync(SnmpService snmp, SnmpOptions options, IReadOnlyList<string> roots, CancellationToken ct, Action<string>? limitReached = null)
    {
        var columns = new List<Dictionary<string, ISnmpData>>();
        foreach (var root in roots)
        {
            var values = await snmp.WalkAsync(options, root, ct, limitReached: limitReached == null ? null : () => limitReached(root));
            columns.Add(values.ToDictionary(v => v.Id.ToString()[(root.Length + 1)..], v => v.Data));
        }
        return new TableColumns(columns);
    }
}

/// <summary>The standard tables of the device tabs (IF-MIB, IP-MIB, RFC1213 routes, ARP, LLDP-MIB).</summary>
public static class DeviceTables
{
    public static readonly string[] InterfaceRoots = ["1.3.6.1.2.1.2.2.1.2", "1.3.6.1.2.1.2.2.1.3", "1.3.6.1.2.1.2.2.1.4", "1.3.6.1.2.1.2.2.1.5", "1.3.6.1.2.1.2.2.1.6", "1.3.6.1.2.1.2.2.1.7", "1.3.6.1.2.1.2.2.1.8", "1.3.6.1.2.1.31.1.1.1.1", "1.3.6.1.2.1.31.1.1.1.15", "1.3.6.1.2.1.31.1.1.1.17"];
    public static readonly string[] IpRoots = ["1.3.6.1.2.1.4.20.1.1", "1.3.6.1.2.1.4.20.1.2", "1.3.6.1.2.1.4.20.1.3", "1.3.6.1.2.1.4.20.1.4"];
    public static readonly string[] RouteRoots = ["1.3.6.1.2.1.4.21.1.1", "1.3.6.1.2.1.4.21.1.11", "1.3.6.1.2.1.4.21.1.7", "1.3.6.1.2.1.4.21.1.2", "1.3.6.1.2.1.4.21.1.3", "1.3.6.1.2.1.4.21.1.8", "1.3.6.1.2.1.4.21.1.9"];
    public static readonly string[] ArpRoots = ["1.3.6.1.2.1.4.22.1.3", "1.3.6.1.2.1.4.22.1.2", "1.3.6.1.2.1.4.22.1.1", "1.3.6.1.2.1.4.22.1.4"];
    public static readonly string[] LldpRoots = ["1.0.8802.1.1.2.1.4.1.1.4", "1.0.8802.1.1.2.1.4.1.1.5", "1.0.8802.1.1.2.1.4.1.1.6", "1.0.8802.1.1.2.1.4.1.1.7", "1.0.8802.1.1.2.1.4.1.1.8", "1.0.8802.1.1.2.1.4.1.1.9", "1.0.8802.1.1.2.1.4.1.1.10"];
    public const string LldpLocalPortRoot = "1.0.8802.1.1.2.1.3.7.1.3";

    /// <summary>Physical ports only: ifConnectorPresent when the device reports it, otherwise the interface type.</summary>
    public static List<InterfaceRow> Interfaces(TableColumns c)
    {
        var hasConnectorData = c[9].Count > 0;
        return c.Keys().Where(k => hasConnectorData ? c.Val(9, k) == "1" : IsPhysicalInterfaceType(c.Val(1, k))).Select(k => new InterfaceRow(k,
            string.IsNullOrWhiteSpace(c.Val(7, k)) ? c.Val(0, k) : c.Val(7, k), InterfaceType(c.Val(1, k)), c.Val(2, k),
            FormatInterfaceSpeed(c.Val(3, k), c.Val(8, k)), Mac(c.Raw(4, k)), Status(c.Val(5, k)), Status(c.Val(6, k)))).ToList();
    }

    public static List<IpAddressRow> IpAddresses(TableColumns c) =>
        c.Keys().Select(k => new IpAddressRow(c.Val(0, k), c.Val(1, k), c.Val(2, k), BroadcastAddress(c.Val(0, k), c.Val(2, k)))).ToList();

    public static List<RouteRow> Routes(TableColumns c) =>
        c.Keys().Select(k => new RouteRow(c.Val(0, k), c.Val(1, k), c.Val(2, k), c.Val(3, k), c.Val(4, k), RouteType(c.Val(5, k)), RouteProtocol(c.Val(6, k)))).ToList();

    public static List<ArpRow> Arp(TableColumns c) =>
        c.Keys().Select(k => new ArpRow(c.Val(0, k), Mac(c.Raw(1, k)), c.Val(2, k), ArpType(c.Val(3, k)))).ToList();

    public static List<LldpRow> Lldp(TableColumns c, IReadOnlyDictionary<string, string> localPorts) =>
        c.Keys().Select(k => new LldpRow(LocalPortName(k, localPorts), LldpChassisId(c.Raw(1, k), c.Val(0, k)), LldpPortId(c.Raw(3, k), c.Val(2, k)), c.Val(4, k), c.Val(5, k), c.Val(6, k))).ToList();

    /// <summary>lldpLocPortDesc by local port number, used to name the local port of each neighbor.</summary>
    public static async Task<Dictionary<string, string>> LldpLocalPortsAsync(SnmpService snmp, SnmpOptions options, CancellationToken ct)
    {
        var values = await snmp.WalkAsync(options, LldpLocalPortRoot, ct);
        return values.ToDictionary(v => v.Id.ToString()[(LldpLocalPortRoot.Length + 1)..], v => v.Data.ToString());
    }

    public static string Status(string value) => value switch { "1" => "up", "2" => "down", "3" => "testing", _ => value };
    public static string FormatSpeed(string value) => ulong.TryParse(value, out var n) ? n >= 1_000_000_000 ? $"{n/1_000_000_000d:0.##} Gbit/s" : n >= 1_000_000 ? $"{n/1_000_000d:0.##} Mbit/s" : n >= 1000 ? $"{n/1000d:0.##} kbit/s" : $"{n} bit/s" : value;
    public static string FormatInterfaceSpeed(string ifSpeed, string highSpeed) =>
        ulong.TryParse(highSpeed, out var mbps) && mbps > 0 ? mbps >= 1000 ? $"{mbps / 1000d:0.##} Gbit/s" : $"{mbps} Mbit/s" : FormatSpeed(ifSpeed);
    public static string Mac(ISnmpData? value)
    {
        if (value is not OctetString octets) return value?.ToString() ?? "";
        var bytes = octets.GetRaw();
        return bytes.Length == 0 ? "" : string.Join(":", bytes.Select(x => x.ToString("X2")));
    }
    public static string InterfaceType(string value) => value switch
    {
        "6" => "ethernetCsmacd", "24" => "softwareLoopback", "53" => "propVirtual", "62" => "fastEther",
        "117" => "gigabitEthernet", "135" => "l2vlan", "161" => "ieee8023adLag", _ => value
    };
    private static bool IsPhysicalInterfaceType(string value) => value is "6" or "62" or "69" or "117";
    public static string BroadcastAddress(string address, string mask)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip) || !System.Net.IPAddress.TryParse(mask, out var netmask)) return "";
        var a = ip.GetAddressBytes(); var m = netmask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return "";
        return new System.Net.IPAddress(a.Zip(m, (x, y) => (byte)(x | ~y)).ToArray()).ToString();
    }
    public static string RouteType(string value) => value switch { "1" => "other", "2" => "invalid", "3" => "direct", "4" => "indirect", _ => value };
    public static string RouteProtocol(string value) => value switch { "1" => "other", "2" => "local", "3" => "netmgmt", "4" => "icmp", "8" => "rip", "13" => "ospf", "14" => "bgp", _ => value };
    public static string ArpType(string value) => value switch { "1" => "other", "2" => "invalid", "3" => "dynamic", "4" => "static", _ => value };
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
}
