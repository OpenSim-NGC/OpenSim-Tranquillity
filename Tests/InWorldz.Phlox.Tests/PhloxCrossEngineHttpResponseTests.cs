using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// One region running YEngine and Phlox, both HTTP pumps running. The core keeps one completed-request queue per
/// region (HttpRequestModule.GetNextCompletedRequest dequeues), and whichever engine's pump takes a response must get it
/// to the script that asked, whichever engine runs it. Before, a YEngine script's response taken by Phlox's pump was
/// posted through Phlox only and lost.
/// Scripts in several prims (the core throttles per prim: 3 at once, then 1 a second) each make requests to a loopback
/// listener that answers each path with its own body and status. Every YEngine request must get exactly one
/// http_response, in the script that made it, with its body and status; no script gets a duplicate, another script's
/// response or a wrong body or status.
/// YEngine's pump (the core's Shared/Api/Plugins/HttpRequest.cs) now offers what it takes to every script engine
/// of the region, so a Phlox script's response it takes reaches it too. And, as SL says ("triggered in all scripts in the
/// prim, not just in the requesting script"), a prim with a YEngine and a Phlox script gets each response in both.
/// In "phlox-state" (runs alone), as PhloxMimeTypeYEngineTests: it needs YEngine's statics and the core HttpRequestModule's
/// process-wide filter at once. The endpoint is a loopback listener opened by an Except entry.
/// </summary>
[Collection("phlox-state")]
public class PhloxCrossEngineHttpResponseTests
{
    private const int PrimsPerEngine = 10;
    private const int RequestsPerScript = 5;   // 50 per engine

    private readonly ITestOutputHelper _out;
    public PhloxCrossEngineHttpResponseTests(ITestOutputHelper o) => _out = o;

    /// <summary>A loopback endpoint: GET /x/&lt;tag&gt;-&lt;n&gt; answers status 200 (even n) or 202 (odd n), body "&lt;tag&gt;-&lt;n&gt;".</summary>
    private sealed class Echo : IDisposable
    {
        private readonly TcpListener m_tcp = new(IPAddress.Loopback, 0);
        private int m_hits;
        public int Port => ((IPEndPoint)m_tcp.LocalEndpoint).Port;
        public int Hits => Volatile.Read(ref m_hits);

        public Echo()
        {
            m_tcp.Start();
            new Thread(Run) { IsBackground = true, Name = "phlox55-echo" }.Start();
        }

        private void Run()
        {
            while (true)
            {
                TcpClient c;
                try { c = m_tcp.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        private void Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 5000;
                    var s = c.GetStream();
                    var buf = new byte[8192];
                    int n = s.Read(buf, 0, buf.Length);
                    string line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];   // GET /x/tag-n HTTP/1.1
                    string body = line.Split(' ')[1].Substring("/x/".Length);
                    int status = int.Parse(body[(body.LastIndexOf('-') + 1)..]) % 2 == 0 ? 200 : 202;
                    Interlocked.Increment(ref m_hits);
                    byte[] resp = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Accepted")}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                    s.Write(resp, 0, resp.Length);
                }
                catch { }
            }
        }

        public void Dispose() => m_tcp.Stop();
    }

    private static string Script(string url, string tag) =>
        "integer n = 0; " +
        "default { state_entry() { llSetTimerEvent(0.3); } " +
        "timer() { if (n >= " + RequestsPerScript + ") { llSetTimerEvent(0); return; } " +
        "  key k = llHTTPRequest(\"" + url + "/" + tag + "-\" + (string)n, [], \"\"); " +
        "  if (k != NULL_KEY && (string)k != \"\") { llSay(0, \"req " + tag + " \" + (string)n + \" \" + (string)k); n++; } } " +
        "http_response(key id, integer st, list m, string b) { llSay(0, \"resp " + tag + " \" + (string)id + \" \" + (string)st + \" \" + b); } }";

    private static readonly Regex Req = new(@"^req (\S+) (\d+) (\S+)$");
    private static readonly Regex Resp = new(@"^resp (\S+) (\S+) (\d+) (\S*)$");

    private sealed record Outcome(int Requests, int Expected, int Hits, int Responses, int LostY, int LostP, int Duplicates,
                                  int WrongScript, int WrongBody, int WrongStatus, int Strays, int Outstanding);

    /// <param name="engines">whose responses the wait is for: "Y", or "YP" for both.</param>
    /// <param name="mixed">The YEngine and the Phlox script of each pair share one prim, and each must hear every
    /// response of that prim (SL). Otherwise each script has a prim of its own.</param>
    private Outcome Run(string engines, bool mixed = false)
    {
        using var echo = new Echo();
        using var r = new PhloxOutboundFilterTests.Rig(except: _ => "127.0.0.1:" + echo.Port, withYEngine: true);
        string url = "http://127.0.0.1:" + echo.Port + "/x";

        var tags = new List<string>();
        for (int p = 0; p < PrimsPerEngine; p++)
        {
            SceneObjectPart shared = mixed ? SceneHelpers.AddSceneObject(r.H.Scene, "core6 " + p, UUID.Random()).RootPart : null;
            foreach (string engine in new[] { "Y", "P" })
            {
                string tag = engine + p;
                var part = shared ?? SceneHelpers.AddSceneObject(r.H.Scene, "phlox55 " + tag, UUID.Random()).RootPart;
                if (engine == "Y") SchedulerHarnessYEngine.Rez(r.H, part, Script(url, tag));
                else r.H.RezScriptInto(part, Script(url, tag));
                tags.Add(tag);
            }
        }
        int expected = tags.Count * RequestsPerScript;

        List<(string Tag, int N, string Key)> reqs = new();
        List<(string Tag, string Key, int Status, string Body)> resps = new();
        void Read()
        {
            var said = r.H.Said;
            reqs = said.Select(s => Req.Match(s)).Where(m => m.Success)
                .Select(m => (m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value)).ToList();
            resps = said.Select(s => Resp.Match(s)).Where(m => m.Success)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value), m.Groups[4].Value)).ToList();
        }

        // Wait for the result: every request made and answered by the listener, and a response delivered for every request
        // of the engines named (or the limit).
        r.PumpUntil(() =>
        {
            Read();
            if (reqs.Count < expected || echo.Hits < expected) return false;
            var heard = resps.Select(s => (s.Tag, s.Key)).ToHashSet();
            return reqs.Where(q => engines.Contains(q.Tag[0]))
                .All(q => Hearers(q.Tag).All(t => heard.Contains((t, q.Key))));
        }, 90);
        r.PumpFor(1.0);   // a duplicate or a stray still on its way lands now
        Read();

        var byKey = reqs.ToDictionary(q => q.Key);
        int lostY = 0, lostP = 0, duplicates = 0, wrongScript = 0, wrongBody = 0, wrongStatus = 0;
        foreach (var q in reqs)
        {
            var got = resps.Where(s => s.Key == q.Key).ToList();
            string[] hearers = Hearers(q.Tag);
            if (hearers.Any(t => !got.Any(g => g.Tag == t))) { if (q.Tag.StartsWith("Y")) lostY++; else lostP++; }
            if (got.GroupBy(g => g.Tag).Any(g => g.Count() > 1)) duplicates++;
            foreach (var g in got)
            {
                if (!hearers.Contains(g.Tag)) wrongScript++;
                if (g.Body != q.Tag + "-" + q.N) wrongBody++;
                if (g.Status != (q.N % 2 == 0 ? 200 : 202)) wrongStatus++;
            }
        }
        int strays = resps.Count(s => !byKey.ContainsKey(s.Key));

        _out.WriteLine($"requests={reqs.Count}/{expected} (Y {reqs.Count(q => q.Tag.StartsWith("Y"))}, P {reqs.Count(q => q.Tag.StartsWith("P"))}) " +
                       $"listener hits={echo.Hits} responses={resps.Count}");
        _out.WriteLine($"lost: YEngine {lostY}, Phlox {lostP}; duplicates={duplicates} wrongScript={wrongScript} " +
                       $"wrongBody={wrongBody} wrongStatus={wrongStatus} strays={strays}; " +
                       $"Phlox pump: dropped={r.H.Engine.AsyncCommands.HttpRequestPlugin.DroppedResponses} " +
                       $"still outstanding={r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount}");
        return new Outcome(reqs.Count, expected, echo.Hits, resps.Count, lostY, lostP, duplicates, wrongScript, wrongBody,
                           wrongStatus, strays, r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount);

        // The scripts that must hear a response to this script's request: itself, and with mixed prims its prim's partner.
        string[] Hearers(string tag) => mixed ? new[] { "Y" + tag.Substring(1), "P" + tag.Substring(1) } : new[] { tag };
    }

    [Fact]
    public void EveryYEngineResponseReachesItsScriptExactlyOnceWhicheverPumpTakesIt()
    {
        var o = Run("Y");
        Assert.Equal(o.Expected, o.Requests);
        Assert.Equal(o.Expected, o.Hits);
        Assert.Equal(0, o.LostY);
        Assert.Equal(0, o.Duplicates);
        Assert.Equal(0, o.WrongScript);
        Assert.Equal(0, o.WrongBody);
        Assert.Equal(0, o.WrongStatus);
        Assert.Equal(0, o.Strays);
    }

    [Fact]
    public void EveryPhloxResponseReachesItsScriptWhicheverPumpTakesIt()
    {
        var o = Run("YP");
        Assert.Equal(o.Expected, o.Requests);
        Assert.Equal(o.Expected, o.Hits);
        Assert.Equal(0, o.LostP);
        Assert.Equal(0, o.LostY);
        Assert.Equal(0, o.Duplicates);
        Assert.Equal(0, o.WrongScript);
        Assert.Equal(0, o.WrongBody);
        Assert.Equal(0, o.WrongStatus);
        Assert.Equal(0, o.Strays);
        Assert.Equal(0, o.Outstanding);   // a Phlox request delivered by YEngine's pump is not left outstanding
    }

    /// <summary>
    /// An event argument in YEngine's types reaches a Phlox script as the value Phlox's VM takes.
    /// </summary>
    [Fact]
    public void YEngineArgumentTypesAreConvertedForPhlox()
    {
        var args = new object[]
        {
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString("s"),
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger(7),
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat(1.5),
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.key("k"),
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3(1, 2, 3),
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion(0, 0, 0, 1),
            new OpenSim.Region.ScriptEngine.Shared.LSL_Types.list(
                new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger(1), new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString("x")),
        };
        // through reflection, so the red run (source without it) builds and fails here
        var convert = typeof(global::Phlox.ScriptEngine.PhloxEngine).GetMethod("ToPhloxArgs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(convert);
        object[] ToPhlox(object[] a) => (object[])convert.Invoke(null, new object[] { a });
        object[] got = ToPhlox(args);
        Assert.Equal("s", Assert.IsType<string>(got[0]));
        Assert.Equal(7, Assert.IsType<int>(got[1]));
        Assert.Equal(1.5f, Assert.IsType<float>(got[2]));
        Assert.Equal("k", Assert.IsType<string>(got[3]));
        Assert.Equal(new Vector3(1, 2, 3), Assert.IsType<Vector3>(got[4]));
        Assert.Equal(new Quaternion(0, 0, 0, 1), Assert.IsType<Quaternion>(got[5]));
        Assert.Equal(new object[] { 1, "x" }, Assert.IsType<object[]>(got[6]));

        var plain = new object[] { "a", 1, new object[0] };
        Assert.Same(plain, ToPhlox(plain));   // nothing to convert: as it was
    }

    /// <summary>
    /// The XML-RPC counterpart: the core's XMLRPCModule is drained by YEngine's pump and Phlox's. Each incoming
    /// remote_data must reach the Phlox script that opened the channel, once, whichever pump took it; the script's
    /// llRemoteDataReply answers the caller. Before, one YEngine's pump took went to YEngine only, and the caller got
    /// "Script timeout" 9 s later. No listener: the module is enabled by a port it never opens (PostInitialise not called)
    /// and requests are handed to XmlRpcRemoteData directly.
    /// </summary>
    [Fact]
    public async Task EveryRemoteDataReachesItsPhloxScriptWhicheverPumpTakesIt()
    {
        // llRemoteDataReply sleeps 3 s (SL) and the module waits 9 s for a reply, so each script answers 2 calls.
        const int Scripts = 15, CallsPerScript = 2;
        using var r = new PhloxOutboundFilterTests.Rig(withYEngine: true);
        var xmlrpc = new XMLRPCModule();
        var cfg = new IniConfigSource();
        cfg.AddConfig("XMLRPC").Set("XmlRpcPort", 1);
        xmlrpc.Initialise(cfg);
        r.H.Scene.RegisterModuleInterface<IXMLRPC>(xmlrpc);

        // YEngine's pump runs once a YEngine script is loaded.
        SchedulerHarnessYEngine.Rez(r.H, SceneHelpers.AddSceneObject(r.H.Scene, "core6 y", UUID.Random()).RootPart,
            "default { state_entry() { llSay(0, \"y up\"); } }");
        for (int t = 0; t < Scripts; t++)
            r.H.RezScriptInto(SceneHelpers.AddSceneObject(r.H.Scene, "core6 p" + t, UUID.Random()).RootPart,
                "default { state_entry() { llOpenRemoteDataChannel(); } " +
                "remote_data(integer t, key ch, key id, string from, integer i, string s) { " +
                "  if (t == REMOTE_DATA_CHANNEL) llSay(0, \"chan p" + t + " \" + (string)ch); " +
                "  else if (t == REMOTE_DATA_REQUEST) { llSay(0, \"got p" + t + " \" + s); llRemoteDataReply(ch, id, \"re \" + s, i); } } }");
        r.PumpUntil(() => r.H.Said.Contains("y up") && r.H.Said.Count(s => s.StartsWith("chan ")) == Scripts, 60);
        Assert.True(r.H.Said.Contains("y up"), "the YEngine script never started");
        var channels = r.H.Said.Where(s => s.StartsWith("chan ")).Select(s => s.Split(' ')).ToDictionary(a => a[1], a => UUID.Parse(a[2]));
        Assert.Equal(Scripts, channels.Count);

        var sent = new List<(string Tag, string Msg, Task<XmlRpcResponse> Call)>();
        foreach (var kv in channels)
            for (int n = 0; n < CallsPerScript; n++)
            {
                string msg = kv.Key + "-" + n;
                var data = new Hashtable { ["Channel"] = kv.Value.ToString(), ["IntValue"] = n, ["StringValue"] = msg };
                sent.Add((kv.Key, msg, Task.Run(() => xmlrpc.XmlRpcRemoteData(
                    new XmlRpcRequest("llRemoteData", new ArrayList { data }), new IPEndPoint(IPAddress.Loopback, 0)))));
            }
        r.PumpUntil(() => sent.All(c => c.Call.IsCompleted), 60);
        r.PumpFor(1.0);   // a duplicate on its way lands now

        int faults = 0, wrong = 0;
        foreach (var c in sent)
        {
            XmlRpcResponse res = await c.Call;
            if (res.IsFault) { faults++; continue; }
            if ((string)((Hashtable)((ArrayList)res.Value)[0])["StringValue"] != "re " + c.Msg) wrong++;
        }
        var got = r.H.Said.Where(s => s.StartsWith("got ")).ToList();
        int misrouted = got.Count(g => { var a = g.Split(' '); return !a[2].StartsWith(a[1] + "-"); });
        _out.WriteLine($"calls={sent.Count} faults={faults} wrong={wrong} delivered={got.Count} distinct={got.Distinct().Count()} misrouted={misrouted}");
        Assert.Equal(0, faults);
        Assert.Equal(0, wrong);
        Assert.Equal(sent.Count, got.Count);
        Assert.Equal(sent.Count, got.Distinct().Count());
        Assert.Equal(0, misrouted);
    }

    /// <summary>
    /// SL - "The corresponding http_response event will be triggered in all scripts in the prim, not just in the
    /// requesting script." A YEngine and a Phlox script in each prim: both hear every response of their prim, once each,
    /// whichever script asked and whichever pump took it.
    /// </summary>
    [Fact]
    public void EveryScriptInAMixedPrimHearsEachResponseOnce()
    {
        var o = Run("YP", mixed: true);
        Assert.Equal(o.Expected, o.Requests);
        Assert.Equal(o.Expected, o.Hits);
        Assert.Equal(0, o.LostP);
        Assert.Equal(0, o.LostY);
        Assert.Equal(0, o.Duplicates);
        Assert.Equal(0, o.WrongScript);
        Assert.Equal(0, o.WrongBody);
        Assert.Equal(0, o.WrongStatus);
        Assert.Equal(0, o.Strays);
        Assert.Equal(2 * o.Expected, o.Responses);
        Assert.Equal(0, o.Outstanding);
    }
}
