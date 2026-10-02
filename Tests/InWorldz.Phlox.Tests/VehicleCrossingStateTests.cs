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
/// event ... if the task is moved to another SIM" (wiki, State). It keeps its globals and its listen; the record of
/// the controls it took does not come back, as the core starts it with no grant; its start parameter is 0
/// (llGetStartParameter: it does not survive "region change (SVC-3258,
/// crossing or teleport)"). Taken controls on a seated avatar travel in the core's agent data for every engine.
/// </summary>
// Runs in parallel: regions (7310, 7310) and (7310, 7309) are used by no other test; the scenes, engines and items are its own.
public class VehicleCrossingStateTests
{
    private const string Vehicle = @"
        integer n;
        default {
            state_entry() { n = 5; llListen(5, """", NULL_KEY, """"); llSay(0, ""entry""); }
            listen(integer c, string nm, key k, string m) { n++; llSay(0, ""n="" + (string)n + "" sp="" + (string)llGetStartParameter()); }
        }";

    private sealed class Region : IDisposable
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

    private static (Region A, Region B) TwoRegions()
    {
        var etmA = new EntityTransferModule();
        var etmB = new EntityTransferModule();
        var lscm = new LocalSimulationConnectorModule();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        var sh = new SceneHelpers();
        TestScene a = sh.SetupScene("Example Region A", UUID.Random(), 7310, 7310);
        TestScene b = sh.SetupScene("Example Region B", UUID.Random(), 7310, 7309);
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

        // The record llTakeControls keeps for a seated driver; the core carries the registration itself.
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

        // The record of taken controls does not come back: the core starts the script in region B with no grant
        // (SceneObjectPartInventory.CreateScriptInstance), and a record rests on the grant. A seated driver's
        // registration travels in the core's agent data instead.
        Assert.False(Script(b, item).ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control));
        Assert.True(WaitFor(() => Script(a, item) == null), "the script is still loaded in region A");
    }
}
