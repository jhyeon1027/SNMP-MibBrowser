using System.Collections.ObjectModel;

namespace SnmpMibBrowser;

public sealed class MibNode
{
    public string Name { get; set; } = "";
    public string Oid { get; set; } = "";
    public string Syntax { get; set; } = "";
    public string Access { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionEnglish { get; set; } = "";
    public string Module { get; set; } = "";
    public string Display => string.IsNullOrEmpty(Oid) ? Name : $"{Name}  ({Oid})";
    public ObservableCollection<MibNode> Children { get; } = [];
    public override string ToString() => Display;
}

public sealed record QueryResult(string Timestamp, string Operation, string Oid, string Name, string Type, string Value, string Duration);

public sealed record InterfaceRow(string Index, string Name, string Type, string Mtu, string Speed, string Mac, string Admin, string Operational);
public sealed record IpAddressRow(string Address, string Interface, string Netmask, string Broadcast);
public sealed record RouteRow(string Destination, string Netmask, string NextHop, string Interface, string Metric, string Type, string Protocol);
public sealed record ArpRow(string Address, string Mac, string Interface, string Type);
public sealed record LldpRow(string LocalPort, string ChassisId, string PortId, string PortDescription, string SystemName, string SystemDescription);

public sealed record SnmpOptions(string Host, int Port, string Version, string Community, string User,
    string AuthType, string AuthPassword, string PrivacyType, string PrivacyPassword, string Context, int Timeout = 3000);

public sealed class SnmpProfile
{
    public string Name { get; set; } = "Default";
    public int Port { get; set; } = 161;
    public string Version { get; set; } = "v2c";
    public string Community { get; set; } = "public";
    public string User { get; set; } = "";
    public string AuthType { get; set; } = "Keine";
    public string AuthPassword { get; set; } = "";
    public string PrivacyType { get; set; } = "Keine";
    public string PrivacyPassword { get; set; } = "";
    public string Context { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class UserSettings
{
    public string Language { get; set; } = "de";
    public bool Dark { get; set; }
    public string MibFolder { get; set; } = "";
    public List<SnmpProfile> Profiles { get; set; } = [];
    public string SelectedProfile { get; set; } = "";
    public bool IncludePrereleases { get; set; }
}

public sealed record GitHubRelease(string Tag, string Name, string Notes, bool Prerelease, string PageUrl, string AssetUrl, string AssetName, string Digest);
