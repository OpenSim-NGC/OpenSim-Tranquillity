using System.Collections.Concurrent;
using System.Net;
using Nini.Config;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Handlers.Base;

public class ControlPlaneAccess
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(ControlPlaneAccess));

    public const string JsonRpcRemoteAddressKey = "__opensim_remote_address";
    public const string JsonRpcLlHttpRequestKey = "__opensim_llhttprequest";

    private static readonly char[] s_separators = { ',', ';', '|', ' ', '\t', '\r', '\n' };

    /// <summary>The entries already warned about: every service builds its own access list from the same settings.</summary>
    private static readonly ConcurrentDictionary<string, byte> s_warned = new();

    /// <summary>
    /// How often, at most, the trusted host names are looked up again. A name is looked up when the access list is
    /// made and again when a call from an address not trusted arrives: a host that did not resolve at startup (it
    /// was not up yet), or whose address changed, is trusted without a restart.
    /// </summary>
    public static readonly TimeSpan NameLookUpInterval = TimeSpan.FromMinutes(1);

    /// <summary>Loopback and the addresses written in the settings.</summary>
    private readonly HashSet<IPAddress> m_addresses = new();

    /// <summary>The host names in the settings: the entry each came from, by name.</summary>
    private readonly Dictionary<string, string> m_names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last addresses each name was found at. Changed only under the look-up flag.</summary>
    private readonly Dictionary<string, IPAddress[]> m_nameAddresses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every trusted address. Read by many request threads at once: replaced whole, never changed.</summary>
    private volatile HashSet<IPAddress> m_trustedHosts = new();

    private readonly HashSet<IPAddress> m_controlPlaneTrustedHosts = new();
    private readonly Func<string, IPAddress[]> m_lookUp = Dns.GetHostAddresses;
    private readonly TimeProvider m_time = TimeProvider.System;
    private long m_nextLookUpTicks;
    private int m_lookingUp;

    public ControlPlaneAccess(IConfigSource config) : this(config, null)
    {
    }

    /// <param name="config">The settings holding the trusted hosts.</param>
    /// <param name="lookUp">Replaces the name lookup; Dns when null.</param>
    /// <param name="time">Replaces the clock that times the lookups again; the system clock when null.</param>
    public ControlPlaneAccess(IConfigSource config, Func<string, IPAddress[]> lookUp, TimeProvider time = null)
    {
        if (lookUp is not null)
            m_lookUp = lookUp;
        if (time is not null)
            m_time = time;

        AddTrustedAddress(IPAddress.Loopback);
        AddTrustedAddress(IPAddress.IPv6Loopback);

        foreach (string entry in GetConfiguredHosts(config))
            AddTrustedHost(entry);

        AddConfiguredLocalHost(config);
        AddExplicitControlPlaneTrustedHosts(config);

        LookUpNames(startup: true);

        m_log.LogInformation("[CONTROL PLANE ACCESS]: Trusted control-plane addresses: {0}",
            string.Join(", ", m_trustedHosts));
    }

    public bool IsTrustedControlPlaneCaller(IPEndPoint remote)
    {
        return remote != null && m_controlPlaneTrustedHosts.Contains(NormalizeAddress(remote.Address));
    }

    public bool AuthorizeControlPlaneMutation(IOSHttpRequest request, IOSHttpResponse response, string operation)
    {
        IPEndPoint remote = request.RemoteIPEndPoint;
        if (IsTrustedControlPlaneCaller(remote))
        {
            m_log.LogDebug("Control-plane accept: {RemoteAddress} for {Operation}", remote.Address, operation);
            return true;
        }

        m_log.LogWarning("Control-plane reject: {RemoteAddress} attempted {Operation}",
            remote?.Address, operation);
        response.StatusCode = (int)HttpStatusCode.Forbidden;
        response.RawBuffer = Array.Empty<byte>();
        return false;
    }

    public bool Authorize(IOSHttpRequest request, IOSHttpResponse response, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
    {
        if (IsLlHttpRequest(request.Headers[ScriptRequestEndPoint.MarkerHeader] != null))
        {
            m_log.LogWarning("[CONTROL PLANE ACCESS]: Refusing {0} {1} from {2}: script HTTP marker is not allowed on control-plane endpoints",
                request.HttpMethod, request.UriPath, request.RemoteIPEndPoint);
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            response.RawBuffer = Array.Empty<byte>();
            return false;
        }

        if (request.RemoteIPEndPoint is IPEndPoint remote && IsTrustedAddress(remote.Address))
            return true;

        m_log.LogWarning("[CONTROL PLANE ACCESS]: Refusing {0} {1} from {2}: source address is not in ControlPlaneTrustedHosts",
            request.HttpMethod, request.UriPath, request.RemoteIPEndPoint);
        response.StatusCode = (int)blockedStatus;
        response.RawBuffer = Array.Empty<byte>();
        return false;
    }

    public bool AuthorizeJsonRpc(OSDMap json, ref JsonRpcResponse response)
    {
        if (json.TryGetValue(JsonRpcLlHttpRequestKey, out OSD llHttpRequest) && IsLlHttpRequest(llHttpRequest.AsBoolean()))
            return MethodNotFound(ref response);

        if (!json.TryGetValue(JsonRpcRemoteAddressKey, out OSD remoteAddress) || !IPAddress.TryParse(remoteAddress.AsString(), out IPAddress address))
            return MethodNotFound(ref response);

        if (IsTrustedAddress(address))
            return true;

        return MethodNotFound(ref response);
    }

    public bool IsTrustedAddress(IPAddress address)
    {
        if (address is null)
            return false;

        address = NormalizeAddress(address);
        if (IPAddress.IsLoopback(address) || m_trustedHosts.Contains(address))
            return true;

        return LookUpNamesAgain() && m_trustedHosts.Contains(address);
    }

    /// <summary>
    /// Looks the names up again when the interval has passed since the last time. One caller does it; the others
    /// meanwhile are judged on the addresses held. True when it ran.
    /// </summary>
    private bool LookUpNamesAgain()
    {
        if (m_names.Count == 0 || m_time.GetUtcNow().UtcTicks < Volatile.Read(ref m_nextLookUpTicks))
            return false;

        if (Interlocked.CompareExchange(ref m_lookingUp, 1, 0) != 0)
            return false;

        try
        {
            if (m_time.GetUtcNow().UtcTicks < Volatile.Read(ref m_nextLookUpTicks))
                return false;

            LookUpNames(startup: false);
            return true;
        }
        finally
        {
            Volatile.Write(ref m_lookingUp, 0);
        }
    }

    /// <summary>
    /// Looks every name up and publishes the trusted addresses. A name whose lookup fails keeps the addresses it
    /// was last found at.
    /// </summary>
    private void LookUpNames(bool startup)
    {
        foreach ((string host, string entry) in m_names)
        {
            IPAddress[] found;
            try
            {
                found = (m_lookUp(host) ?? Array.Empty<IPAddress>()).Select(NormalizeAddress).Distinct().ToArray();
            }
            catch (Exception e)
            {
                WarnOnce(entry, "could not be looked up (" + e.Message + ")");
                continue;
            }

            if (found.Length == 0)
            {
                WarnOnce(entry, "has no address");
                continue;
            }

            m_nameAddresses.TryGetValue(host, out IPAddress[] before);
            if (!startup && (before is null || !found.ToHashSet().SetEquals(before)))
                m_log.LogInformation("[CONTROL PLANE ACCESS]: Trusted host {Entry} is now at {Addresses} (was {Before})",
                    entry, string.Join(", ", (IEnumerable<IPAddress>)found),
                    before is null ? "not found" : string.Join(", ", (IEnumerable<IPAddress>)before));
            m_nameAddresses[host] = found;
        }

        HashSet<IPAddress> trusted = new(m_addresses);
        foreach (IPAddress[] found in m_nameAddresses.Values)
            trusted.UnionWith(found);
        m_trustedHosts = trusted;

        Volatile.Write(ref m_nextLookUpTicks, (m_time.GetUtcNow() + NameLookUpInterval).UtcTicks);
    }

    /// <summary>
    /// A caller of an XML-RPC method is trusted: its address is, and its request is not a script's. A script's
    /// llHTTPRequest to this host's own public address comes from an address this host may trust.
    /// </summary>
    public bool IsTrustedCaller(IPEndPoint remote)
    {
        return remote is not null && remote is not ScriptRequestEndPoint && IsTrustedAddress(remote.Address);
    }

    public bool AuthorizePrivilegedInstantMessage(byte dialog, IPEndPoint remoteClient)
    {
        if (!IsPrivilegedInstantMessageDialog(dialog))
            return true;

        return IsTrustedCaller(remoteClient);
    }

    public static bool IsPrivilegedInstantMessageDialog(byte dialog)
    {
        return dialog == 250 || dialog == (byte)OpenMetaverse.InstantMessageDialog.GodLikeRequestTeleport;
    }

    /// <summary>
    /// The host an entry names: an address, a name, a URL, a host:port or an [IPv6]:port. Empty when it names none.
    /// An address is read as one first: .NET takes "robust:8003" and "fd00::1" for URIs of the schemes "robust" and
    /// "fd00", with no host, and a lookup of an empty name answers this machine's own addresses.
    /// </summary>
    public static string HostOf(string entry)
    {
        entry = entry?.Trim() ?? string.Empty;
        if (entry.Length == 0 || IPAddress.TryParse(entry, out _))
            return entry;

        if (Uri.TryCreate(entry, UriKind.Absolute, out Uri uri) && uri.Host.Length > 0)
            return uri.Host;

        if (entry[0] == '[')
        {
            int endBracket = entry.IndexOf(']');
            return endBracket > 0 ? entry.Substring(1, endBracket - 1) : string.Empty;
        }

        int colon = entry.LastIndexOf(':');
        if (colon >= 0 && entry.IndexOf(':') == colon)
            return entry.Substring(0, colon);

        return entry;
    }

    private static bool IsLlHttpRequest(bool hasSecondLifeShardHeader)
    {
        return hasSecondLifeShardHeader;
    }

    private static bool MethodNotFound(ref JsonRpcResponse response)
    {
        response.Error.Code = ErrorCode.MethodNotFound;
        response.Error.Message = "Method not found";
        return false;
    }

    /// <summary>
    /// The entries of every list of trusted hosts. A list in [Security] used to hide the one in [Network], which was
    /// then still read for the mutation callers.
    /// </summary>
    private static IEnumerable<string> GetConfiguredHosts(IConfigSource config)
    {
        string[] sections = ["Security", "Network"];
        string[] keys = ["ControlPlaneTrustedHosts", "TrustedControlPlaneHosts"];

        foreach (string sectionName in sections)
        {
            IConfig section = config?.Configs[sectionName];
            if (section == null)
                continue;

            foreach (string key in keys)
            {
                foreach (string entry in Entries(section.GetString(key, string.Empty)))
                    yield return entry;
            }
        }
    }

    private static string[] Entries(string list) =>
        (list ?? string.Empty).Split(s_separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void AddConfiguredLocalHost(IConfigSource config)
    {
        IConfig network = config?.Configs["Network"];
        if (network == null)
            return;

        AddTrustedHost(network.GetString("hostname", string.Empty));
    }

    private void AddExplicitControlPlaneTrustedHosts(IConfigSource config)
    {
        IConfig network = config?.Configs["Network"];

        // The same list as the trusted hosts, read the same way; only its addresses count here. A name is not an
        // error: the trusted hosts take it.
        foreach (string entry in Entries(network?.GetString("ControlPlaneTrustedHosts", string.Empty)))
        {
            if (IPAddress.TryParse(HostOf(entry), out IPAddress address))
                m_controlPlaneTrustedHosts.Add(NormalizeAddress(address));
            else
                m_log.LogDebug(
                    "[CONTROL PLANE ACCESS]: ControlPlaneTrustedHosts entry {Entry} is not an address: not a mutation caller",
                    entry);
        }

        m_log.LogInformation(
            "[CONTROL PLANE ACCESS]: Explicit mutation caller addresses configured: {AddressCount}",
            m_controlPlaneTrustedHosts.Count);
    }

    private void AddTrustedHost(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return;

        string host = HostOf(entry);
        if (IPAddress.TryParse(host, out IPAddress address))
        {
            AddTrustedAddress(address);
            return;
        }

        // never look up an empty name: the answer is this machine's own addresses
        if (host.Length == 0)
        {
            WarnOnce(entry, "names no host");
            return;
        }

        if (host.Contains('/'))
        {
            WarnOnce(entry, "is an address range, which is not supported: list each address");
            return;
        }

        // looked up once every entry is read, and again later (see NameLookUpInterval)
        m_names.TryAdd(host, entry);
    }

    private static void WarnOnce(string entry, string problem)
    {
        if (s_warned.TryAdd(entry + "\n" + problem, 0))
            m_log.LogWarning(
                "[CONTROL PLANE ACCESS]: Trusted host {Entry} {Problem}: calls from it are refused until it is found",
                entry, problem);
    }

    private void AddTrustedAddress(IPAddress address)
    {
        m_addresses.Add(NormalizeAddress(address));
    }

    /// <summary>
    /// An address as the lists hold it: an IPv4-mapped IPv6 address as IPv4, and an IPv6 address without its zone. A
    /// link-local caller arrives with the number of the interface it came in on ("fe80::1%2"), which an entry
    /// ("fe80::1", "fe80::1%eth0") does not match.
    /// </summary>
    private static IPAddress NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());

        return address;
    }
}
