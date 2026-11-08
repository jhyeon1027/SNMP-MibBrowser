using Lextm.SharpSnmpLib;

namespace SnmpMibBrowser;

public static class OidUtil
{
    // Arcs are unsigned 32-bit (TimeTicks indexes such as lldpRemTimeMark exceed int.MaxValue on long-running devices).
    public static string SortKey(string oid) => string.Join('.', oid.Split('.').Select(x => ulong.TryParse(x, out var n) ? n.ToString("D10") : x));

    public static string Parent(string oid)
    {
        var cut = oid.LastIndexOf('.');
        return cut > 0 ? oid[..cut] : string.Empty;
    }

    /// <summary>Digits separated by dots (no range check).</summary>
    public static bool IsNumeric(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('.').All(part => part.Length > 0 && part.All(char.IsDigit));

    /// <summary>A numeric OID whose arcs all fit in 32 bits, i.e. one that can be sent to an agent.</summary>
    public static bool IsValid(string value) => IsNumeric(value) && value.Split('.').All(part => uint.TryParse(part, out _));

    /// <summary>Trims spaces and a leading dot (".1.3.6..." as printed by net-snmp).</summary>
    public static string Normalize(string value) => value.Trim().TrimStart('.');
}

public static class ValueFormat
{
    // OctetStrings that are not clean UTF-8 text (MAC addresses, binary IDs) are shown as hex.
    public static string Display(ISnmpData data)
    {
        if (data is not OctetString octets) return data.ToString();
        var bytes = octets.GetRaw();
        try
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            if (!text.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))) return text;
        }
        catch (System.Text.DecoderFallbackException) { }
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}

/// <summary>German/English text selection used by the shared logic (the terminal app is English only).</summary>
public readonly record struct Lang(bool German)
{
    public static readonly Lang English = new(false);
    public static Lang From(string language) => new(language == "de");
    public string T(string de, string en) => German ? de : en;
}
