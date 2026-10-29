/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A list give to an avatar is all of the list or none of it (Scene.MoveTaskInventoryItemsAllOrNone), also when the
/// prim's inventory changes while the items are being added. The change is made from the scene's
/// OnNewInventoryItemUploadComplete event, which fires as each item lands, so it falls between the first item and
/// the second the way another script's llRemoveInventory or a permissions edit could.
/// </summary>
// Each test builds its own scene and subscribes only to that scene's event manager: the class runs in parallel.
public class GiveListAllOrNoneTests
{
    private readonly ITestOutputHelper _out;
    public GiveListAllOrNoneTests(ITestOutputHelper o) => _out = o;

    private const int IW_DELIVER_OK = 0;

    private static TaskInventoryItem AddGift(SchedulerHarness h, string name)
        => TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, name, UUID.Random(), UUID.Random(), "a gift");

    private string Run(SchedulerHarness h, string body, string marker)
    {
        h.RezScript("default { state_entry() { " + body + " } }");
        var until = DateTime.UtcNow.AddSeconds(30);   // a first compile on a loaded machine can take longer than 8 s
        while (DateTime.UtcNow < until && !h.Said.Any(s => s.StartsWith(marker))) h.PumpOnce();
        var debug = h.SaidOn.Where(s => s.Channel == 0x7FFFFFFF).Select(s => s.Message);
        _out.WriteLine($"said=[{string.Join(" | ", h.Said)}] debug=[{string.Join(" | ", debug)}]");
        return h.Said.FirstOrDefault(s => s.StartsWith(marker));
    }

    /// <summary>The names of the items in the user's folders named <paramref name="folderName"/>.</summary>
    private static List<string> GivenInto(SchedulerHarness h, UUID user, string folderName)
    {
        var names = new List<string>();
        foreach (var folder in (h.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>()).Where(f => f.Name == folderName))
            names.AddRange((h.Scene.InventoryService.GetFolderItems(user, folder.ID) ?? new List<InventoryItemBase>()).Select(i => i.Name));
        return names;
    }

    /// <summary>When the first item lands in <paramref name="user"/>'s inventory, apply <paramref name="change"/> to the second.</summary>
    private static void ChangeSecondWhenFirstLands(SchedulerHarness h, UUID user, Action<TaskInventoryItem> change, TaskInventoryItem second)
    {
        bool done = false;
        h.Scene.EventManager.OnNewInventoryItemUploadComplete += (item, _) =>
        {
            if (done || item.Owner != user) return;
            done = true;
            change(second);
        };
    }

    private static void StripTransfer(TaskInventoryItem item) => item.CurrentPermissions &= ~(uint)OpenSim.Framework.PermissionMask.Transfer;

    [Fact]
    public void ListToAPresentAvatarIsWholeWhenAnItemLosesTransferDuringTheGive()
    {
        using var h = new SchedulerHarness();
        var account = UserAccountHelpers.CreateUserWithInventory(h.Scene);
        var present = SceneHelpers.AddScenePresence(h.Scene, account.PrincipalID);
        AddGift(h, "listA1");
        var second = AddGift(h, "listA2");
        ChangeSecondWhenFirstLands(h, present.UUID, StripTransfer, second);

        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventoryList(LINK_THIS, \"{present.UUID}\", \"boxA\", [\"listA1\", \"listA2\"]));", "rc=");

        Assert.Equal("rc=" + IW_DELIVER_OK, line);
        Assert.Equal(new[] { "listA1", "listA2" }, GivenInto(h, present.UUID, "boxA").OrderBy(n => n));
    }

    [Fact]
    public void ListToAPresentAvatarIsWholeWhenAnItemIsRemovedDuringTheGive()
    {
        using var h = new SchedulerHarness();
        var account = UserAccountHelpers.CreateUserWithInventory(h.Scene);
        var present = SceneHelpers.AddScenePresence(h.Scene, account.PrincipalID);
        AddGift(h, "listB1");
        var second = AddGift(h, "listB2");
        ChangeSecondWhenFirstLands(h, present.UUID, item => h.Prim.Inventory.RemoveInventoryItem(item.ItemID), second);

        Run(h, $"llGiveInventoryList(\"{present.UUID}\", \"boxB\", [\"listB1\", \"listB2\"]); llSay(0, \"gave\");", "gave");

        Assert.Equal(new[] { "listB1", "listB2" }, GivenInto(h, present.UUID, "boxB").OrderBy(n => n));
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == 0x7FFFFFFF);
    }

    [Fact]
    public void ListToAnAbsentAvatarIsWholeWhenAnItemLosesTransferDuringTheGive()
    {
        using var h = new SchedulerHarness();
        var absent = UserAccountHelpers.CreateUserWithInventory(h.Scene);   // an account, never in this region
        AddGift(h, "listC1");
        var second = AddGift(h, "listC2");
        ChangeSecondWhenFirstLands(h, absent.PrincipalID, StripTransfer, second);

        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventoryList(LINK_THIS, \"{absent.PrincipalID}\", \"boxC\", [\"listC1\", \"listC2\"]));", "rc=");

        Assert.Equal("rc=" + IW_DELIVER_OK, line);
        Assert.Equal(new[] { "listC1", "listC2" }, GivenInto(h, absent.PrincipalID, "boxC").OrderBy(n => n));
    }

    [Fact]
    public void ListWithAnItemThatCannotBeGivenGivesNothingAndMakesNoFolder()
    {
        using var h = new SchedulerHarness();
        var account = UserAccountHelpers.CreateUserWithInventory(h.Scene);
        var present = SceneHelpers.AddScenePresence(h.Scene, account.PrincipalID);
        AddGift(h, "listD1");
        StripTransfer(AddGift(h, "listD2"));

        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventoryList(LINK_THIS, \"{present.UUID}\", \"boxD\", [\"listD1\", \"listD2\"]));", "rc=");

        Assert.NotEqual("rc=" + IW_DELIVER_OK, line);
        Assert.Empty(GivenInto(h, present.UUID, "boxD"));
        Assert.DoesNotContain(h.Scene.InventoryService.GetInventorySkeleton(present.UUID), f => f.Name == "boxD");
    }
}
