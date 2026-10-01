/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Inventory names in LSL order and the iwGetLink* inventory functions. Names are sorted with Halcyon's InvNameComparer
/// (LSLSystemAPI.cs:5057-5096: case-insensitive, punctuation and digits before letters), as SL lists inventory
/// alphabetically. The iwGetLink* functions search every prim of a multi-prim target, documented as an extension;
/// iwRemoveLinkInventory removes from every selected prim and iwGetLinkInventoryPermMask ANDs them, -1 when no prim holds
/// the item (Halcyon :5498-5503, :12680-12686). A missing item for the creator functions is "" and an error on
/// DEBUG_CHANNEL (YEngine LSL_Api.llGetInventoryCreator). iwSearch*'s refused match types say Halcyon's text (:5142-5146).
/// llAllowInventoryDrop recomputes the object's flags at once (Halcyon :6382-6388).
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class LinkInventoryOrderTests
{
    private const int NOTECARD = 7, LINK_SET = -1;
    private const string LslErr = "Script error: LSL Runtime Error: ";

    private static readonly string[] Shuffled = { "b", "c", "A", "_x", "1" };
    private static readonly string[] InLslOrder = { "1", "_x", "A", "b", "c" };

    [Fact]
    public void LlGetInventoryNameListsNamesInLslOrder()
    {
        using var r = new InventoryGivesRig();
        foreach (var n in Shuffled) AddNotecard(r.H, r.H.Prim, n);
        var got = Enumerable.Range(0, 5).Select(i => (string)r.Accounted(a => a.llGetInventoryName(NOTECARD, i)).Ret).ToArray();
        Assert.Equal(InLslOrder, got);
        Assert.Equal("", r.Accounted(a => a.llGetInventoryName(NOTECARD, 5)).Ret);
        Assert.Equal("", r.Accounted(a => a.llGetInventoryName(NOTECARD, -1)).Ret);
    }

    [Fact]
    public void SearchesAndLinkNamesAreInLslOrderAcrossEveryPrim()
    {
        using var r = new InventoryGivesRig();
        var child = r.AddChild("child");
        AddNotecard(r.H, r.H.Prim, "b");
        AddNotecard(r.H, child, "A");
        AddNotecard(r.H, r.H.Prim, "1");
        AddNotecard(r.H, child, "c");
        var search = (InWorldz.Phlox.Types.LSLList)r.Accounted(a => a.iwSearchLinkInventory(LINK_SET, NOTECARD, "", 0)).Ret;
        Assert.Equal(new object[] { "1", "A", "b", "c" }, search.Data);
        var own = (InWorldz.Phlox.Types.LSLList)r.Accounted(a => a.iwSearchInventory(NOTECARD, "", 0)).Ret;
        Assert.Equal(new object[] { "1", "b" }, own.Data);
        var names = Enumerable.Range(0, 4).Select(i => (string)r.Accounted(a => a.iwGetLinkInventoryName(LINK_SET, NOTECARD, i)).Ret).ToArray();
        Assert.Equal(new[] { "1", "A", "b", "c" }, names);
    }

    [Fact]
    public void LinkSetFindsAChildPrimsItem()
    {
        using var r = new InventoryGivesRig();
        var child = r.AddChild("child");
        AddNotecard(r.H, child, "card");
        Assert.Equal(NOTECARD, r.Accounted(a => a.iwGetLinkInventoryType(LINK_SET, "card")).Ret);
    }

    [Fact]
    public void RemoveLinkInventoryRemovesFromEverySelectedPrim()
    {
        using var r = new InventoryGivesRig();
        var c2 = r.AddChild("c2");
        var c3 = r.AddChild("c3");
        AddNotecard(r.H, c2, "card");
        AddNotecard(r.H, c3, "card");
        r.Accounted(a => { a.iwRemoveLinkInventory(LINK_SET, "card"); return null; });
        Assert.Null(c2.Inventory.GetInventoryItem("card"));
        Assert.Null(c3.Inventory.GetInventoryItem("card"));
    }

    [Fact]
    public void LinkPermMaskIsMinusOneWhenMissingAndTheAndOfEveryPrimThatHasIt()
    {
        using var r = new InventoryGivesRig();
        var c2 = r.AddChild("c2");
        var c3 = r.AddChild("c3");
        AddNotecard(r.H, c2, "card").CurrentPermissions = 0x0000E000;
        AddNotecard(r.H, c3, "card").CurrentPermissions = 0x0000A000;
        Assert.Equal(-1, r.Accounted(a => a.iwGetLinkInventoryPermMask(LINK_SET, "nope", 1)).Ret);
        Assert.Equal(0x0000A000, r.Accounted(a => a.iwGetLinkInventoryPermMask(LINK_SET, "card", 1)).Ret);
        Assert.Equal(0x0000E000, r.Accounted(a => a.iwGetLinkInventoryPermMask(2, "card", 1)).Ret);
    }

    [Fact]
    public void AMissingCreatorIsEmptyWithAnError()
    {
        using var r = new InventoryGivesRig();
        var (ms, ret) = r.Accounted(a => a.llGetInventoryCreator("nope"));
        Assert.Equal("", ret);
        Assert.Equal(new[] { "Script error: No item named 'nope'" }, r.Errors);
        Assert.Equal(15, ms);
        r.AddChild("child");
        Assert.Equal("", r.Accounted(a => a.iwGetLinkInventoryCreator(2, "nope")).Ret);
        Assert.Equal(new[] { "Script error: No item named 'nope' in link 2" }, r.Errors);
        Assert.Equal(UUID.Zero.ToString(), r.Accounted(a => a.iwGetLinkInventoryCreator(9, "nope")).Ret);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void RefusedMatchTypesSayHalcyonsText()
    {
        using var r = new InventoryGivesRig();
        r.Accounted(a => a.iwSearchInventory(NOTECARD, "x", 3));
        Assert.Equal(new[] { LslErr + "IW_MATCH_COUNT is not a valid matching type for iwSearchInventory or iwSearchLinkInventory." }, r.Errors);
        r.Accounted(a => a.iwSearchLinkInventory(1, NOTECARD, "x", 4));
        Assert.Equal(new[] { LslErr + "IW_MATCH_COUNT_REGEX is not a valid matching type for iwSearchInventory or iwSearchLinkInventory." }, r.Errors);
        var (ms, ret) = r.Accounted(a => a.iwSearchInventory(NOTECARD, "x", 5));
        Assert.Empty(((InWorldz.Phlox.Types.LSLList)ret).Data);
        Assert.Empty(r.Errors);
        Assert.Equal(0, ms);
    }

    [Fact]
    public void AllowInventoryDropIsInTheObjectFlagsAtOnce()
    {
        using var r = new InventoryGivesRig();
        var root = r.H.Prim.ParentGroup.RootPart;
        r.Accounted(a => { a.llAllowInventoryDrop(1); return null; });
        Assert.NotEqual(0u, root.GetEffectiveObjectFlags() & (uint)PrimFlags.AllowInventoryDrop);
        r.Accounted(a => { a.llAllowInventoryDrop(0); return null; });
        Assert.Equal(0u, root.GetEffectiveObjectFlags() & (uint)PrimFlags.AllowInventoryDrop);
    }
}
