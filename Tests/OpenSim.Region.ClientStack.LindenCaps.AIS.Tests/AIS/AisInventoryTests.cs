using System.Linq;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The backend surface the fetch routes need (A1 Part 1), against an in-memory inventory: folder and item reads,
/// sub-folders, the COF resolve, link-target resolution and the depth walk. No Scene, no ScenePresence — the
/// composition is exactly what Phase 2 will reuse on Robust (Ledger P-2).
/// </summary>
[TestFixture]
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

    [Test]
    public void get_item_and_get_folder_are_scoped_to_the_owner()
    {
        var b = Inventory();
        Assert.That(b.GetItem(Agent, Shirt)?.Name, Is.EqualTo("Blue Shirt"));
        Assert.That(b.GetItem(Agent, UUID.Random()), Is.Null);
        Assert.That(b.GetItem(Stranger, Shirt), Is.Null, "another agent must not read this inventory");
        Assert.That(b.GetFolder(Agent, Clothing)?.Version, Is.EqualTo(7), "the folder version is read fresh (T4)");
        Assert.That(b.GetFolder(Stranger, Clothing), Is.Null);
    }

    [Test]
    public void get_sub_folders_returns_the_direct_children_only()
    {
        var b = Inventory();
        var subs = b.GetSubFolders(Agent, Clothing);
        Assert.That(subs.Select(f => f.ID), Is.EquivalentTo(new[] { Outfits }));
        Assert.That(b.GetSubFolders(Agent, Party), Is.Empty);
    }

    [Test]
    public void the_cof_resolves_by_folder_type()
    {
        var b = Inventory();
        var cof = AisInventory.GetCurrentOutfit(b, Agent);
        Assert.That(cof?.ID, Is.EqualTo(Cof));
        // A7: resolution moved from the service's own folders[0] to a deterministic scan of the skeleton, because
        // an agent can own more than one folder of a type and the service picks arbitrarily between them.
        Assert.That(b.Calls, Does.Contain("GetInventorySkeleton"), "resolved by folder type over the skeleton (T2)");

        b.CurrentOutfitId = UUID.Zero;
        b.Folders.Remove(Cof);
        Assert.That(AisInventory.GetCurrentOutfit(b, Agent), Is.Null, "an agent with no COF resolves to null, not an exception");
    }

    // ---------------- links ----------------

    [Test]
    public void folder_contents_split_links_out_of_items()
    {
        var b = Inventory();
        var contents = AisInventory.GetContents(b, Agent, Cof);
        Assert.That(contents, Is.Not.Null);
        Assert.That(contents.Items, Is.Empty, "the COF holds only links");
        Assert.That(contents.Links.Select(l => l.ID), Is.EquivalentTo(new[] { LinkToShirt, LinkToPants }));

        var clothing = AisInventory.GetContents(b, Agent, Clothing);
        Assert.That(clothing.Items.Select(i => i.ID), Is.EquivalentTo(new[] { Shirt, Pants }));
        Assert.That(clothing.Links, Is.Empty);
        Assert.That(AisInventory.GetContents(b, Agent, UUID.Random()), Is.Null);
    }

    [Test]
    public void link_targets_in_another_folder_are_resolved_in_one_batched_call()
    {
        var b = Inventory();
        var contents = AisInventory.GetContents(b, Agent, Cof);
        b.Calls.Clear();

        var targets = AisInventory.ResolveLinkTargets(b, Agent, contents.Links);

        Assert.That(targets.Select(t => t.ID), Is.EquivalentTo(new[] { Shirt, Pants }),
            "the targets live in Clothing, not in the folder the links are in");
        Assert.That(b.Calls.Count(c => c.StartsWith("GetItems")), Is.EqualTo(1),
            "one GetMultipleItems for every link, as FetchInvDescHandler.ProcessLinks does (T5)");
        Assert.That(b.Calls, Does.Contain("GetItems[2]"));
    }

    [Test]
    public void a_broken_link_resolves_to_nothing_and_a_link_to_a_link_is_dropped()
    {
        var b = Inventory();
        var broken = new UUID("99999999-9999-4999-8999-999999999991");
        b.AddLink(broken, Cof, "gone", UUID.Random());          // target does not exist
        var chained = new UUID("99999999-9999-4999-8999-999999999992");
        b.AddLink(chained, Cof, "link to a link", LinkToShirt); // target is itself a link

        var contents = AisInventory.GetContents(b, Agent, Cof);
        var targets = AisInventory.ResolveLinkTargets(b, Agent, contents.Links);

        Assert.That(targets.Select(t => t.ID), Is.EquivalentTo(new[] { Shirt, Pants }),
            "broken links resolve to nothing; links to links are dropped as the descendents cap drops them");
    }

    // ---------------- depth ----------------

    [Test]
    public void walk_expands_exactly_the_requested_number_of_generations()
    {
        var b = Inventory();

        var d0 = AisInventory.Walk(b, Agent, Clothing, 0);
        Assert.That(d0.Select(c => c.Folder.ID), Is.EqualTo(new[] { Clothing }), "depth 0 expands the folder itself only");

        var d1 = AisInventory.Walk(b, Agent, Clothing, 1);
        Assert.That(d1.Select(c => c.Folder.ID), Is.EqualTo(new[] { Clothing, Outfits }));

        var d2 = AisInventory.Walk(b, Agent, Clothing, 2);
        Assert.That(d2.Select(c => c.Folder.ID), Is.EqualTo(new[] { Clothing, Outfits, Party }),
            "depth 2 reaches the grandchild");

        var d5 = AisInventory.Walk(b, Agent, Clothing, 5);
        Assert.That(d5.Select(c => c.Folder.ID), Is.EqualTo(new[] { Clothing, Outfits, Party }),
            "a depth deeper than the tree stops at the tree");

        Assert.That(AisInventory.Walk(b, Agent, UUID.Random(), 3), Is.Empty, "an unknown folder walks to nothing");
    }

    // ---------------- orphans ----------------

    [Test]
    public void orphans_are_the_folders_whose_parent_is_gone()
    {
        var b = Inventory();
        Assert.That(AisInventory.FindOrphans(b, Agent).Folders, Is.Empty, "a consistent tree has no orphans");

        b.Folders.Remove(Outfits);   // Party's parent disappears
        var orphans = AisInventory.FindOrphans(b, Agent);
        Assert.That(orphans.Folders.Select(f => f.ID), Is.EquivalentTo(new[] { Party }));
        Assert.That(orphans.Items, Is.Empty, "orphaned items are not reported: the service has no query for them");
    }
}
