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

    private readonly HashSet<IPAddress> m_trustedHosts = new();

    public ControlPlaneAccess(IConfigSource config)
    {
        AddTrustedAddress(IPAddress.Loopback);
        AddTrustedAddress(IPAddress.IPv6Loopback);

        string hosts = GetConfiguredHosts(config);
        foreach (string host in hosts.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            AddTrustedHost(host.Trim());

        AddConfiguredLocalHost(config);

        m_log.LogInformation("[CONTROL PLANE ACCESS]: Trusted control-plane addresses configured: {0}", m_trustedHosts.Count);
    }

    public bool Authorize(IOSHttpRequest request, IOSHttpResponse response, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
    {
        if (IsLlHttpRequest(request.Headers["X-SecondLife-Shard"] != null))
        {
            m_log.LogWarning("[CONTROL PLANE ACCESS]: Refusing {0} {1} from {2}: script HTTP marker is not allowed on control-plane endpoints",
                request.HttpMethod, request.UriPath, request.RemoteIPEndPoint);
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            response.RawBuffer = Array.Empty<byte>();
            return false;
        }

        if (IsTrustedAddress(request.RemoteIPEndPoint.Address))
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
        address = NormalizeAddress(address);
        return IPAddress.IsLoopback(address) || m_trustedHosts.Contains(address);
    }

    public bool AuthorizePrivilegedInstantMessage(byte dialog, IPEndPoint remoteClient)
    {
        if (!IsPrivilegedInstantMessageDialog(dialog))
            return true;

        return remoteClient != null && IsTrustedAddress(remoteClient.Address);
    }

    public static bool IsPrivilegedInstantMessageDialog(byte dialog)
    {
        return dialog == 250 || dialog == (byte)OpenMetaverse.InstantMessageDialog.GodLikeRequestTeleport;
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

    private void AddConfiguredLocalHost(IConfigSource config)
    {
        IConfig network = config?.Configs["Network"];
        if (network == null)
            return;

        AddTrustedHost(network.GetString("hostname", string.Empty));
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