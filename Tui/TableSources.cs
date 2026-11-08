using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace SnmpMibBrowser.Tui;

/// <summary>Presents a list as a table; the list may grow, the TableView reads it on every draw.</summary>
public sealed class RowTableSource<T>(IReadOnlyList<T> rows, params (string Name, Func<T, string> Get)[] columns) : ITableSource
{
    public string[] ColumnNames { get; } = columns.Select(c => c.Name).ToArray();
    public int Columns => columns.Length;
    public int Rows => rows.Count;
    public object this[int row, int col] => columns[col].Get(rows[row]);
    public string Cell(int row, int col) => columns[col].Get(rows[row]);
}

/// <summary>Plain string grid (WALK table, device tables).</summary>
public sealed class GridTableSource(string[] columns, IReadOnlyList<string?[]> rows) : ITableSource
{
    public string[] ColumnNames { get; } = columns;
    public int Columns => ColumnNames.Length;
    public int Rows => rows.Count;
    public object this[int row, int col] => rows[row][col] ?? "";
}

public static class TableViewExtensions
{
    /// <summary>Rows that are selected (or hold the cursor), in display order.</summary>
    public static List<int> SelectedRows(this TableView table)
    {
        if (table.Table is not { Rows: > 0 } source) return [];
        return table.GetAllSelectedCells().Select(p => p.Y).Where(y => y >= 0 && y < source.Rows).Distinct().Order().ToList();
    }

    /// <summary>Tab-separated text with a header line (pastes into Excel as columns).</summary>
    public static string RowsAsText(this TableView table, IEnumerable<int> rows)
    {
        var source = table.Table!;
        var text = new StringBuilder(string.Join('\t', source.ColumnNames));
        foreach (var r in rows)
            text.Append('\n').Append(string.Join('\t', Enumerable.Range(0, source.Columns).Select(c => source[r, c]?.ToString() ?? "")));
        return text.ToString();
    }
}

/// <summary>
/// Hands the mouse to the terminal (or back to the app). While the app has it, the terminal reports clicks and
/// drags to the app and cannot select text; released, drag-select and the terminal's copy work again.
/// Sends the xterm mouse-tracking off/on sequences, because ignoring mouse events alone leaves tracking on.
/// </summary>
public static class TerminalMouse
{
    public static bool Released { get; private set; }

    public static void Set(IApplication app, bool release)
    {
        Released = release;
        app.Mouse.IsMouseDisabled = release;
        app.Driver?.WriteRaw(release ? Terminal.Gui.Drivers.EscSeqUtils.CSI_DisableMouseEvents : Terminal.Gui.Drivers.EscSeqUtils.CSI_EnableMouseEvents);
    }
}

/// <summary>
/// Copies text to the clipboard of the PC running the terminal, using the OSC 52 escape sequence
/// (works over SSH in terminals that support it, e.g. Windows Terminal; others ignore it silently).
/// </summary>
public static class TerminalClipboard
{
    public static void Copy(IApplication app, string text)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        app.Driver?.WriteRaw($"\u001b]52;c;{payload}\u0007");
    }
}
