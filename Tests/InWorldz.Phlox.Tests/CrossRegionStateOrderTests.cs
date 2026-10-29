/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Two regions of one simulator share one state database, each through its own state manager. A script that crossed
/// into the second region and loaded there is that region's: the first region's unload of the object it left, done
/// after that load, never writes its older state over the row, and never takes the script off the list of loaded
/// scripts the purge spares. Nothing orders the first region's unload after the second region's load: the core starts
/// the arriving object's scripts and only then deletes the object it left (EntityTransferModule), and each region's
/// loader runs on its own thread.
/// </summary>
// Runs in parallel: each test has its own harnesses, items and assets, and rows under random item ids in the state
// database every harness shares; nothing process-wide is changed beyond its own rows.
public class CrossRegionStateOrderTests
{
    private const string Phlox = "InWorldz.Phlox";
    private const int NewRez = 1, PrimCrossing = 2;

    private const string Counter = @"
        integer g = 1;
        default {
            state_entry() { llSay(0, ""entry""); }
            touch_start(integer t) { g = 5; llSay(0, ""g5""); }
        }";

    private static object Global0(UUID item)
        => StateManager.Decode(SavedStateRig.Row(item)!.Value.Blob).ToRuntimeState().Globals[0];

    [Fact]
    public void TheFirstRegionsLateUnloadDoesNotWriteItsOlderStateOverTheSecondRegionsRow()
    {
        using var a = new SchedulerHarness();
        using var b = new SchedulerHarness();
        UUID item = UUID.Random(), asset = UUID.Random();
        TaskInventoryHelpers.AddScript(a.Scene.AssetService, a.Prim, item, asset, "counter", Counter);
        Assert.True(a.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, NewRez));
        Assert.True(a.PumpUntil(() => a.Said.Contains("entry")), SavedStateRig.SaidText(a));
        a.PumpUntilIdle(TimeSpan.FromSeconds(2));

        // The crossing: the state the object carries, and the object started in the second region.
        string carried = a.Engine.GetXMLState(item);
        Assert.NotEqual(string.Empty, carried);
        var there = SceneHelpers.AddSceneObject(b.Scene, "Example Vehicle", a.Prim.OwnerID);
        TaskInventoryHelpers.AddScript(b.Scene.AssetService, there.RootPart, item, asset, "counter", Counter);
        Assert.True(b.Engine.SetXMLState(item, carried));
        Assert.Equal(1, there.CreateScriptInstances(0, false, Phlox, PrimCrossing));
        Assert.True(b.PumpUntil(() => b.InterpreterFor(item) != null), "the script did not load in the second region");
        b.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("entry", b.Said);

        // It runs there, and its newer state is saved.
        b.PostTouch(item);
        Assert.True(b.PumpUntil(() => b.Said.Contains("g5")), SavedStateRig.SaidText(b));
        b.Engine.SaveAllState();
        Assert.Equal(5, Global0(item));

        // Only now does the first region unload the object it left.
        a.Scene.DeleteSceneObject(a.Prim.ParentGroup, false);
        Assert.True(a.PumpUntil(() => a.InterpreterFor(item) == null), "the first region did not unload the script");
        SavedStateRig.WaitForWrites(a);

        Assert.Equal(5, Global0(item));
        Assert.True(StateManager.IsLoadedAnywhere(item), "the first region's unload took the script off the loaded list");
    }

    private static InWorldz.Phlox.VM.Interpreter Script(InWorldz.Phlox.VM.CompiledScript compiled, UUID item, int g)
    {
        var api = RecordingSystemApi.Create(out _);
        var shim = new InWorldz.Phlox.Glue.SyscallShim(call => call());
        shim.SystemAPI = api;
        var interp = new InWorldz.Phlox.VM.Interpreter(compiled, shim) { ItemId = item };
        shim.Interpreter = interp;
        interp.ScriptState.Globals[0] = g;
        return interp;
    }

    /// <summary>
    /// Two managers on one file each hold a queued write for one item: a load sees both written, in the order they were
    /// queued, and neither is left marked pending.
    /// </summary>
    [Fact]
    public void ALoadWritesEveryManagersQueuedWriteForItsItemInOrder()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "cross-region-state");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
        var compiled = PhloxCompiler.CompileTo("integer g = 1; default { state_entry() { } }", out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        compiled.AssetId = UUID.Random();
        UUID item = UUID.Random();
        using var first = new StateManager(null, db);
        using var second = new StateManager(null, db);

        first.QueueUnloadSave(Script(compiled, item, 1));    // queued first: the older state
        second.QueueUnloadSave(Script(compiled, item, 5));   // queued last: the newer one
        var loaded = second.LoadState(item, compiled.AssetId);
        Assert.Equal(5, loaded.ToRuntimeState().Globals[0]);

        // Nothing is left to land later over the row.
        Assert.True(first.WaitForWrites());
        Assert.True(second.WaitForWrites());
        Assert.Equal(5, second.LoadState(item, compiled.AssetId).ToRuntimeState().Globals[0]);
        Assert.Equal(1, first.WritesDone);
        Assert.Equal(1, second.WritesDone);
    }
}
