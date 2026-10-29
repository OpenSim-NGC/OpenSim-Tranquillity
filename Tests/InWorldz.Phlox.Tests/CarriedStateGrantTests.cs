/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Carried state never gives a script a grant its item does not hold. The core zeroes a script item's grant whenever it
/// starts the script (SceneObjectPartInventory.CreateScriptInstance) and when the owner changes (ChangeInventoryOwner), so
/// after a take and rez, by the same owner or another, the restored script holds no grant, and the records of grants it
/// used (taken controls, PERMISSION_SILENT_ESTATE_MANAGEMENT) are gone: nothing acts on them later. SL: the script loses
/// PERMISSION_TAKE_CONTROLS "on reset, or if the object is deleted, detached, or dropped" (llTakeControls).
/// </summary>
// Runs in parallel: each test has its own harness, avatars, items and assets; nothing process-wide is changed.
public class CarriedStateGrantTests
{
    private const int NewRez = 1;
    private const int TakeControls = 0x4;
    private const int SilentEstate = 0x4000;

    private const string Taker = @"
        default {
            state_entry() { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""touched""); }
            control(key id, integer l, integer e) { }
        }";

    /// <summary>
    /// A taker running with controls taken from <paramref name="driver"/> and both records set, serialized as a take
    /// writes it, and rezzed again through the core with new item ids, owned by <paramref name="newOwner"/> when given.
    /// Returns the rezzed copy's script item once the restored script is loaded.
    /// </summary>
    private static TaskInventoryItem TakeAndRez(SchedulerHarness h, ScenePresence driver, UUID? newOwner, out SceneObjectGroup copy)
    {
        var asset = UUID.Random();
        var item = UUID.Random();
        var inv = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, asset, "taker", Taker);
        inv.PermsGranter = driver.UUID;
        inv.PermsMask = TakeControls | SilentEstate;
        SavedStateRig.PostRez(h, h.Prim, item, Taker, 0, false, 0);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        Assert.True(driver.HasScriptControls(item));
        var interp = (Interpreter)h.InterpreterFor(item);
        lock (interp.ScriptState.EventQueueLock)
            interp.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.SilentEstateManagement] = new object[] { 1 };

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
        Assert.Contains("Engine=\"InWorldz.Phlox\"", xml);
        copy = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        copy.ResetIDs();
        if (newOwner is UUID owner)
            foreach (SceneObjectPart p in copy.Parts)
            {
                p.OwnerID = owner;
                p.Inventory.ChangeInventoryOwner(owner);
            }
        copy.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(4, 0, 0);
        h.Scene.AddNewSceneObject(copy, false);
        TaskInventoryItem rezzed = copy.RootPart.Inventory.GetInventoryItems().Single();

        h.ClearSaid(item);
        copy.CreateScriptInstances(0, true, h.Engine.Name, NewRez);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(rezzed.ItemID) != null), "the rezzed script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h.Said);   // restored, not started fresh
        return rezzed;
    }

    private static void AssertNoGrantAndNoRecords(SchedulerHarness h, TaskInventoryItem rezzed)
    {
        Assert.Equal(0, rezzed.PermsMask);
        Assert.Equal(UUID.Zero, rezzed.PermsGranter);
        var misc = ((Interpreter)h.InterpreterFor(rezzed.ItemID)).ScriptState.MiscAttributes;
        Assert.False(misc.ContainsKey((int)RuntimeState.MiscAttr.Control), "the record of taken controls came back");
        Assert.False(misc.ContainsKey((int)RuntimeState.MiscAttr.SilentEstateManagement), "the silent-estate record came back");
    }

    /// <summary>A grant the new owner gives later, and that avatar arriving seated: the old controls are not taken.</summary>
    private static void AssertALaterGrantTakesNothing(SchedulerHarness h, SceneObjectGroup copy, TaskInventoryItem rezzed)
    {
        var newDriver = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        rezzed.PermsGranter = newDriver.UUID;
        rezzed.PermsMask = TakeControls;
        newDriver.AbsolutePosition = copy.AbsolutePosition + new Vector3(1, 1, 0);
        newDriver.HandleAgentRequestSit(newDriver.ControllingClient, newDriver.UUID, copy.RootPart.UUID, Vector3.Zero);
        Assert.Equal(copy, newDriver.ParentPart?.ParentGroup);
        h.Scene.EventManager.TriggerOnMakeRootAgent(newDriver);
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.False(newDriver.HasScriptControls(rezzed.ItemID), "a record from before the owner change took controls");
    }

    /// <summary>
    /// Given, sold or bought: the object comes back owned by someone else. No mask bit, no granter, no controls and no
    /// record comes back from the state it carried, and a grant the new owner gives later does not bring the old controls.
    /// </summary>
    [Fact]
    public void AnOwnerChangeBringsBackNoGrantNoRecordAndNoControls()
    {
        using var h = new SchedulerHarness();
        var driver = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        var rezzed = TakeAndRez(h, driver, UUID.Random(), out SceneObjectGroup copy);
        AssertNoGrantAndNoRecords(h, rezzed);
        Assert.False(driver.HasScriptControls(rezzed.ItemID));
        AssertALaterGrantTakesNothing(h, copy, rezzed);
    }

    /// <summary>
    /// The same owner takes the object and rezzes it: the core starts the script with no grant, and SL documents that a
    /// script loses PERMISSION_TAKE_CONTROLS when its object is deleted. The script keeps its state, not its grants.
    /// </summary>
    [Fact]
    public void TheSameOwnerRezzingItAgainGetsNoGrantBack()
    {
        using var h = new SchedulerHarness();
        var driver = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        var rezzed = TakeAndRez(h, driver, null, out SceneObjectGroup copy);
        AssertNoGrantAndNoRecords(h, rezzed);
        Assert.False(driver.HasScriptControls(rezzed.ItemID));
        AssertALaterGrantTakesNothing(h, copy, rezzed);
    }
}
