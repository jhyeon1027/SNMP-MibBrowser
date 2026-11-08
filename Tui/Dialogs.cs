#pragma warning disable CS0618 // TextView is obsolete in Terminal.Gui 2.5 but is the built-in multi-line text view.
using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SnmpMibBrowser.Tui;

public static class Dialogs
{
    public static DropDownList Choice(IEnumerable<string> items, string value, int width) =>
        new() { Source = new ListWrapper<string>(new ObservableCollection<string>(items)), ReadOnly = true, Text = value, Width = width };

    /// <summary>One-line text input; null when cancelled.</summary>
    public static string? Input(IApplication app, string title, string label, string initial = "")
    {
        var field = new TextField { X = 0, Y = 1, Width = 50, Text = initial };
        using var dialog = new Dialog { Title = title };
        dialog.Add(new Label { Text = label, X = 0, Y = 0 }, field);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });
        app.Run(dialog);
        return dialog.Canceled ? null : field.Text.Trim();
    }

    /// <summary>
    /// Read-only text on the whole screen, without a frame, so terminal lines hold only the text. The mouse is handed
    /// to the terminal while it is open: drag to select and copy with the terminal (Termius Ctrl+Shift+C, MobaXterm right-click).
    /// </summary>
    public static void ShowText(IApplication app, string title, string text, string hint)
    {
        var header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = $"{title}  —  drag with the mouse to select, copy with the terminal; ↑↓ PgUp PgDn scroll; Esc closes" };
        var view = new TextView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true, WordWrap = false, Text = text };
        using var dialog = new Dialog { Width = Dim.Fill(), Height = Dim.Fill(), BorderStyle = Terminal.Gui.Drawing.LineStyle.None, ShadowStyle = null };
        dialog.Add(header, view, new Label { Text = hint, X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() });
        var wasReleased = TerminalMouse.Released;
        TerminalMouse.Set(app, true);
        try { app.Run(dialog); }
        finally { if (!wasReleased) TerminalMouse.Set(app, false); }
    }

    /// <summary>Picks one entry of a list; null when cancelled.</summary>
    public static string? Pick(IApplication app, string title, IReadOnlyList<string> items, string? extraButton = null)
    {
        if (items.Count == 0) return null;
        var list = new ListView
        {
            X = 0, Y = 0, Width = Math.Clamp(items.Max(i => i.Length) + 2, 30, 70), Height = Math.Min(items.Count, 15),
            Source = new ListWrapper<string>(new ObservableCollection<string>(items))
        };
        list.Value = 0;
        using var dialog = new Dialog { Title = title };
        dialog.Add(list);
        dialog.AddButton(new Button { Text = "Cancel" });
        if (extraButton != null) dialog.AddButton(new Button { Text = extraButton });
        dialog.AddButton(new Button { Text = "OK" });
        app.Run(dialog);
        if (dialog.Canceled)
            return extraButton != null && dialog.Result == 1 ? "\0" + extraButton : null;
        return list.Value is int i && i >= 0 && i < items.Count ? items[i] : null;
    }

    public static bool Confirm(IApplication app, string title, string message) =>
        MessageBox.Query(app, title, message, "No", "Yes") == 1;

    public static void Error(IApplication app, string title, string message) =>
        MessageBox.ErrorQuery(app, title, message, "OK");
}

/// <summary>SNMP SET: OID, data type and value. The caller confirms and sends.</summary>
public sealed class SetDialog : Dialog
{
    public static readonly string[] Types = ["OctetString", "Integer32", "Gauge32", "Counter32", "TimeTicks", "IpAddress", "ObjectIdentifier"];
    private readonly TextField _oid, _value;
    private readonly DropDownList _type;

    public SetDialog(string oid)
    {
        Title = "SNMP SET";
        _oid = new TextField { X = 12, Y = 0, Width = 50, Text = oid };
        _type = Dialogs.Choice(Types, Types[0], 20);
        _type.X = 12; _type.Y = 2;
        _value = new TextField { X = 12, Y = 4, Width = 50 };
        Add(new Label { Text = "OID", X = 0, Y = 0 }, _oid, new Label { Text = "Type", X = 0, Y = 2 }, _type, new Label { Text = "Value", X = 0, Y = 4 }, _value,
            new Label { Text = "SET changes a value on the target device.", X = 0, Y = 6 });
        AddButton(new Button { Text = "Cancel" });
        AddButton(new Button { Text = "Write value" });
    }

    public string Oid => OidUtil.Normalize(_oid.Text);
    public string Type => _type.Text;
    public string Value => _value.Text;
}
