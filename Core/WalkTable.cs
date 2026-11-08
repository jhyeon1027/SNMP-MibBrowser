using Lextm.SharpSnmpLib;

namespace SnmpMibBrowser;

/// <summary>WALK result pivoted into a table: one row per index, one column per MIB object (raw OID if unknown).</summary>
public sealed record WalkTable(List<string> Columns, List<string[]> Rows)
{
    public static WalkTable Build(IEnumerable<Variable> variables, MibLibrary mib)
    {
        var cells = new List<(string Index, string Column, string Value)>();
        foreach (var variable in variables)
        {
            var oid = variable.Id.ToString(); var node = mib.Resolve(oid);
            var column = node?.Name ?? oid; var index = node != null && oid.Length > node.Oid.Length ? oid[(node.Oid.Length + 1)..] : "0";
            cells.Add((index, column, ValueFormat.Display(variable.Data)));
        }
        var columns = new List<string> { "Index" };
        columns.AddRange(cells.Select(x => x.Column).Distinct());
        var position = columns.Select((name, i) => (name, i)).Skip(1).ToDictionary(x => x.name, x => x.i);
        var rows = new List<string[]>();
        foreach (var group in cells.GroupBy(x => x.Index).OrderBy(x => OidUtil.SortKey(string.IsNullOrWhiteSpace(x.Key) ? "0" : x.Key)))
        {
            var row = new string[columns.Count];
            row[0] = group.Key;
            foreach (var cell in group) row[position[cell.Column]] = cell.Value;
            rows.Add(row);
        }
        return new WalkTable(columns, rows);
    }
}
