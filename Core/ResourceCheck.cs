using Lextm.SharpSnmpLib;

namespace SnmpMibBrowser;

/// <summary>CPU, memory, disk, load and sensors from standard MIBs (HOST-RESOURCES, UCD-SNMP, ENTITY-SENSOR).</summary>
public static class ResourceCheck
{
    public const string HrProcessorLoad = "1.3.6.1.2.1.25.3.3.1.2", HrStorage = "1.3.6.1.2.1.25.2.3.1";
    public const string UcdMemory = "1.3.6.1.4.1.2021.4", UcdSystemStats = "1.3.6.1.4.1.2021.11", UcdLoad = "1.3.6.1.4.1.2021.10.1.3", UcdDisk = "1.3.6.1.4.1.2021.9.1";
    public const string EntSensor = "1.3.6.1.2.1.99.1.1.1", EntPhysicalName = "1.3.6.1.2.1.47.1.1.1.1.7";

    /// <summary>Columns to walk, in the order <see cref="Build"/> expects. MIBs a device does not implement return no rows.</summary>
    public static readonly string[] Roots =
    [
        HrProcessorLoad, HrStorage + ".2", HrStorage + ".3", HrStorage + ".4", HrStorage + ".5", HrStorage + ".6",
        UcdMemory, UcdSystemStats, UcdLoad, UcdDisk + ".2", UcdDisk + ".9",
        EntSensor + ".1", EntSensor + ".2", EntSensor + ".3", EntSensor + ".4", EntSensor + ".5", EntPhysicalName
    ];

    public sealed record Result(List<ResourceRow> Rows, List<string> Supported, List<string> NotSupported)
    {
        public string Summary(Lang lang) => lang.T("Unterstützt: ", "Supported: ") + (Supported.Count > 0 ? string.Join(", ", Supported) : "-") +
            (NotSupported.Count > 0 ? lang.T(" · Nicht unterstützt: ", " · Not supported: ") + string.Join(", ", NotSupported) : "");
    }

    public static Result Build(TableColumns c, Lang lang)
    {
        var rows = new List<ResourceRow>();
        var hr = AddHostResources(rows, lang, c[0], c[1], c[2], c[3], c[4], c[5]);
        var ucd = AddUcd(rows, lang, c[6], c[7], c[8], c[9], c[10]);
        var sensors = AddSensors(rows, lang, c[11], c[12], c[13], c[14], c[15], c[16]);
        var supported = new[] { ("HOST-RESOURCES", hr), ("UCD-SNMP", ucd), ("ENTITY-SENSOR", sensors) };
        return new Result(rows, supported.Where(x => x.Item2).Select(x => x.Item1).ToList(), supported.Where(x => !x.Item2).Select(x => x.Item1).ToList());
    }

    private static long? Num(Dictionary<string, ISnmpData> column, string key) =>
        column.TryGetValue(key, out var value) && long.TryParse(value.ToString(), out var n) ? n : null;
    private static string Gb(double bytes) => bytes >= 1024d * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0} MB";
    private static string Percent(double part, double total) => total > 0 ? $"{part * 100 / total:0} %" : "-";

    private static bool AddHostResources(List<ResourceRow> rows, Lang lang, Dictionary<string, ISnmpData> load, Dictionary<string, ISnmpData> type,
        Dictionary<string, ISnmpData> descr, Dictionary<string, ISnmpData> units, Dictionary<string, ISnmpData> size, Dictionary<string, ISnmpData> used)
    {
        const string source = "HOST-RESOURCES";
        var cpus = load.Keys.OrderBy(OidUtil.SortKey).ToList();
        for (var i = 0; i < cpus.Count; i++)
            rows.Add(new("CPU", lang.T($"CPU {i + 1} Last", $"CPU {i + 1} load"), $"{load[cpus[i]]} %", source, $"{HrProcessorLoad}.{cpus[i]}"));
        if (cpus.Count > 1)
        {
            var avg = cpus.Select(k => Num(load, k) ?? 0).Average();
            rows.Add(new("CPU", lang.T("CPU-Durchschnitt", "CPU average"), $"{avg:0} %", source + lang.T(" (berechnet)", " (computed)"), "", lang.T("Mittelwert aller hrProcessorLoad-Werte", "Average of all hrProcessorLoad values")));
        }
        // hrStorageTypes: 2 = RAM, 3 = virtual memory, 4 = fixed disk.
        foreach (var key in type.Keys.OrderBy(OidUtil.SortKey))
        {
            var kind = type[key].ToString();
            var category = kind switch { "1.3.6.1.2.1.25.2.1.2" or "1.3.6.1.2.1.25.2.1.3" => "Memory", "1.3.6.1.2.1.25.2.1.4" => "Disk", _ => null };
            if (category == null || Num(size, key) is not long total || total <= 0) continue;
            var unit = Num(units, key) ?? 1;
            var usedValue = Num(used, key) ?? 0;
            var name = descr.TryGetValue(key, out var d) ? ValueFormat.Display(d) : key;
            rows.Add(new(category, name, $"{Percent(usedValue, total)} ({Gb(usedValue * (double)unit)} / {Gb(total * (double)unit)})", source, $"{HrStorage}.6.{key}",
                lang.T($"Belegt = hrStorageUsed × {unit} Bytes, Gesamt = hrStorageSize ({HrStorage}.5.{key})", $"Used = hrStorageUsed × {unit} bytes, total = hrStorageSize ({HrStorage}.5.{key})")));
        }
        return load.Count > 0 || type.Count > 0;
    }

    private static bool AddUcd(List<ResourceRow> rows, Lang lang, Dictionary<string, ISnmpData> memory, Dictionary<string, ISnmpData> stats,
        Dictionary<string, ISnmpData> load, Dictionary<string, ISnmpData> diskPath, Dictionary<string, ISnmpData> diskPercent)
    {
        const string source = "UCD-SNMP";
        if (Num(stats, "11.0") is long idle)
            rows.Add(new("CPU", lang.T("CPU-Auslastung (100 − Leerlauf)", "CPU usage (100 − idle)"), $"{100 - idle} %", source, $"{UcdSystemStats}.11.0", lang.T("ssCpuIdle; Auslastung = 100 − Wert", "ssCpuIdle; usage = 100 − value")));
        // UCD memory values are in KB.
        if (Num(memory, "5.0") is long totalKb && totalKb > 0)
        {
            var avail = Num(memory, "6.0") ?? 0; var buffers = Num(memory, "14.0") ?? 0; var cached = Num(memory, "15.0") ?? 0;
            var usedKb = Math.Max(0, totalKb - avail - buffers - cached);
            rows.Add(new("Memory", lang.T("RAM belegt (ohne Puffer/Cache)", "RAM used (excl. buffers/cache)"), $"{Percent(usedKb, totalKb)} ({Gb(usedKb * 1024d)} / {Gb(totalKb * 1024d)})", source + lang.T(" (berechnet)", " (computed)"), "",
                "(memTotalReal − memAvailReal − memBuffer − memCached) / memTotalReal"));
            rows.Add(new("Memory", "memTotalReal", $"{totalKb:N0} KB", source, $"{UcdMemory}.5.0"));
            rows.Add(new("Memory", "memAvailReal", $"{avail:N0} KB", source, $"{UcdMemory}.6.0"));
            if (memory.ContainsKey("14.0")) rows.Add(new("Memory", "memBuffer", $"{buffers:N0} KB", source, $"{UcdMemory}.14.0"));
            if (memory.ContainsKey("15.0")) rows.Add(new("Memory", "memCached", $"{cached:N0} KB", source, $"{UcdMemory}.15.0"));
        }
        if (Num(memory, "3.0") is long swapTotal && swapTotal > 0)
        {
            var swapAvail = Num(memory, "4.0") ?? 0;
            rows.Add(new("Memory", lang.T("Swap belegt", "Swap used"), $"{Percent(swapTotal - swapAvail, swapTotal)} ({Gb((swapTotal - swapAvail) * 1024d)} / {Gb(swapTotal * 1024d)})", source, $"{UcdMemory}.4.0", lang.T("memAvailSwap; Gesamt = memTotalSwap (.3.0)", "memAvailSwap; total = memTotalSwap (.3.0)")));
        }
        foreach (var (key, minutes) in new[] { ("1", 1), ("2", 5), ("3", 15) })
            if (load.TryGetValue(key, out var value))
                rows.Add(new("Load", lang.T($"Systemlast {minutes} min", $"Load average {minutes} min"), ValueFormat.Display(value), source, $"{UcdLoad}.{key}"));
        foreach (var key in diskPercent.Keys.OrderBy(OidUtil.SortKey))
            rows.Add(new("Disk", diskPath.TryGetValue(key, out var path) ? ValueFormat.Display(path) : key, $"{diskPercent[key]} %", source, $"{UcdDisk}.9.{key}"));
        return memory.Count > 0 || stats.Count > 0 || load.Count > 0 || diskPercent.Count > 0;
    }

    private static bool AddSensors(List<ResourceRow> rows, Lang lang, Dictionary<string, ISnmpData> type, Dictionary<string, ISnmpData> scale,
        Dictionary<string, ISnmpData> precision, Dictionary<string, ISnmpData> value, Dictionary<string, ISnmpData> status, Dictionary<string, ISnmpData> names)
    {
        // RFC 3433 SensorDataScale (note exa(14) before peta(15)) and SensorDataType units.
        var exponents = new Dictionary<long, int> { [1] = -24, [2] = -21, [3] = -18, [4] = -15, [5] = -12, [6] = -9, [7] = -6, [8] = -3, [9] = 0, [10] = 3, [11] = 6, [12] = 9, [13] = 12, [14] = 18, [15] = 15, [16] = 21, [17] = 24 };
        foreach (var key in value.Keys.OrderBy(OidUtil.SortKey))
        {
            if (Num(value, key) is not long raw) continue;
            var kind = Num(type, key) ?? 2;
            var real = raw * Math.Pow(10, exponents.GetValueOrDefault(Num(scale, key) ?? 9) - (Num(precision, key) ?? 0));
            var text = kind switch
            {
                8 => $"{real:0.#} °C", 10 => $"{real:0} RPM", 3 => $"{real:0.##} V AC", 4 => $"{real:0.##} V DC", 5 => $"{real:0.##} A", 6 => $"{real:0.#} W",
                7 => $"{real:0.#} Hz", 9 => $"{real:0.#} %RH", 11 => $"{real:0.#} CMM", 12 => raw == 1 ? "true" : "false", _ => real.ToString("0.###")
            };
            var state = Num(status, key) switch { 2 => lang.T(" (nicht verfügbar)", " (unavailable)"), 3 => lang.T(" (außer Betrieb)", " (nonoperational)"), _ => "" };
            var name = names.TryGetValue(key, out var n) && ValueFormat.Display(n).Length > 0 ? ValueFormat.Display(n) : $"Sensor {key}";
            rows.Add(new("Sensor", name, text + state, "ENTITY-SENSOR", $"{EntSensor}.4.{key}",
                lang.T($"Rohwert {raw}, Typ {kind}, Skala {Num(scale, key)}, Präzision {Num(precision, key)}", $"Raw {raw}, type {kind}, scale {Num(scale, key)}, precision {Num(precision, key)}")));
        }
        return value.Count > 0;
    }
}
