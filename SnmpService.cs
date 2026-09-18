using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace SnmpMibBrowser;

public sealed class SnmpService
{
    public Task<IList<Variable>> GetAsync(SnmpOptions o, string oid, bool next, CancellationToken ct) =>
        Task.Run(() => Send(o, oid, next ? "NEXT" : "GET"), ct);

    public Task<IList<Variable>> WalkAsync(SnmpOptions o, string oid, CancellationToken ct) => Task.Run(() =>
    {
        var results = new List<Variable>();
        var current = oid;
        for (var i = 0; i < 10000; i++)
        {
            ct.ThrowIfCancellationRequested();
            var answer = Send(o, current, "NEXT");
            if (answer.Count == 0 || !answer[0].Id.ToString().StartsWith(oid + ".", StringComparison.Ordinal) || answer[0].Id.ToString() == current) break;
            results.Add(answer[0]); current = answer[0].Id.ToString();
        }
        return (IList<Variable>)results;
    }, ct);

    public Task<IList<Variable>> SetAsync(SnmpOptions o, string oid, string type, string value, CancellationToken ct) => Task.Run(() =>
    {
        var variable = new Variable(new ObjectIdentifier(Normalize(oid)), MakeData(type, value));
        if (o.Version == "v3") return SendV3(o, [variable], "SET");
        var endpoint = Resolve(o);
        return Messenger.Set(o.Version == "v1" ? VersionCode.V1 : VersionCode.V2, endpoint, new OctetString(o.Community), [variable], o.Timeout);
    }, ct);

    private IList<Variable> Send(SnmpOptions o, string oid, string operation)
    {
        var vars = new List<Variable> { new(new ObjectIdentifier(Normalize(oid))) };
        if (o.Version == "v3") return SendV3(o, vars, operation);
        var version = o.Version == "v1" ? VersionCode.V1 : VersionCode.V2;
        var endpoint = Resolve(o);
        if (operation == "GET") return Messenger.Get(version, endpoint, new OctetString(o.Community), vars, o.Timeout);
        var request = new GetNextRequestMessage(0, version, new OctetString(o.Community), vars);
        return request.GetResponse(o.Timeout, endpoint).Variables();
    }

    private IList<Variable> SendV3(SnmpOptions o, IList<Variable> vars, string operation)
    {
        if (string.IsNullOrWhiteSpace(o.User)) throw new ArgumentException("Für SNMPv3 ist ein Benutzername erforderlich.");
        var endpoint = Resolve(o);
        var privacy = BuildPrivacy(o);
        var discovery = Messenger.GetNextDiscovery(operation == "SET" ? SnmpType.SetRequestPdu : operation == "NEXT" ? SnmpType.GetNextRequestPdu : SnmpType.GetRequestPdu);
        var report = discovery.GetResponse(o.Timeout, endpoint);
        ISnmpMessage request = operation switch
        {
            "SET" => new SetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, new OctetString(o.User), new OctetString(o.Context), vars, privacy, Messenger.MaxMessageSize, report),
            "NEXT" => new GetNextRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, new OctetString(o.User), new OctetString(o.Context), vars, privacy, Messenger.MaxMessageSize, report),
            _ => new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, new OctetString(o.User), new OctetString(o.Context), vars, privacy, Messenger.MaxMessageSize, report)
        };
        return request.GetResponse(o.Timeout, endpoint).Variables();
    }

    private static IPrivacyProvider BuildPrivacy(SnmpOptions o)
    {
        IAuthenticationProvider auth = o.AuthType switch
        {
            "MD5" => new MD5AuthenticationProvider(new OctetString(o.AuthPassword)),
            "SHA-1" => new SHA1AuthenticationProvider(new OctetString(o.AuthPassword)),
            "SHA-256" => new SHA256AuthenticationProvider(new OctetString(o.AuthPassword)),
            "SHA-384" => new SHA384AuthenticationProvider(new OctetString(o.AuthPassword)),
            "SHA-512" => new SHA512AuthenticationProvider(new OctetString(o.AuthPassword)),
            _ => DefaultAuthenticationProvider.Instance
        };
        return o.PrivacyType switch
        {
            "DES" => new DESPrivacyProvider(new OctetString(o.PrivacyPassword), auth),
            "AES-128" => new AESPrivacyProvider(new OctetString(o.PrivacyPassword), auth),
            "AES-192" => new AES192PrivacyProvider(new OctetString(o.PrivacyPassword), auth),
            "AES-256" => new AES256PrivacyProvider(new OctetString(o.PrivacyPassword), auth),
            _ => new DefaultPrivacyProvider(auth)
        };
    }

    private static IPEndPoint Resolve(SnmpOptions o)
    {
        if (!IPAddress.TryParse(o.Host, out var ip)) ip = Dns.GetHostAddresses(o.Host).First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return new IPEndPoint(ip, o.Port);
    }
    private static string Normalize(string oid) => oid.Trim().TrimStart('.');
    private static ISnmpData MakeData(string type, string value) => type switch
    {
        "Integer32" => new Integer32(int.Parse(value)), "Gauge32" => new Gauge32(uint.Parse(value)),
        "Counter32" => new Counter32(uint.Parse(value)), "TimeTicks" => new TimeTicks(uint.Parse(value)),
        "IpAddress" => new IP(value), "ObjectIdentifier" => new ObjectIdentifier(Normalize(value)), _ => new OctetString(value)
    };
}
