using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Threading;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.TrustedHypergrid;
using OpenSim.Server.Handlers.Hypergrid;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using Xunit;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.TrustedHypergrid.Tests;

/// <summary>
/// Slice 3b — enforcement, minimum viable (ADR-005, ADR-011): Blocked is the only refusal.
/// Covers the published per-request context, its non-leak guarantees, the single refusal rule,
/// every default under which no refusal is possible, and the end-to-end XML-RPC handler path.
/// </summary>
public class EnforcementTests : IDisposable
{
    private readonly List<string> m_tempFiles = new();

    public void Dispose()
    {
        TrustedHypergridHooks.Runtime = null;
        TrustedHypergridHooks.Lookup = null;
        GridTrustContext.Current = null;
        foreach (string f in m_tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private string TempFile(string ext)
    {
        string f = Path.Combine(Path.GetTempPath(), "thg-enforce-" + Guid.NewGuid().ToString("N") + ext);
        m_tempFiles.Add(f);
        return f;
    }

    private IConfigSource Config(bool enabled = true, string gatekeeperAuthType = null, string networkAuthType = null)
    {
        IniConfigSource cfg = new IniConfigSource();
        IConfig dbs = cfg.AddConfig("DatabaseService");
        dbs.Set("StorageProvider", Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.SQLite.dll"));
        dbs.Set("ConnectionString", "URI=file:" + TempFile(".db") + ",version=3");
        IConfig th = cfg.AddConfig(TrustedHypergridRuntime.ConfigSection);
        th.Set("Enabled", enabled ? "true" : "false");
        th.Set("PrivateKeyFile", TempFile(".ini"));
        cfg.AddConfig("Hypergrid").Set("GatekeeperURI", "http://home.example:8002");
        if (gatekeeperAuthType != null)
            cfg.AddConfig("GatekeeperService").Set("AuthType", gatekeeperAuthType);
        if (networkAuthType != null)
            cfg.AddConfig("Network").Set("AuthType", networkAuthType);
        return cfg;
    }

    /// <summary>Our Robust: registry + runtime (verifier bound to the registry), hooks published.</summary>
    private TrustedGridRegistryService Arm(bool enabled = true)
    {
        IConfigSource cfg = Config(enabled);
        TrustedGridRegistryService reg = new(cfg);
        TrustedHypergridHooks.Runtime = TrustedHypergridRuntime.FromConfig(cfg, reg);
        TrustedHypergridHooks.Lookup = reg;
        return reg;
    }

    /// <summary>A remote Tranquillity grid that signs with tg_uri.</summary>
    private sealed class Remote
    {
        public TrustedHypergridRuntime Runtime;
        public string Uri;
        public Hashtable Signed(string method, string regionName = "Welcome")
        {
            Hashtable p = new() { ["region_name"] = regionName };
            Runtime.Signer.SignInto(p, method, DateTime.UtcNow);
            return p;
        }
    }

    private Remote NewRemote(string host)
    {
        IniConfigSource cfg = new IniConfigSource();
        IConfig th = cfg.AddConfig(TrustedHypergridRuntime.ConfigSection);
        th.Set("Enabled", "true");
        th.Set("PrivateKeyFile", TempFile(".ini"));
        cfg.AddConfig("Hypergrid").Set("GatekeeperURI", "http://" + host + ":8002");
        TrustedHypergridRuntime rt = TrustedHypergridRuntime.FromConfig(cfg);
        return new Remote { Runtime = rt, Uri = "http://" + host + ":8002/" };
    }

    private static bool Auth(IServiceAuth auth) => auth.Authenticate(new NameValueCollection(), (k, v) => { }, out _);

    private static XmlRpcRequest Rpc(string method, Hashtable p) => new(method, new ArrayList { p });

    private sealed class FakeGatekeeper : IGatekeeperService
    {
        public int LinkCalls, GetCalls;
        public bool LinkLocalRegion(string regionDescriptor, out UUID regionID, out ulong regionHandle, out string externalName,
            out string imageURL, out string reason, out int sizeX, out int sizeY)
        {
            LinkCalls++;
            regionID = UUID.Random(); regionHandle = 1; externalName = "http://home.example:8002/ Welcome";
            imageURL = string.Empty; reason = string.Empty; sizeX = 256; sizeY = 256;
            return true;
        }
        public GridRegion GetHyperlinkRegion(UUID regionID, UUID agentID, string agentHomeURI, out string message)
        {
            GetCalls++;
            message = null;
            return new GridRegion
            {
                RegionID = regionID, RegionName = "Welcome", ExternalHostName = "home.example", HttpPort = 9000,
                ServerURI = "http://home.example:9000/", InternalEndPoint = new IPEndPoint(IPAddress.Loopback, 9000),
            };
        }
        public bool LoginAgent(GridRegion source, AgentCircuitData aCircuit, GridRegion destination, out string reason)
        {
            reason = string.Empty;
            return true;
        }
    }

    // ---- 1. Authenticate: false ONLY for Blocked --------------------------------------------------

    [Fact]
    public void Authenticate_BlockedContext_ReturnsFalse_403()
    {
        TrustedGridAuthentication auth = new();
        using (GridTrustContext.Enter(new GridTrustContext { GridId = UUID.Random(), Tier = GridTrustContext.TierBlocked, Outcome = VerificationOutcome.Verified }))
        {
            Assert.False(auth.Authenticate(new NameValueCollection(), (k, v) => { }, out HttpStatusCode code));
            Assert.Equal(HttpStatusCode.Forbidden, code);
        }
    }

    public static IEnumerable<object[]> NonBlockedContexts()
    {
        yield return new object[] { "Trusted verified", new GridTrustContext { GridId = UUID.Random(), Tier = GridTrustContext.TierTrusted, Outcome = VerificationOutcome.Verified } };
        yield return new object[] { "Open verified (pending/unknown)", new GridTrustContext { GridId = UUID.Random(), Tier = GridTrustContext.TierOpen, Outcome = VerificationOutcome.Verified } };
        yield return new object[] { "Open unverified (unsigned/tampered)", GridTrustContext.Open };
        yield return new object[] { "no context at all", null };
    }

    [Theory]
    [MemberData(nameof(NonBlockedContexts))]
    public void Authenticate_EveryNonBlockedCase_ReturnsTrue(string label, GridTrustContext ctx)
    {
        TrustedGridAuthentication auth = new();
        using (GridTrustContext.Enter(ctx))
        {
            Assert.True(auth.Authenticate(new NameValueCollection(), (k, v) => { }, out HttpStatusCode code), label);
            Assert.Equal(HttpStatusCode.OK, code);
        }
        Assert.True(auth.Authenticate(string.Empty), label + " (string form)");
    }

    // ---- 2. Current: published for the request only, never leaks --------------------------------

    [Fact]
    public void Enter_PublishesForScope_RestoresOnDispose_IncludingNested()
    {
        Assert.Null(GridTrustContext.Current);
        GridTrustContext a = new() { Tier = GridTrustContext.TierBlocked };
        GridTrustContext b = new() { Tier = GridTrustContext.TierTrusted };
        using (GridTrustContext.Enter(a))
        {
            Assert.Same(a, GridTrustContext.Current);
            using (GridTrustContext.Enter(b))
                Assert.Same(b, GridTrustContext.Current);
            Assert.Same(a, GridTrustContext.Current);
        }
        Assert.Null(GridTrustContext.Current);
    }

    [Fact]
    public void Current_DoesNotLeak_BetweenSequentialRequests_OnOneThread()
    {
        TrustedGridAuthentication auth = new();
        // request 1: Blocked
        using (GridTrustContext.Enter(new GridTrustContext { Tier = GridTrustContext.TierBlocked }))
            Assert.False(Auth(auth));
        // request 2 on the same thread, unclassified: must not inherit request 1
        Assert.Null(GridTrustContext.Current);
        Assert.True(Auth(auth));
        using (GridTrustContext.Enter(null))
            Assert.True(Auth(auth));
        // an exception inside the scope still clears it
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (GridTrustContext.Enter(new GridTrustContext { Tier = GridTrustContext.TierBlocked }))
                throw new InvalidOperationException("handler failed");
        }));
        Assert.Null(GridTrustContext.Current);
        Assert.True(Auth(auth));
    }

    [Fact]
    public void Current_DoesNotLeak_BetweenConcurrentRequests_OnDifferentThreads()
    {
        const int n = 8;
        TrustedGridAuthentication auth = new();
        Barrier barrier = new(n);
        Exception failure = null;
        Thread[] threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    bool blocked = idx % 2 == 0;
                    GridTrustContext mine = new() { GridId = UUID.Random(), Tier = blocked ? GridTrustContext.TierBlocked : GridTrustContext.TierTrusted };
                    using (GridTrustContext.Enter(mine))
                    {
                        barrier.SignalAndWait();                 // everyone has published
                        for (int k = 0; k < 200; k++)
                        {
                            Assert.Same(mine, GridTrustContext.Current);
                            Assert.Equal(!blocked, Auth(auth));
                            Thread.Yield();
                        }
                        barrier.SignalAndWait();                 // everyone finished reading
                    }
                    Assert.Null(GridTrustContext.Current);
                }
                catch (Exception e) { failure ??= e; }
            });
            threads[i].Start();
        }
        foreach (Thread t in threads) t.Join();
        Assert.Null(failure);
        Assert.Null(GridTrustContext.Current);
    }

    // ---- 3. The refusal requires ALL of: Enabled, hgtrust block, a verifying signature, AuthType --

    [Fact]
    public void BlockedGrid_VerifiedSignature_ClassifyScope_AuthenticateFalse_ThenForget_True()
    {
        TrustedGridRegistryService reg = Arm();
        TrustedGridAuthentication auth = new();
        Remote bad = NewRemote("bad.example");

        // first contact records it Open/pending — no refusal possible yet
        using (TrustedHypergridHooks.Classify(bad.Signed("link_region"), "link_region"))
            Assert.True(Auth(auth));
        Assert.Equal((int)TrustTier.Open, reg.Find(bad.Uri).Tier);

        reg.Block(bad.Uri);                                       // the operator's `hgtrust block`
        using (TrustedHypergridHooks.Classify(bad.Signed("link_region"), "link_region"))
        {
            Assert.Equal(GridTrustContext.TierBlocked, GridTrustContext.Current.Tier);
            Assert.False(Auth(auth));
        }
        Assert.Null(GridTrustContext.Current);

        Assert.True(reg.Forget(bad.Uri));                         // `hgtrust forget`
        using (TrustedHypergridHooks.Classify(bad.Signed("get_region"), "get_region"))
            Assert.True(Auth(auth));                              // back to first contact: Open
        Assert.Equal((int)TrustTier.Open, reg.Find(bad.Uri).Tier);
    }

    [Fact]
    public void BlockedGrid_UnsignedRequest_IsNotRefused_BlockNeedsAVerifyingSignature()
    {
        TrustedGridRegistryService reg = Arm();
        Remote bad = NewRemote("bad.example");
        reg.RecordPresentedKey(bad.Uri, bad.Runtime.Keypair.PublicKey, bad.Runtime.Fingerprint, DateTime.UtcNow);
        reg.Block(bad.Uri);

        // unsigned (a stock grid, or the blocked grid not signing): Open/Unverified → proceeds
        using (TrustedHypergridHooks.Classify(new Hashtable { ["region_name"] = "x" }, "link_region"))
        {
            Assert.Equal(GridTrustContext.TierOpen, GridTrustContext.Current.Tier);
            Assert.True(Auth(new TrustedGridAuthentication()));
        }
    }

    [Fact]
    public void EnabledFalse_WithBlockedRowPresent_AuthenticateTrue_NothingPublished()
    {
        // Build the registry with a Blocked row, then run with Enabled=false: the runtime is
        // disabled, Classify publishes null, and the authenticator proceeds — identical to a grid
        // without this code (Design Brief §11.7).
        IConfigSource cfg = Config(enabled: true);
        TrustedGridRegistryService reg = new(cfg);
        Remote bad = NewRemote("bad.example");
        reg.RecordPresentedKey(bad.Uri, bad.Runtime.Keypair.PublicKey, bad.Runtime.Fingerprint, DateTime.UtcNow);
        reg.Block(bad.Uri);
        Assert.Equal((int)TrustTier.Blocked, reg.Find(bad.Uri).Tier);

        ((IConfig)cfg.Configs[TrustedHypergridRuntime.ConfigSection]).Set("Enabled", "false");
        TrustedHypergridHooks.Runtime = TrustedHypergridRuntime.FromConfig(cfg, reg);
        TrustedHypergridHooks.Lookup = reg;
        Assert.False(TrustedHypergridHooks.Runtime.Enabled);

        using (TrustedHypergridHooks.Classify(bad.Signed("link_region"), "link_region"))
        {
            Assert.Null(GridTrustContext.Current);
            Assert.True(Auth(new TrustedGridAuthentication()));
        }
    }

    [Fact]
    public void EmptyRegistry_And_TrustedOpenPending_NoRefusalPossible()
    {
        TrustedGridRegistryService reg = Arm();
        TrustedGridAuthentication auth = new();
        Remote a = NewRemote("a.example"); Remote b = NewRemote("b.example"); Remote c = NewRemote("c.example");

        // empty registry: verified but unknown → Open
        using (TrustedHypergridHooks.Classify(a.Signed("link_region"), "link_region"))
            Assert.True(Auth(auth));

        reg.Approve(a.Uri, "john");                               // Trusted
        // b: recorded pending (Open) by the call above? no — record now
        using (TrustedHypergridHooks.Classify(b.Signed("link_region"), "link_region")) { }
        Assert.Equal((int)TrustState.Pending, reg.Find(b.Uri).State);

        using (TrustedHypergridHooks.Classify(a.Signed("get_region"), "get_region"))
        {
            Assert.Equal(GridTrustContext.TierTrusted, GridTrustContext.Current.Tier);
            Assert.True(Auth(auth));
        }
        using (TrustedHypergridHooks.Classify(b.Signed("get_region"), "get_region"))
        {
            Assert.Equal(GridTrustContext.TierOpen, GridTrustContext.Current.Tier);
            Assert.True(Auth(auth));
        }
        using (TrustedHypergridHooks.Classify(c.Signed("get_region"), "get_region"))   // unknown until now
            Assert.True(Auth(auth));
    }

    [Fact]
    public void IsConfigured_OnlyForTrustedGridAuthentication_InGatekeeperOrNetworkSection()
    {
        Assert.False(TrustedGridAuthentication.IsConfigured(Config(), "GatekeeperService"));
        Assert.False(TrustedGridAuthentication.IsConfigured(Config(gatekeeperAuthType: "None"), "GatekeeperService"));
        Assert.False(TrustedGridAuthentication.IsConfigured(Config(gatekeeperAuthType: "BasicHttpAuthentication"), "GatekeeperService"));
        Assert.True(TrustedGridAuthentication.IsConfigured(Config(gatekeeperAuthType: "TrustedGridAuthentication"), "GatekeeperService"));
        Assert.True(TrustedGridAuthentication.IsConfigured(Config(networkAuthType: "TrustedGridAuthentication"), "GatekeeperService"));
        Assert.False(TrustedGridAuthentication.IsConfigured(null, "GatekeeperService"));
    }

    // ---- 4. End to end through the XML-RPC handlers ------------------------------------------------

    [Fact]
    public void Handlers_BlockedGrid_RefusedOnlyWhenAuthConfigured_TrustedAndOpenIdentical()
    {
        TrustedGridRegistryService reg = Arm();
        FakeGatekeeper gk = new();
        HypergridHandlers armed = new(gk, new TrustedGridAuthentication());   // AuthType configured
        HypergridHandlers unarmed = new(gk);                                   // AuthType not configured
        IPEndPoint ep = new(IPAddress.Loopback, 12345);
        Remote bad = NewRemote("bad.example"); Remote good = NewRemote("good.example"); Remote open = NewRemote("open.example");

        // first contact for all three (recorded Open/pending), then operator actions
        armed.LinkRegionRequest(Rpc("link_region", bad.Signed("link_region")), ep);
        armed.LinkRegionRequest(Rpc("link_region", good.Signed("link_region")), ep);
        armed.LinkRegionRequest(Rpc("link_region", open.Signed("link_region")), ep);
        reg.Block(bad.Uri);
        reg.Approve(good.Uri, "john");
        int callsBefore = gk.LinkCalls + gk.GetCalls;

        // Blocked + armed → refused, gatekeeper never called
        Hashtable r1 = (Hashtable)armed.LinkRegionRequest(Rpc("link_region", bad.Signed("link_region")), ep).Value;
        Assert.Equal("False", r1["result"]);
        Assert.Equal(HypergridHandlers.RefusalMessage, r1["message"]);
        Hashtable r2 = (Hashtable)armed.GetRegion(Rpc("get_region", bad.Signed("get_region")), ep).Value;
        Assert.Equal("False", r2["result"]);
        Assert.Equal(callsBefore, gk.LinkCalls + gk.GetCalls);
        Assert.Null(GridTrustContext.Current);                                // cleared after the request

        // Blocked + NOT armed → proceeds (the refusal requires AuthType)
        Hashtable r3 = (Hashtable)unarmed.LinkRegionRequest(Rpc("link_region", bad.Signed("link_region")), ep).Value;
        Assert.Equal("True", r3["result"]);

        // ADR-011: Trusted and Open receive identical responses through the armed handlers
        Hashtable t = (Hashtable)armed.LinkRegionRequest(Rpc("link_region", good.Signed("link_region")), ep).Value;
        Hashtable o = (Hashtable)armed.LinkRegionRequest(Rpc("link_region", open.Signed("link_region")), ep).Value;
        Assert.Equal("True", t["result"]); Assert.Equal("True", o["result"]);
        Assert.Equal(t["external_name"], o["external_name"]);
        Assert.False(t.ContainsKey("message")); Assert.False(o.ContainsKey("message"));
        Hashtable tg = (Hashtable)armed.GetRegion(Rpc("get_region", good.Signed("get_region")), ep).Value;
        Hashtable og = (Hashtable)armed.GetRegion(Rpc("get_region", open.Signed("get_region")), ep).Value;
        Assert.Equal("true", tg["result"]); Assert.Equal("true", og["result"]);
        Assert.Equal(tg["server_uri"], og["server_uri"]);

        // a stock grid (unsigned) through the armed handlers → proceeds
        Hashtable s = (Hashtable)armed.LinkRegionRequest(Rpc("link_region", new Hashtable { ["region_name"] = "Welcome" }), ep).Value;
        Assert.Equal("True", s["result"]);

        // after forget, the blocked grid is back to first contact and proceeds
        reg.Forget(bad.Uri);
        Hashtable r4 = (Hashtable)armed.LinkRegionRequest(Rpc("link_region", bad.Signed("link_region")), ep).Value;
        Assert.Equal("True", r4["result"]);
        Assert.Equal((int)TrustTier.Open, reg.Find(bad.Uri).Tier);
    }

    [Fact]
    public void Handlers_Disabled_NeverRefuse_EvenWhenArmed_AndBlockedRowExists()
    {
        IConfigSource cfg = Config(enabled: true);
        TrustedGridRegistryService reg = new(cfg);
        Remote bad = NewRemote("bad.example");
        reg.RecordPresentedKey(bad.Uri, bad.Runtime.Keypair.PublicKey, bad.Runtime.Fingerprint, DateTime.UtcNow);
        reg.Block(bad.Uri);
        ((IConfig)cfg.Configs[TrustedHypergridRuntime.ConfigSection]).Set("Enabled", "false");
        TrustedHypergridHooks.Runtime = TrustedHypergridRuntime.FromConfig(cfg, reg);
        TrustedHypergridHooks.Lookup = reg;

        HypergridHandlers armed = new(new FakeGatekeeper(), new TrustedGridAuthentication());
        Hashtable r = (Hashtable)armed.LinkRegionRequest(Rpc("link_region", bad.Signed("link_region")), new IPEndPoint(IPAddress.Loopback, 1)).Value;
        Assert.Equal("True", r["result"]);
        Assert.Null(GridTrustContext.Current);
    }
}
