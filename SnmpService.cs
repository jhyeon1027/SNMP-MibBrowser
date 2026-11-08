using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace SnmpMibBrowser;

public sealed class SnmpService
{
    // Community that last worked per host:port:version, so a long community list is probed only once per device.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _workingCommunity = new();

    private static string CommunityKey(SnmpOptions o) => $"{o.Host}:{o.Port}:{o.Version}";

    /// <summary>The v1/v2c community the device last answered with, or null for v3 / not known yet.</summary>
    public string? CommunityUsed(SnmpOptions o) =>
        o.Version == "v3" ? null
        : o.Communities is { Count: > 1 } ? (_workingCommunity.TryGetValue(CommunityKey(o), out var c) ? c : null)
        : o.Community;

    /// <summary>Raised (on a worker thread) when probing a community list finds the one the device accepts: host, community, position, count.</summary>
    public event Action<string, string, int, int>? CommunityAccepted;

    public const int MaxWalkSteps = 10000;

    public Task<IList<Variable>> GetAsync(SnmpOptions o, string oid, bool next, CancellationToken ct) =>
        Task.Run(() => Send(o, oid, next ? "NEXT" : "GET", ct), ct);

    // maxDepth > 0 limits the walk to that many arcs below oid. For each deeper
    // subtree only its first object is kept, then the rest of it is skipped.
    // limitReached is invoked when the walk stops at MaxWalkSteps before reaching the end of the subtree.
    public Task<IList<Variable>> WalkAsync(SnmpOptions o, string oid, CancellationToken ct, int maxDepth = 0, Action? limitReached = null) => Task.Run(() =>
    {
        var results = new List<Variable>();
        var rootArcs = oid.Split('.').Length;
        var current = oid;
        string? skippedPrefix = null;
        var completed = false;
        for (var i = 0; i < MaxWalkSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var answer = Send(o, current, "NEXT", ct);
            if (answer.Count == 0 || !answer[0].Id.ToString().StartsWith(oid + ".", StringComparison.Ordinal) || answer[0].Id.ToString() == current) { completed = true; break; }
            var id = answer[0].Id.ToString();
            var arcs = id.Split('.');
            var prefix = maxDepth > 0 && arcs.Length - rootArcs > maxDepth ? string.Join('.', arcs.Take(rootArcs + maxDepth)) : null;
            if (prefix == null || prefix != skippedPrefix) results.Add(answer[0]);
            skippedPrefix = prefix;
            // GETNEXT on prefix.4294967295 (largest sub-identifier) returns the first object after that
            // subtree. Only jump when that is ahead of the current object, otherwise step normally.
            var jump = prefix + "." + uint.MaxValue;
            current = prefix != null && CompareOids(id, jump) < 0 ? jump : id;
        }
        if (!completed) limitReached?.Invoke();
        return (IList<Variable>)results;
    }, ct);

    public Task<IList<Variable>> SetAsync(SnmpOptions o, string oid, string type, string value, CancellationToken ct) => Task.Run(() =>
    {
        var variable = new Variable(new ObjectIdentifier(Normalize(oid)), MakeData(type, value));
        if (o.Version == "v3") return SendV3(o, [variable], "SET");
        var endpoint = Resolve(o);
        IList<Variable> SetWith(string community) => Messenger.Set(o.Version == "v1" ? VersionCode.V1 : VersionCode.V2, endpoint, new OctetString(community), [variable], o.Timeout);
        if (o.Communities is not { Count: > 1 } communities) return SetWith(o.Community);
        // Write communities often differ from read ones, so SET always walks the list in order and skips
        // communities that time out or are refused (noAccess / authorizationError).
        Exception? last = null;
        for (var i = 0; i < communities.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { var result = SetWith(communities[i]); CommunityAccepted?.Invoke(o.Host, communities[i], i + 1, communities.Count); return result; }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException ex) { last = ex; }
            catch (ErrorException ex) when (ex.Body != null && ex.Body.Pdu().ErrorStatus.ToInt32() is 6 or 16) { last = ex; }
        }
        throw new System.TimeoutException($"SET was not accepted with any of the {communities.Count} communities. Last error: {last?.Message}");
    }, ct);

    private IList<Variable> Send(SnmpOptions o, string oid, string operation, CancellationToken ct)
    {
        if (o.Version == "v3" || o.Communities is not { Count: > 1 } communities) return SendWithRetries(o, oid, operation);
        var key = CommunityKey(o);
        if (_workingCommunity.TryGetValue(key, out var known) && communities.Contains(known))
        {
            try { return SendWithRetries(o with { Community = known }, oid, operation); }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) { _workingCommunity.TryRemove(key, out _); }
        }
        // Wrong communities are silently dropped by agents, so each candidate costs one timeout (no retries).
        // Any SNMP response, including an error status, means the community was accepted.
        for (var i = 0; i < communities.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = SendOnce(o with { Community = communities[i] }, oid, operation);
                Accept(i);
                return result;
            }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) { }
            catch (ErrorException) { Accept(i); throw; }
        }
        throw new System.TimeoutException($"No response from {o.Host} with any of the {communities.Count} communities.");

        void Accept(int i)
        {
            _workingCommunity[key] = communities[i];
            CommunityAccepted?.Invoke(o.Host, communities[i], i + 1, communities.Count);
        }
    }

    // GET/GETNEXT are retried on timeout. SET is not, to avoid writing twice.
    private IList<Variable> SendWithRetries(SnmpOptions o, string oid, string operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return SendOnce(o, oid, operation); }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) when (attempt < o.Retries) { }
        }
    }

    private IList<Variable> SendOnce(SnmpOptions o, string oid, string operation)
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
        var response = request.GetResponse(o.Timeout, endpoint);
        // An agent that rejects the request (wrong user, password, algorithm, ...) answers with a
        // REPORT PDU whose variable is a usmStats counter. That must not be shown as the value.
        if (response.Pdu().TypeCode == SnmpType.ReportPdu) throw new SnmpV3ReportException(response.Variables());
        return response.Variables();
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
    private static int CompareOids(string a, string b)
    {
        var x = a.Split('.').Select(uint.Parse).ToArray(); var y = b.Split('.').Select(uint.Parse).ToArray();
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length.CompareTo(y.Length);
    }
    private static ISnmpData MakeData(string type, string value) => type switch
    {
        "Integer32" => new Integer32(int.Parse(value)), "Gauge32" => new Gauge32(uint.Parse(value)),
        "Counter32" => new Counter32(uint.Parse(value)), "TimeTicks" => new TimeTicks(uint.Parse(value)),
        "IpAddress" => new IP(value), "ObjectIdentifier" => new ObjectIdentifier(Normalize(value)), _ => new OctetString(value)
    };
}

/// <summary>SNMPv3 request rejected by the agent; the message names the usmStats reason.</summary>
public sealed class SnmpV3ReportException(IList<Variable> report) : Exception(Describe(report))
{
    private static string Describe(IList<Variable> report)
    {
        var oid = report.Count > 0 ? report[0].Id.ToString() : "";
        return oid switch
        {
            "1.3.6.1.6.3.15.1.1.1.0" => "SNMPv3: security level not supported by the agent (check the Auth/Privacy settings).",
            "1.3.6.1.6.3.15.1.1.2.0" => "SNMPv3: message outside the agent's time window.",
            "1.3.6.1.6.3.15.1.1.3.0" => "SNMPv3: unknown user name.",
            "1.3.6.1.6.3.15.1.1.4.0" => "SNMPv3: unknown engine ID.",
            "1.3.6.1.6.3.15.1.1.5.0" => "SNMPv3: wrong authentication password or algorithm.",
            "1.3.6.1.6.3.15.1.1.6.0" => "SNMPv3: decryption failed (wrong privacy password or algorithm).",
            _ => $"SNMPv3: request rejected by the agent (report {oid})."
        };
    }
}
