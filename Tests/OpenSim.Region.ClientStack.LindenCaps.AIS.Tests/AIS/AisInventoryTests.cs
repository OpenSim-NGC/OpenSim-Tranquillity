/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Linq;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The backend surface the fetch routes need, against an in-memory inventory: folder and item reads,
/// sub-folders, the COF resolve, link-target resolution and the depth walk. No Scene, no ScenePresence — the
/// composition is exactly what Phase 2 will reuse on Robust.
/// </summary>
public class AisInventoryTests
{
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly UUID Stranger = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Clothing = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Outfits = new("66666666-6666-4666-8666-666666666666");
    private static readonly UUID Party = new("77777777-7777-4777-8777-777777777771");
    private static readonly UUID Cof = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly UUID Shirt = new("22222222-2222-4222-8222-222222222222");
    private static readonly UUID Pants = new("22222222-2222-4222-8222-222222222223");
    private static readonly UUID LinkToShirt = new("88888888-8888-4888-8888-888888888888");
    private static readonly UUID LinkToPants = new("88888888-8888-4888-8888-888888888889");

    /// <summary>
    /// Root ├ Clothing (shirt, pants) ├ Outfits ├ Party  and a COF holding links that point at items in Clothing
    /// — i.e. links whose targets live in another folder.
    /// </summary>
    private static FakeAisBackend Inventory()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddFolder(Outfits, Clothing, "Outfits", 2);
        b.AddFolder(Party, Outfits, "Party", 5);
        b.AddFolder(Cof, Root, "Current Outfit", 11, (short)FolderType.CurrentOutfit);
        b.CurrentOutfitId = Cof;
        b.AddItem(Shirt, Clothing, "Blue Shirt");
        b.AddItem(Pants, Clothing, "Grey Pants");
        b.AddLink(LinkToShirt, Cof, "Blue Shirt", Shirt);
        b.AddLink(LinkToPants, Cof, "Grey Pants", Pants);
        return b;
    }

    // ---------------- primitives ----------------

    [Fact]
    public void get_item_and_get_folder_are_scoped_to_the_owner()
    {
        var b = Inventory();
        Assert.Equal("Blue Shirt", b.GetItem(Agent, Shirt)?.Name);
        Assert.Null(b.GetItem(Agent, UUID.Random()));
        Assert.True(b.GetItem(Stranger, Shirt) is null, "another agent must not read this inventory");
        Assert.Equal((ushort?)7, b.GetFolder(Agent, Clothing)?.Version);
        Assert.Null(b.GetFolder(Stranger, Clothing));
    }

    [Fact]
    public void get_sub_folders_returns_the_direct_children_only()
    {
        var b = Inventory();
        var subs = b.GetSubFolders(Agent, Clothing);
        Assert.Equivalent(new[] { Outfits }, subs.Select(f => f.ID), true);
        Assert.Empty(b.GetSubFolders(Agent, Party));
    }

    [Fact]
    public void the_cof_resolves_by_folder_type()
    {
        var b = Inventory();
        var cof = AisInventory.GetCurrentOutfit(b, Agent);
        Assert.Equal(Cof, cof?.ID);
        // A7: resolution moved from the service's own folders[0] to a deterministic scan of the skeleton, because
        // an agent can own more than one folder of a type and the service picks arbitrarily between them.
        Assert.Contains("GetInventorySkeleton", b.Calls);

        b.CurrentOutfitId = UUID.Zero;
        b.Folders.Remove(Cof);
        Assert.True(AisInventory.GetCurrentOutfit(b, Agent) is null, "an agent with no COF resolves to null, not an exception");
    }

    // ---------------- links ----------------

    [Fact]
    public void folder_contents_split_links_out_of_items()
    {
        var b = Inventory();
        var contents = AisInventory.GetContents(b, Agent, Cof);
        Assert.NotNull(contents);
        Assert.Empty(contents.Items);
        Assert.Equivalent(new[] { LinkToShirt, LinkToPants }, contents.Links.Select(l => l.ID), true);

        var clothing = AisInventory.GetContents(b, Agent, Clothing);
        Assert.Equivalent(new[] { Shirt, Pants }, clothing.Items.Select(i => i.ID), true);
        Assert.Empty(clothing.Links);
        Assert.Null(AisInventory.GetContents(b, Agent, UUID.Random()));
    }

    [Fact]
    public void link_targets_in_another_folder_are_resolved_in_one_batched_call()
    {
        var b = Inventory();
        var contents = AisInventory.GetContents(b, Agent, Cof);
        b.Calls.Clear();

        var targets = AisInventory.ResolveLinkTargets(b, Agent, contents.Links);

        Assert.Equivalent(new[] { Shirt, Pants }, targets.Select(t => t.ID), true);
        Assert.Equal(1, b.Calls.Count(c => c.StartsWith("GetItems")));
        Assert.Contains("GetItems[2]", b.Calls);
    }

    [Fact]
    public void a_broken_link_resolves_to_nothing_and_a_link_to_a_link_is_dropped()
    {
        var b = Inventory();
        var broken = new UUID("99999999-9999-4999-8999-999999999991");
        b.AddLink(broken, Cof, "gone", UUID.Random());          // target does not exist
        var chained = new UUID("99999999-9999-4999-8999-999999999992");
        b.AddLink(chained, Cof, "link to a link", LinkToShirt); // target is itself a link

        var contents = AisInventory.GetContents(b, Agent, Cof);
        var targets = AisInventory.ResolveLinkTargets(b, Agent, contents.Links);

        Assert.Equivalent(new[] { Shirt, Pants }, targets.Select(t => t.ID), true);
    }

    // ---------------- depth ----------------

    [Fact]
    public void walk_expands_exactly_the_requested_number_of_generations()
    {
        var b = Inventory();

        var d0 = AisInventory.Walk(b, Agent, Clothing, 0);
        Assert.Equal(new[] { Clothing }, d0.Select(c => c.Folder.ID));

        var d1 = AisInventory.Walk(b, Agent, Clothing, 1);
        Assert.Equal(new[] { Clothing, Outfits }, d1.Select(c => c.Folder.ID));

        var d2 = AisInventory.Walk(b, Agent, Clothing, 2);
        Assert.Equal(new[] { Clothing, Outfits, Party }, d2.Select(c => c.Folder.ID));

        var d5 = AisInventory.Walk(b, Agent, Clothing, 5);
        Assert.Equal(new[] { Clothing, Outfits, Party }, d5.Select(c => c.Folder.ID));

        Assert.Empty(AisInventory.Walk(b, Agent, UUID.Random(), 3));
    }

    // ---------------- orphans ----------------

    [Fact]
    public void orphans_are_the_folders_whose_parent_is_gone()
    {
        var b = Inventory();
        Assert.Empty(AisInventory.FindOrphans(b, Agent).Folders);

        b.Folders.Remove(Outfits);   // Party's parent disappears
        var orphans = AisInventory.FindOrphans(b, Agent);
        Assert.Equivalent(new[] { Party }, orphans.Folders.Select(f => f.ID), true);
        Assert.Empty(orphans.Items);
    }
}
