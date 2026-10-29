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

    // ── the grant a worn script carries ──────────────────────────────────────

    private const int Debit = 0x2, TakeControls = 0x4, TriggerAnimation = 0x10, Attach = 0x20, ControlCamera = 0x800;
    private const int Asked = TakeControls | TriggerAnimation | Attach;

    /// <summary>Asks its wearer for what SL grants a wearer silently, once, and takes the controls with the answer.</summary>
    private const string Wearer = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            attach(key id) {
                if (id != NULL_KEY) {
                    llSay(0, ""attach perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey());
                    if (llGetPermissions() == 0)
                        llRequestPermissions(id, PERMISSION_TAKE_CONTROLS | PERMISSION_TRIGGER_ANIMATION | PERMISSION_ATTACH);
                }
            }
            run_time_permissions(integer p) {
                llSay(0, ""rtp="" + (string)p);
                if (p & PERMISSION_TAKE_CONTROLS) llTakeControls(CONTROL_FWD, TRUE, FALSE);
            }
            control(key id, integer l, integer e) { }
        }";

    private static SchedulerHarness WithAttachments()
    {
        var h = new SchedulerHarness();
        var config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        SceneHelpers.SetupSceneModules(h.Scene, config, new AttachmentsModule(), new BasicInventoryAccessModule());
        typeof(Scene).GetField("m_defaultScriptEngine", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Scene, "InWorldz.Phlox");
        return h;
    }

    /// <summary>A core call made on a region thread while the scheduler runs on its own, as in a region.</summary>
    private static void OnRegionThread(SchedulerHarness h, Action call)
    {
        SetMasterRunning(h, true);
        var stop = new ManualResetEventSlim(false);
        var scheduler = new Thread(() => { while (!stop.IsSet) h.PumpOnce(); }) { IsBackground = true };
        try
        {
            scheduler.Start();
            CarriedStateTests.OnOwnThread(() => { call(); return 0; });
        }
        finally
        {
            stop.Set();
            scheduler.Join();
            SetMasterRunning(h, false);
        }
    }

    /// <summary>
    /// The owner wears the object; its script asks silently and takes the controls; debit is then granted by a dialog.
    /// Returns the worn object; <paramref name="invItem"/> is the owner's inventory item for it.
    /// </summary>
    private static SceneObjectGroup WearAndGrant(SchedulerHarness h, ScenePresence sp, out InventoryItemBase invItem)
    {
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, sp.UUID, "worn", 0x10);
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, so.RootPart, "wearer script", Wearer);
        invItem = UserInventoryHelpers.AddInventoryItem(h.Scene, so, 0x100, 0x1000);
        var worn = (SceneObjectGroup)h.Scene.AttachmentsModule.RezSingleAttachmentFromInventory(sp, invItem.ID, (uint)AttachmentPoint.Chest);
        UUID first = ScriptOf(worn);
        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=" + Asked)), SavedStateRig.SaidText(h));
        Assert.True(h.PumpUntil(() => sp.HasScriptControls(first)), "the first wear took no controls");
        TaskInventoryItem item = worn.RootPart.Inventory.GetInventoryItem(first);
        item.PermsMask |= Debit;   // as a dialog answer adds it
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        return worn;
    }

    /// <summary>
    /// A logout and a login (the core's DeRezAttachments, then RezAttachments from the appearance), the way worn
    /// objects also travel by teleport: every bit a wearer is given silently comes back with the controls, and no
    /// run_time_permissions is posted. Debit needs a dialog, so it does not come back: the script asks again.
    /// </summary>
    [Fact]
    public void ALogoutAndWearAgainGivesBackTheSilentBitsAndTheControlsButNotDebit()
    {
        using var h = WithAttachments();
        UserAccount user = UserAccountHelpers.CreateUserWithInventory(h.Scene, TestHelpers.ParseTail(0x2));
        ScenePresence sp = SceneHelpers.AddScenePresence(h.Scene, user);
        var worn = WearAndGrant(h, sp, out InventoryItemBase invItem);
        Assert.Equal(Asked | Debit, worn.RootPart.Inventory.GetInventoryItem(ScriptOf(worn)).PermsMask);

        OnRegionThread(h, () => h.Scene.AttachmentsModule.DeRezAttachments(sp));
        Assert.Contains("SavedScriptState", System.Text.Encoding.UTF8.GetString(
            h.Scene.AssetService.Get(h.Scene.InventoryService.GetItem(sp.UUID, invItem.ID).AssetID.ToString()).Data));

        // The login: the core wears again what the avatar's appearance lists (AttachmentsModule.RezAttachments).
        h.Scene.AttachmentsModule.RezAttachments(sp);
        var again = sp.GetAttachments().Single();
        UUID second = ScriptOf(again);
        string expect = "attach perms=" + Asked + " key=" + sp.UUID;
        Assert.True(h.PumpUntil(() => h.Said.Contains(expect)), SavedStateRig.SaidText(h));
        Assert.True(h.PumpUntil(() => sp.HasScriptControls(second)), "the controls did not come back");
        TaskInventoryItem item = again.RootPart.Inventory.GetInventoryItem(second);
        Assert.Equal(Asked, item.PermsMask);
        Assert.Equal(sp.UUID, item.PermsGranter);
        Assert.Equal(1, h.Said.Count(s => s == "rtp=" + Asked));   // the first wear's only
        Assert.DoesNotContain("entry", h.Said.SkipWhile(s => s != expect));
    }

    /// <summary>
    /// A detach into inventory and a wear again: the core takes PERMISSION_TAKE_CONTROLS and PERMISSION_CONTROL_CAMERA
    /// from every script item before it saves the object (AttachmentsModule.DetachSingleAttachmentToInv,
    /// RemoveScriptsPermissions(4 | 2048)), as SL's script "will also lose this permission ... if the object is ...
    /// detached" (llTakeControls). The other silent bits come back; the controls do not.
    /// </summary>
    [Fact]
    public void ADetachAndWearAgainGivesBackTheSilentBitsTheDetachLeft()
    {
        using var h = WithAttachments();
        UserAccount user = UserAccountHelpers.CreateUserWithInventory(h.Scene, TestHelpers.ParseTail(0x3));
        ScenePresence sp = SceneHelpers.AddScenePresence(h.Scene, user);
        var worn = WearAndGrant(h, sp, out InventoryItemBase invItem);

        OnRegionThread(h, () => h.Scene.AttachmentsModule.DetachSingleAttachmentToInv(sp, worn));
        var again = (SceneObjectGroup)h.Scene.AttachmentsModule.RezSingleAttachmentFromInventory(sp, invItem.ID, (uint)AttachmentPoint.Chest);
        UUID second = ScriptOf(again);
        int left = Asked & ~(TakeControls | ControlCamera);
        Assert.True(h.PumpUntil(() => h.Said.Contains("attach perms=" + left + " key=" + sp.UUID)), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.False(sp.HasScriptControls(second));
        Assert.Equal(left, again.RootPart.Inventory.GetInventoryItem(second).PermsMask);
    }

    /// <summary>
    /// Given to another resident, who wears it: the object's owner is the new one (the core's rez from inventory changes
    /// it), so nothing of the first owner's grant comes back; the script asks its new wearer.
    /// </summary>
    [Fact]
    public void GivenToAnotherResidentWhoWearsItNothingOfTheFirstOwnersComesBack()
    {
        using var h = WithAttachments();
        UserAccount first = UserAccountHelpers.CreateUserWithInventory(h.Scene, TestHelpers.ParseTail(0x4));
        UserAccount second = UserAccountHelpers.CreateUserWithInventory(h.Scene, TestHelpers.ParseTail(0x5));
        ScenePresence sp1 = SceneHelpers.AddScenePresence(h.Scene, first);
        ScenePresence sp2 = SceneHelpers.AddScenePresence(h.Scene, second);
        WearAndGrant(h, sp1, out InventoryItemBase invItem);
        OnRegionThread(h, () => h.Scene.AttachmentsModule.DeRezAttachments(sp1));
        InventoryItemBase saved = h.Scene.InventoryService.GetItem(sp1.UUID, invItem.ID);

        // The give: a copy of the item in the second resident's inventory, the same saved asset.
        var given = new InventoryItemBase
        {
            ID = UUID.Random(), Name = saved.Name, AssetID = saved.AssetID, Owner = sp2.UUID,
            AssetType = saved.AssetType, InvType = saved.InvType,
            BasePermissions = saved.BasePermissions, CurrentPermissions = saved.CurrentPermissions,
            NextPermissions = saved.NextPermissions, EveryOnePermissions = saved.EveryOnePermissions,
            Folder = h.Scene.InventoryService.GetFolderForType(sp2.UUID, FolderType.Object).ID,
        };
        h.Scene.AddInventoryItem(given);

        var worn = (SceneObjectGroup)h.Scene.AttachmentsModule.RezSingleAttachmentFromInventory(sp2, given.ID, (uint)AttachmentPoint.Chest);
        UUID script = ScriptOf(worn);
        Assert.Equal(sp2.UUID, worn.OwnerID);
        Assert.True(h.PumpUntil(() => h.Said.Contains("attach perms=0 key=" + UUID.Zero)), SavedStateRig.SaidText(h));
        Assert.True(h.PumpUntil(() => sp2.HasScriptControls(script)), "the new wearer was not asked");
        TaskInventoryItem item = worn.RootPart.Inventory.GetInventoryItem(script);
        Assert.Equal(sp2.UUID, item.PermsGranter);
        Assert.Equal(Asked, item.PermsMask);
        Assert.False(sp1.HasScriptControls(script));
    }
}
