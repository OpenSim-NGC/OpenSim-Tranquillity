using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

public class ControlPlaneAccessTests
{
    [Fact]
    public void AuthorizeBlocksExternalAddressWhenNoTrustedHostsConfigured()
    {
        ControlPlaneAccess access = new(new IniConfigSource());
        TestOSHttpResponse response = new();

        bool authorized = access.Authorize(RequestFrom("203.0.113.10"), response);

        Xunit.Assert.False(authorized);
        Xunit.Assert.Equal((int)HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void AuthorizeAllowsConfiguredTrustedAddress()
    {
        IniConfigSource config = new();
        config.AddConfig("Security").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        ControlPlaneAccess access = new(config);
        TestOSHttpResponse response = new();

        bool authorized = access.Authorize(RequestFrom("203.0.113.10"), response);

        Xunit.Assert.True(authorized);
        Xunit.Assert.Equal(0, response.StatusCode);
    }

    [Fact]
    public void AuthorizeBlocksScriptMarkedRequestFromTrustedAddress()
    {
        IniConfigSource config = new();
        config.AddConfig("Security").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        ControlPlaneAccess access = new(config);
        TestOSHttpResponse response = new();
        FakeRequest request = RequestFrom("203.0.113.10");
        request.Headers["X-SecondLife-Shard"] = "OpenSim";

        bool authorized = access.Authorize(request, response);

        Xunit.Assert.False(authorized);
        Xunit.Assert.Equal((int)HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void AuthorizePrivilegedInstantMessageBlocksUntrustedCaller()
    {
        ControlPlaneAccess access = new(new IniConfigSource());

        bool authorized = access.AuthorizePrivilegedInstantMessage(250, new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000));

        Xunit.Assert.False(authorized);
    }

    [Fact]
    public void AuthorizePrivilegedInstantMessageAllowsTrustedCaller()
    {
        IniConfigSource config = new();
        config.AddConfig("Security").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        ControlPlaneAccess access = new(config);

        bool authorized = access.AuthorizePrivilegedInstantMessage((byte)OpenMetaverse.InstantMessageDialog.GodLikeRequestTeleport, new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000));

        Xunit.Assert.True(authorized);
    }

    [Fact]
    public void AuthorizePrivilegedInstantMessageAllowsOrdinaryDialogFromUntrustedCaller()
    {
        ControlPlaneAccess access = new(new IniConfigSource());

        bool authorized = access.AuthorizePrivilegedInstantMessage((byte)OpenMetaverse.InstantMessageDialog.MessageFromAgent, new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000));

        Xunit.Assert.True(authorized);
    }

    private static FakeRequest RequestFrom(string address)
    {
        return new FakeRequest(new IPEndPoint(IPAddress.Parse(address), 9000));
    }

    private sealed class FakeRequest : IOSHttpRequest
    {
        public FakeRequest(IPEndPoint remoteEndPoint)
        {
            RemoteIPEndPoint = remoteEndPoint;
        }

        public string[] AcceptTypes => Array.Empty<string>();
        public Encoding ContentEncoding => Encoding.UTF8;
        public long ContentLength => 0;
        public long ContentLength64 => 0;
        public string ContentType => string.Empty;
        public bool HasEntityBody => false;
        public NameValueCollection Headers { get; } = new();
        public string HttpMethod => "POST";
        public Stream InputStream => Stream.Null;
        public bool IsSecured => false;
        public bool KeepAlive => false;
        public NameValueCollection QueryString { get; } = new();
        public Hashtable Query { get; } = new();
        public HashSet<string> QueryFlags { get; } = new();
        public Dictionary<string, string> QueryAsDictionary { get; } = new();
        public string RawUrl => string.Empty;
        public IPEndPoint RemoteIPEndPoint { get; }
        public IPEndPoint LocalIPEndPoint => new(IPAddress.Any, 0);
        public Uri Url => new("http://localhost/");
        public string UriPath => string.Empty;
        public string UserAgent => string.Empty;
        public double ArrivalTS => 0;
    }
}