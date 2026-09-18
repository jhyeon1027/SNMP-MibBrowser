using System.Text.RegularExpressions;
using System.IO;

namespace SnmpMibBrowser;

public sealed class MibParser
{
    private static readonly Regex ModuleRx = new(@"(?m)^\s*([A-Za-z][\w-]*)\s+DEFINITIONS\s*::=\s*BEGIN", RegexOptions.Compiled);
    // Name and declaration type must be on the same line. Using \s+ here lets an
    // IMPORTS block consume the next OBJECT-TYPE and creates bogus OID parents.
    private static readonly Regex AssignmentRx = new(@"(?ms)^[ \t]*([A-Za-z][\w-]*)[ \t]+(OBJECT-TYPE|OBJECT IDENTIFIER|MODULE-IDENTITY|NOTIFICATION-TYPE|OBJECT-IDENTITY|TEXTUAL-CONVENTION)\b(.*?)::=\s*\{\s*([\w-]+)[ \t]+(\d+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex SyntaxRx = new(@"(?im)^\s*SYNTAX\s+([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex AccessRx = new(@"(?im)^\s*(?:MAX-ACCESS|ACCESS)\s+([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex DescriptionRx = new("(?is)DESCRIPTION\\s+\"(.*?)\"", RegexOptions.Compiled);

    public IReadOnlyList<MibNode> ParseFiles(IEnumerable<string> files)
    {
        var raw = new List<(MibNode Node, string Parent, int Arc)>();
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var module = ModuleRx.Match(text).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(module)) module = Path.GetFileName(file);
            foreach (Match m in AssignmentRx.Matches(text))
            {
                var body = m.Groups[3].Value;
                raw.Add((new MibNode {
                    Name = m.Groups[1].Value, Module = module,
                    Syntax = Clean(SyntaxRx.Match(body).Groups[1].Value),
                    Access = Clean(AccessRx.Match(body).Groups[1].Value),
                    Description = Clean(DescriptionRx.Match(body).Groups[1].Value)
                }, m.Groups[4].Value, int.Parse(m.Groups[5].Value)));
            }
        }

        var known = StandardRoots().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        // Never replace canonical roots (iso/org/dod/internet/...) with aliases
        // found in individual MIB files.
        foreach (var item in raw) known.TryAdd(item.Node.Name, item.Node);
        var unresolved = raw.ToList();
        for (var pass = 0; pass < raw.Count + 2 && unresolved.Count > 0; pass++)
        {
            var progress = false;
            foreach (var item in unresolved.ToArray())
            {
                if (!known.TryGetValue(item.Parent, out var parent) || string.IsNullOrEmpty(parent.Oid)) continue;
                item.Node.Oid = parent.Oid + "." + item.Arc;
                unresolved.Remove(item); progress = true;
            }
            if (!progress) break;
        }
        return raw.Select(x => x.Node).Where(x => !string.IsNullOrEmpty(x.Oid)).GroupBy(x => x.Oid).Select(x => x.First()).OrderBy(x => OidKey(x.Oid)).ToList();
    }

    private static IEnumerable<MibNode> StandardRoots()
    {
        var roots = new[] { ("iso","1"),("org","1.3"),("dod","1.3.6"),("internet","1.3.6.1"),("directory","1.3.6.1.1"),("mgmt","1.3.6.1.2"),("mib-2","1.3.6.1.2.1"),("private","1.3.6.1.4"),("enterprises","1.3.6.1.4.1"),("experimental","1.3.6.1.3") };
        return roots.Select(x => new MibNode { Name=x.Item1, Oid=x.Item2, Module="SNMPv2-SMI" });
    }

    private static string Clean(string text) => Regex.Replace(text.Replace("\"\"", "\""), @"\s+", " ").Trim(' ', '"');
    private static string OidKey(string oid) => string.Join('.', oid.Split('.').Select(x => int.Parse(x).ToString("D10")));
}
