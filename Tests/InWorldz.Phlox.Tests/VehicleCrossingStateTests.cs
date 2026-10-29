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
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A scripted vehicle crosses from one region into the next, each region with its own Phlox engine and scheduler thread,
/// through the core's crossing (EntityTransferModule, LocalSimulationConnector: GetStateSnapshot, then SetState). The
/// script carries on in the new region: SL's "A script will NOT automatically re-enter the default state state_entry
/// event ... if the task is moved to another SIM" (wiki, State). It keeps its globals and its listen; its start parameter
/// is 0 (llGetStartParameter: it does not survive "region change (SVC-3258, crossing or teleport)"). Its grant comes back
/// only as a silent llRequestPermissions would give it, once the granter has arrived seated on it; until then the grant
/// and the record of the controls it took wait as a claim. Taken controls on a seated avatar travel in the core's agent
/// data for every engine.
/// </summary>
// Runs in parallel: regions (7310, 7310), (7310, 7309), (7320, 7320), (7320, 7319), (7350, 7350) and (7350, 7349) are
// used by no other test; the scenes, engines and items are its own.
public class VehicleCrossingStateTests
{
    private const string Vehicle = @"
        integer n;
        default {
            state_entry() { n = 5; llListen(5, """", NULL_KEY, """"); llSay(0, ""entry""); }
            listen(integer c, string nm, key k, string m) { n++; llSay(0, ""n="" + (string)n + "" sp="" + (string)llGetStartParameter()); }
        }";

    internal sealed class Region : IDisposable
    {
        public TestScene Scene;
        public PhloxEngine Engine;
        public readonly List<string> Said = new();
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

    internal static (Region A, Region B) TwoRegions(uint x = 7310)
    {
        var etmA = new EntityTransferModule();
        var etmB = new EntityTransferModule();
        var lscm = new LocalSimulationConnectorModule();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        var sh = new SceneHelpers();
        TestScene a = sh.SetupScene("Example Region A", UUID.Random(), x, x);
        TestScene b = sh.SetupScene("Example Region B", UUID.Random(), x, x - 1);
        SceneHelpers.SetupSceneModules(new Scene[] { a, b }, config, lscm);
        SceneHelpers.SetupSceneModules(a, config, etmA);
        SceneHelpers.SetupSceneModules(b, config, etmB);
        // Regions whose default engine is Phlox ([Startup] DefaultScriptEngine): the core starts an arriving object's
        // scripts with that name, and offers a crossing's states to that engine (SceneObjectGroup.SetState).
        var defaultEngine = typeof(Scene).GetField("m_defaultScriptEngine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        defaultEngine.SetValue(a, "InWorldz.Phlox");
        defaultEngine.SetValue(b, "InWorldz.Phlox");

        // The class's own bytecode folder, as SchedulerHarness gives each test class one.
        string dir = (string)typeof(SchedulerHarness).GetMethod("BytecodeDirForCaller", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        return (Start(a, dir), Start(b, dir));
    }

    internal static bool WaitFor(Func<bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            System.Threading.Thread.Sleep(50);
        }
        return done();
    }

    private static Interpreter Script(Region r, UUID item)
    {
        var exe = SavedStateRig.Field(r.Engine, "m_ExeScheduler");
        return (Interpreter)exe.GetType().GetMethod("FindScript")!.Invoke(exe, new object[] { item });
    }

    [Fact]
    public void AScriptedVehicleCrossesWithItsStateGlobalsAndListen()
    {
        var (a, b) = TwoRegions();
        using var ra = a;
        using var rb = b;

        var sog = SceneHelpers.AddSceneObject(a.Scene, "Example Vehicle", UUID.Random());
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID objectId = sog.UUID;
        UUID item = UUID.Random();
        TaskInventoryHelpers.AddScript(a.Scene.AssetService, sog.RootPart, item, UUID.Random(), "vehicle", Vehicle);
        sog.CreateScriptInstances(9, true, a.Engine.Name, 1);
        Assert.True(WaitFor(() => a.Heard("entry")), a.Text());

        a.Scene.SimChat("go", OpenSim.Framework.ChatTypeEnum.Region, 5, sog.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(WaitFor(() => a.Heard("n=6 sp=9")), a.Text());

        // The grant and the record llTakeControls keeps for a seated driver; the core carries the registration itself.
        UUID driver = UUID.Random();
        var invA = sog.RootPart.Inventory.GetInventoryItem(item);
        invA.PermsGranter = driver;
        invA.PermsMask = 0x4;   // PERMISSION_TAKE_CONTROLS
        var interpA = Script(a, item);
        lock (interpA.ScriptState.EventQueueLock)
            interpA.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] = new object[] { 1, 1, 0 };

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the vehicle did not reach region B");
        Assert.True(WaitFor(() => Script(b, item) != null), "the script did not start in region B");

        var there = b.Scene.GetSceneObjectGroup(objectId);
        b.Scene.SimChat("go", OpenSim.Framework.ChatTypeEnum.Region, 5, there.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(WaitFor(() => b.Heard("n=7 sp=0")), "region B: " + b.Text());
        Assert.DoesNotContain("entry", b.Said);

        // The driver has not arrived in region B: the grant waits as a claim, and the record of taken controls waits with
        // it. Nothing acts on them: the item holds no grant (the core starts the script with none,
        // SceneObjectPartInventory.CreateScriptInstance). A seated driver's registration travels in the core's agent data.
        Assert.True(Script(b, item).ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control));
        Assert.Equal(0, there.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.True(WaitFor(() => Script(a, item) == null), "the script is still loaded in region A");
    }

    private const string Driven = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            control(key id, integer l, integer e) { llSay(0, ""ctl "" + (string)id); }
            touch_start(integer t) { llSay(0, ""perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
        }";

    internal static LSLSystemAPI Api(Region r, UUID item)
    {
        var exe = SavedStateRig.Field(r.Engine, "m_ExeScheduler");
        var apis = (Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(exe, "m_Apis");
        lock (apis) return apis.TryGetValue(item, out var api) ? api : null;
    }

    /// <summary>
    /// A vehicle crosses before its driver. In region B nothing acts until the driver arrives seated on it: then the bits a
    /// silent llRequestPermissions gives a sitter come back (debit does not: it needs a dialog), the controls are taken
    /// again, with no run_time_permissions, and the driver's controls reach the script. The driver's own crossing (agent
    /// transfer) is not run here: the driver is added to region B, seated, and made a root agent, as the core does at the
    /// end of an arrival (the seat is set before OnMakeRootAgent).
    /// </summary>
    [Fact]
    public void AVehicleCrossingBeforeItsDriverGetsTheGrantAndControlsBackWhenTheDriverArrivesSeated()
    {
        var (a, b) = TwoRegions(7320);
        using var ra = a;
        using var rb = b;

        var sog = SceneHelpers.AddSceneObject(a.Scene, "Example Vehicle", UUID.Random());
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID objectId = sog.UUID;
        UUID item = UUID.Random();
        TaskInventoryHelpers.AddScript(a.Scene.AssetService, sog.RootPart, item, UUID.Random(), "driven", Driven);
        sog.CreateScriptInstances(0, true, a.Engine.Name, 1);
        Assert.True(WaitFor(() => a.Heard("entry")), a.Text());

        UUID driver = UUID.Random();
        var invA = sog.RootPart.Inventory.GetInventoryItem(item);
        invA.PermsGranter = driver;
        invA.PermsMask = 0x4 | 0x10 | 0x2;   // TAKE_CONTROLS | TRIGGER_ANIMATION | DEBIT
        var interpA = Script(a, item);
        lock (interpA.ScriptState.EventQueueLock)
            interpA.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] = new object[] { 1, 1, 0 };

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the vehicle did not reach region B");
        Assert.True(WaitFor(() => Api(b, item) != null), "the script did not start in region B");
        var there = b.Scene.GetSceneObjectGroup(objectId);
        var invB = there.RootPart.Inventory.GetInventoryItem(item);

        // Before the driver arrives: a claim, and nothing acts.
        Assert.True(WaitFor(() => Api(b, item).HasGrantClaim), "no claim waits in region B");
        Assert.Equal(0, invB.PermsMask);
        Assert.Equal(UUID.Zero, invB.PermsGranter);

        var sp = SceneHelpers.AddScenePresence(b.Scene, driver);
        sp.AbsolutePosition = there.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, there.RootPart.UUID, Vector3.Zero);
        Assert.Equal(there, sp.ParentPart?.ParentGroup);
        b.Scene.EventManager.TriggerOnMakeRootAgent(sp);

        Assert.True(WaitFor(() => sp.HasScriptControls(item)), "the controls were not taken when the driver arrived");
        Assert.Equal(driver, invB.PermsGranter);
        Assert.Equal(0x4 | 0x10, invB.PermsMask);
        Assert.False(Api(b, item).HasGrantClaim);
        Assert.DoesNotContain(b.Said, s => s.StartsWith("rtp", StringComparison.Ordinal));

        // The driver presses forward: the control event reaches the script.
        typeof(ScenePresence).GetMethod("SendControlsToScripts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sp, new object[] { 1u });   // AGENT_CONTROL_AT_POS, CONTROL_FWD
        Assert.True(WaitFor(() => b.Heard("ctl " + driver)), "region B: " + b.Text());
    }

    private const string ToldOnArrival = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            changed(integer c) { llSay(0, ""changed "" + (string)c + "" perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
        }";

    /// <summary>
    /// A vehicle crosses before its driver. Its script's changed(CHANGED_REGION) waits until the driver has arrived seated
    /// and the grant waiting for the driver is decided, so the script reads the driver's grant inside the event, once
    /// (Halcyon posts it when the last rider is seated, ScenePresence.ContinueSitAsRootAgent).
    /// </summary>
    [Fact]
    public void AVehicleScriptIsToldOfTheCrossingOnceItsDriverHasArrivedWithTheDriversGrant()
    {
        var (a, b) = TwoRegions(7350);
        using var ra = a;
        using var rb = b;

        var sog = SceneHelpers.AddSceneObject(a.Scene, "Example Vehicle", UUID.Random());
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID objectId = sog.UUID;
        UUID item = UUID.Random();
        TaskInventoryHelpers.AddScript(a.Scene.AssetService, sog.RootPart, item, UUID.Random(), "told", ToldOnArrival);
        sog.CreateScriptInstances(0, true, a.Engine.Name, 1);
        Assert.True(WaitFor(() => a.Heard("entry")), a.Text());

        UUID driver = UUID.Random();
        var invA = sog.RootPart.Inventory.GetInventoryItem(item);
        invA.PermsGranter = driver;
        invA.PermsMask = 0x4 | 0x10;   // TAKE_CONTROLS | TRIGGER_ANIMATION

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the vehicle did not reach region B");
        Assert.True(WaitFor(() => Api(b, item) != null), "the script did not start in region B");
        Assert.True(WaitFor(() => Api(b, item).HasGrantClaim), "no claim waits in region B");
        var there = b.Scene.GetSceneObjectGroup(objectId);

        // The driver arrives as the core's crossing brings it: a child agent here, seated back on the vehicle when its
        // move completes, before OnMakeRootAgent (ScenePresence.MakeRootAgent).
        ArrivalWaitsForRidersTests.ArriveSeated(b.Scene, driver, there);

        Assert.True(WaitFor(() => b.Heard("changed 256 perms=20 key=" + driver)), "region B: " + b.Text());
        lock (b.Said) Assert.Single(b.Said, s => s.StartsWith("changed 256 ", StringComparison.Ordinal));
    }
}
