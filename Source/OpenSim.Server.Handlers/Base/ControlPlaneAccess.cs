using System.Net;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.Handlers.Base;

public class ControlPlaneAccess
{
    private readonly HashSet<IPAddress> m_trustedHosts = new();

    public ControlPlaneAccess(IConfigSource config)
    {
        AddTrustedAddress(IPAddress.Loopback);
        AddTrustedAddress(IPAddress.IPv6Loopback);

        string hosts = GetConfiguredHosts(config);
        foreach (string host in hosts.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            AddTrustedHost(host.Trim());
    }

    public bool Authorize(IOSHttpRequest request, IOSHttpResponse response, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
    {
        if (request.Headers["X-SecondLife-Shard"] != null)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            response.RawBuffer = Array.Empty<byte>();
            return false;
        }

        IPAddress address = NormalizeAddress(request.RemoteIPEndPoint.Address);
        if (IPAddress.IsLoopback(address) || m_trustedHosts.Contains(address))
            return true;

        response.StatusCode = (int)blockedStatus;
        response.RawBuffer = Array.Empty<byte>();
        return false;
    }

    private static string GetConfiguredHosts(IConfigSource config)
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
                string value = section.GetString(key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return string.Empty;
    }

    private void AddTrustedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;

        if (Uri.TryCreate(host, UriKind.Absolute, out Uri uri))
            host = uri.Host;
        else if (host[0] == '[')
        {
            int endBracket = host.IndexOf(']');
            if (endBracket > 0)
                host = host.Substring(1, endBracket - 1);
        }
        else
        {
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon)
                host = host.Substring(0, colon);
        }

        if (IPAddress.TryParse(host, out IPAddress address))
        {
            AddTrustedAddress(address);
            return;
        }

        try
        {
            foreach (IPAddress resolvedAddress in Dns.GetHostAddresses(host))
                AddTrustedAddress(resolvedAddress);
        }
        catch
        {
        }
    }

    private void AddTrustedAddress(IPAddress address)
    {
        m_trustedHosts.Add(NormalizeAddress(address));
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        return address;
    }
}