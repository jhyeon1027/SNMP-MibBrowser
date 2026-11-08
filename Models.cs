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

public sealed record QueryResult(string Timestamp, string Operation, string Oid, string Name, string Type, string Value, string Duration, string Target = "");

public sealed record InterfaceRow(string Index, string Name, string Type, string Mtu, string Speed, string Mac, string Admin, string Operational);
public sealed record IpAddressRow(string Address, string Interface, string Netmask, string Broadcast);
public sealed record RouteRow(string Destination, string Netmask, string NextHop, string Interface, string Metric, string Type, string Protocol);
public sealed record ArpRow(string Address, string Mac, string Interface, string Type);
public sealed record LldpRow(string LocalPort, string ChassisId, string PortId, string PortDescription, string SystemName, string SystemDescription);

public sealed record SnmpOptions(string Host, int Port, string Version, string Community, string User,
    string AuthType, string AuthPassword, string PrivacyType, string PrivacyPassword, string Context, int Timeout = 3000, int Retries = 1, IReadOnlyList<string>? Communities = null);

public sealed class SnmpProfile
{
    public string Name { get; set; } = "Default";
    public int Port { get; set; } = 161;
    public string Version { get; set; } = "v2c";
    public string Community { get; set; } = "public";
    // Ordered v1/v2c communities to try. Older settings files only have Community.
    public List<string> Communities { get; set; } = [];
    public List<string> CommunityList() => Communities.Count > 0 ? [.. Communities] : [Community];
    public string User { get; set; } = "";
    public string AuthType { get; set; } = "None";
    public string AuthPassword { get; set; } = "";
    public string PrivacyType { get; set; } = "None";
    public string PrivacyPassword { get; set; } = "";
    public string Context { get; set; } = "";
    public int Timeout { get; set; } = 3000;
    public int Retries { get; set; } = 1;
    public override string ToString() => Name;
}

public sealed class UserSettings
{
    public string Language { get; set; } = "en";
    public bool Dark { get; set; }
    public string MibFolder { get; set; } = "";
    public List<SnmpProfile> Profiles { get; set; } = [];
    public string SelectedProfile { get; set; } = "";
    public bool IncludePrereleases { get; set; }
    public List<string> RecentTargets { get; set; } = [];
    // Multi-target window inputs, restored the next time it opens. MultiPresetOids == null means defaults.
    public string MultiTargets { get; set; } = "";
    public List<string>? MultiPresetOids { get; set; }
    public string MultiCustomOids { get; set; } = "";
}

public sealed record GitHubRelease(string Tag, string Name, string Notes, bool Prerelease, string PageUrl, string AssetUrl, string AssetName, string Digest);

// One OID in the WALK compare tab: its value per round and whether any round differs.
public sealed class CompareRow(string oid, string name, string type, int rounds) : System.ComponentModel.INotifyPropertyChanged
{
    public string Oid { get; } = oid;
    public string Name { get; } = name;
    public string Type { get; set; } = type;
    public ObservableCollection<string> Values { get; } = new(Enumerable.Repeat("", rounds));
    public bool Changed { get; private set; }
    public long? Change { get; private set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    // Compares only the rounds completed so far; a missing value ("") counts as a difference.
    public void Evaluate(int completedRounds)
    {
        var seen = Values.Take(completedRounds).ToList();
        Changed = seen.Distinct(StringComparer.Ordinal).Count() > 1;
        var numbers = seen.Select(v => long.TryParse(v, out var n) ? n : (long?)null).Where(n => n.HasValue).Select(n => n!.Value).ToList();
        Change = numbers.Count >= 2 && numbers.Count == seen.Count ? numbers.Max() - numbers.Min() : null;
        PropertyChanged?.Invoke(this, new(nameof(Changed)));
        PropertyChanged?.Invoke(this, new(nameof(Change)));
        PropertyChanged?.Invoke(this, new(nameof(Type)));
    }
}

// One target in the multi-target window.
public sealed class MultiTargetRow(string target, int oidCount) : System.ComponentModel.INotifyPropertyChanged
{
    public string Target { get; } = target;
    // IPv4 targets sort numerically (192.168.2.9 before 192.168.2.10); host names after them.
    public string SortKey { get; } = System.Net.IPAddress.TryParse(target, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        ? string.Concat(ip.GetAddressBytes().Select(b => b.ToString("D3"))) : "~" + target.ToLowerInvariant();
    public string Status { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string Community { get; private set; } = "";
    public string Duration { get; private set; } = "";
    public bool Failed { get; private set; }
    public ObservableCollection<string> Values { get; } = new(Enumerable.Repeat("", oidCount));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void SetResult(string status, bool failed, string community, string duration, string detail = "")
    {
        Status = status; Failed = failed; Community = community; Duration = duration; Detail = detail;
        foreach (var name in new[] { nameof(Status), nameof(Failed), nameof(Community), nameof(Duration), nameof(Detail) }) PropertyChanged?.Invoke(this, new(name));
    }

    public void Reset()
    {
        for (var i = 0; i < Values.Count; i++) Values[i] = "";
        SetResult("", false, "", "");
    }
}

// One line of the Resources tab. Oid is the instance that holds the value ("" for computed lines).
public sealed record ResourceRow(string Category, string Item, string Value, string Source, string Oid, string Detail = "");
