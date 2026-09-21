using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// One test per URL shape in Docs/feature/ais-v3/AIS-V3-SPEC.md §1a (the viewer's llaisapi.cpp request builders),
/// plus the "current" alias (§1b) and the cap-prefix / rejection cases.
/// </summary>
[TestFixture]
public class AisRouterTests
{
    private static readonly UUID Cat = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Item = new("22222222-2222-4222-8222-222222222222");
    private static readonly UUID Tid = new("33333333-3333-4333-8333-333333333333");
    private static readonly UUID C1 = new("44444444-4444-4444-8444-444444444444");
    private static readonly UUID C2 = new("55555555-5555-4555-8555-555555555555");

    [Test] // §1a #1, llaisapi.cpp:115
    public void CreateInventory_is_POST_category_parent_with_tid()
    {
        var r = AisRouter.Parse("POST", $"/category/{Cat}?tid={Tid}");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.CreateInventory));
        Assert.That(r.Id, Is.EqualTo(Cat));
        Assert.That(r.Tid, Is.EqualTo(Tid));
        Assert.That(r.IsAlias, Is.False);
    }

    [Test] // §1a #2, :161
    public void SlamFolder_is_PUT_category_id_links_with_tid()
    {
        var r = AisRouter.Parse("PUT", $"/category/{Cat}/links?tid={Tid}");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.SlamFolder));
        Assert.That(r.Id, Is.EqualTo(Cat));
        Assert.That(r.Tid, Is.EqualTo(Tid));
    }

    [Test] // §1a #3, :197
    public void RemoveCategory_is_DELETE_category_id()
    {
        var r = AisRouter.Parse("DELETE", $"/category/{Cat}");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.RemoveCategory));
        Assert.That(r.Id, Is.EqualTo(Cat));
    }

    [Test] // §1a #4, :234
    public void RemoveItem_is_DELETE_item_id()
    {
        var r = AisRouter.Parse("DELETE", $"/item/{Item}");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.RemoveItem));
        Assert.That(r.Id, Is.EqualTo(Item));
    }

    [Test] // §1a #5, :275-278: the viewer appends ",depth=0" to the tid VALUE
    public void CopyLibraryCategory_is_COPY_category_with_tid_and_comma_depth()
    {
        var r = AisRouter.Parse("COPY", $"/category/{Cat}?tid={Tid},depth=0");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.CopyCategory));
        Assert.That(r.Id, Is.EqualTo(Cat));
        Assert.That(r.Tid, Is.EqualTo(Tid), "the tid must be parsed off the comma-suffixed value");
        Assert.That(r.Depth, Is.EqualTo(0));
        var full = AisRouter.Parse("COPY", $"/category/{Cat}?tid={Tid}");
        Assert.That(full.Depth, Is.EqualTo(-1), "no depth means copy subfolders");
    }

    [Test] // §1a #6, :318
    public void PurgeDescendents_is_DELETE_category_id_children()
    {
        var r = AisRouter.Parse("DELETE", $"/category/{Cat}/children");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.PurgeDescendents));
        Assert.That(r.Id, Is.EqualTo(Cat));
    }

    [Test] // §1a #7, :355
    public void UpdateCategory_is_PATCH_category_id()
    {
        Assert.That(AisRouter.Parse("PATCH", $"/category/{Cat}").Operation, Is.EqualTo(AisOperation.UpdateCategory));
    }

    [Test] // §1a #8, :391
    public void UpdateItem_is_PATCH_item_id()
    {
        Assert.That(AisRouter.Parse("PATCH", $"/item/{Item}").Operation, Is.EqualTo(AisOperation.UpdateItem));
    }

    [Test] // §1a #9, :426
    public void FetchItem_is_GET_item_id()
    {
        var r = AisRouter.Parse("GET", $"/item/{Item}");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchItem));
        Assert.That(r.Id, Is.EqualTo(Item));
    }

    [Test] // §1a #10, :461-474
    public void FetchCategoryChildren_is_GET_category_id_children_with_depth()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/children?depth=50");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCategoryChildren));
        Assert.That(r.Depth, Is.EqualTo(50));
        Assert.That(r.Children, Is.Empty);
        Assert.That(AisRouter.Parse("GET", $"/category/{Cat}/children").Depth, Is.EqualTo(-1));
    }

    [Test] // §1a #11, :514: any identifier string, including the alias
    public void FetchCategoryChildren_by_identifier_accepts_current()
    {
        var r = AisRouter.Parse("GET", "/category/current/children?depth=0");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCategoryChildren));
        Assert.That(r.IsAlias, Is.True);
        Assert.That(r.Identifier, Is.EqualTo("current"));
        Assert.That(r.Id, Is.EqualTo(UUID.Zero));
        Assert.That(r.Depth, Is.EqualTo(0));
    }

    [Test] // §1a #12, :565-578
    public void FetchCategoryCategories_is_GET_category_id_categories_with_depth()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/categories?depth=3");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCategoryCategories));
        Assert.That(r.Depth, Is.EqualTo(3));
    }

    [Test] // §1a #13, :642-648
    public void FetchCategorySubset_is_GET_children_with_children_list()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/children?depth=1&children={C1},{C2}");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCategorySubset));
        Assert.That(r.Depth, Is.EqualTo(1));
        Assert.That(r.Children, Is.EqualTo(new[] { C1, C2 }));
    }

    [Test] // §1a #14 / §1b, :692
    public void FetchCOF_is_GET_category_current_links()
    {
        var r = AisRouter.Parse("GET", "/category/current/links");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCOF));
        Assert.That(r.IsAlias, Is.True);
        Assert.That(r.Identifier, Is.EqualTo("current"));
    }

    [Test] // §1a #15, :728
    public void FetchCategoryLinks_is_GET_category_id_links()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/links");
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCategoryLinks));
        Assert.That(r.Id, Is.EqualTo(Cat));
        Assert.That(r.IsAlias, Is.False);
    }

    [Test] // §1a #16, :765
    public void FetchOrphans_is_GET_orphans()
    {
        Assert.That(AisRouter.Parse("GET", "/orphans").Operation, Is.EqualTo(AisOperation.FetchOrphans));
        Assert.That(AisRouter.Parse("DELETE", "/orphans").Operation, Is.EqualTo(AisOperation.Unknown));
    }

    [Test]
    public void the_cap_prefix_is_stripped_and_unknown_query_keys_are_kept()
    {
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var r = AisRouter.Parse("get", $"{cap}/category/{Cat}/children?depth=2&simulate=true", cap);
        Assert.That(r.Operation, Is.EqualTo(AisOperation.FetchCategoryChildren));
        Assert.That(r.Verb, Is.EqualTo("GET"));
        Assert.That(r.Path, Is.EqualTo($"/category/{Cat}/children"));
        Assert.That(r.Query["simulate"], Is.EqualTo("true"));
    }

    [Test]
    public void shapes_outside_the_spec_are_Unknown()
    {
        Assert.That(AisRouter.Parse("GET", $"/category/{Cat}").Operation, Is.EqualTo(AisOperation.Unknown), "no GET on a bare category in llaisapi.cpp");
        Assert.That(AisRouter.Parse("POST", $"/item/{Item}").Operation, Is.EqualTo(AisOperation.Unknown));
        Assert.That(AisRouter.Parse("GET", "/category/not-a-uuid/children").Operation, Is.EqualTo(AisOperation.Unknown), "only 'current' is an alias");
        Assert.That(AisRouter.Parse("GET", "/").Operation, Is.EqualTo(AisOperation.Unknown));
        Assert.That(AisRouter.Parse("GET", $"/category/{Cat}/children/extra").Operation, Is.EqualTo(AisOperation.Unknown));
        Assert.That(AisRouter.Parse("PUT", $"/category/{Cat}/children").Operation, Is.EqualTo(AisOperation.Unknown));
    }
}
