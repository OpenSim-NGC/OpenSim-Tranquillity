/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;
using OpenMetaverse;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// One test per URL shape in Docs/feature/ais-v3/AIS-V3-SPEC.md §1a (the viewer's llaisapi.cpp request builders),
/// plus the "current" alias (§1b) and the cap-prefix / rejection cases.
/// </summary>
public class AisRouterTests
{
    private static readonly UUID Cat = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Item = new("22222222-2222-4222-8222-222222222222");
    private static readonly UUID Tid = new("33333333-3333-4333-8333-333333333333");
    private static readonly UUID C1 = new("44444444-4444-4444-8444-444444444444");
    private static readonly UUID C2 = new("55555555-5555-4555-8555-555555555555");

    [Fact] // §1a #1, llaisapi.cpp:115
    public void CreateInventory_is_POST_category_parent_with_tid()
    {
        var r = AisRouter.Parse("POST", $"/category/{Cat}?tid={Tid}");
        Assert.Equal(AisOperation.CreateInventory, r.Operation);
        Assert.Equal(Cat, r.Id);
        Assert.Equal(Tid, r.Tid);
        Assert.False(r.IsAlias);
    }

    [Fact] // §1a #2, :161
    public void SlamFolder_is_PUT_category_id_links_with_tid()
    {
        var r = AisRouter.Parse("PUT", $"/category/{Cat}/links?tid={Tid}");
        Assert.Equal(AisOperation.SlamFolder, r.Operation);
        Assert.Equal(Cat, r.Id);
        Assert.Equal(Tid, r.Tid);
    }

    [Fact] // §1a #3, :197
    public void RemoveCategory_is_DELETE_category_id()
    {
        var r = AisRouter.Parse("DELETE", $"/category/{Cat}");
        Assert.Equal(AisOperation.RemoveCategory, r.Operation);
        Assert.Equal(Cat, r.Id);
    }

    [Fact] // §1a #4, :234
    public void RemoveItem_is_DELETE_item_id()
    {
        var r = AisRouter.Parse("DELETE", $"/item/{Item}");
        Assert.Equal(AisOperation.RemoveItem, r.Operation);
        Assert.Equal(Item, r.Id);
    }

    [Fact] // §1a #5, :275-278: the viewer appends ",depth=0" to the tid VALUE
    public void CopyLibraryCategory_is_COPY_category_with_tid_and_comma_depth()
    {
        var r = AisRouter.Parse("COPY", $"/category/{Cat}?tid={Tid},depth=0");
        Assert.Equal(AisOperation.CopyCategory, r.Operation);
        Assert.Equal(Cat, r.Id);
        Assert.Equal(Tid, r.Tid);
        Assert.Equal(0, r.Depth);
        var full = AisRouter.Parse("COPY", $"/category/{Cat}?tid={Tid}");
        Assert.Equal(-1, full.Depth);
    }

    [Fact] // §1a #6, :318
    public void PurgeDescendents_is_DELETE_category_id_children()
    {
        var r = AisRouter.Parse("DELETE", $"/category/{Cat}/children");
        Assert.Equal(AisOperation.PurgeDescendents, r.Operation);
        Assert.Equal(Cat, r.Id);
    }

    [Fact] // §1a #7, :355
    public void UpdateCategory_is_PATCH_category_id()
    {
        Assert.Equal(AisOperation.UpdateCategory, AisRouter.Parse("PATCH", $"/category/{Cat}").Operation);
    }

    [Fact] // §1a #8, :391
    public void UpdateItem_is_PATCH_item_id()
    {
        Assert.Equal(AisOperation.UpdateItem, AisRouter.Parse("PATCH", $"/item/{Item}").Operation);
    }

    [Fact] // §1a #9, :426
    public void FetchItem_is_GET_item_id()
    {
        var r = AisRouter.Parse("GET", $"/item/{Item}");
        Assert.Equal(AisOperation.FetchItem, r.Operation);
        Assert.Equal(Item, r.Id);
    }

    [Fact] // §1a #10, :461-474
    public void FetchCategoryChildren_is_GET_category_id_children_with_depth()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/children?depth=50");
        Assert.Equal(AisOperation.FetchCategoryChildren, r.Operation);
        Assert.Equal(50, r.Depth);
        Assert.Empty(r.Children);
        Assert.Equal(-1, AisRouter.Parse("GET", $"/category/{Cat}/children").Depth);
    }

    [Fact] // §1a #11, :514: any identifier string, including the alias
    public void FetchCategoryChildren_by_identifier_accepts_current()
    {
        var r = AisRouter.Parse("GET", "/category/current/children?depth=0");
        Assert.Equal(AisOperation.FetchCategoryChildren, r.Operation);
        Assert.True(r.IsAlias);
        Assert.Equal("current", r.Identifier);
        Assert.Equal(UUID.Zero, r.Id);
        Assert.Equal(0, r.Depth);
    }

    [Fact] // §1a #12, :565-578
    public void FetchCategoryCategories_is_GET_category_id_categories_with_depth()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/categories?depth=3");
        Assert.Equal(AisOperation.FetchCategoryCategories, r.Operation);
        Assert.Equal(3, r.Depth);
    }

    [Fact] // §1a #13, :642-648
    public void FetchCategorySubset_is_GET_children_with_children_list()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/children?depth=1&children={C1},{C2}");
        Assert.Equal(AisOperation.FetchCategorySubset, r.Operation);
        Assert.Equal(1, r.Depth);
        Assert.Equal(new[] { C1, C2 }, r.Children);
    }

    [Fact] // §1a #14 / §1b, :692
    public void FetchCOF_is_GET_category_current_links()
    {
        var r = AisRouter.Parse("GET", "/category/current/links");
        Assert.Equal(AisOperation.FetchCOF, r.Operation);
        Assert.True(r.IsAlias);
        Assert.Equal("current", r.Identifier);
    }

    [Fact] // §1a #15, :728
    public void FetchCategoryLinks_is_GET_category_id_links()
    {
        var r = AisRouter.Parse("GET", $"/category/{Cat}/links");
        Assert.Equal(AisOperation.FetchCategoryLinks, r.Operation);
        Assert.Equal(Cat, r.Id);
        Assert.False(r.IsAlias);
    }

    [Fact] // §1a #16, :765
    public void FetchOrphans_is_GET_orphans()
    {
        Assert.Equal(AisOperation.FetchOrphans, AisRouter.Parse("GET", "/orphans").Operation);
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("DELETE", "/orphans").Operation);
    }

    [Fact]
    public void the_cap_prefix_is_stripped_and_unknown_query_keys_are_kept()
    {
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var r = AisRouter.Parse("get", $"{cap}/category/{Cat}/children?depth=2&simulate=true", cap);
        Assert.Equal(AisOperation.FetchCategoryChildren, r.Operation);
        Assert.Equal("GET", r.Verb);
        Assert.Equal($"/category/{Cat}/children", r.Path);
        Assert.Equal("true", r.Query["simulate"]);
    }

    [Fact]
    public void shapes_outside_the_spec_are_Unknown()
    {
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("GET", $"/category/{Cat}").Operation);
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("POST", $"/item/{Item}").Operation);
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("GET", "/category/not-a-uuid/children").Operation);
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("GET", "/").Operation);
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("GET", $"/category/{Cat}/children/extra").Operation);
        Assert.Equal(AisOperation.Unknown, AisRouter.Parse("PUT", $"/category/{Cat}/children").Operation);
    }
}
