namespace SnmpMibBrowser;

/// <summary>The MIB objects known to the browser: the built-in standard catalog plus imported vendor MIBs.</summary>
public sealed class MibLibrary
{
    private readonly MibParser _parser = new();

    public List<MibNode> Nodes { get; private set; } = StandardMibCatalog.Load().ToList();
    /// <summary>Number of files of the last import, null while only the built-in catalog is loaded.</summary>
    public int? LoadedFileCount { get; private set; }

    public static readonly string[] MibExtensions = [".mib", ".my", ".txt", ""];

    public static IEnumerable<string> MibFilesIn(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(x => MibExtensions.Contains(Path.GetExtension(x).ToLowerInvariant()));

    /// <summary>Parses MIB files and merges them. Returns (accepted, skipped) object counts.</summary>
    public (int Accepted, int Protected) Load(IReadOnlyCollection<string> files)
    {
        var parsed = _parser.ParseFiles(files);
        var existingOids = Nodes.Select(x => x.Oid).ToHashSet(StringComparer.Ordinal);
        var accepted = parsed.Where(x => existingOids.Contains(x.Oid) || IsImportableOid(x.Oid)).ToList();
        // Existing/built-in definitions win. This prevents imported standard MIBs
        // from replacing public catalog entries or appearing twice.
        Nodes = Nodes.Concat(accepted).Where(x => !string.IsNullOrWhiteSpace(x.Oid)).GroupBy(x => x.Oid).Select(x => x.First()).OrderBy(x => x.Oid).ToList();
        LoadedFileCount = files.Count;
        return (accepted.Count, parsed.Count - accepted.Count);
    }

    private static bool IsImportableOid(string oid) =>
        oid == "1.3.6.1.4.1" || oid.StartsWith("1.3.6.1.4.1.", StringComparison.Ordinal) ||
        oid == "1.3.6.1.3" || oid.StartsWith("1.3.6.1.3.", StringComparison.Ordinal);

    public static readonly IReadOnlyDictionary<string, string> RootLabels = new Dictionary<string, string>
    {
        ["1"]="iso", ["1.0"]="standard", ["1.3"]="org", ["1.3.6"]="dod", ["1.3.6.1"]="internet",
        ["1.3.6.1.1"]="directory", ["1.3.6.1.2"]="mgmt", ["1.3.6.1.2.1"]="mib-2", ["1.3.6.1.2.1.10"]="transmission",
        ["1.3.6.1.2.1.2"]="interfaces", ["1.3.6.1.2.1.4"]="ip", ["1.3.6.1.2.1.6"]="tcp", ["1.3.6.1.2.1.7"]="udp",
        ["1.3.6.1.2.1.17"]="dot1dBridge", ["1.3.6.1.2.1.25"]="host", ["1.3.6.1.2.1.47"]="entityMIB",
        ["1.3.6.1.3"]="experimental", ["1.3.6.1.4"]="private", ["1.3.6.1.4.1"]="enterprises",
        ["1.0.8802"]="ieee", ["1.0.8802.1"]="standards", ["1.0.8802.1.1"]="ieee802dot1", ["1.0.8802.1.1.2"]="lldpMIB"
    };

    /// <summary>The most specific known object containing the OID (e.g. ifDescr for 1.3.6.1.2.1.2.2.1.2.5).</summary>
    public MibNode? Resolve(string oid) =>
        Nodes.Where(n => oid == n.Oid || oid.StartsWith(n.Oid + ".", StringComparison.Ordinal)).OrderByDescending(n => n.Oid.Length).FirstOrDefault();

    /// <summary>ifDescr.5 instead of 1.3.6.1.2.1.2.2.1.2.5; the numeric OID if no object is known.</summary>
    public string Symbolic(string oid)
    {
        var node = Resolve(oid);
        return node == null ? oid : node.Name + oid[node.Oid.Length..];
    }

    public bool IsScalar(MibNode node)
    {
        if (!node.Access.StartsWith("read", StringComparison.OrdinalIgnoreCase)) return false;
        return !Nodes.Any(parent => node.Oid.StartsWith(parent.Oid + ".", StringComparison.Ordinal) && parent.Syntax.Contains("SEQUENCE", StringComparison.OrdinalIgnoreCase));
    }

    public string QueryOidFor(MibNode node) => IsScalar(node) ? node.Oid + ".0" : node.Oid;

    /// <summary>Appends .0 for a GET of a known scalar object given without instance.</summary>
    public string NormalizeGetOid(string oid)
    {
        oid = OidUtil.Normalize(oid);
        if (oid.EndsWith(".0", StringComparison.Ordinal)) return oid;
        var exact = Nodes.FirstOrDefault(n => n.Oid == oid);
        return exact != null && IsScalar(exact) ? oid + ".0" : oid;
    }

    /// <summary>Tree search: a numeric OID selects the most specific known object, text matches name, OID and description.</summary>
    public (List<MibNode> Matches, string? Focus) Search(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return (Nodes, null);
        var normalizedOid = q.TrimStart('.');
        if (OidUtil.IsNumeric(normalizedOid))
        {
            // An entered OID may include a scalar or table index that has no MIB
            // node of its own. Select the most specific known object in that case.
            var match = Nodes
                .Where(x => normalizedOid == x.Oid || normalizedOid.StartsWith(x.Oid + ".", StringComparison.Ordinal))
                .OrderByDescending(x => x.Oid.Split('.').Length)
                .FirstOrDefault();
            if (match != null) return ([match], match.Oid);
        }
        return (Nodes.Where(x =>
            x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.Oid.Contains(normalizedOid, StringComparison.Ordinal) ||
            x.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.DescriptionEnglish.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(), null);
    }

    /// <summary>
    /// The tree for a set of objects, including the structural parents they need. Labels are resolved
    /// against the whole library so parents of a search result keep their names.
    /// </summary>
    public List<OidTreeEntry> BuildTree(IEnumerable<MibNode> nodes)
    {
        var nodeList = nodes.ToList();
        var exact = Nodes.Concat(nodeList)
            .Where(x => !string.IsNullOrWhiteSpace(x.Oid))
            .GroupBy(x => x.Oid)
            .ToDictionary(x => x.Key, x => x.First());
        var allOids = new HashSet<string>();
        foreach (var node in nodeList)
        {
            var parts = node.Oid.Split('.');
            for (var i = 1; i <= parts.Length; i++) allOids.Add(string.Join('.', parts.Take(i)));
        }
        var result = new List<OidTreeEntry>();
        foreach (var oid in allOids
            .OrderBy(x => x.Count(c => c == '.'))
            .ThenBy(OidUtil.Parent, StringComparer.Ordinal)
            .ThenBy(x => RootLabels.TryGetValue(x, out var canonical) ? canonical : exact.TryGetValue(x, out var node) ? node.Name : x.Split('.').Last(), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(OidUtil.SortKey))
        {
            exact.TryGetValue(oid, out var node);
            // Canonical public-tree labels are immutable. Imported aliases may
            // enrich details, but cannot rename the standard OID hierarchy.
            var name = RootLabels.TryGetValue(oid, out var canonical) ? canonical : node?.Name ?? oid.Split('.').Last();
            var model = node ?? new MibNode { Name = name, Oid = oid, Module = "OID tree", Description = "Struktureller OID-Knoten", DescriptionEnglish = "Structural OID node" };
            result.Add(new OidTreeEntry(oid, name, model));
        }
        return result;
    }

    public static readonly IReadOnlySet<string> ExpandedByDefault = new HashSet<string> { "1", "1.3", "1.3.6", "1.3.6.1", "1.3.6.1.2", "1.3.6.1.2.1", "1.3.6.1.2.1.1" };

    public string Details(MibNode n, Lang lang, string newline = "\n")
    {
        var description = !lang.German && !string.IsNullOrWhiteSpace(n.DescriptionEnglish) ? n.DescriptionEnglish : n.Description;
        var lines = lang.German
            ? new[] { $"Name:         {n.Name}", $"OID:          {n.Oid}", $"Modul:        {n.Module}", $"Syntax:       {n.Syntax}", $"Zugriff:      {n.Access}", "", "Beschreibung:", description }
            : new[] { $"Name:         {n.Name}", $"OID:          {n.Oid}", $"Module:       {n.Module}", $"Syntax:       {n.Syntax}", $"Access:       {n.Access}", "", "Description:", description };
        return string.Join(newline, lines);
    }
}

/// <summary>One tree node in parent-before-child order; the parent is <see cref="OidUtil.Parent"/> of Oid.</summary>
public sealed record OidTreeEntry(string Oid, string Label, MibNode Node);
