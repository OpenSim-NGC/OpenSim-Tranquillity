/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// On a server-baking region the Current Outfit Folder decides which clothing is worn. The LL viewer never removes a
/// body part link (<c>removeItemsFromAvatar</c> skips <c>AT_BODYPART</c>, llappearancemgr.cpp:4224-4227) and keeps
/// the old body part link when an outfit replacement brings none (<c>updateCOF</c>, :2163-2170), while clothing comes
/// only from the new folder unless it is appending (:2172-2179). So a clothing type with no link in the folder is
/// not worn, and a body part type with no link keeps what the agent wears. These tests pin both halves.
/// </summary>
public class CofWearableRemovalTests
{
    private const int Shape = 0, Skin = 1, Hair = 2, Eyes = 3, Shirt = 4, Pants = 5, Alpha = 13, Tattoo = 14, Physics = 15, Universal = 16;
    private static readonly int[] BodyParts = { Shape, Skin, Hair, Eyes };

    private static AvatarWearable[] Empty()
    {
        var w = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < w.Length; i++) w[i] = new AvatarWearable();
        return w;
    }

    private static (AvatarWearable[] Worn, Dictionary<int, UUID> Items) WearAll(params int[] types)
    {
        var w = Empty();
        var items = new Dictionary<int, UUID>();
        foreach (var t in types) { items[t] = UUID.Random(); w[t].Add(items[t], UUID.Random()); }
        return (w, items);
    }

    private static CofWearableLink Link(int type, UUID item, string desc = "") => new(UUID.Random(), desc, item, type, UUID.Random());

    // (a) the only shirt link leaves the folder
    [Fact]
    public void RemovingTheOnlyShirtLinkDropsTheShirt()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt, Pants);
        var links = BodyParts.Select(t => Link(t, items[t])).Append(Link(Pants, items[Pants])).ToList();

        var derived = CofWearables.Derive(worn, links, 0, out var changed);

        Assert.True(changed);
        Assert.Equal(0, derived[Shirt].Count);
        Assert.Equal(items[Pants], derived[Pants][0].ItemID);
    }

    // (b) an outfit replaced by a folder with neither shirt nor pants
    [Fact]
    public void ReplacingTheOutfitWithBodyPartsOnlyDropsShirtAndPants()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt, Pants);
        var newShape = UUID.Random();
        var links = new List<CofWearableLink> { Link(Shape, newShape), Link(Skin, items[Skin]), Link(Hair, items[Hair]), Link(Eyes, items[Eyes]) };

        var derived = CofWearables.Derive(worn, links, 0, out var changed);

        Assert.True(changed);
        Assert.Equal(0, derived[Shirt].Count);
        Assert.Equal(0, derived[Pants].Count);
        Assert.Equal(newShape, derived[Shape][0].ItemID);
    }

    [Theory]
    [InlineData(Alpha)]
    [InlineData(Tattoo)]
    [InlineData(Physics)]
    [InlineData(Universal)]
    public void EveryOtherClothingTypeIsDroppedToo(int type)
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, type);
        var links = BodyParts.Select(t => Link(t, items[t])).ToList();

        var derived = CofWearables.Derive(worn, links, 0, out var changed);

        Assert.True(changed);
        Assert.Equal(0, derived[type].Count);
    }

    // (c) a body part with no link keeps what is worn
    [Theory]
    [InlineData(Shape)]
    [InlineData(Skin)]
    [InlineData(Hair)]
    [InlineData(Eyes)]
    public void ABodyPartWithNoLinkKeepsTheWornOne(int missing)
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt);
        var links = BodyParts.Where(t => t != missing).Select(t => Link(t, items[t])).Append(Link(Shirt, items[Shirt])).ToList();

        var derived = CofWearables.Derive(worn, links, 0, out var changed);

        Assert.False(changed);
        Assert.Equal(1, derived[missing].Count);
        Assert.Equal(items[missing], derived[missing][0].ItemID);
    }

    [Fact]
    public void ACofHoldingNoBodyPartLinkAtAllKeepsEveryBodyPart()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt, Pants);

        var derived = CofWearables.Derive(worn, new[] { Link(Pants, items[Pants]) }, 0, out var changed);

        Assert.True(changed);
        Assert.Equal(0, derived[Shirt].Count);
        foreach (var t in BodyParts) Assert.Equal(items[t], derived[t][0].ItemID);
    }

    // (d) a link this region could not resolve says nothing about its type, so no clothing type is dropped
    [Fact]
    public void AnUnresolvedLinkKeepsEveryClothingSlot()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt, Pants);
        var links = BodyParts.Select(t => Link(t, items[t])).ToList();

        var derived = CofWearables.Derive(worn, links, 1, out var changed);

        Assert.False(changed);
        Assert.Equal(items[Shirt], derived[Shirt][0].ItemID);
        Assert.Equal(items[Pants], derived[Pants][0].ItemID);
    }

    // a read that found nothing is not a statement that the avatar is naked
    [Fact]
    public void AFolderWithNoWearableLinkAtAllChangesNothing()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt);

        var derived = CofWearables.Derive(worn, Array.Empty<CofWearableLink>(), 0, out var changed);

        Assert.False(changed);
        Assert.Equal(items[Shirt], derived[Shirt][0].ItemID);
    }

    // (e) layers
    [Fact]
    public void DroppingOneOfTwoShirtsKeepsTheOtherAndItsOrder()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes);
        var under = UUID.Random(); var over = UUID.Random(); var top = UUID.Random();
        var links = BodyParts.Select(t => Link(t, items[t])).ToList();
        links.Add(Link(Shirt, top, "@402"));
        links.Add(Link(Shirt, under, "@400"));
        links.Add(Link(Shirt, over, "@401"));
        var three = CofWearables.Derive(worn, links, 0, out _);
        Assert.Equal(new[] { under, over, top }, Enumerable.Range(0, 3).Select(i => three[Shirt][i].ItemID));

        // The middle shirt is taken off; the viewer renumbers nothing here, the order of the rest holds.
        links.RemoveAll(l => l.TargetItemId == over);
        var two = CofWearables.Derive(three, links, 0, out var changed);

        Assert.True(changed);
        Assert.Equal(new[] { under, top }, Enumerable.Range(0, two[Shirt].Count).Select(i => two[Shirt][i].ItemID));
    }

    [Fact]
    public void TheInputIsNeverMutated()
    {
        var (worn, items) = WearAll(Shape, Skin, Hair, Eyes, Shirt);

        CofWearables.Derive(worn, BodyParts.Select(t => Link(t, items[t])).ToList(), 0, out _);

        Assert.Equal(items[Shirt], worn[Shirt][0].ItemID);
    }
}
