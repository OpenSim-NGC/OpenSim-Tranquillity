using System.Net;

namespace OpenSim.Services.HypergridService;

public static class HypergridEgressPolicy
{
    public static bool IsAllowedTarget(string targetUri, string localGatewayUri = null)
    {
        if (UserAgentService.IsLocalGridURI(localGatewayUri, targetUri))
            return true;

        if (!Uri.TryCreate(targetUri, UriKind.Absolute, out Uri uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

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