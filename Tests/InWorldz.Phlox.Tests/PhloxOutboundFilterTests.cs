using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A Phlox script's outbound requests obey the core's outbound URL filter,
/// [Network] OutboundDisallowForUserScripts (default: loopback, private and reserved IPv4 ranges) and
/// OutboundDisallowForUserScriptsExcept, exactly as YEngine's llHTTPRequest does (LSL_Api.cs:14762): the core
/// HttpRequestModule's own filter object, checked after the throttle and before anything is sent. A refused call gets
/// YEngine's result: "llHttpRequest: Request to &lt;url&gt; disallowed by filter" on DEBUG_CHANNEL, "" to the script, no
/// request and no http_response. What reaches the network is read by a loopback listener, not the script's view.
/// </summary>
[Collection("phlox-http")]
public class PhloxOutboundFilterTests
{
    private const int DebugChannel = 2147483647;

    /// <summary>A loopback endpoint that counts connections and answers 200.</summary>
    internal sealed class Counter : IDisposable
    {
        private readonly TcpListener m_tcp = new(IPAddress.Loopback, 0);
        private readonly Thread m_thread;
        private int m_hits;
        public int Port => ((IPEndPoint)m_tcp.LocalEndpoint).Port;
        public int Hits => Volatile.Read(ref m_hits);

        public Counter()
        {
            m_tcp.Start();
            m_thread = new Thread(Run) { IsBackground = true, Name = "phlox51-counter" };
            m_thread.Start();
        }

        private void Run()
        {
            while (true)
            {
                TcpClient c;
                try { c = m_tcp.AcceptTcpClient(); } catch { return; }
                Interlocked.Increment(ref m_hits);
                using (c)
                {
                    try
                    {
                        c.ReceiveTimeout = 2000;
                        var s = c.GetStream();
                        var buf = new byte[8192];
                        s.Read(buf, 0, buf.Length);
                        byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        s.Write(resp, 0, resp.Length);
                    }
                    catch { }
                }
            }
        }

        public void Dispose() => m_tcp.Stop();
    }

    /// <summary>
    /// A Phlox scene with the core HttpRequestModule. The module's filter is process-wide (built by the first Initialise
    /// until the last Close), so every class that builds one is in "phlox-http" and runs one at a time.
    /// </summary>
    internal sealed class Rig : IDisposable
    {
        public readonly Counter C = new();
        public readonly SchedulerHarness H;
        public readonly HttpRequestModule Http = new();

        public Rig(string disallow = null, Func<int, string> except = null, bool withYEngine = false)
        {
            H = new SchedulerHarness(cfg =>
            {
                IConfig n = cfg.Configs["Network"] ?? cfg.AddConfig("Network");
                if (disallow != null) n.Set("OutboundDisallowForUserScripts", disallow);
                if (except != null) n.Set("OutboundDisallowForUserScriptsExcept", except(C.Port));
            }, withYEngine);
            Http.Initialise(H.Config);
            Http.AddRegion(H.Scene);
        }

        public string Loopback(string host = "127.0.0.1") => "http://" + host + ":" + C.Port + "/phlox51";

        /// <summary>
        /// A Phlox script calls llHTTPRequest(url) and says "req=&lt;key&gt;" and, on a response, "status=&lt;n&gt;". Pumps
        /// until the script has spoken and then until a response arrives or the window ends.
        /// </summary>
        public void Request(string url, bool expectSent)
        {
            H.RezScript("default { state_entry() { key k = llHTTPRequest(" + Lsl(url) + ", [], \"\"); llSay(0, \"req=\" + (string)k); } " +
                        "http_response(key id, integer st, list m, string b) { llSay(0, \"status=\" + (string)st); } }");
            PumpUntil(() => H.Said.Any(s => s.StartsWith("req=")) && (!expectSent || H.Said.Any(s => s.StartsWith("status="))),
                      30);
            if (!expectSent) PumpFor(0.5);   // anything on its way would land now
        }

        public void PumpUntil(Func<bool> done, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && !done()) { if (!H.PumpOnceBusy()) Thread.Sleep(2); }
        }

        public void PumpFor(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) { if (!H.PumpOnceBusy()) Thread.Sleep(2); }
        }

        public void Dispose()
        {
            Http.RemoveRegion(H.Scene);
            Http.Close();
            C.Dispose();
            H.Dispose();
        }
    }

    internal static string Lsl(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static string Blocked(string function, string url) => function + ": Request to " + url + " disallowed by filter";

    private static void AssertRefused(Rig r, string url)
    {
        Assert.Equal(0, r.C.Hits);
        Assert.Contains("req=", r.H.Said);                                   // "" to the script, as YEngine (not NULL_KEY)
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("status="));      // no http_response
        Assert.Contains((DebugChannel, Blocked("llHttpRequest", url)), r.H.SaidOn);
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("Script error"));
    }

    private static void AssertSent(Rig r)
    {
        Assert.True(r.C.Hits >= 1, "no request reached the listener; said=[" + string.Join(" | ", r.H.Said) + "]");
        Assert.Contains("status=200", r.H.Said);
        Assert.DoesNotContain(r.H.SaidOn, m => m.Channel == DebugChannel && m.Message.Contains("disallowed by filter"));
    }

    // ── the default list ──────────────────────────────────────────────────────

    [Fact]
    public void LoopbackIsRefusedByDefaultAndNothingIsSent()
    {
        using var r = new Rig();
        string url = r.Loopback();
        r.Request(url, expectSent: false);
        AssertRefused(r, url);
    }

    /// <summary>Every range of the default list (OutboundUrlFilter.cs:72-73); nothing listens there, the check comes first.</summary>
    [Theory]
    [InlineData("http://0.0.0.1/x")]
    [InlineData("http://10.1.2.3/x")]
    [InlineData("http://100.64.0.1/x")]
    [InlineData("http://127.5.5.5:8002/x")]
    [InlineData("http://169.254.1.1/x")]
    [InlineData("http://172.16.0.1/x")]
    [InlineData("http://172.31.255.254:8003/x")]
    [InlineData("http://192.0.0.1/x")]
    [InlineData("http://192.0.2.1/x")]
    [InlineData("http://192.88.99.1/x")]
    [InlineData("http://192.168.0.10:9000/lslhttp/x/")]
    [InlineData("http://198.18.0.1/x")]
    [InlineData("http://198.51.100.1/x")]
    [InlineData("http://203.0.113.1/x")]
    [InlineData("http://224.0.0.1/x")]
    [InlineData("http://240.0.0.1/x")]
    [InlineData("http://255.255.255.255/x")]
    [InlineData("https://10.0.0.1/x")]
    public void EachDefaultRangeIsRefused(string url)
    {
        using var r = new Rig();
        r.Request(url, expectSent: false);
        AssertRefused(r, url);
    }

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://172.32.0.1/")]      // just past 172.16/12
    [InlineData("http://192.169.0.1/")]     // just past 192.168/16
    [InlineData("http://11.0.0.1/")]
    public void APublicAddressIsAllowedByTheFilter(string url)
    {
        // The filter's answer alone: no request goes to the internet from a unit test.
        using var r = new Rig();
        Assert.True(r.Http.CheckAllowed(new Uri(url)));
    }

    [Fact]
    public void AnAddressOutsideTheListIsSent()
    {
        // End to end through the script: a list without 127/8 makes the loopback listener an allowed destination.
        using var r = new Rig(disallow: "10.0.0.0/8|192.168.0.0/16");
        r.Request(r.Loopback(), expectSent: true);
        AssertSent(r);
    }

    [Fact]
    public void AHostNameIsResolvedAndItsPrivateAddressRefused()
    {
        using var r = new Rig();
        string url = r.Loopback("localhost");
        r.Request(url, expectSent: false);
        AssertRefused(r, url);
    }

    [Fact]
    public void AnIpv6LiteralIsRefusedAsTheFilterRefusesAHostWithNoIpv4Address()
    {
        using var r = new Rig();
        string url = "http://[::1]:" + r.C.Port + "/phlox51";
        r.Request(url, expectSent: false);
        AssertRefused(r, url);
    }

    // ── the exceptions ────────────────────────────────────────────────────────

    [Fact]
    public void AnExceptEntryForTheAddressAndPortAllowsIt()
    {
        using var r = new Rig(except: port => "127.0.0.1:" + port);
        r.Request(r.Loopback(), expectSent: true);
        AssertSent(r);
    }

    [Fact]
    public void AnExceptEntryForAnotherPortDoesNotAllowIt()
    {
        using var r = new Rig(except: port => "127.0.0.1:" + (port == 1 ? 2 : port - 1));
        string url = r.Loopback();
        r.Request(url, expectSent: false);
        AssertRefused(r, url);
    }

    [Fact]
    public void AnExceptNetworkAllowsTheWholeRange()
    {
        using var r = new Rig(except: _ => "127.0.0.0/8");
        r.Request(r.Loopback(), expectSent: true);
        AssertSent(r);
    }

    [Fact]
    public void AnExceptHostNameEntryIsResolvedAtStartup()
    {
        // The usual case: OutboundDisallowForUserScriptsExcept = <grid host>:<http port>.
        using var r = new Rig(except: port => "localhost:" + port);
        r.Request(r.Loopback("localhost"), expectSent: true);
        AssertSent(r);
    }

    // ── the region's own URL ──────────────────────────────────────────────────

    /// <summary>
    /// An llRequestURL URL is "http://" + ExternalHostNameForLSL + ":" + port + "/lslhttp/&lt;id&gt;" (core UrlModule.cs:241).
    /// YEngine gives it no allowance: its host is checked like any other. On a region whose host name resolves to a
    /// private address (say grid.example.org resolves to 192.168.0.10 on the region server) it is refused unless
    /// an Except entry names it. The listener stands in for the region's HTTP server.
    /// </summary>
    [Fact]
    public void TheRegionsOwnUrlHasNoAllowanceAndAnExceptEntryOpensIt()
    {
        string ownUrl(Rig r) => "http://localhost:" + r.C.Port + "/lslhttp/" + UUID.Random() + "/";
        using (var r = new Rig())
        {
            string url = ownUrl(r);
            r.Request(url, expectSent: false);
            AssertRefused(r, url);
        }
        using (var r = new Rig(except: port => "localhost:" + port))
        {
            r.Request(ownUrl(r), expectSent: true);
            AssertSent(r);
        }
    }

    // ── order and other paths ─────────────────────────────────────────────────

    [Fact]
    public void AnUrlTheCoreCannotOpenIsNotReportedAsFiltered()
    {
        using var r = new Rig();
        r.Request("ftp://127.0.0.1/x", expectSent: false);
        Assert.Equal(0, r.C.Hits);
        Assert.DoesNotContain(r.H.SaidOn, m => m.Message.Contains("disallowed by filter"));
    }

    [Fact]
    public void AnRedirectToAPrivateAddressIsStillRefusedByTheCore()
    {
        // The core checks every redirect with the same filter (HttpRequestModule.cs:717), for both engines.
        using var r = new Rig(except: port => "127.0.0.1:" + port);
        Assert.False(r.Http.CheckAllowed(new Uri("http://10.9.9.9/")));
        Assert.True(r.Http.CheckAllowed(new Uri(r.Loopback())));
    }

    /// <summary>
    /// Records what reaches the core XML-RPC module. The core's sender does not run in the harness, and what matters is
    /// whether Phlox hands the destination to the core at all (the core posts it unchecked, for YEngine too).
    /// </summary>
    private sealed class RecordingXmlRpc : IXMLRPC
    {
        public readonly List<string> Sent = new();
        public UUID OpenXMLRPCChannel(uint localID, UUID itemID, UUID channelID) => UUID.Random();
        public void CloseXMLRPCChannel(UUID channelKey) { }
        public bool hasRequests() => false;
        public void RemoteDataReply(string channel, string message_id, string sdata, int idata) { }
        public bool IsEnabled() => true;
        public IXmlRpcRequestInfo GetNextCompletedRequest() => null;
        public void RemoveCompletedRequest(UUID id) { }
        public void DeleteChannels(UUID itemID) { }
        public UUID SendRemoteData(uint localID, UUID itemID, string channel, string dest, int idata, string sdata)
        {
            lock (Sent) Sent.Add(dest);
            return UUID.Random();
        }
        public IServiceRequest GetNextCompletedSRDRequest() => null;
        public void RemoveCompletedSRDRequest(UUID id) { }
        public void CancelSRDRequests(UUID itemID) { }
        public int Port => 0;
    }

    private static RecordingXmlRpc SendRemoteData(Rig r, string url)
    {
        var x = new RecordingXmlRpc();
        r.H.Scene.RegisterModuleInterface<IXMLRPC>(x);
        r.H.RezScript("default { state_entry() { key k = llSendRemoteData(NULL_KEY, " + Lsl(url) + ", 1, \"s\"); llSay(0, \"req=[\" + (string)k + \"]\"); } }");
        r.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("req=")), 30);
        return x;
    }

    [Fact]
    public void LlSendRemoteDataToARefusedAddressIsRefusedTheSameWayAndNothingIsSent()
    {
        using var r = new Rig();
        string url = r.Loopback();
        var x = SendRemoteData(r, url);
        lock (x.Sent) Assert.Empty(x.Sent);
        Assert.Contains("req=[]", r.H.Said);
        Assert.Contains((DebugChannel, Blocked("llSendRemoteData", url)), r.H.SaidOn);
    }

    [Fact]
    public void LlSendRemoteDataToAnAllowedAddressIsHandedToTheCore()
    {
        using var r = new Rig(except: port => "127.0.0.1:" + port);
        string url = r.Loopback();
        var x = SendRemoteData(r, url);
        lock (x.Sent) Assert.Equal(new[] { url }, x.Sent);
        Assert.DoesNotContain(r.H.SaidOn, m => m.Message.Contains("disallowed by filter"));
    }
}

/// <summary>
/// The two engines side by side on one region, the same refused request from each: the same text on
/// DEBUG_CHANNEL, the same "" return, no request. In "phlox-state" (runs alone): it needs YEngine's statics and the core
/// HttpRequestModule's process-wide filter at once.
/// </summary>
[Collection("phlox-state")]
public class PhloxOutboundFilterYEngineTests
{
    private const int DebugChannel = 2147483647;

    [Fact]
    public void ARefusedRequestLooksTheSameFromYEngineAndPhlox()
    {
        using var r = new PhloxOutboundFilterTests.Rig(withYEngine: true);
        string url = r.Loopback();
        string src = "default { state_entry() { key k = llHTTPRequest(" + PhloxOutboundFilterTests.Lsl(url) + ", [], \"\"); " +
                     "llSay(0, \"req=[\" + (string)k + \"]\"); } http_response(key id, integer st, list m, string b) { llSay(0, \"status=\" + (string)st); } }";

        // YEngine
        var yPart = r.H.Prim;
        var item = SchedulerHarnessYEngine.Rez(r.H, yPart, src);
        r.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("req=")), 30);
        r.PumpFor(1.0);
        r.PumpUntil(() => r.H.SaidOn.Any(m => m.Channel == DebugChannel && m.Message == PhloxOutboundFilterTests.Blocked("llHttpRequest", url)), 30);
        var ySaid = r.H.Said.ToList();
        var yDebug = r.H.SaidOn.Where(m => m.Channel == DebugChannel).Select(m => m.Message).Distinct().ToList();
        r.H.ClearSaid(item);

        // Phlox
        r.H.RezScript(src);
        r.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("req=")), 30);
        r.PumpFor(1.0);
        r.PumpUntil(() => r.H.SaidOn.Any(m => m.Channel == DebugChannel && m.Message == PhloxOutboundFilterTests.Blocked("llHttpRequest", url)), 30);
        var pSaid = r.H.Said.ToList();
        var pDebug = r.H.SaidOn.Where(m => m.Channel == DebugChannel).Select(m => m.Message).Distinct().ToList();

        string expected = PhloxOutboundFilterTests.Blocked("llHttpRequest", url);
        Assert.Contains(expected, yDebug);
        Assert.Contains(expected, pDebug);
        Assert.Contains("req=[]", ySaid);
        Assert.Contains("req=[]", pSaid);
        Assert.DoesNotContain(ySaid, s => s.StartsWith("status="));
        Assert.DoesNotContain(pSaid, s => s.StartsWith("status="));
        Assert.Equal(0, r.C.Hits);
    }
}

/// <summary>CrossEngineChatTests' way of putting a script on YEngine.</summary>
internal static class SchedulerHarnessYEngine
{
    public static UUID Rez(SchedulerHarness h, OpenSim.Region.Framework.Scenes.SceneObjectPart part, string source)
    {
        var item = OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(
            h.Scene.AssetService, part, UUID.Random(), UUID.Random(), "yscript" + Guid.NewGuid().ToString("N")[..6], source);
        Assert.True(part.Inventory.CreateScriptInstance(item.ItemID, 0, false, h.YEngine.ScriptEngineName, 1));
        var errors = h.YEngine.GetScriptErrors(item.ItemID);
        Assert.True(errors.Count == 0, "YEngine did not compile it: " + string.Join(" | ", errors.Cast<object>()));
        // YEngine holds a new script suspended until the region resumes the object's scripts, as it does after a rez.
        part.ParentGroup.ResumeScripts();
        return item.ItemID;
    }
}

/// <summary>
/// The classes that build the core HttpRequestModule, whose outbound filter and clients are process-wide
/// statics (built by the first Initialise, dropped by the last Close). They run one at a time, in parallel with the rest.
/// </summary>
[CollectionDefinition("phlox-http")]
public class PhloxHttpCollection { }
