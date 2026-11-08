using System.ComponentModel;
using System.Globalization;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace SnmpMibBrowser;

/// <summary>One monitored OID: samples within the history window, counters converted to a per-second rate.</summary>
public sealed class MonitorItem(string name, string target, string oid, SnmpOptions options, string color) : INotifyPropertyChanged
{
    public const int MaxItems = 20;
    public static readonly string[] Palette = ["#7350A8", "#2E86C1", "#D2557F", "#28A07A", "#E08A1E", "#8E5CC9", "#1F9EA8", "#C0392B", "#6C8A1F", "#B5651D"];

    private string _name = name;
    private ulong? _lastCounter;
    private DateTime _lastCounterTime;

    public string Name { get => _name; set { _name = value; Notify(nameof(Name)); } }
    public string Target { get; } = target;
    public string Oid { get; } = oid;
    public SnmpOptions Options { get; } = options;
    /// <summary>Graph color as #RRGGBB.</summary>
    public string Color { get; } = color;
    public TimeSpan History { get; set; } = TimeSpan.FromMinutes(10);
    public List<(DateTime Time, double Value)> Points { get; } = [];
    public bool IsRate { get; private set; }
    public bool NonNumeric { get; private set; }
    public string Current { get; private set; } = "…";
    public string CurrentDetail { get; private set; } = "";
    public string Stats { get; private set; } = "";
    public string Status { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised when the graph has to be redrawn.</summary>
    public event Action? Updated;

    public void AddSample(ISnmpData data, DateTime now)
    {
        Status = "";
        switch (data.TypeCode)
        {
            case SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView:
                Status = data.TypeCode.ToString(); Current = "-"; break;
            // Counters only grow; the graph shows the change per second. A smaller value means a
            // 32-bit wrap (Counter32) or a counter reset (Counter64), which is skipped.
            case SnmpType.Counter32 or SnmpType.Counter64:
                var raw = data is Counter64 c64 ? c64.ToUInt64() : ((Counter32)data).ToUInt32();
                IsRate = true;
                if (_lastCounter is ulong last)
                {
                    var seconds = (now - _lastCounterTime).TotalSeconds;
                    double? delta = raw >= last ? raw - last : data.TypeCode == SnmpType.Counter32 ? raw + 4294967296d - last : null;
                    if (delta is double d && seconds > 0) { var rate = d / seconds; Points.Add((now, rate)); Current = Si(rate) + "/s"; }
                }
                else Current = "…";   // a rate needs two samples
                CurrentDetail = $"{data.TypeCode} {raw:N0}";
                _lastCounter = raw; _lastCounterTime = now;
                break;
            default:
                var number = data switch
                {
                    Integer32 i => i.ToInt32(), Gauge32 g => g.ToUInt32(), TimeTicks t => t.ToUInt32(),
                    _ => double.TryParse(data.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null
                };
                if (number is double n) { Points.Add((now, n)); Current = Plain(n); NonNumeric = false; }
                else { Current = ValueFormat.Display(data); NonNumeric = true; }
                CurrentDetail = data.TypeCode.ToString();
                break;
        }
    }

    public void AddError(string reason) { Status = $"⚠ {reason} ({DateTime.Now:HH:mm:ss})"; Notify(nameof(Status)); }

    /// <summary>Polls the item once; errors are stored in Status. Throws only OperationCanceledException.</summary>
    public async Task PollAsync(SnmpService snmp, CancellationToken ct)
    {
        try
        {
            var answer = await snmp.GetAsync(Options, Oid, false, ct);
            if (answer.Count > 0) AddSample(answer[0].Data, DateTime.Now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or System.TimeoutException) { AddError("Timeout"); }
        catch (SnmpV3ReportException ex) { AddError(ex.Message); }
        catch (ErrorException ex) { AddError("SNMP error " + ex.Body?.Pdu().ErrorStatus); }
        catch (Exception ex) { AddError(ex.Message); }
    }

    public void Refresh(DateTime now)
    {
        Points.RemoveAll(p => p.Time < now - History);
        Stats = Points.Count == 0 ? "" :
            $"min {Format(Points.Min(p => p.Value))}  max {Format(Points.Max(p => p.Value))}  avg {Format(Points.Average(p => p.Value))}";
        foreach (var name in new[] { nameof(Current), nameof(CurrentDetail), nameof(Stats), nameof(Status) }) Notify(name);
        Updated?.Invoke();
    }

    public void ClearHistory() { Points.Clear(); _lastCounter = null; Stats = ""; Notify(nameof(Stats)); Updated?.Invoke(); }

    public string Format(double value) => IsRate ? Si(value) + "/s" : Plain(value);
    private static string Plain(double v) => Math.Abs(v % 1) < 1e-9 ? v.ToString("N0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Si(double v) => Math.Abs(v) >= 1e9 ? $"{v / 1e9:0.##} G" : Math.Abs(v) >= 1e6 ? $"{v / 1e6:0.##} M" : Math.Abs(v) >= 1e3 ? $"{v / 1e3:0.##} K" : $"{v:0.##}";
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}
