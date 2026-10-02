/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A worn object is detached into inventory and worn again through the core's AttachmentsModule (detach:
/// PrepareScriptInstanceForSave, SaveScriptedState, the item's asset updated; attach: RezSingleAttachmentFromInventory).
/// The script keeps its state: on_rez, then attach, and no state_entry. SL: "A script will NOT automatically re-enter
/// the default state state_entry event when the task is rezzed or attached" (wiki, State); "on_rez will be triggered
/// prior to attach when attaching from inventory" (wiki, on_rez).
/// </summary>
// Runs in parallel: its own harness, avatar, inventory and assets; nothing process-wide is changed.
public class AttachCarriedStateTests
{
    private const string Worn = @"
        integer n;
        default {
            state_entry() { n = 5; llSay(0, ""entry""); }
            touch_start(integer t) { n++; llSay(0, ""n="" + (string)n); }
            on_rez(integer p) { llSay(0, ""rez n="" + (string)n); }
            attach(key id) { if (id != NULL_KEY) llSay(0, ""attach n="" + (string)n); }
        }";

    private static void SetMasterRunning(SchedulerHarness h, bool running)
    {
        var ms = SavedStateRig.Field(h.Engine, "m_MasterScheduler");
        ms.GetType().GetField("m_Stop", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ms, !running);
    }

    private static UUID ScriptOf(SceneObjectGroup so) => so.RootPart.Inventory.GetInventoryItems(InventoryType.LSL).Single().ItemID;

    [Fact]
    public void ADetachAndAttachAgainKeepsTheScriptsState()
    {
        using var h = new SchedulerHarness();
        var config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        SceneHelpers.SetupSceneModules(h.Scene, config, new AttachmentsModule(), new BasicInventoryAccessModule());
        // A region whose default engine is Phlox ([Startup] DefaultScriptEngine): the core starts a worn object's scripts
        // with that engine's name.
        typeof(Scene).GetField("m_defaultScriptEngine", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Scene, "InWorldz.Phlox");

        UserAccount user = UserAccountHelpers.CreateUserWithInventory(h.Scene, TestHelpers.ParseTail(0x1));
        ScenePresence sp = SceneHelpers.AddScenePresence(h.Scene, user);
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, sp.UUID, "worn", 0x10);
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, so.RootPart, "worn script", Worn);
        InventoryItemBase invItem = UserInventoryHelpers.AddInventoryItem(h.Scene, so, 0x100, 0x1000);

        var worn = (SceneObjectGroup)h.Scene.AttachmentsModule.RezSingleAttachmentFromInventory(sp, invItem.ID, (uint)AttachmentPoint.Chest);
        UUID first = ScriptOf(worn);
        Assert.True(h.PumpUntil(() => h.Said.Contains("attach n=5")), SavedStateRig.SaidText(h));
        h.PostTouch(first);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=6")), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));

        // Detach from a region thread while the scheduler runs on its own, as in a region.
        SetMasterRunning(h, true);
        var stop = new ManualResetEventSlim(false);
        var scheduler = new Thread(() => { while (!stop.IsSet) h.PumpOnce(); }) { IsBackground = true };
        try
        {
            scheduler.Start();
            CarriedStateTests.OnOwnThread(() => { h.Scene.AttachmentsModule.DetachSingleAttachmentToInv(sp, worn); return 0; });
        }
        finally
        {
            stop.Set();
            scheduler.Join();
            SetMasterRunning(h, false);
        }
        Assert.Contains("SavedScriptState", System.Text.Encoding.UTF8.GetString(
            h.Scene.AssetService.Get(h.Scene.InventoryService.GetItem(sp.UUID, invItem.ID).AssetID.ToString()).Data));

        h.ClearSaid(first);
        var again = (SceneObjectGroup)h.Scene.AttachmentsModule.RezSingleAttachmentFromInventory(sp, invItem.ID, (uint)AttachmentPoint.Chest);
        UUID second = ScriptOf(again);
        Assert.True(h.PumpUntil(() => h.Said.Contains("attach n=6")), SavedStateRig.SaidText(h));
        var said = h.Said.ToList();
        Assert.True(said.IndexOf("rez n=6") >= 0 && said.IndexOf("rez n=6") < said.IndexOf("attach n=6"), SavedStateRig.SaidText(h));
        Assert.DoesNotContain("entry", said);
        h.PostTouch(second);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=7")), SavedStateRig.SaidText(h));
    }
}
