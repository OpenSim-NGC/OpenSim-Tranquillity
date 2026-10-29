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
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Plugins;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.CrossEngine.Tests;

/// <summary>
/// A region can run more than one script engine. A dataserver answer, and an osMessageObject message, must reach every
/// script in the prim whichever engine runs it, exactly once each, and no script in any other prim.
///
/// SL (wiki dataserver): "Dataserver requests will trigger dataserver events in all scripts within the same prim where
/// the request was made." and "dataserver events will not be triggered in scripts contained in other prims in the same
/// linked object." OpenSim (wiki osMessageObject): "the dataserver event is passed the UUID of the calling prim and a
/// string message", and "All scripts with dataserver event will receive it".
///
/// "YEngine" below is a stand-in registered through the shared AsyncCommandManager, as YEngine is, so the real shared
/// Dataserver plugin serves it, and the real OSSL_Api runs on it. The "other" engine is only an IScriptModule of the
/// region, as a second engine is. No network is used.
///
/// This is the only class in the assembly that initialises OSSL_Api, whose shared settings are read once per process;
/// every test here uses the same OSSL configuration, so the class can run in parallel with the others.
/// </summary>
public class CrossEngineDataserverTests : OpenSimTestCase
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static StandInEngine AddYEngine(Scene scene, out AsyncCommandManager commands)
    {
        StandInEngine e = new StandInEngine("YEngine", scene);
        scene.RegisterModuleInterface<IScriptModule>(e);
        commands = new AsyncCommandManager(e);
        return e;
    }

    private static StandInEngine AddOtherEngine(Scene scene, bool acceptsAnything = false)
    {
        StandInEngine e = new StandInEngine("Other", scene) { AcceptsAnything = acceptsAnything };
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

    private static void AssertLslDataserver(Received r, UUID item, string key, string data)
    {
        Assert.Equal(item, r.ItemID);
        Assert.Equal("dataserver", r.EventName);
        Assert.Equal(2, r.Args.Length);
        Assert.Equal(key, Assert.IsType<LSL_Types.LSLString>(r.Args[0]).m_string);
        Assert.Equal(data, Assert.IsType<LSL_Types.LSLString>(r.Args[1]).m_string);
    }

    private static void AssertPlainDataserver(Received r, UUID item, string key, string data)
    {
        Assert.Equal(item, r.ItemID);
        Assert.Equal("dataserver", r.EventName);
        Assert.Equal(2, r.Args.Length);
        Assert.Equal(key, Assert.IsType<string>(r.Args[0]));
        Assert.Equal(data, Assert.IsType<string>(r.Args[1]));
    }

    /// <summary>The requester, a second YEngine script and another engine's script share the prim: each gets the answer once.</summary>
    [Fact]
    public void AnAnswerReachesEveryScriptInTheRequestersPrimInEveryEngineOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out AsyncCommandManager commands);
        StandInEngine other = AddOtherEngine(scene);

        const uint prim = 920001;
        UUID requester = UUID.Random();
        UUID secondY = UUID.Random();
        UUID otherScript = UUID.Random();
        yengine.AddScript(prim, requester);
        yengine.AddScript(prim, secondY);
        other.AddScript(prim, otherScript);

        Dataserver ds = commands.DataserverPlugin;
        string handle = UUID.Random().ToString();
        UUID key = ds.RegisterRequest(prim, requester, handle);
        ds.DataserverReply(handle, "the answer");

        Assert.Equal(2, yengine.Delivered.Count);
        AssertLslDataserver(yengine.Delivered.Single(r => r.ItemID == requester), requester, key.ToString(), "the answer");
        AssertLslDataserver(yengine.Delivered.Single(r => r.ItemID == secondY), secondY, key.ToString(), "the answer");
        AssertPlainDataserver(Assert.Single(other.Delivered), otherScript, key.ToString(), "the answer");

        // Answered once: a second reply for the same request is not posted again.
        ds.DataserverReply(handle, "again");
        Assert.Equal(2, yengine.Delivered.Count);
        Assert.Single(other.Delivered);
    }

    /// <summary>The asynchronous form (the plugin runs the request's action on its pool): same rule.</summary>
    [Fact]
    public void AnAsyncAnswerReachesAnotherEnginesScriptInThePrimOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out AsyncCommandManager commands);
        StandInEngine other = AddOtherEngine(scene);

        const uint prim = 920002;
        UUID requester = UUID.Random();
        UUID otherScript = UUID.Random();
        yengine.AddScript(prim, requester);
        other.AddScript(prim, otherScript);

        Dataserver ds = commands.DataserverPlugin;
        UUID key = ds.RegisterRequest(prim, requester, eventID => ds.DataserverReply(eventID, "async answer"));

        WaitFor(() => yengine.Delivered.Count >= 1 && other.Delivered.Count >= 1, "both engines to get the answer");
        WaitFor(() => ds.DataserverRequestsCount == 0, "the request to be finished");

        AssertLslDataserver(Assert.Single(yengine.Delivered), requester, key.ToString(), "async answer");
        AssertPlainDataserver(Assert.Single(other.Delivered), otherScript, key.ToString(), "async answer");
    }

    /// <summary>An answer posted at once (cached data) reaches every engine's scripts in the prim, once each.</summary>
    [Fact]
    public void AnImmediateAnswerReachesEveryEnginesScriptsInThePrimOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out AsyncCommandManager commands);
        StandInEngine other = AddOtherEngine(scene);

        const uint prim = 920003;
        UUID requester = UUID.Random();
        UUID otherScript = UUID.Random();
        yengine.AddScript(prim, requester);
        other.AddScript(prim, otherScript);

        string key = commands.DataserverPlugin.RequestWithImediatePost(prim, requester, "cached");

        AssertLslDataserver(Assert.Single(yengine.Delivered), requester, key, "cached");
        AssertPlainDataserver(Assert.Single(other.Delivered), otherScript, key, "cached");
    }

    /// <summary>
    /// Scripts in another prim (in either engine) never get the answer, even from an engine that says it took any
    /// event, as one may.
    /// </summary>
    [Fact]
    public void AScriptInAnotherPrimGetsNoAnswer()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out AsyncCommandManager commands);
        StandInEngine other = AddOtherEngine(scene, acceptsAnything: true);

        const uint prim = 920004;
        const uint anotherPrim = 920005;
        UUID requester = UUID.Random();
        UUID yElsewhere = UUID.Random();
        UUID otherElsewhere = UUID.Random();
        yengine.AddScript(prim, requester);
        yengine.AddScript(anotherPrim, yElsewhere);
        other.AddScript(anotherPrim, otherElsewhere);

        Dataserver ds = commands.DataserverPlugin;
        string handle = UUID.Random().ToString();
        UUID key = ds.RegisterRequest(prim, requester, handle);
        ds.DataserverReply(handle, "only here");

        AssertLslDataserver(Assert.Single(yengine.Delivered), requester, key.ToString(), "only here");
        Assert.Empty(other.Delivered);
    }

    /// <summary>
    /// Kept behaviour: an answer for a request the plugin has dropped (the requester was reset or removed) reaches no
    /// script in the prim, in any engine.
    /// </summary>
    [Fact]
    public void AnAnswerForARemovedRequesterReachesNoEngine()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out AsyncCommandManager commands);
        StandInEngine other = AddOtherEngine(scene);

        const uint prim = 920006;
        UUID requester = UUID.Random();
        UUID secondY = UUID.Random();
        UUID otherScript = UUID.Random();
        yengine.AddScript(prim, requester);
        yengine.AddScript(prim, secondY);
        other.AddScript(prim, otherScript);

        Dataserver ds = commands.DataserverPlugin;
        string handle = UUID.Random().ToString();
        ds.RegisterRequest(prim, requester, handle);
        ds.RemoveEvents(prim, requester);
        ds.DataserverReply(handle, "too late");

        Assert.Empty(yengine.Delivered);
        Assert.Empty(other.Delivered);
    }

    /// <summary>
    /// Another engine that throws neither reaches the caller nor keeps the event from the engines after it: the calling
    /// engine's scripts and a third engine's script in the prim still get each answer once.
    /// </summary>
    [Fact]
    public void AnEngineThatThrowsKeepsTheAnswerFromNoOtherEngine()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out AsyncCommandManager commands);
        StandInEngine thrower = AddOtherEngine(scene);
        thrower.Throws = true;
        StandInEngine third = AddOtherEngine(scene);

        const uint prim = 920007;
        UUID requester = UUID.Random();
        UUID secondY = UUID.Random();
        UUID throwerScript = UUID.Random();
        UUID thirdScript = UUID.Random();
        yengine.AddScript(prim, requester);
        yengine.AddScript(prim, secondY);
        thrower.AddScript(prim, throwerScript);
        third.AddScript(prim, thirdScript);

        Dataserver ds = commands.DataserverPlugin;
        string handle = UUID.Random().ToString();
        string key = ds.RegisterRequest(prim, requester, handle).ToString();
        ds.DataserverReply(handle, "the answer");
        string immediate = ds.RequestWithImediatePost(prim, requester, "cached");

        Assert.Equal(2, thrower.Attempts);
        List<Received> y = yengine.Delivered;
        Assert.Equal(4, y.Count);
        AssertLslDataserver(Assert.Single(y, r => r.ItemID == requester && IsKey(r, key)), requester, key, "the answer");
        AssertLslDataserver(Assert.Single(y, r => r.ItemID == secondY && IsKey(r, key)), secondY, key, "the answer");
        AssertLslDataserver(Assert.Single(y, r => r.ItemID == requester && IsKey(r, immediate)), requester, immediate, "cached");
        AssertLslDataserver(Assert.Single(y, r => r.ItemID == secondY && IsKey(r, immediate)), secondY, immediate, "cached");
        List<Received> t = third.Delivered;
        Assert.Equal(2, t.Count);
        AssertPlainDataserver(t[0], thirdScript, key, "the answer");
        AssertPlainDataserver(t[1], thirdScript, immediate, "cached");
    }

    private static bool IsKey(Received r, string key) =>
        r.Args.Length > 0 && r.Args[0] is LSL_Types.LSLString s && s.m_string == key;

    // ── osMessageObject ──────────────────────────────────────────────────────

    private static IConfigSource OsslConfig()
    {
        IniConfigSource config = new IniConfigSource();
        IConfig ossl = config.AddConfig("OSSL");
        ossl.Set("AllowOSFunctions", "true");
        ossl.Set("Allow_osMessageObject", "true");
        return config;
    }

    private static OSSL_Api OsslOn(StandInEngine engine, SceneObjectPart host)
    {
        TaskInventoryItem item = new TaskInventoryItem { ItemID = UUID.Random(), Name = "sender" };
        OSSL_Api api = new OSSL_Api();
        api.Initialize(engine, host, item);
        return api;
    }

    /// <summary>
    /// Every script in the target prim gets the message once, in each engine, the sender's prim key as the query key.
    /// The scope stays the one prim named: another prim of the same object gets nothing.
    /// </summary>
    [Fact]
    public void OsMessageObjectReachesEveryScriptInTheTargetPrimInEveryEngineOnce()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene, out _);
        yengine.ConfigSourceOverride = OsslConfig();
        StandInEngine other = AddOtherEngine(scene);

        SceneObjectGroup sender = SceneHelpers.AddSceneObject(scene, "sender", UUID.Random());
        SceneObjectGroup target = SceneHelpers.AddSceneObject(scene, 2, UUID.Random(), "target", 0x40);
        SceneObjectPart targetRoot = target.RootPart;
        SceneObjectPart targetChild = target.Parts.Single(p => p != targetRoot);

        UUID y1 = UUID.Random();
        UUID y2 = UUID.Random();
        UUID o1 = UUID.Random();
        UUID yChild = UUID.Random();
        UUID oChild = UUID.Random();
        yengine.AddScript(targetRoot.LocalId, y1);
        yengine.AddScript(targetRoot.LocalId, y2);
        other.AddScript(targetRoot.LocalId, o1);
        yengine.AddScript(targetChild.LocalId, yChild);
        other.AddScript(targetChild.LocalId, oChild);

        OSSL_Api api = OsslOn(yengine, sender.RootPart);
        api.osMessageObject(targetRoot.UUID.ToString(), "hello");

        string from = sender.RootPart.UUID.ToString();
        Assert.Equal(2, yengine.Delivered.Count);
        AssertLslDataserver(yengine.Delivered.Single(r => r.ItemID == y1), y1, from, "hello");
        AssertLslDataserver(yengine.Delivered.Single(r => r.ItemID == y2), y2, from, "hello");
        AssertPlainDataserver(Assert.Single(other.Delivered), o1, from, "hello");
    }

    // ── stand-in engine ──────────────────────────────────────────────────────

    public sealed record Received(UUID ItemID, string EventName, object[] Args);

    /// <summary>
    /// Runs a set of scripts (item ids by prim local id) and records what they are posted. Like a real engine, it
    /// delivers only to scripts it runs. AcceptsAnything makes it answer true for any prim or item, as an engine may.
    /// Throws makes its PostObjectEvent throw, counting the attempts.
    /// </summary>
    private sealed class StandInEngine : IScriptEngine, IScriptModule
    {
        private readonly Scene m_scene;
        private readonly ConcurrentDictionary<uint, List<UUID>> m_scripts = new();
        private readonly IConfigSource m_configSource;

        public StandInEngine(string name, Scene scene)
        {
            Name = name;
            m_scene = scene;
            IniConfigSource cs = new IniConfigSource();
            Config = cs.AddConfig(name);
            m_configSource = cs;
        }

        public string Name { get; }
        public bool AcceptsAnything { get; init; }
        public bool Throws { get; set; }
        public int Attempts => Volatile.Read(ref m_attempts);
        private int m_attempts;
        public IConfigSource ConfigSourceOverride { get; set; }
        public ConcurrentQueue<Received> ReceivedQueue { get; } = new();
        public List<Received> Delivered => ReceivedQueue.ToList();

        public void AddScript(uint localID, UUID itemID) =>
            m_scripts.GetOrAdd(localID, _ => new List<UUID>()).Add(itemID);

        private bool Runs(UUID itemID) => m_scripts.Values.Any(l => l.Contains(itemID));

        public Scene World => m_scene;

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            if (Throws)
            {
                Interlocked.Increment(ref m_attempts);
                throw new InvalidOperationException(Name + " failed to post");
            }
            if (!m_scripts.TryGetValue(localID, out List<UUID> items))
                return AcceptsAnything;
            foreach (UUID item in items)
                ReceivedQueue.Enqueue(new Received(item, parms.EventName, parms.Params));
            return true;
        }

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            if (!Runs(itemID))
                return AcceptsAnything;
            ReceivedQueue.Enqueue(new Received(itemID, parms.EventName, parms.Params));
            return true;
        }

        // Unused by the paths under test.
        public IScriptModule ScriptModule => this;
        public IConfig Config { get; }
        public IConfigSource ConfigSource => ConfigSourceOverride ?? m_configSource;
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
