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
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.CrossEngine.Tests;

/// <summary>
/// A region can run more than one script engine. A link message must reach every script in the prims it targets,
/// whichever engine runs the script, exactly once each.
///
/// SL (wiki llMessageLinked): "It triggers a link_message event with the same parameters num, str, and id in all
/// scripts in the prim(s) described by link." and "A script can hear its own linked messages if link targets the prim
/// it is in." SL (wiki link_message): sender_num is "The link number of the prim that contained the script that called
/// llMessageLinked."
///
/// "YEngine" below is a stand-in the real shared LSL_Api runs on, as YEngine's scripts do. The other engines are only
/// IScriptModules of the region, as a second engine is. Scripts are task-inventory items of a real linked object. No
/// network is used.
/// </summary>
public class CrossEngineLinkMessageTests : OpenSimTestCase
{
    private const int LINK_ROOT = 1, LINK_SET = -1, LINK_ALL_OTHERS = -2, LINK_ALL_CHILDREN = -3, LINK_THIS = -4;

    /// <summary>A three-prim object; in each prim one YEngine script and one script of the other engine.</summary>
    private sealed class Rig
    {
        public TestScene Scene;
        public StandInEngine YEngine;
        public StandInEngine Other;
        public SceneObjectGroup Group;
        public UUID[] Y = new UUID[4];       // by link number 1..3
        public UUID[] O = new UUID[4];
        public UUID Sender;                 // a second YEngine script, in link 2
        public LSL_Api Api;
    }

    private static Rig ThreePrimRig(bool addOther = true)
    {
        Rig r = new Rig { Scene = new SceneHelpers().SetupScene() };
        r.YEngine = AddYEngine(r.Scene);
        if (addOther)
            r.Other = AddOtherEngine(r.Scene);
        r.Group = SceneHelpers.AddSceneObject(r.Scene, 3, UUID.Random(), "linked", 0x50);
        for (int link = 1; link <= 3; link++)
        {
            SceneObjectPart part = r.Group.GetLinkNumPart(link);
            r.Y[link] = AddScript(part, r.YEngine, "y" + link);
            if (addOther)
                r.O[link] = AddScript(part, r.Other, "o" + link);
        }
        SceneObjectPart senderPart = r.Group.GetLinkNumPart(2);
        r.Sender = AddScript(senderPart, r.YEngine, "sender");
        r.Api = ApiOn(r.YEngine, senderPart, r.Sender);
        return r;
    }

    private static StandInEngine AddYEngine(Scene scene)
    {
        StandInEngine e = new StandInEngine("YEngine", scene);
        scene.RegisterModuleInterface<IScriptModule>(e);
        return e;
    }

    private static StandInEngine AddOtherEngine(Scene scene, string name = "Other")
    {
        StandInEngine e = new StandInEngine(name, scene);
        scene.StackModuleInterface<IScriptModule>(e);
        return e;
    }

    private static UUID AddScript(SceneObjectPart part, StandInEngine engine, string name)
    {
        TaskInventoryItem item = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            Name = name,
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
        };
        part.Inventory.AddInventoryItem(item, false);
        engine.AddScript(item.ItemID);
        return item.ItemID;
    }

    private static LSL_Api ApiOn(StandInEngine engine, SceneObjectPart host, UUID itemID)
    {
        LSL_Api api = new LSL_Api();
        api.Initialize(engine, host, host.Inventory.GetInventoryItem(itemID));
        return api;
    }

    private static void AssertLslLinkMessage(Received r, int sender, int num, string msg, string id)
    {
        Assert.Equal("link_message", r.EventName);
        Assert.Equal(4, r.Args.Length);
        Assert.Equal(sender, Assert.IsType<LSL_Types.LSLInteger>(r.Args[0]).value);
        Assert.Equal(num, Assert.IsType<LSL_Types.LSLInteger>(r.Args[1]).value);
        Assert.Equal(msg, Assert.IsType<LSL_Types.LSLString>(r.Args[2]).m_string);
        Assert.Equal(id, Assert.IsType<LSL_Types.LSLString>(r.Args[3]).m_string);
    }

    private static void AssertPlainLinkMessage(Received r, int sender, int num, string msg, string id)
    {
        Assert.Equal("link_message", r.EventName);
        Assert.Equal(4, r.Args.Length);
        Assert.Equal(sender, Assert.IsType<int>(r.Args[0]));
        Assert.Equal(num, Assert.IsType<int>(r.Args[1]));
        Assert.Equal(msg, Assert.IsType<string>(r.Args[2]));
        Assert.Equal(id, Assert.IsType<string>(r.Args[3]));
    }

    /// <summary>The scripts of an engine that got the message, by item, each once (a repeat fails the test).</summary>
    private static List<UUID> Receivers(StandInEngine e)
    {
        List<UUID> items = e.Delivered.Select(d => d.ItemID).ToList();
        Assert.Equal(items.Count, items.Distinct().Count());
        return items;
    }

    /// <summary>
    /// A YEngine script's message to the whole linkset reaches the other engine's script in the sender's own prim and
    /// in each other prim, once each, with the values that engine expects.
    /// </summary>
    [Fact]
    public void AYEngineLinkMessageReachesAnotherEnginesScriptsInItsOwnPrimAndTheOtherPrimsOnceEach()
    {
        Rig r = ThreePrimRig();
        string key = UUID.Random().ToString();

        r.Api.llMessageLinked(LINK_SET, 7, "hello", key);

        List<UUID> other = Receivers(r.Other);
        Assert.Equal(new[] { r.O[1], r.O[2], r.O[3] }.OrderBy(u => u), other.OrderBy(u => u));
        foreach (Received d in r.Other.Delivered)
            AssertPlainLinkMessage(d, 2, 7, "hello", key);
    }

    public static IEnumerable<object[]> Targets() => new[]
    {
        new object[] { "LINK_SET", LINK_SET, new[] { 1, 2, 3 } },
        new object[] { "LINK_ALL_OTHERS", LINK_ALL_OTHERS, new[] { 1, 3 } },
        new object[] { "LINK_ALL_CHILDREN", LINK_ALL_CHILDREN, new[] { 2, 3 } },
        new object[] { "LINK_ROOT", LINK_ROOT, new[] { 1 } },
        new object[] { "LINK_THIS", LINK_THIS, new[] { 2 } },
        new object[] { "link number 3", 3, new[] { 3 } },
    };

    /// <summary>
    /// The sender is in link 2. Each target reaches exactly the scripts of the prims SL names, in both engines, once
    /// each; the sender hears itself when its own prim is targeted.
    /// </summary>
    [Theory]
    [MemberData(nameof(Targets))]
    public void EachTargetReachesTheScriptsOfThePrimsItNamesInEveryEngineOnce(string name, int link, int[] prims)
    {
        Rig r = ThreePrimRig();

        r.Api.llMessageLinked(link, 1, name, UUID.Zero.ToString());

        List<UUID> expectY = prims.Select(p => r.Y[p]).ToList();
        if (prims.Contains(2))
            expectY.Add(r.Sender);
        List<UUID> expectO = prims.Select(p => r.O[p]).ToList();

        Assert.Equal(expectY.OrderBy(u => u), Receivers(r.YEngine).OrderBy(u => u));
        Assert.Equal(expectO.OrderBy(u => u), Receivers(r.Other).OrderBy(u => u));
    }

    /// <summary>The calling engine's scripts get it exactly as before: LSL values, the sender's link number.</summary>
    [Fact]
    public void TheSendersOwnEngineGetsItAsBefore()
    {
        Rig r = ThreePrimRig();
        string key = UUID.Random().ToString();

        r.Api.llMessageLinked(LINK_SET, 42, "same as before", key);

        Assert.Equal(4, r.YEngine.Delivered.Count);
        foreach (Received d in r.YEngine.Delivered)
            AssertLslLinkMessage(d, 2, 42, "same as before", key);
    }

    /// <summary>In a one-prim object the sender's link number is 0 (SL), for every engine.</summary>
    [Fact]
    public void InAOnePrimObjectTheSenderNumberIsZeroForEveryEngine()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        StandInEngine yengine = AddYEngine(scene);
        StandInEngine other = AddOtherEngine(scene);
        SceneObjectGroup single = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "single", 0x60);
        UUID sender = AddScript(single.RootPart, yengine, "sender");
        UUID o = AddScript(single.RootPart, other, "o");

        ApiOn(yengine, single.RootPart, sender).llMessageLinked(LINK_THIS, 3, "one", UUID.Zero.ToString());

        AssertLslLinkMessage(Assert.Single(yengine.Delivered), 0, 3, "one", UUID.Zero.ToString());
        Received got = Assert.Single(other.Delivered);
        Assert.Equal(o, got.ItemID);
        AssertPlainLinkMessage(got, 0, 3, "one", UUID.Zero.ToString());
    }

    /// <summary>
    /// An engine that throws on every post: the calling engine's scripts and a third engine's scripts still get the
    /// message once each, and nothing reaches the sending script.
    /// </summary>
    [Fact]
    public void AnEngineThatThrowsKeepsTheMessageFromNoOtherScript()
    {
        Rig r = ThreePrimRig();
        r.Other.Throws = true;
        StandInEngine third = AddOtherEngine(r.Scene, "Third");
        UUID t1 = AddScript(r.Group.GetLinkNumPart(1), third, "t1");
        UUID t3 = AddScript(r.Group.GetLinkNumPart(3), third, "t3");

        Exception thrown = Record.Exception(() => r.Api.llMessageLinked(LINK_SET, 5, "still", UUID.Zero.ToString()));

        Assert.Null(thrown);
        Assert.True(r.Other.Attempts > 0, "the throwing engine was never offered the message");
        Assert.Equal(new[] { r.Y[1], r.Y[2], r.Y[3], r.Sender }.OrderBy(u => u), Receivers(r.YEngine).OrderBy(u => u));
        Assert.Equal(new[] { t1, t3 }.OrderBy(u => u), Receivers(third).OrderBy(u => u));
        foreach (Received d in third.Delivered)
            AssertPlainLinkMessage(d, 2, 5, "still", UUID.Zero.ToString());
    }

    /// <summary>
    /// An engine may both register and stack itself as the region's IScriptModule (as Phlox does): it is one engine,
    /// and its scripts get the message once.
    /// </summary>
    [Fact]
    public void AnEngineThatRegistersAndStacksItselfGetsTheMessageOnce()
    {
        Rig r = ThreePrimRig();
        r.Scene.RegisterModuleInterface<IScriptModule>(r.Other);
        r.Scene.StackModuleInterface<IScriptModule>(r.Other);
        r.Scene.StackModuleInterface<IScriptModule>(r.YEngine);

        r.Api.llMessageLinked(LINK_THIS, 9, "once", UUID.Zero.ToString());

        Assert.Equal(new[] { r.O[2] }, Receivers(r.Other));
        Assert.Equal(new[] { r.Y[2], r.Sender }.OrderBy(u => u), Receivers(r.YEngine).OrderBy(u => u));
    }

    /// <summary>A region with only YEngine: the same posts, the same values, the same order, nothing else.</summary>
    [Fact]
    public void AYEngineOnlyRegionSeesTheSamePostsAsBefore()
    {
        Rig r = ThreePrimRig(addOther: false);

        r.Api.llMessageLinked(LINK_SET, 11, "only", UUID.Zero.ToString());

        Assert.Equal(4, r.YEngine.Delivered.Count);
        Assert.Equal(4, r.YEngine.Offered);
        foreach (Received d in r.YEngine.Delivered)
            AssertLslLinkMessage(d, 2, 11, "only", UUID.Zero.ToString());
    }

    // ── stand-in engine ──────────────────────────────────────────────────────

    public sealed record Received(UUID ItemID, string EventName, object[] Args);

    /// <summary>
    /// Runs a set of script items and records what they are posted. Like a real engine, it delivers only to scripts it
    /// runs. Throws makes every post to it throw, counting the attempts. Offered counts every PostScriptEvent call.
    /// </summary>
    private sealed class StandInEngine : IScriptEngine, IScriptModule
    {
        private readonly Scene m_scene;
        private readonly ConcurrentDictionary<UUID, byte> m_scripts = new();
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
        public bool Throws { get; set; }
        public int Attempts => Volatile.Read(ref m_attempts);
        private int m_attempts;
        public int Offered => Volatile.Read(ref m_offered);
        private int m_offered;
        public ConcurrentQueue<Received> ReceivedQueue { get; } = new();
        public List<Received> Delivered => ReceivedQueue.ToList();

        public void AddScript(UUID itemID) => m_scripts[itemID] = 0;

        private bool Runs(UUID itemID) => m_scripts.ContainsKey(itemID);

        public Scene World => m_scene;

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            Interlocked.Increment(ref m_offered);
            if (Throws)
            {
                Interlocked.Increment(ref m_attempts);
                throw new InvalidOperationException(Name + " failed to post");
            }
            if (!Runs(itemID))
                return false;
            ReceivedQueue.Enqueue(new Received(itemID, parms.EventName, parms.Params));
            return true;
        }

        public bool PostObjectEvent(uint localID, EventParams parms) => false;

        // Unused by the paths under test.
        public IScriptModule ScriptModule => this;
        public IConfig Config { get; }
        public IConfigSource ConfigSource => m_configSource;
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
