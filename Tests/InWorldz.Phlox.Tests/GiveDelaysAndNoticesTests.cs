/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Gives. The delays: llGiveInventory "If destination is an avatar the script sleeps for 2.0 seconds. (Giving to
/// objects or attachments has no delay)" (SL wiki), as Halcyon's _GiveInventory (LSLSystemAPI.cs:5351), which
/// iwGiveLinkInventory (2 s) and iwDeliverInventory (100 ms) share; llGiveInventoryList "causes the script to sleep
/// for 3.0 seconds" (SL wiki, no exception for prims) on every path; iwGiveLinkInventoryList and iwDeliverInventoryList
/// wait only for an avatar (Halcyon :8469-8473). A list give to an avatar in the region sends the TaskInventoryOffered
/// notice, and an item that cannot be given aborts the give (Halcyon :8478-8501). List entries may be task item keys
/// (Halcyon :8453-8458); a missing entry is shouted on DEBUG_CHANNEL and the rest given (SL). iwGiveLinkInventoryList
/// gives one folder, from the first prim that holds a listed item (Halcyon :8511-8518). The notice comes from the
/// object's root prim (Halcyon :5419-5427).
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class GiveDelaysAndNoticesTests
{
    private const int IW_DELIVER_OK = 0, IW_DELIVER_PRIM = 4, IW_DELIVER_PERM = 6;

    private static ScenePresence Present(InventoryGivesRig r, ConcurrentQueue<GridInstantMessage> ims = null)
    {
        var account = UserAccountHelpers.CreateUserWithInventory(r.H.Scene);
        var sp = SceneHelpers.AddScenePresence(r.H.Scene, account.PrincipalID);
        if (ims != null) ((TestClient)sp.ControllingClient).OnReceivedInstantMessage += im => ims.Enqueue(im);
        return sp;
    }

    private static SceneObjectPart Box(InventoryGivesRig r)
        => SceneHelpers.AddSceneObject(r.H.Scene, "box", r.H.Prim.OwnerID).RootPart;

    private static int Count(InventoryGivesRig r, UUID user, string name)
    {
        int n = 0;
        foreach (var folder in r.H.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>())
            n += r.H.Scene.InventoryService.GetFolderItems(user, folder.ID)?.Count(i => i.Name == name) ?? 0;
        return n;
    }

    private static int Folders(InventoryGivesRig r, UUID user, string name)
        => (r.H.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>()).Count(f => f.Name == name);

    [Fact]
    public void SingleGivesWaitOnlyForAnAvatar()
    {
        using var r = new InventoryGivesRig();
        var box = Box(r);
        var sp = Present(r);
        for (int i = 0; i < 6; i++) AddNotecard(r.H, r.H.Prim, "g" + i);
        Assert.Equal(0, r.Accounted(a => { a.llGiveInventory(box.UUID.ToString(), "g0"); return null; }).Ms);
        Assert.Equal(2000, r.Accounted(a => { a.llGiveInventory(sp.UUID.ToString(), "g1"); return null; }).Ms);
        Assert.Equal(0, r.Accounted(a => { a.iwGiveLinkInventory(1, box.UUID.ToString(), "g2"); return null; }).Ms);
        Assert.Equal(2000, r.Accounted(a => { a.iwGiveLinkInventory(1, sp.UUID.ToString(), "g3"); return null; }).Ms);
        Assert.Equal((0, (object)IW_DELIVER_OK), r.Accounted(a => { a.iwDeliverInventory(1, box.UUID.ToString(), "g4"); return null; }));
        Assert.Equal((100, (object)IW_DELIVER_OK), r.Accounted(a => { a.iwDeliverInventory(1, sp.UUID.ToString(), "g5"); return null; }));
        Assert.NotNull(box.Inventory.GetInventoryItem("g0"));
        Assert.Equal(1, Count(r, sp.UUID, "g1"));
    }

    [Fact]
    public void LlGiveInventoryListSleepsThreeSecondsOnEveryPath()
    {
        using var r = new InventoryGivesRig();
        var box = Box(r);
        AddNotecard(r.H, r.H.Prim, "g");
        Assert.Equal(3000, r.Accounted(a => { a.llGiveInventoryList(box.UUID.ToString(), "f", L("g")); return null; }).Ms);
        Assert.Equal(3000, r.Accounted(a => { a.llGiveInventoryList(box.UUID.ToString(), "f", L("nothing")); return null; }).Ms);
        Assert.Equal(3000, r.Accounted(a => { a.llGiveInventoryList("not a key", "f", L("g")); return null; }).Ms);
        Assert.NotNull(box.Inventory.GetInventoryItem("g"));
    }

    [Fact]
    public void LinkAndDeliverListGivesWaitOnlyForAnAvatar()
    {
        using var r = new InventoryGivesRig();
        var box = Box(r);
        var sp = Present(r);
        for (int i = 0; i < 4; i++) AddNotecard(r.H, r.H.Prim, "g" + i);
        Assert.Equal(0, r.Accounted(a => { a.iwGiveLinkInventoryList(1, box.UUID.ToString(), "f", L("g0")); return null; }).Ms);
        Assert.Equal(3000, r.Accounted(a => { a.iwGiveLinkInventoryList(1, sp.UUID.ToString(), "f", L("g1")); return null; }).Ms);
        Assert.Equal((0, (object)IW_DELIVER_OK), r.Accounted(a => { a.iwDeliverInventoryList(1, box.UUID.ToString(), "f", L("g2")); return null; }));
        Assert.Equal((100, (object)IW_DELIVER_OK), r.Accounted(a => { a.iwDeliverInventoryList(1, sp.UUID.ToString(), "f", L("g3")); return null; }));
        Assert.NotNull(box.Inventory.GetInventoryItem("g0"));
        Assert.Equal(1, Count(r, sp.UUID, "g1"));
    }

    [Fact]
    public void AListGiveToAnAvatarHereSendsTheOfferNoticeFromTheRootPrim()
    {
        using var r = new InventoryGivesRig();
        var child = r.AddChild("child prim");
        var ims = new ConcurrentQueue<GridInstantMessage>();
        var sp = Present(r, ims);
        AddNotecard(r.H, child, "g");
        r.Accounted(a => { a.iwGiveLinkInventoryList(2, sp.UUID.ToString(), "folder", L("g")); return null; });
        Assert.Equal(1, Count(r, sp.UUID, "g"));
        var im = Assert.Single(ims);
        Assert.Equal((byte)InstantMessageDialog.TaskInventoryOffered, im.dialog);
        Assert.Equal(r.H.Prim.ParentGroup.RootPart.Name, im.fromAgentName);
        Assert.StartsWith("'folder'", im.message);
    }

    [Fact]
    public void AnItemThatCannotBeGivenAbortsTheListGive()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        AddNotecard(r.H, r.H.Prim, "ok");
        var locked = AddNotecard(r.H, r.H.Prim, "locked");
        locked.CurrentPermissions = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Modify);   // no transfer
        var (ms, rc) = r.Accounted(a => { a.iwDeliverInventoryList(1, sp.UUID.ToString(), "f", L("ok", "locked")); return null; });
        Assert.Equal(IW_DELIVER_PERM, rc);
        Assert.Equal(100, ms);
        Assert.Equal(0, Count(r, sp.UUID, "ok"));
        Assert.Equal(0, Folders(r, sp.UUID, "f"));
    }

    [Fact]
    public void ListEntriesMayBeItemKeysAndAMissingEntryIsShoutedWhileTheRestIsGiven()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var byKey = AddNotecard(r.H, r.H.Prim, "by key");
        AddNotecard(r.H, r.H.Prim, "by name");
        r.Accounted(a => { a.llGiveInventoryList(sp.UUID.ToString(), "f", L(byKey.ItemID.ToString(), "by name", "nope")); return null; });
        Assert.Equal(1, Count(r, sp.UUID, "by key"));
        Assert.Equal(1, Count(r, sp.UUID, "by name"));
        Assert.Equal(new[] { "Script error: Could not find item 'nope'" }, r.Errors);
    }

    [Fact]
    public void IwGiveLinkInventoryListGivesOneFolderFromTheFirstPrimHoldingAListedItem()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var c2 = r.AddChild("c2");
        var c3 = r.AddChild("c3");
        AddNotecard(r.H, c2, "g");
        AddNotecard(r.H, c3, "g");
        r.Accounted(a => { a.iwGiveLinkInventoryList(-1, sp.UUID.ToString(), "f", L("g")); return null; });   // LINK_SET
        Assert.Equal(1, Folders(r, sp.UUID, "f"));
        Assert.Equal(1, Count(r, sp.UUID, "g"));
    }

    [Fact]
    public void ABadKeyToALinkWithNoPrimIsDeliverPrim()
    {
        using var r = new InventoryGivesRig();
        Assert.Equal(IW_DELIVER_PRIM, r.Accounted(a => { a.iwDeliverInventory(9, "not a key", "g"); return null; }).Ret);
    }
}
