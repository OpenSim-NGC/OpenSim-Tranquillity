using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Server.Handlers.Inventory;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Theory = Xunit.TheoryAttribute;

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
    public void AuthorizeAllowsConfiguredLocalNetworkHost()
    {
        IniConfigSource config = new();
        config.AddConfig("Network").Set("hostname", "203.0.113.10");
        ControlPlaneAccess access = new(config);
        TestOSHttpResponse response = new();

        bool authorized = access.Authorize(RequestFrom("203.0.113.10"), response);

        Xunit.Assert.True(authorized);
        Xunit.Assert.Equal(0, response.StatusCode);
    }

    [Fact]
    public void IsTrustedControlPlaneCallerUsesExplicitNetworkAddressesOnly()
    {
        IniConfigSource config = new();
        config.AddConfig("Network")
            .Set("ControlPlaneTrustedHosts", "203.0.113.10, 2001:db8::10");
        config.Configs["Network"].Set("hostname", "203.0.113.11");
        ControlPlaneAccess access = new(config);

        Xunit.Assert.True(access.IsTrustedControlPlaneCaller(
            new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000)));
        Xunit.Assert.True(access.IsTrustedControlPlaneCaller(
            new IPEndPoint(IPAddress.Parse("::ffff:203.0.113.10"), 9000)));
        Xunit.Assert.True(access.IsTrustedControlPlaneCaller(
            new IPEndPoint(IPAddress.Parse("2001:db8::10"), 9000)));
        Xunit.Assert.False(access.IsTrustedControlPlaneCaller(
            new IPEndPoint(IPAddress.Parse("203.0.113.11"), 9000)));
        Xunit.Assert.False(access.IsTrustedControlPlaneCaller(
            new IPEndPoint(IPAddress.Loopback, 9000)));
    }

    [Fact]
    public void AuthorizeControlPlaneMutationReturnsForbiddenForUntrustedPeer()
    {
        ControlPlaneAccess access = new(new IniConfigSource());
        TestOSHttpResponse response = new();

        bool authorized = access.AuthorizeControlPlaneMutation(
            RequestFrom("203.0.113.10"), response, "POST /assets");

        Xunit.Assert.False(authorized);
        Xunit.Assert.Equal((int)HttpStatusCode.Forbidden, response.StatusCode);
        Xunit.Assert.Empty(response.RawBuffer);
    }

    [Fact]
    public void AuthorizeControlPlaneMutationDoesNotTrustForwardedAddressHeaders()
    {
        IniConfigSource config = new();
        config.AddConfig("Network").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        ControlPlaneAccess access = new(config);
        TestOSHttpResponse response = new();
        FakeRequest request = RequestFrom("198.51.100.20");
        request.Headers["X-Forwarded-For"] = "203.0.113.10";

        bool authorized = access.AuthorizeControlPlaneMutation(request, response, "POST /assets");

        Xunit.Assert.False(authorized);
        Xunit.Assert.Equal((int)HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void XInventoryMutationIsNotGatedByControlPlane()
    {
        IniConfigSource config = new();
        config.AddConfig("InventoryService")
            .Set("StorageProvider", "OpenSim.Tests.Common.dll:TestXInventoryDataPlugin");
        IInventoryService service = ServerUtils.LoadPlugin<IInventoryService>(
            "OpenSim.Services.InventoryService.dll:XInventoryService", new object[] { config });
        Xunit.Assert.NotNull(service);
        ControlPlaneAccess access = new(new IniConfigSource());
        XInventoryConnectorPostHandler handler = new(service, null, access);
        TestOSHttpResponse response = new();
        UUID principal = UUID.Random();
        using MemoryStream body = new(Encoding.UTF8.GetBytes($"METHOD=CREATEUSERINVENTORY&PRINCIPAL={principal}"));

        byte[] result = handler.Handle("/xinventory", body, RequestFrom("203.0.113.10"), response);

        Xunit.Assert.Equal(0, response.StatusCode);
        Xunit.Assert.Equal("True", ServerUtils.ParseXmlResponse(Encoding.UTF8.GetString(result))["RESULT"]);
        Xunit.Assert.NotNull(service.GetRootFolder(principal));
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

    // What a lookup answers: robust's address, and for an empty name (which Dns answers with this machine's own
    // addresses) a stand-in for those.
    private static readonly IPAddress s_robust = IPAddress.Parse("192.0.2.50");
    private static readonly IPAddress s_thisMachine = IPAddress.Parse("198.51.100.7");

    private sealed class Lookups
    {
        public readonly List<string> Names = new();

        public IPAddress[] LookUp(string name)
        {
            lock (Names)
                Names.Add(name);
            return name switch
            {
                "" => new[] { s_thisMachine },
                "robust" or "robust.example.invalid" => new[] { s_robust },
                _ => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound),
            };
        }
    }

    private static ControlPlaneAccess Trusting(string section, string hosts, Lookups lookups)
    {
        IniConfigSource config = new();
        config.AddConfig(section).Set("ControlPlaneTrustedHosts", hosts);
        return new ControlPlaneAccess(config, lookups.LookUp);
    }

    // .NET takes "robust:8003" for a URI of the scheme "robust", with an empty host. The empty name was looked up,
    // which answers this machine's own addresses: robust was refused, and this machine trusted in its place.
    [Theory]
    [InlineData("robust:8003")]
    [InlineData("robust.example.invalid:8003")]
    [InlineData("http://robust:8003")]
    [InlineData("http://robust.example.invalid:8003/")]
    [InlineData("robust")]
    public void AHostAndPortEntryTrustsTheHostNotThisMachine(string entry)
    {
        Lookups lookups = new();
        ControlPlaneAccess access = Trusting("Network", entry, lookups);

        Xunit.Assert.True(access.IsTrustedAddress(s_robust), entry + " does not trust robust");
        Xunit.Assert.False(access.IsTrustedAddress(s_thisMachine), entry + " trusts this machine's addresses");
        Xunit.Assert.DoesNotContain("", lookups.Names);
    }

    // "fd00::1" too is a URI to .NET, of the scheme "fd00".
    [Theory]
    [InlineData("fd00::1", "fd00::1")]
    [InlineData("[fd00::1]", "fd00::1")]
    [InlineData("[fd00::1]:8003", "fd00::1")]
    [InlineData("http://[fd00::1]:8003/", "fd00::1")]
    [InlineData("2001:db8::10", "2001:db8::10")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    [InlineData("192.168.1.5:8003", "192.168.1.5")]
    [InlineData("http://192.168.1.5:8003", "192.168.1.5")]
    public void AnAddressEntryTrustsTheAddressWithoutALookup(string entry, string trusted)
    {
        Lookups lookups = new();
        ControlPlaneAccess access = Trusting("Network", entry, lookups);

        Xunit.Assert.True(access.IsTrustedAddress(IPAddress.Parse(trusted)), entry + " does not trust " + trusted);
        Xunit.Assert.False(access.IsTrustedAddress(s_thisMachine));
        Xunit.Assert.Empty(lookups.Names);
    }

    [Theory]
    [InlineData(":8003")]
    [InlineData("[]:8003")]
    [InlineData("localhost:8003")]
    [InlineData("file:///etc/hosts")]
    [InlineData("10.20.0.0/24")]
    [InlineData("nosuchhost.example.invalid")]
    public void NoEntryLooksUpAnEmptyName(string entry)
    {
        Lookups lookups = new();
        ControlPlaneAccess access = Trusting("Network", entry, lookups);

        Xunit.Assert.DoesNotContain("", lookups.Names);
        Xunit.Assert.False(access.IsTrustedAddress(s_thisMachine), entry + " trusts this machine's addresses");
        Xunit.Assert.True(access.IsTrustedAddress(IPAddress.Loopback));
    }

    [Fact]
    public void TheHostnameSettingIsReadTheSameWay()
    {
        Lookups lookups = new();
        IniConfigSource config = new();
        config.AddConfig("Network").Set("hostname", "robust:8003");
        ControlPlaneAccess access = new(config, lookups.LookUp);

        Xunit.Assert.True(access.IsTrustedAddress(s_robust));
        Xunit.Assert.False(access.IsTrustedAddress(s_thisMachine));
    }

    // The first list found was the only one read: a list in [Security] hid the one in [Network].
    [Fact]
    public void TheListsInSecurityAndNetworkAreBothRead()
    {
        IniConfigSource config = new();
        config.AddConfig("Security").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        config.AddConfig("Network").Set("ControlPlaneTrustedHosts", "203.0.113.11");
        config.Configs["Network"].Set("TrustedControlPlaneHosts", "203.0.113.12");
        ControlPlaneAccess access = new(config, new Lookups().LookUp);

        Xunit.Assert.True(access.IsTrustedAddress(IPAddress.Parse("203.0.113.10")));
        Xunit.Assert.True(access.IsTrustedAddress(IPAddress.Parse("203.0.113.11")));
        Xunit.Assert.True(access.IsTrustedAddress(IPAddress.Parse("203.0.113.12")));
    }

    // The mutation callers were read from the same setting split on commas only, and an address with its port was
    // refused as invalid; the documented separators and forms now work for both lists.
    [Fact]
    public void TheMutationCallersAreReadLikeTheTrustedHosts()
    {
        IniConfigSource config = new();
        config.AddConfig("Network").Set("ControlPlaneTrustedHosts", "203.0.113.10 203.0.113.11;[2001:db8::10]:8003|robust:8003");
        ControlPlaneAccess access = new(config, new Lookups().LookUp);

        foreach (string a in new[] { "203.0.113.10", "203.0.113.11", "2001:db8::10" })
        {
            Xunit.Assert.True(access.IsTrustedControlPlaneCaller(new IPEndPoint(IPAddress.Parse(a), 9000)), a);
            Xunit.Assert.True(access.IsTrustedAddress(IPAddress.Parse(a)), a);
        }

        // a name is a trusted host, but not a mutation caller: those are addresses only
        Xunit.Assert.True(access.IsTrustedAddress(s_robust));
        Xunit.Assert.False(access.IsTrustedControlPlaneCaller(new IPEndPoint(s_robust, 9000)));
    }

    [Theory]
    [InlineData("robust:8003", "robust")]
    [InlineData("fd00::1", "fd00::1")]
    [InlineData("[fd00::1]:8003", "[fd00::1]:8003")]
    [InlineData("http://robust:8003/x", "robust")]
    [InlineData("  robust  ", "robust")]
    [InlineData(":8003", "")]
    [InlineData("", "")]
    public void HostOfNamesTheHost(string entry, string host)
    {
        Xunit.Assert.Equal(host, ControlPlaneAccess.HostOf(entry));
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A name server whose answers change: a name with no answer is not found.</summary>
    private sealed class ChangingNames
    {
        public readonly Dictionary<string, IPAddress[]> Answers = new();
        public int Lookups;

        public IPAddress[] LookUp(string name)
        {
            Interlocked.Increment(ref Lookups);
            lock (Answers)
            {
                if (Answers.TryGetValue(name, out IPAddress[] a))
                    return a;
            }
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }
    }

    private static ControlPlaneAccess Trusting(string hosts, ChangingNames names, FakeClock clock)
    {
        IniConfigSource config = new();
        config.AddConfig("Network").Set("ControlPlaneTrustedHosts", hosts);
        return new ControlPlaneAccess(config, names.LookUp, clock);
    }

    // A name was looked up once, when the service started: a Robust that was not up yet (its container started
    // after the region's) was refused until the region restarted.
    [Fact]
    public void ANameNotFoundAtStartupIsTrustedOnceItIsFound()
    {
        ChangingNames names = new();
        FakeClock clock = new();
        ControlPlaneAccess access = Trusting("robust:8003", names, clock);
        Xunit.Assert.False(access.IsTrustedAddress(s_robust));

        names.Answers["robust"] = new[] { s_robust };
        clock.Now += TimeSpan.FromSeconds(30);
        Xunit.Assert.False(access.IsTrustedAddress(s_robust), "looked up again before the interval passed");

        clock.Now += ControlPlaneAccess.NameLookUpInterval;
        Xunit.Assert.True(access.IsTrustedAddress(s_robust));
    }

    [Fact]
    public void AHostThatMovesIsTrustedAtItsNewAddressOnly()
    {
        IPAddress moved = IPAddress.Parse("192.0.2.51");
        ChangingNames names = new();
        names.Answers["robust"] = new[] { s_robust };
        FakeClock clock = new();
        ControlPlaneAccess access = Trusting("robust", names, clock);
        Xunit.Assert.True(access.IsTrustedAddress(s_robust));

        names.Answers["robust"] = new[] { moved };
        clock.Now += ControlPlaneAccess.NameLookUpInterval;

        Xunit.Assert.True(access.IsTrustedAddress(moved));
        Xunit.Assert.False(access.IsTrustedAddress(s_robust), "the address the host left is still trusted");
    }

    [Fact]
    public void ALookupThatFailsKeepsTheAddressesLastFound()
    {
        ChangingNames names = new();
        names.Answers["robust"] = new[] { s_robust };
        FakeClock clock = new();
        ControlPlaneAccess access = Trusting("robust", names, clock);

        names.Answers.Clear();
        clock.Now += ControlPlaneAccess.NameLookUpInterval;
        Xunit.Assert.False(access.IsTrustedAddress(s_thisMachine));

        Xunit.Assert.True(access.IsTrustedAddress(s_robust), "a failed lookup dropped robust");
    }

    // A refused caller looks the names up at most once an interval, and never when there are none.
    [Fact]
    public void RefusedCallsDoNotRepeatTheLookups()
    {
        ChangingNames names = new();
        FakeClock clock = new();
        ControlPlaneAccess byName = Trusting("robust", names, clock);
        int atStart = names.Lookups;

        for (int i = 0; i < 100; i++)
            byName.IsTrustedAddress(s_thisMachine);
        Xunit.Assert.Equal(atStart, names.Lookups);

        clock.Now += ControlPlaneAccess.NameLookUpInterval;
        for (int i = 0; i < 100; i++)
            byName.IsTrustedAddress(s_thisMachine);
        Xunit.Assert.Equal(atStart + 1, names.Lookups);

        ChangingNames none = new();
        ControlPlaneAccess byAddress = Trusting("192.0.2.50", none, clock);
        clock.Now += ControlPlaneAccess.NameLookUpInterval;
        byAddress.IsTrustedAddress(s_thisMachine);
        Xunit.Assert.Equal(0, none.Lookups);
    }

    // A link-local caller arrives with the number of the interface it came in on, which the entry has not got.
    [Theory]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%3")]
    [InlineData("[fe80::1%3]:8003")]
    public void ALinkLocalEntryMatchesItsCallerOnAnyInterface(string entry)
    {
        ControlPlaneAccess access = Trusting(entry, new ChangingNames(), new FakeClock());
        IPAddress caller = new(IPAddress.Parse("fe80::1").GetAddressBytes(), 2);

        Xunit.Assert.True(access.IsTrustedAddress(caller));
        Xunit.Assert.True(access.IsTrustedControlPlaneCaller(new IPEndPoint(caller, 9000)));
        Xunit.Assert.False(access.IsTrustedAddress(IPAddress.Parse("fe80::2")));
    }

    // A script's llHTTPRequest to this host's own public address comes from an address it may trust. The HTTP checks
    // refuse its marker; an XML-RPC handler sees only the caller, which is marked for it.
    [Fact]
    public void AScriptsXmlRpcCallFromATrustedAddressIsNotTrusted()
    {
        ControlPlaneAccess access = Trusting("203.0.113.10", new ChangingNames(), new FakeClock());
        IPEndPoint host = new(IPAddress.Parse("203.0.113.10"), 9000);
        ScriptRequestEndPoint script = new(host);

        Xunit.Assert.True(access.IsTrustedCaller(host));
        Xunit.Assert.False(access.IsTrustedCaller(script));
        Xunit.Assert.False(access.AuthorizePrivilegedInstantMessage(250, script));
        Xunit.Assert.False(access.AuthorizePrivilegedInstantMessage(
            (byte)OpenMetaverse.InstantMessageDialog.GodLikeRequestTeleport, script));
        Xunit.Assert.True(access.AuthorizePrivilegedInstantMessage(
            (byte)OpenMetaverse.InstantMessageDialog.MessageFromAgent, script));
        Xunit.Assert.False(access.IsTrustedCaller(null));
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