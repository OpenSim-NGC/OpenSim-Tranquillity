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
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.CrossEngine.Tests;

/// <summary>
/// A region can run more than one script engine. object_rez, email and linkset_data, posted by the shared script API,
/// must reach every script SL names, whichever engine runs it, exactly once each.
///
/// SL (wiki object_rez): "Triggers in all running scripts with an object_rez event, AND in the same prim as the script
/// calling llRezObject or llRezAtRoot." and "Does NOT trigger in linked prims."
/// SL (wiki email): "The email queue is associated with the prim and any script in the prim can access it."
/// SL (wiki linkset_data): "The linkset_data event fires in all scripts in a linkset whenever the datastore has been
/// modified through a call to one of the llLinksetData functions."
///
/// "YEngine" below is a stand-in the real shared LSL_Api runs on, as YEngine's scripts do. The other engines are only
/// IScriptModules of the region, as a second engine is. Scripts are task-inventory items of a real linked object. No
/// network is used.
/// </summary>
public class CrossEngineObjectEventTests : OpenSimTestCase
{
    private static readonly TimeSpan WaitCap = TimeSpan.FromSeconds(30);

    /// <summary>A three-prim object; in each prim one YEngine script and one script of the other engine.</summary>
    private sealed class Rig
    {
        public TestScene Scene;
        public readonly object Gate = new object();
        public StandInEngine YEngine;
        public StandInEngine Other;
        public SceneObjectGroup Group;
        public SceneObjectPart Caller;      // link 2
        public UUID[] Y = new UUID[4];       // by link number 1..3
        public UUID[] O = new UUID[4];
        public UUID CallerScript;           // a second YEngine script, in link 2
        public StandInEmail Email;
        public LSL_Api Api;
    }

    private static Rig ThreePrimRig(bool addOther = true)
    {
        Rig r = new Rig { Scene = new SceneHelpers().SetupScene() };
        r.YEngine = new StandInEngine("YEngine", r.Scene, r.Gate);
        r.Scene.RegisterModuleInterface<IScriptModule>(r.YEngine);
        if (addOther)
            r.Other = AddOtherEngine(r, "Other");
        r.Email = new StandInEmail();
        r.Scene.RegisterModuleInterface<IEmailModule>(r.Email);

        r.Group = SceneHelpers.AddSceneObject(r.Scene, 3, UUID.Random(), "linked", 0x70);
        for (int link = 1; link <= 3; link++)
        {
            SceneObjectPart part = r.Group.GetLinkNumPart(link);
            r.Y[link] = AddScript(part, r.YEngine, "y" + link);
            if (addOther)
                r.O[link] = AddScript(part, r.Other, "o" + link);
        }
        r.Caller = r.Group.GetLinkNumPart(2);
        r.CallerScript = AddScript(r.Caller, r.YEngine, "caller");

        SceneObjectGroup toRez = SceneHelpers.CreateSceneObject(1, r.Group.OwnerID, "rezzed", 0x90);
        TaskInventoryHelpers.AddSceneObject(r.Scene.AssetService, r.Caller, "rezzable", UUID.Random(), toRez, UUID.Random());

        r.Api = new LSL_Api();
        r.Api.Initialize(r.YEngine, r.Caller, r.Caller.Inventory.GetInventoryItem(r.CallerScript));
        return r;
    }

    private static StandInEngine AddOtherEngine(Rig r, string name)
    {
        StandInEngine e = new StandInEngine(name, r.Scene, r.Gate);
        r.Scene.StackModuleInterface<IScriptModule>(e);
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

    private static void PutKey(Rig r, string key, string value)
    {
        SceneObjectPart root = r.Group.RootPart;
        root.LinksetData ??= new LinksetData();
        Assert.Equal(0, root.LinksetData.AddOrUpdateLinksetDataKey(key, value, string.Empty));
    }

    /// <summary>Waits for a condition the engines' posts make true, with a generous cap.</summary>
    private static bool WaitUntil(Rig r, Func<bool> done)
    {
        DateTime end = DateTime.UtcNow + WaitCap;
        lock (r.Gate)
        {
            while (!done())
            {
                TimeSpan left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(r.Gate, left))
                    return done();
            }
            return true;
        }
    }

    // ── the calls under test ─────────────────────────────────────────────────

    /// <summary>
    /// One way a script makes the shared API post one of these events. Run makes the call; Wait waits until the posts
    /// it causes have been made (the rez calls post from a worker); Expected returns, for the rig, the event name, the
    /// prim the calling engine is posted for (as on develop), the link numbers SL names, and the plain values.
    /// </summary>
    private sealed class Call
    {
        public string Name;
        public string Event;
        public bool Async;
        public int[] Prims;
        public int PostedLink;
        public Action<Rig> Prepare = _ => { };
        public Func<Rig, object> Run;
        public Func<Rig, object, object[]> Plain;
        public Action<object[], object[]> AssertLslLike;
        public override string ToString() => Name;
    }

    private static readonly Call[] Calls =
    {
        new Call
        {
            Name = "llRezObjectWithParams", Event = "object_rez", Async = true, Prims = new[] { 2 }, PostedLink = 2,
            Run = r => r.Api.llRezObjectWithParams("rezzable", new LSL_Types.list()).m_string,
            Plain = (r, ret) => new object[] { (string)ret },
        },
        new Call
        {
            Name = "llRezAtRoot", Event = "object_rez", Async = true, Prims = new[] { 2 }, PostedLink = 2,
            Run = r =>
            {
                r.Api.llRezAtRoot("rezzable", r.Api.llGetPos(), new LSL_Types.Vector3(0, 0, 0),
                    new LSL_Types.Quaternion(0, 0, 0, 1), 0);
                return null;
            },
            Plain = null, // the key is the one the calling engine was given
        },
        new Call
        {
            Name = "llGetNextEmail", Event = "email", Prims = new[] { 2 }, PostedLink = 2,
            Prepare = r => r.Email.Next = new Email
            {
                time = "1700000000", sender = "sender@example.com", subject = "subject", message = "body", numLeft = 3,
            },
            Run = r => { r.Api.llGetNextEmail(string.Empty, string.Empty); return null; },
            Plain = (r, _) => new object[] { "1700000000", "sender@example.com", "subject", "body", 3 },
        },
        new Call
        {
            Name = "llLinksetDataWrite", Event = "linkset_data", Prims = new[] { 1, 2, 3 }, PostedLink = 1,
            Run = r => r.Api.llLinksetDataWrite("color", "blue"),
            Plain = (r, _) => new object[] { ScriptBaseClass.LINKSETDATA_UPDATE, "color", "blue" },
        },
        new Call
        {
            Name = "llLinksetDataReset", Event = "linkset_data", Prims = new[] { 1, 2, 3 }, PostedLink = 1,
            Prepare = r => PutKey(r, "color", "blue"),
            Run = r => { r.Api.llLinksetDataReset(); return null; },
            Plain = (r, _) => new object[] { ScriptBaseClass.LINKSETDATA_RESET, string.Empty, string.Empty },
        },
        new Call
        {
            Name = "llLinksetDataDeleteFound", Event = "linkset_data", Prims = new[] { 1, 2, 3 }, PostedLink = 2,
            Prepare = r => PutKey(r, "color", "blue"),
            Run = r => r.Api.llLinksetDataDeleteFound("color", string.Empty),
            Plain = (r, _) => new object[] { ScriptBaseClass.LINKSETDATA_MULTIDELETE, "color", string.Empty },
        },
        new Call
        {
            Name = "llLinksetDataDelete", Event = "linkset_data", Prims = new[] { 1, 2, 3 }, PostedLink = 1,
            Prepare = r => PutKey(r, "color", "blue"),
            Run = r => r.Api.llLinksetDataDelete("color"),
            Plain = (r, _) => new object[] { ScriptBaseClass.LINKSETDATA_DELETE, "color", string.Empty },
        },
    };

    public static IEnumerable<object[]> AllCalls() => Calls.Select(c => new object[] { c.Name });

    private static Call Get(string name) => Calls.Single(c => c.Name == name);

    /// <summary>The script items of the given prims, every engine's, each once.</summary>
    private static int ScriptCount(Rig r, int[] prims) =>
        prims.Sum(p => r.Group.GetLinkNumPart(p).Inventory.GetInventoryItems()
            .Count(i => i.Type == (int)AssetType.LSLText));

    /// <summary>
    /// Makes the call, then waits until the calling engine has its post and each engine in waitFor has been offered
    /// every script item SL names (posts made from a worker are complete then).
    /// </summary>
    private static object Fire(Rig r, Call c, params StandInEngine[] waitFor)
    {
        c.Prepare(r);
        object ret = c.Run(r);
        if (c.Async)
        {
            int items = ScriptCount(r, c.Prims);
            bool done = WaitUntil(r, () =>
                r.YEngine.ObjectPosts.Any(p => p.EventName == c.Event)
                && waitFor.All(e => e.Offered >= items));
            Assert.True(done, c.Name + ": the posts were not all made within " + WaitCap);
        }
        return ret;
    }

    private static object[] ExpectedPlain(Rig r, Call c, object ret)
    {
        if (c.Plain is not null)
            return c.Plain(r, ret);
        ObjectPost own = Assert.Single(r.YEngine.ObjectPosts);
        return new object[] { Assert.IsType<LSL_Types.LSLString>(own.Args[0]).m_string };
    }

    private static void AssertPlain(Received got, string eventName, object[] expected)
    {
        Assert.Equal(eventName, got.EventName);
        Assert.Equal(expected.Length, got.Args.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.IsType(expected[i].GetType(), got.Args[i]);
            Assert.Equal(expected[i], got.Args[i]);
        }
    }

    /// <summary>The scripts of an engine that got the event, by item, each once (a repeat fails the test).</summary>
    private static List<UUID> Receivers(StandInEngine e)
    {
        List<UUID> items = e.Delivered.Select(d => d.ItemID).ToList();
        Assert.Equal(items.Count, items.Distinct().Count());
        return items;
    }

    // ── tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The other engine's scripts in the prims SL names get the event once each, with plain values, and no other script
    /// of that engine gets it: the rezzing prim for object_rez, the calling prim for email, every prim for linkset_data.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCalls))]
    public void TheOtherEnginesScriptsThatSLNamesGetItOnceEach(string call)
    {
        Call c = Get(call);
        Rig r = ThreePrimRig();

        object ret = Fire(r, c, r.Other);

        List<UUID> expected = c.Prims.Select(p => r.O[p]).ToList();
        Assert.Equal(expected.OrderBy(u => u), Receivers(r.Other).OrderBy(u => u));
        object[] plain = ExpectedPlain(r, c, ret);
        foreach (Received d in r.Other.Delivered)
            AssertPlain(d, c.Event, plain);
    }

    /// <summary>
    /// The calling engine is posted to exactly as before: one PostObjectEvent for the same prim with the same LSL
    /// values, and no per-script post.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCalls))]
    public void TheCallingEngineIsPostedToAsBefore(string call)
    {
        Call c = Get(call);
        Rig r = ThreePrimRig();

        object ret = Fire(r, c);

        ObjectPost own = Assert.Single(r.YEngine.ObjectPosts);
        Assert.Equal(c.Event, own.EventName);
        Assert.Equal(r.Group.GetLinkNumPart(c.PostedLink).LocalId, own.LocalID);
        AssertLslValues(own.Args, ExpectedPlain(r, c, ret));
        Assert.Equal(0, r.YEngine.Offered);
    }

    /// <summary>
    /// An engine that throws on every post: nothing reaches the calling script, the calling engine is posted to as
    /// before, and a third engine's scripts SL names still get the event once each.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCalls))]
    public void AnEngineThatThrowsKeepsTheEventFromNoOtherScript(string call)
    {
        Call c = Get(call);
        Rig r = ThreePrimRig();
        r.Other.Throws = true;
        StandInEngine third = AddOtherEngine(r, "Third");
        UUID[] t = new UUID[4];
        for (int link = 1; link <= 3; link++)
            t[link] = AddScript(r.Group.GetLinkNumPart(link), third, "t" + link);

        object ret = null;
        Exception thrown = Record.Exception(() => ret = Fire(r, c, r.Other, third));

        Assert.Null(thrown);
        Assert.True(r.Other.Attempts > 0, "the throwing engine was never offered the event");
        ObjectPost own = Assert.Single(r.YEngine.ObjectPosts);
        Assert.Equal(c.Event, own.EventName);
        Assert.Equal(c.Prims.Select(p => t[p]).OrderBy(u => u), Receivers(third).OrderBy(u => u));
        object[] plain = ExpectedPlain(r, c, ret);
        foreach (Received d in third.Delivered)
            AssertPlain(d, c.Event, plain);
    }

    /// <summary>
    /// An engine may both register and stack itself as the region's IScriptModule (as Phlox does): it is one engine,
    /// and each of its scripts gets the event once.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCalls))]
    public void AnEngineThatRegistersAndStacksItselfGetsTheEventOnce(string call)
    {
        Call c = Get(call);
        Rig r = ThreePrimRig();
        r.Scene.RegisterModuleInterface<IScriptModule>(r.Other);
        r.Scene.StackModuleInterface<IScriptModule>(r.Other);
        r.Scene.StackModuleInterface<IScriptModule>(r.YEngine);

        Fire(r, c, r.Other);

        Assert.Equal(c.Prims.Select(p => r.O[p]).OrderBy(u => u), Receivers(r.Other).OrderBy(u => u));
        Assert.Equal(ScriptCount(r, c.Prims), r.Other.Offered);
        Assert.Single(r.YEngine.ObjectPosts);
    }

    /// <summary>A region with only YEngine: the same single PostObjectEvent as before, and nothing else.</summary>
    [Theory]
    [MemberData(nameof(AllCalls))]
    public void AYEngineOnlyRegionSeesTheSamePostsAsBefore(string call)
    {
        Call c = Get(call);
        Rig r = ThreePrimRig(addOther: false);

        object ret = Fire(r, c);

        ObjectPost own = Assert.Single(r.YEngine.ObjectPosts);
        Assert.Equal(c.Event, own.EventName);
        Assert.Equal(r.Group.GetLinkNumPart(c.PostedLink).LocalId, own.LocalID);
        AssertLslValues(own.Args, ExpectedPlain(r, c, ret));
        Assert.Equal(0, r.YEngine.Offered);
    }

    private static void AssertLslValues(object[] args, object[] plain)
    {
        Assert.Equal(plain.Length, args.Length);
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] is int n)
                Assert.Equal(n, Assert.IsType<LSL_Types.LSLInteger>(args[i]).value);
            else
                Assert.Equal((string)plain[i], Assert.IsType<LSL_Types.LSLString>(args[i]).m_string);
        }
    }

    // ── stand-ins ────────────────────────────────────────────────────────────

    public sealed record Received(UUID ItemID, string EventName, object[] Args);
    public sealed record ObjectPost(uint LocalID, string EventName, object[] Args);

    /// <summary>An email module whose queue holds the one email a test puts there.</summary>
    private sealed class StandInEmail : IEmailModule
    {
        public Email Next;
        public Email GetNextEmail(UUID objectID, string sender, string subject) => Interlocked.Exchange(ref Next, null);
        public void SendEmail(UUID objectID, UUID ownerID, string address, string subject, string body) { }
        public void AddPartMailBox(UUID objectID) { }
        public void RemovePartMailBox(UUID objectID) { }
    }

    /// <summary>
    /// Runs a set of script items and records what it is posted. Like a real engine, it delivers a per-script post only
    /// to scripts it runs. Throws makes every per-script post throw, counting the attempts. Offered counts every
    /// per-script post. Each record pulses the rig's gate, for waiting on posts made from a worker.
    /// </summary>
    private sealed class StandInEngine : IScriptEngine, IScriptModule
    {
        private readonly Scene m_scene;
        private readonly object m_gate;
        private readonly ConcurrentDictionary<UUID, byte> m_scripts = new();
        private readonly IConfigSource m_configSource;
        private readonly ConcurrentQueue<Received> m_received = new();
        private readonly ConcurrentQueue<ObjectPost> m_objectPosts = new();

        public StandInEngine(string name, Scene scene, object gate)
        {
            Name = name;
            m_scene = scene;
            m_gate = gate;
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
        public List<Received> Delivered => m_received.ToList();
        public List<ObjectPost> ObjectPosts => m_objectPosts.ToList();

        public void AddScript(UUID itemID) => m_scripts[itemID] = 0;

        private bool Runs(UUID itemID) => m_scripts.ContainsKey(itemID);

        private void Pulse()
        {
            lock (m_gate)
                Monitor.PulseAll(m_gate);
        }

        public Scene World => m_scene;

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            try
            {
                Interlocked.Increment(ref m_offered);
                if (Throws)
                {
                    Interlocked.Increment(ref m_attempts);
                    throw new InvalidOperationException(Name + " failed to post");
                }
                if (!Runs(itemID))
                    return false;
                m_received.Enqueue(new Received(itemID, parms.EventName, parms.Params));
                return true;
            }
            finally
            {
                Pulse();
            }
        }

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            m_objectPosts.Enqueue(new ObjectPost(localID, parms.EventName, parms.Params));
            Pulse();
            return true;
        }

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
