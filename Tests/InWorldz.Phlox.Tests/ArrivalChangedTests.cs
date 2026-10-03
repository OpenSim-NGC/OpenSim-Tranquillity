/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What a script is told when its object arrives in a region with its state. SL's changed event: CHANGED_REGION (0x100)
/// "The object has changed region by crossing a region boundary (or by teleporting, if attached). This event only occurs
/// in the root prim of a linkset"; CHANGED_TELEPORT (0x200) "The avatar this object is attached to has teleported. This
/// event only occurs in the root prim of an attachment". The core says which arrival it is by the state source it starts
/// the scripts with (ScenePresence.GetStateSource: PrimCrossing for a walk across a border, Teleporting for a teleport).
/// A teleport posts one event with both bits, as YEngine does (XMRInstCtor, changedEvent_CRT). The other ways a script
/// starts (a rez, a wear, a login, a region start, a failed teleport) get what they got before, and no CHANGED_REGION.
/// </summary>
// Runs in parallel: each test has its own harness, items and assets; the two-region test uses regions (7330, 7330) and
// (7330, 7329), which no other test uses. Nothing process-wide is changed.
public class ArrivalChangedTests
{
    private const int None = -1, RegionStart = 0, NewRez = 1, PrimCrossing = 2, AttachedRez = 4, Teleporting = 5;

    private const string Src = @"
        integer n;
        default {
            state_entry() { n = 5; llSay(0, ""state_entry""); }
            on_rez(integer p) { llSay(0, ""on_rez""); }
            attach(key id) { llSay(0, ""attach""); }
            changed(integer c) { llSay(0, ""changed "" + (string)c); }
            touch_start(integer t) { n++; llSay(0, ""n="" + (string)n); }
        }";

    private static void Wear(SceneObjectGroup g, UUID avatar)
    {
        g.AttachedAvatar = avatar;
        g.IsAttachment = true;
        g.AttachmentPoint = (uint)AttachmentPoint.Chest;
    }

    /// <summary>Starts the script in one engine and saves it there; the item and asset to restore.</summary>
    private static (UUID Item, UUID Asset) Saved(string src = Src)
    {
        var asset = UUID.Random(); var item = UUID.Random();
        using var h1 = new SchedulerHarness();
        SavedStateRig.Rez(h1, h1.Prim, src, asset, item, 0, false, NewRez);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("state_entry")), SavedStateRig.SaidText(h1));
        h1.SaveState(item);
        return (item, asset);
    }

    /// <summary>
    /// Restores <paramref name="item"/> in <paramref name="part"/> with the load the core makes, lets everything the load
    /// posted run, then touches it: whatever the load posted is said before the touch's line, in order.
    /// </summary>
    private static List<string> ArriveAndTouch(SchedulerHarness h, SceneObjectPart part, UUID item, UUID asset,
                                               bool postOnRez, int stateSource, string marker = "n=6")
    {
        SavedStateRig.Rez(h, part, Src, asset, item, 0, postOnRez, stateSource);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null, TimeSpan.FromSeconds(15)), "not loaded: " + h.StatusOf(item));
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains(marker)), SavedStateRig.SaidText(h));
        return h.Said.ToList();
    }

    // ── the restore, arrival by arrival ──────────────────────────────────────

    /// <summary>
    /// The script keeps its state (n=6 after one touch, no state_entry) and gets exactly the start-up events its arrival
    /// brings, once, before anything that came after it. A teleport's flags go to an attachment only: the core can start a
    /// box's scripts with Teleporting when the box's owner arrived in the region by teleport
    /// (EntityTransferModule.GetStateSource reads the owner's presence for any object), and a box gets CHANGED_REGION.
    /// </summary>
    [Theory]
    [InlineData("an object crossing", PrimCrossing, false, false, new[] { "changed 256" })]
    [InlineData("an attachment crossing with its wearer", PrimCrossing, false, true, new[] { "changed 256" })]
    [InlineData("a teleport to another region while worn", Teleporting, false, true, new[] { "changed 768" })]
    [InlineData("an object the core starts as Teleporting", Teleporting, false, false, new[] { "changed 256" })]
    [InlineData("a region start", RegionStart, false, false, new[] { "changed 1024" })]
    [InlineData("a take and rez", NewRez, true, false, new[] { "on_rez" })]
    [InlineData("a wear or a login while worn", AttachedRez, true, true, new[] { "on_rez", "attach" })]
    [InlineData("a failed teleport's restart", None, false, true, new string[0])]
    public void EachArrivalGetsItsOwnEventsOnce(string arrival, int stateSource, bool postOnRez, bool worn, string[] expected)
    {
        var (item, asset) = Saved();
        using var h = new SchedulerHarness();
        if (worn) Wear(h.Prim.ParentGroup, UUID.Random());
        var said = ArriveAndTouch(h, h.Prim, item, asset, postOnRez, stateSource);
        Assert.True(expected.Append("n=6").SequenceEqual(said), arrival + ": " + SavedStateRig.SaidText(h));
    }

    /// <summary>
    /// The core's own choice for worn objects arriving with their wearer: ScenePresence.GetStateSource, from the flags the
    /// avatar arrived with (Default after a walk across a border, set by a teleport).
    /// </summary>
    [Theory]
    [InlineData(TeleportFlags.Default, "changed 256")]
    [InlineData(TeleportFlags.ViaLocation, "changed 768")]
    [InlineData(TeleportFlags.ViaLandmark, "changed 768")]
    public void AWornObjectArrivingWithItsWearerIsToldWhatTheCoreSaysTheArrivalWas(TeleportFlags flags, string expected)
    {
        var (item, asset) = Saved();
        using var h = new SchedulerHarness();
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        Wear(h.Prim.ParentGroup, sp.UUID);
        sp.TeleportFlags = flags;
        var said = ArriveAndTouch(h, h.Prim, item, asset, false, sp.GetStateSource());
        Assert.True(new[] { expected, "n=6" }.SequenceEqual(said), SavedStateRig.SaidText(h));
    }

    /// <summary>SL: "This event does not occur in child prims of objects when they cross a region boundary."</summary>
    [Theory]
    [InlineData(PrimCrossing, false)]
    [InlineData(Teleporting, true)]
    public void AScriptInAChildPrimIsToldNothing(int stateSource, bool worn)
    {
        var (item, asset) = Saved();
        using var h = new SchedulerHarness();
        var owner = UUID.Random();
        SceneObjectGroup linked = SceneHelpers.CreateSceneObject(2, owner, "Example Linkset", 0x50);
        h.Scene.AddNewSceneObject(linked, false);
        if (worn) Wear(linked, owner);
        var said = ArriveAndTouch(h, linked.Parts.Single(p => !p.IsRoot), item, asset, false, stateSource);
        Assert.True(new[] { "n=6" }.SequenceEqual(said), SavedStateRig.SaidText(h));
    }

    /// <summary>
    /// An object that arrives with no state for its script (an earlier build, another engine's state) starts it fresh:
    /// state_entry, then the arrival's changed event, as YEngine posts it after a fresh start and Halcyon to whatever
    /// scripts run in the object.
    /// </summary>
    [Theory]
    [InlineData(PrimCrossing, false, "changed 256")]
    [InlineData(Teleporting, true, "changed 768")]
    public void AScriptArrivingWithoutStateStartsFreshAndIsToldTheArrival(int stateSource, bool worn, string expected)
    {
        using var h = new SchedulerHarness();
        if (worn) Wear(h.Prim.ParentGroup, UUID.Random());
        var said = ArriveAndTouch(h, h.Prim, UUID.Random(), UUID.Random(), false, stateSource);
        Assert.True(new[] { "state_entry", expected, "n=6" }.SequenceEqual(said), SavedStateRig.SaidText(h));
    }

    // ── a script that was busy when its state was taken ──────────────────────

    private const string Sleeper = @"
        default {
            state_entry() { llSay(0, ""state_entry""); }
            changed(integer c) { llSay(0, ""changed "" + (string)c); }
            touch_start(integer t) { llSay(0, ""asleep""); llSleep(1.0); llSay(0, ""woke""); }
        }";

    /// <summary>
    /// Saved asleep in the middle of an event: it finishes that event after the arrival, then gets the arrival's event,
    /// once. The event waits on the script's own queue while it sleeps; it is not lost.
    /// </summary>
    [Theory]
    [InlineData(PrimCrossing, false, "changed 256")]
    [InlineData(Teleporting, true, "changed 768")]
    public void AScriptSavedAsleepFinishesItsEventThenIsToldTheArrival(int stateSource, bool worn, string expected)
    {
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SavedStateRig.Rez(h1, h1.Prim, Sleeper, asset, item, 0, false, NewRez);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("state_entry")), SavedStateRig.SaidText(h1));
            h1.PostTouch(item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("asleep") && h1.RunStateOf(item) == "Sleeping"), SavedStateRig.SaidText(h1));
            h1.SaveState(item);
        }
        using var h = new SchedulerHarness();
        if (worn) Wear(h.Prim.ParentGroup, UUID.Random());
        SavedStateRig.Rez(h, h.Prim, Sleeper, asset, item, 0, false, stateSource);
        Assert.True(h.PumpUntil(() => h.Said.Contains(expected), TimeSpan.FromSeconds(15)), SavedStateRig.SaidText(h) + " " + h.StatusOf(item));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s == "woke") == 2), SavedStateRig.SaidText(h));
        Assert.True(new[] { "woke", expected, "asleep", "woke" }.SequenceEqual(h.Said), SavedStateRig.SaidText(h));
    }

    private const string Busy = @"
        integer n;
        default {
            state_entry() { llSay(0, ""state_entry""); }
            changed(integer c) { llSay(0, ""changed "" + (string)c); }
            touch_start(integer t) { llSay(0, ""begin""); integer i; for (i = 0; i < 200000; i++) n++; llSay(0, ""end "" + (string)n); }
        }";

    /// <summary>
    /// Saved running in the middle of an event (its timeslice ran out in a loop): it finishes that event after the
    /// arrival, then gets the arrival's event, once.
    /// </summary>
    [Fact]
    public void AScriptSavedMidEventFinishesItsEventThenIsToldTheArrival()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        // Off, Halcyon's chat pause (ChatThrottle) cannot leave the script Sleeping right after "begin".
        Action<IConfigSource> noThrottle = cfg => cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", "false");
        using (var h1 = new SchedulerHarness(noThrottle))
        {
            SavedStateRig.Rez(h1, h1.Prim, Busy, asset, item, 0, false, NewRez);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("state_entry")), SavedStateRig.SaidText(h1));
            h1.PostTouch(item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("begin")), SavedStateRig.SaidText(h1));
            Assert.Equal("Running", h1.RunStateOf(item));
            Assert.DoesNotContain(h1.Said, s => s.StartsWith("end"));
            h1.SaveState(item);
        }
        using var h = new SchedulerHarness(noThrottle);
        SavedStateRig.Rez(h, h.Prim, Busy, asset, item, 0, false, PrimCrossing);
        Assert.True(h.PumpUntil(() => h.Said.Contains("changed 256"), TimeSpan.FromSeconds(15)), SavedStateRig.SaidText(h) + " " + h.StatusOf(item));
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        Assert.True(new[] { "end 200000", "changed 256" }.SequenceEqual(h.Said), SavedStateRig.SaidText(h));
    }

    // ── through the core ─────────────────────────────────────────────────────

    /// <summary>
    /// A teleport within the region restores nothing; the core posts CHANGED_TELEPORT to the worn object's root part
    /// (EntityTransferModule.TeleportAgentWithinRegion), and the script gets it once, with no CHANGED_REGION.
    /// </summary>
    [Fact]
    public void ATeleportWithinTheRegionWhileWornGivesChangedTeleportOnly()
    {
        using var h = new SchedulerHarness();
        var etm = new EntityTransferModule();
        var config = new IniConfigSource();
        config.AddConfig("Modules").Set("EntityTransferModule", etm.Name);
        SceneHelpers.SetupSceneModules(h.Scene, config, etm);

        var sp = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        sp.AbsolutePosition = new Vector3(30, 31, 32);
        var worn = SceneHelpers.AddSceneObject(h.Scene, "Example Attachment", sp.UUID);
        Wear(worn, sp.UUID);
        sp.AddAttachment(worn);
        var item = SavedStateRig.Rez(h, worn.RootPart, Src, UUID.Random(), UUID.Random(), 0, true, AttachedRez);
        Assert.True(h.PumpUntil(() => h.Said.Contains("attach")), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h.ClearSaid(item);

        h.Scene.RequestTeleportLocation(sp.ControllingClient, h.Scene.RegionInfo.RegionHandle,
            new Vector3(10, 11, 12), new Vector3(20, 21, 22), (uint)TeleportFlags.ViaLocation);
        Assert.Equal(new Vector3(10, 11, 12), sp.AbsolutePosition);
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=6")), SavedStateRig.SaidText(h));
        Assert.True(new[] { "changed 512", "n=6" }.SequenceEqual(h.Said), SavedStateRig.SaidText(h));
    }

    private const string Crosser = @"
        integer n;
        default {
            state_entry() { n = 5; llListen(5, """", NULL_KEY, """"); llSay(0, ""state_entry "" + llGetScriptName()); }
            changed(integer c) { llSay(0, llGetScriptName() + "" changed "" + (string)c); }
            listen(integer c, string nm, key k, string m) { n++; llSay(0, llGetScriptName() + "" n="" + (string)n); }
        }";

    private sealed class Region : IDisposable
    {
        public TestScene Scene;
        public PhloxEngine Engine;
        public readonly List<string> Said = new();
        public List<string> Lines() { lock (Said) return Said.ToList(); }
        public bool Heard(string s) { lock (Said) return Said.Contains(s); }
        public string Text() { lock (Said) return "[" + string.Join(" | ", Said) + "]"; }

        public void Dispose()
        {
            try { Engine.RemoveRegion(Scene); } catch { }
            try { Engine.Close(); } catch { }
        }
    }

    private static Region Start(TestScene scene, string bytecodeDir)
    {
        var config = new IniConfigSource();
        config.AddConfig("InWorldz.Phlox").Set("Enabled", "true");
        config.AddConfig("Startup").Set("DefaultScriptEngine", "InWorldz.Phlox");
        var r = new Region { Scene = scene, Engine = new PhloxEngine() };
        r.Engine.BytecodeCacheDir = bytecodeDir;
        r.Engine.Initialise(config);
        r.Engine.AddRegion(scene);
        if (scene.RequestModuleInterface<IWorldComm>() is null)
            scene.RegisterModuleInterface<IWorldComm>(NullWorldComm.Create());
        r.Engine.RegionLoaded(scene);
        scene.EventManager.OnChatFromWorld += (_, chat) => { lock (r.Said) r.Said.Add(chat.Message ?? string.Empty); };
        return r;
    }

    private static bool WaitFor(Func<bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            System.Threading.Thread.Sleep(50);
        }
        return done();
    }

    /// <summary>
    /// A linked object crosses into the next region through the core's crossing (EntityTransferModule,
    /// LocalSimulationConnector), each region with its own engine on its own thread. The root prim's script is told
    /// CHANGED_REGION once; the child prim's script is told nothing; both keep their state.
    /// </summary>
    [Fact]
    public void ALinkedObjectCrossingTellsOnlyItsRootPrimsScriptOnce()
    {
        var etmA = new EntityTransferModule();
        var etmB = new EntityTransferModule();
        var lscm = new LocalSimulationConnectorModule();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);
        var sh = new SceneHelpers();
        TestScene sa = sh.SetupScene("Example Region A", UUID.Random(), 7330, 7330);
        TestScene sb = sh.SetupScene("Example Region B", UUID.Random(), 7330, 7329);
        SceneHelpers.SetupSceneModules(new Scene[] { sa, sb }, config, lscm);
        SceneHelpers.SetupSceneModules(sa, config, etmA);
        SceneHelpers.SetupSceneModules(sb, config, etmB);
        var defaultEngine = typeof(Scene).GetField("m_defaultScriptEngine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        defaultEngine.SetValue(sa, "InWorldz.Phlox");
        defaultEngine.SetValue(sb, "InWorldz.Phlox");
        string dir = (string)typeof(SchedulerHarness).GetMethod("BytecodeDirForCaller", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        using var a = Start(sa, dir);
        using var b = Start(sb, dir);

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(2, UUID.Random(), "Example Linkset", 0x60);
        sa.AddNewSceneObject(sog, false);
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID objectId = sog.UUID;
        SceneObjectPart child = sog.Parts.Single(p => !p.IsRoot);
        TaskInventoryHelpers.AddScript(sa.AssetService, sog.RootPart, UUID.Random(), UUID.Random(), "root", Crosser);
        TaskInventoryHelpers.AddScript(sa.AssetService, child, UUID.Random(), UUID.Random(), "child", Crosser);
        sog.CreateScriptInstances(0, true, a.Engine.Name, NewRez);
        Assert.True(WaitFor(() => a.Heard("state_entry root") && a.Heard("state_entry child")), a.Text());
        sa.SimChat("go", OpenSim.Framework.ChatTypeEnum.Region, 5, sog.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(WaitFor(() => a.Heard("root n=6") && a.Heard("child n=6")), a.Text());

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the object did not reach region B");
        Assert.True(WaitFor(() => b.Heard("root changed 256")), "region B: " + b.Text());

        var there = b.Scene.GetSceneObjectGroup(objectId);
        b.Scene.SimChat("go", OpenSim.Framework.ChatTypeEnum.Region, 5, there.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(WaitFor(() => b.Heard("root n=7") && b.Heard("child n=7")), "region B: " + b.Text());
        var lines = b.Lines();
        Assert.Equal(1, lines.Count(s => s == "root changed 256"));
        Assert.True(lines.IndexOf("root changed 256") < lines.IndexOf("root n=7"), b.Text());
        Assert.DoesNotContain(lines, s => s.StartsWith("state_entry"));
        // The rest is the test's own "go" and, when it lands, the core's own CHANGED_POSITION (OpenSim's 0x8000, posted
        // to every part when the core sets the object's position, SceneObjectGroup.AbsolutePosition), not counted here.
        Assert.True(lines.Except(new[] { "go", "root changed 256", "root n=7", "child n=7" })
                         .All(s => s is "root changed 32768" or "child changed 32768"), b.Text());
    }
}
