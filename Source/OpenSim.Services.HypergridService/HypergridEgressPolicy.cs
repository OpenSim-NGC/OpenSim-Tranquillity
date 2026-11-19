using System.Net;
using Nini.Config;

namespace OpenSim.Services.HypergridService;

public static class HypergridEgressPolicy
{
    /// <summary>
    /// The grids at a private, loopback or link-local address this grid may still send its users to and
    /// call back, from [Hypergrid] AllowPrivateGrids: gatekeeper addresses ("http://10.0.0.5:8002",
    /// "lan-grid:8002"), as host:port in lower case. Empty by default: the address filter keeps a grid on
    /// the internet from being steered at its own network, and a grid on a private network (a LAN grid,
    /// two grids on one host) can name the others it trusts.
    /// </summary>
    public static HashSet<string> PrivateGridsAllowed(IConfigSource config)
    {
        HashSet<string> grids = new(StringComparer.OrdinalIgnoreCase);
        string list = config?.Configs["Hypergrid"]?.GetString("AllowPrivateGrids", string.Empty) ?? string.Empty;
        foreach (string entry in list.Split(new[] { ',', ';', '|', ' ', '\t' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Authority(entry.Contains("://") ? entry : "http://" + entry) is string authority)
                grids.Add(authority);
        }
        return grids;
    }

    /// <summary>host:port of an absolute URI, in lower case; null when it has no host.</summary>
    private static string Authority(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri u) && u.Host.Length > 0
            ? u.Host.ToLowerInvariant() + ":" + u.Port
            : null;

    public static bool IsAllowedTarget(string targetUri, string localGatewayUri = null,
        ISet<string> privateGridsAllowed = null)
    {
        if (UserAgentService.IsLocalGridURI(localGatewayUri, targetUri))
            return true;

        if (!Uri.TryCreate(targetUri, UriKind.Absolute, out Uri uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (privateGridsAllowed is { Count: > 0 } && Authority(targetUri) is string authority
            && privateGridsAllowed.Contains(authority))
            return true;

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
        }
        catch
        {
            return false;
        }

        return addresses.Length > 0 && Array.TrueForAll(addresses, IsAllowedAddress);
    }

    public static bool IsAllowedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return IsAllowedIPv4(bytes);

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return IsAllowedIPv6(bytes);

        return false;
    }

    private static bool IsAllowedIPv4(byte[] bytes)
    {
        if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127)
            return false;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            return false;
        if (bytes[0] == 169 && bytes[1] == 254)
            return false;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return false;
        if (bytes[0] == 192 && bytes[1] == 168)
            return false;
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            return false;
        if (bytes[0] >= 224)
            return false;
        if (bytes[0] == 169 && bytes[1] == 254 && bytes[2] == 169 && bytes[3] == 254)
            return false;

        return true;
    }

    private static bool IsAllowedIPv6(byte[] bytes)
    {
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            return false;
        if ((bytes[0] & 0xfe) == 0xfc)
            return false;
        if (bytes[0] == 0xff)
            return false;

        return true;
    }
}