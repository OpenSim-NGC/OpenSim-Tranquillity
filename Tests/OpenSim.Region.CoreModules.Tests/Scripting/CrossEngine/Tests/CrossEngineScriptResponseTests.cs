/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

#nullable disable

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.CrossEngine.Tests;

/// <summary>
/// A region can run more than one script engine. The core hands each completed llHTTPRequest (and each XML-RPC
/// remote_data) to exactly one engine's pump. These tests check that whichever pump takes a response, it reaches the
/// scripts it belongs to, exactly once each, in whichever engine runs them.
///
/// SL: "The corresponding http_response event will be triggered in all scripts in the prim, not just in the requesting
/// script." (wiki llHTTPRequest). remote_data goes to the one script that opened the channel.
///
/// "YEngine" below is a stand-in registered through the shared AsyncCommandManager, as YEngine is, so the real shared
/// pump (Shared/Api/Plugins) serves it. The "other" engine is only an IScriptModule of the region, as a second engine
/// is. No network is used: completed requests are handed to the real core modules directly.
/// </summary>
[Collection("OutboundProcessWideState")]
public class CrossEngineScriptResponseTests : OpenSimTestCase
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static IConfigSource HttpConfig()
    {
        IniConfigSource config = new IniConfigSource();
        config.AddConfig("Startup");
        // The module's filter is process-wide and set up by whichever test builds a module first; this matches the
        // exception the tests that send real requests to a local listener need.
        config.AddConfig("Network").Set("OutboundDisallowForUserScriptsExcept", "127.0.0.1/32");
        return config;
    }

    private static HttpRequestModule AddHttpModule(Scene scene)
    {
        HttpRequestModule http = new HttpRequestModule();
        http.Initialise(HttpConfig());
        http.AddRegion(scene);
        return http;
    }

    private static void Complete(HttpRequestModule http, uint localID, UUID itemID, UUID reqID, int status, string body)
    {
        http.GotCompletedRequest(new HttpRequestClass
        {
            RequestModule = http,
            ReqID = reqID,
            ItemID = itemID,
            LocalID = localID,
            Status = status,
            ResponseBody = body
        });
    }

    /// <summary>A stand-in registered the way YEngine is: into the shared AsyncCommandManager, whose pump serves it.</summary>
    private static StandInEngine AddYEngine(Scene scene, string name)
    {
        StandInEngine e = new StandInEngine(name, scene);
        scene.RegisterModuleInterface<IScriptModule>(e);
        _ = new AsyncCommandManager(e);
        return e;
    }

    /// <summary>A stand-in for a second engine: only a script module of the region, as a second engine is.</summary>
    private static StandInEngine AddOtherEngine(Scene scene, string name, bool acceptsAnything = false)
    {
        StandInEngine e = new StandInEngine(name, scene) { AcceptsAnything = acceptsAnything };
        scene.StackModuleInterface<IScriptModule>(e);
        return e;
    }

    private static void WaitFor(Func<bool> done, string what)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > Limit)
                Assert.Fail("timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Wait until the shared pump has made a full pass after this point, so anything it was delivering has been
    /// delivered. Each pass reads every engine's World more than once; passes run one after another.
    /// </summary>
    private static void WaitForPumpPasses(StandInEngine yengine, int passes = 2)
    {
        int start = yengine.WorldReads;
        WaitFor(() => yengine.WorldReads >= start + 4 * passes, "the shared pump to pass again");
    }

    [Fact]
    public void AnotherEnginesHttpResponseTakenByYEnginesPumpReachesItsScriptOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        HttpRequestModule http = AddHttpModule(scene);
        StandInEngine yengine = AddYEngine(scene, "YEngine");
        StandInEngine other = AddOtherEngine(scene, "Other");

        const uint prim = 910001;
        UUID otherScript = UUID.Random();
        other.AddScript(prim, otherScript);

        UUID reqID = UUID.Random();
        Complete(http, prim, otherScript, reqID, 202, "for the other engine");

        WaitFor(() => other.Delivered.Count >= 1, "the other engine's script to get its http_response");
        WaitForPumpPasses(yengine);

        Received r = Assert.Single(other.Delivered);
        Assert.Equal(otherScript, r.ItemID);
        Assert.Equal("http_response", r.EventName);
        AssertPlainHttpResponse(r, reqID, 202, "for the other engine");
        Assert.Empty(yengine.Delivered);
    }

    [Fact]
    public void YEnginesOwnHttpResponsesAreUnchanged()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        HttpRequestModule http = AddHttpModule(scene);
        StandInEngine yengine = AddYEngine(scene, "YEngine");
        StandInEngine other = AddOtherEngine(scene, "Other");

        const uint prim = 910002;
        UUID yScript = UUID.Random();
        yengine.AddScript(prim, yScript);

        UUID reqID = UUID.Random();
        Complete(http, prim, yScript, reqID, 200, "for yengine");

        WaitFor(() => yengine.Delivered.Count >= 1, "the YEngine script to get its http_response");
        WaitForPumpPasses(yengine);

        Received r = Assert.Single(yengine.Delivered);
        Assert.Equal(yScript, r.ItemID);
        Assert.Equal("http_response", r.EventName);
        Assert.Equal(4, r.Args.Length);
        Assert.IsType<LSL_Types.LSLString>(r.Args[0]);
        Assert.IsType<LSL_Types.LSLInteger>(r.Args[1]);
        Assert.IsType<LSL_Types.list>(r.Args[2]);
        Assert.IsType<LSL_Types.LSLString>(r.Args[3]);
        Assert.Equal(reqID.ToString(), r.Args[0].ToString());
        Assert.Equal(200, (int)(LSL_Types.LSLInteger)r.Args[1]);
        Assert.Equal("for yengine", r.Args[3].ToString());
        Assert.Empty(other.Delivered);
    }

    /// <summary>What YEngine always got: LSLString id, LSLInteger status, list, LSLString body.</summary>
    private static void AssertLslHttpResponse(Received r, UUID reqID, int status, string body)
    {
        Assert.Equal(4, r.Args.Length);
        Assert.Equal(reqID.ToString(), Assert.IsType<LSL_Types.LSLString>(r.Args[0]).m_string);
        Assert.Equal(status, Assert.IsType<LSL_Types.LSLInteger>(r.Args[1]).value);
        Assert.IsType<LSL_Types.list>(r.Args[2]);
        Assert.Equal(body, Assert.IsType<LSL_Types.LSLString>(r.Args[3]).m_string);
    }

    /// <summary>
    /// What an engine the shared pump does not serve gets: plain values, as core modules post to any engine (UrlModule).
    /// </summary>
    private static void AssertPlainHttpResponse(Received r, UUID reqID, int status, string body)
    {
        Assert.Equal(4, r.Args.Length);
        Assert.Equal(reqID.ToString(), Assert.IsType<string>(r.Args[0]));
        Assert.Equal(status, Assert.IsType<int>(r.Args[1]));
        Assert.Empty(Assert.IsType<object[]>(r.Args[2]));
        Assert.Equal(body, Assert.IsType<string>(r.Args[3]));
    }

    /// <summary>
    /// SL: every script in the prim gets the http_response. A prim with scripts in three engines (one of which, like
    /// Phlox, says yes to any prim that exists): each script gets each response once, whoever asked.
    /// </summary>
    [Fact]
    public void EveryScriptInAMixedPrimGetsEachHttpResponseOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        HttpRequestModule http = AddHttpModule(scene);
        StandInEngine yengine = AddYEngine(scene, "YEngine");
        StandInEngine greedy = AddOtherEngine(scene, "Greedy", acceptsAnything: true);
        StandInEngine other = AddOtherEngine(scene, "Other");

        const uint prim = 910003;
        UUID yScript = UUID.Random(), greedyScript = UUID.Random(), otherScript = UUID.Random();
        yengine.AddScript(prim, yScript);
        greedy.AddScript(prim, greedyScript);
        other.AddScript(prim, otherScript);

        UUID[] askers = { yScript, greedyScript, otherScript };
        List<UUID> reqs = new();
        foreach (UUID asker in askers)
        {
            UUID reqID = UUID.Random();
            reqs.Add(reqID);
            Complete(http, prim, asker, reqID, 200, "body " + reqID);
        }

        foreach (StandInEngine e in new[] { yengine, greedy, other })
            WaitFor(() => e.Delivered.Count >= reqs.Count, e.Name + "'s script to get every response");
        WaitForPumpPasses(yengine);

        foreach (StandInEngine e in new[] { yengine, greedy, other })
        {
            Assert.Equal(reqs.Count, e.Delivered.Count);
            foreach (UUID reqID in reqs)
            {
                Received r = Assert.Single(e.Delivered, x => x.Args[0].ToString() == reqID.ToString());
                if (e == yengine)
                    AssertLslHttpResponse(r, reqID, 200, "body " + reqID);
                else
                    AssertPlainHttpResponse(r, reqID, 200, "body " + reqID);
            }
        }
    }

    /// <summary>
    /// Local ids are per region, so another region can have a prim with the same one. A response for a prim whose
    /// scripts all run in the other engine must not land in the other region's YEngine prim.
    /// </summary>
    [Fact]
    public void AnHttpResponseNeverReachesAnotherRegionsPrimWithTheSameLocalId()
    {
        TestScene scene1 = new SceneHelpers().SetupScene("xeng-1", UUID.Random(), 1000, 1000);
        TestScene scene2 = new SceneHelpers().SetupScene("xeng-2", UUID.Random(), 1001, 1000);
        HttpRequestModule http1 = AddHttpModule(scene1);
        AddHttpModule(scene2);
        StandInEngine yengine1 = AddYEngine(scene1, "YEngine-1");
        StandInEngine yengine2 = AddYEngine(scene2, "YEngine-2");
        StandInEngine other1 = AddOtherEngine(scene1, "Other-1");

        const uint prim = 910004;
        UUID otherScript = UUID.Random(), region2Script = UUID.Random();
        other1.AddScript(prim, otherScript);
        yengine2.AddScript(prim, region2Script);

        Complete(http1, prim, otherScript, UUID.Random(), 200, "region 1");

        WaitFor(() => other1.Delivered.Count + yengine2.Delivered.Count >= 1, "the http_response to be delivered anywhere");
        WaitForPumpPasses(yengine1);

        Assert.Empty(yengine2.Delivered);
        Assert.Empty(yengine1.Delivered);
        Assert.Single(other1.Delivered);
    }

    // ── XML-RPC ──────────────────────────────────────────────────────────────

    /// <summary>
    /// YEngine alone, two regions, a prim with a script at the same local id in each: a response goes to its own
    /// region's prim, whichever region's YEngine the shared pump lists first.
    /// </summary>
    [Fact]
    public void WithYEngineAloneAnHttpResponseReachesItsOwnRegionsPrim()
    {
        TestScene sceneA = new SceneHelpers().SetupScene("yonly-a", UUID.Random(), 1002, 1000);
        TestScene sceneB = new SceneHelpers().SetupScene("yonly-b", UUID.Random(), 1003, 1000);
        AddHttpModule(sceneA);
        HttpRequestModule httpB = AddHttpModule(sceneB);
        StandInEngine yengineA = AddYEngine(sceneA, "YEngine-A");   // listed first
        StandInEngine yengineB = AddYEngine(sceneB, "YEngine-B");

        const uint prim = 910008;
        UUID scriptA = UUID.Random(), scriptB = UUID.Random();
        yengineA.AddScript(prim, scriptA);
        yengineB.AddScript(prim, scriptB);

        Complete(httpB, prim, scriptB, UUID.Random(), 200, "region B");

        WaitFor(() => yengineA.Delivered.Count + yengineB.Delivered.Count >= 1, "the http_response to be delivered anywhere");
        WaitForPumpPasses(yengineB);

        Assert.Empty(yengineA.Delivered);
        Received r = Assert.Single(yengineB.Delivered);
        Assert.Equal(scriptB, r.ItemID);
        Assert.Equal("region B", r.Args[3].ToString());
    }

    private static XMLRPCModule AddXmlRpcModule(Scene scene)
    {
        XMLRPCModule xmlrpc = new XMLRPCModule();
        xmlrpc.Initialise(new IniConfigSource());   // no XmlRpcPort: no listener is started
        scene.RegisterModuleInterface<IXMLRPC>(xmlrpc);
        return xmlrpc;
    }

    private static Task<XmlRpcResponse> SendRemoteData(XMLRPCModule xmlrpc, UUID channel, int idata, string sdata)
    {
        Hashtable data = new Hashtable
        {
            ["Channel"] = channel.ToString(),
            ["IntValue"] = idata,
            ["StringValue"] = sdata
        };
        XmlRpcRequest request = new XmlRpcRequest("llRemoteData", new ArrayList { data });
        return Task.Run(() => xmlrpc.XmlRpcRemoteData(request, new IPEndPoint(IPAddress.Loopback, 0)));
    }

    [Fact]
    public async Task AnotherEnginesRemoteDataTakenByYEnginesPumpReachesItsScriptOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        XMLRPCModule xmlrpc = AddXmlRpcModule(scene);
        StandInEngine yengine = AddYEngine(scene, "YEngine");
        StandInEngine other = AddOtherEngine(scene, "Other", acceptsAnything: true);

        const uint prim = 910005;
        UUID otherScript = UUID.Random();
        other.AddScript(prim, otherScript);
        other.OnRemoteData = r => xmlrpc.RemoteDataReply(r.Args[1].ToString(), r.Args[2].ToString(), "reply " + r.Args[5], 7);

        UUID channel = xmlrpc.OpenXMLRPCChannel(prim, otherScript, UUID.Zero);
        Task<XmlRpcResponse> call = SendRemoteData(xmlrpc, channel, 3, "hello");

        XmlRpcResponse result = await call.WaitAsync(Limit);
        WaitForPumpPasses(yengine);

        Assert.False(result.IsFault, "the script did not answer: " + result.FaultString);
        Hashtable answer = (Hashtable)((ArrayList)result.Value)[0];
        Assert.Equal("reply hello", answer["StringValue"]);

        Received r = Assert.Single(other.Delivered);
        Assert.Equal(otherScript, r.ItemID);
        Assert.Equal("remote_data", r.EventName);
        Assert.Equal(6, r.Args.Length);
        Assert.Equal(2, Assert.IsType<int>(r.Args[0]));
        Assert.Equal(channel.ToString(), Assert.IsType<string>(r.Args[1]));
        Assert.IsType<string>(r.Args[2]);
        Assert.Equal(string.Empty, Assert.IsType<string>(r.Args[3]));
        Assert.Equal(3, Assert.IsType<int>(r.Args[4]));
        Assert.Equal("hello", Assert.IsType<string>(r.Args[5]));
        Assert.Empty(yengine.Delivered);
    }

    [Fact]
    public async Task YEnginesOwnRemoteDataIsUnchanged()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        XMLRPCModule xmlrpc = AddXmlRpcModule(scene);
        StandInEngine yengine = AddYEngine(scene, "YEngine");
        StandInEngine other = AddOtherEngine(scene, "Other", acceptsAnything: true);

        const uint prim = 910006;
        UUID yScript = UUID.Random();
        yengine.AddScript(prim, yScript);
        yengine.OnRemoteData = r => xmlrpc.RemoteDataReply(r.Args[1].ToString(), r.Args[2].ToString(), "y " + r.Args[5], 1);

        UUID channel = xmlrpc.OpenXMLRPCChannel(prim, yScript, UUID.Zero);
        Task<XmlRpcResponse> call = SendRemoteData(xmlrpc, channel, 1, "ping");

        XmlRpcResponse result = await call.WaitAsync(Limit);
        WaitForPumpPasses(yengine);

        Assert.False(result.IsFault, "the script did not answer: " + result.FaultString);
        Assert.Equal("y ping", ((Hashtable)((ArrayList)result.Value)[0])["StringValue"]);
        Received r = Assert.Single(yengine.Delivered);
        Assert.Equal(yScript, r.ItemID);
        Assert.Equal(2, Assert.IsType<LSL_Types.LSLInteger>(r.Args[0]).value);
        Assert.Equal("ping", Assert.IsType<LSL_Types.LSLString>(r.Args[5]).m_string);
        Assert.Empty(other.Delivered);
    }

    /// <summary>
    /// Two pumps drain the one XML-RPC module. Taking a completed request must hand it out once: a second pump asking
    /// before the first has removed it must not get the same request.
    /// </summary>
    [Fact]
    public async Task ACompletedXmlRpcRequestIsHandedToOnePumpOnly()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        XMLRPCModule xmlrpc = AddXmlRpcModule(scene);   // no engine: nothing else drains it

        UUID item = UUID.Random();
        UUID channel = xmlrpc.OpenXMLRPCChannel(910007, item, UUID.Zero);
        Task<XmlRpcResponse> call = SendRemoteData(xmlrpc, channel, 0, "once");
        WaitFor(() => xmlrpc.hasRequests(), "the request to arrive");

        IXmlRpcRequestInfo first = xmlrpc.GetNextCompletedRequest();
        IXmlRpcRequestInfo second = xmlrpc.GetNextCompletedRequest();
        Assert.NotNull(first);
        Assert.Null(second);

        xmlrpc.RemoveCompletedRequest(first.GetMessageID());
        xmlrpc.RemoteDataReply(channel.ToString(), first.GetMessageID().ToString(), "done", 0);
        XmlRpcResponse result = await call.WaitAsync(Limit);
        Assert.False(result.IsFault);
    }

    // ── stand-in engine ──────────────────────────────────────────────────────

    public sealed record Received(UUID ItemID, string EventName, object[] Args);

    /// <summary>
    /// Runs a set of scripts (item ids by prim local id) and records what they are posted. Like a real engine, it
    /// delivers only to scripts it runs. AcceptsAnything makes it answer true for any prim or item, as Phlox does.
    /// </summary>
    private sealed class StandInEngine : IScriptEngine, IScriptModule
    {
        private readonly Scene m_scene;
        private readonly ConcurrentDictionary<uint, List<UUID>> m_scripts = new();
        private int m_worldReads;

        public StandInEngine(string name, Scene scene)
        {
            Name = name;
            m_scene = scene;
            IniConfigSource cs = new IniConfigSource();
            Config = cs.AddConfig(name);
            ConfigSource = cs;
        }

        public string Name { get; }
        public bool AcceptsAnything { get; init; }
        public ConcurrentQueue<Received> ReceivedQueue { get; } = new();
        public List<Received> Delivered => ReceivedQueue.ToList();
        public Action<Received> OnRemoteData { get; set; }
        public int WorldReads => Volatile.Read(ref m_worldReads);

        public void AddScript(uint localID, UUID itemID) =>
            m_scripts.GetOrAdd(localID, _ => new List<UUID>()).Add(itemID);

        private bool Runs(UUID itemID) => m_scripts.Values.Any(l => l.Contains(itemID));

        private void Record(UUID itemID, EventParams p)
        {
            Received r = new Received(itemID, p.EventName, p.Params);
            ReceivedQueue.Enqueue(r);
            if (p.EventName == "remote_data")
                OnRemoteData?.Invoke(r);
        }

        public Scene World
        {
            get
            {
                Interlocked.Increment(ref m_worldReads);
                return m_scene;
            }
        }

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            if (!m_scripts.TryGetValue(localID, out List<UUID> items))
                return AcceptsAnything;
            foreach (UUID item in items)
                Record(item, parms);
            return true;
        }

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            if (!Runs(itemID))
                return AcceptsAnything;
            Record(itemID, parms);
            return true;
        }

        // Unused by the pumps.
        public IScriptModule ScriptModule => this;
        public IConfig Config { get; }
        public IConfigSource ConfigSource { get; }
        public string ScriptEngineName => Name;
        public string ScriptEnginePath => string.Empty;
        public string ScriptClassName => string.Empty;
        public string ScriptBaseClassName => string.Empty;
        public string[] ScriptReferencedAssemblies => null;
        public ParameterInfo[] ScriptBaseClassParameters => null;
        public IScriptWorkItem QueueEventHandler(object parms) => null;
        public void CancelScriptEvent(UUID itemID, string eventName) { }
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => false;
        public DetectParams GetDetectParams(UUID item, int number) => null;
        public void SetMinEventDelay(UUID itemID, double delay) { }
        public int GetStartParameter(UUID itemID) => 0;
        public void SetScriptState(UUID itemID, bool state, bool self) { }
        public bool GetScriptState(UUID itemID) => true;
        public void SetState(UUID itemID, string newState) { }
        public void ApiResetScript(UUID itemID) { }
        public void ResetScript(UUID itemID) { }
        public IScriptApi GetApi(UUID itemID, string name) => null;
        public void SleepScript(UUID itemID, int delay) { }

#pragma warning disable 0067
        public event ScriptRemoved OnScriptRemoved;
        public event ObjectRemoved OnObjectRemoved;
#pragma warning restore 0067
        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new ArrayList();
        public bool HasScript(UUID itemID, out bool running) { running = Runs(itemID); return running; }
        public void SaveAllState() { }
        public void StartProcessing() { }
        public float GetScriptExecutionTime(List<UUID> itemIDs) => 0f;
        public int GetScriptsMemory(List<UUID> itemIDs) => 0;
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => new();
        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            totaltime = 0f;
            totalmemory = 0f;
            return Array.Empty<ScriptTopStatsData>();
        }
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene) { }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
    }
}
