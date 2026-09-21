using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S10. Firestorm's "Add" puts a second wearable of one type in the COF. The LL viewer orders same-type wearables
/// by the COF link item's description — <c>"@" + (type * 100 + index)</c>, written by
/// <c>LLAppearanceMgr::getWearableOrderingDescUpdates</c> (llappearancemgr.cpp:3676-3702) from
/// <c>build_order_string</c> (:3637-3642) — and layers them in that index order, later index on top
/// (<c>LLTexLayerTemplate::render</c>, lltexlayer.cpp:1659-1689, over the cache built 0..n-1 at :1615-1638).
///
/// <para>
/// Observed on Ebony (1.1.246, 2026-09-06 10:09:52 UTC): both "Shirt" and "Shirt2" were linked in the COF, a
/// <c>CofChanged</c> bake fired and reused 6/6, and the Avatars record for the agent held only
/// <c>Wearable 4:0</c>. The second shirt never reached <see cref="AvatarAppearance.Wearables"/>, so the
/// composite had nothing to layer.
/// </para>
/// </summary>
public class CofWearableOrderTests
{
    private static AvatarWearable[] Empty()
    {
        var w = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < w.Length; i++) w[i] = new AvatarWearable();
        return w;
    }

    private static AvatarWearable[] With(params (int Type, UUID Item, UUID Asset)[] worn)
    {
        var w = Empty();
        foreach (var (type, item, asset) in worn) w[type].Add(item, asset);
        return w;
    }

    private static CofWearableLink Link(string desc, int type, UUID item, UUID asset)
        => new(UUID.Random(), desc, item, type, asset);

    private const int Shirt = 4;    // WearableType.Shirt / LLWearableType::WT_SHIRT
    private const int Pants = 5;

    // ------------------------------------------------------------------ 1. the derivation

    [Fact]
    public void TwoShirtLinks_DeriveInAtOrder_NotInFolderOrder()
    {
        var shirt0 = UUID.Random(); var asset0 = UUID.Random();
        var shirt1 = UUID.Random(); var asset1 = UUID.Random();

        // Handed over in the wrong order on purpose: the folder listing has no order of its own, the description does.
        var links = new[]
        {
            Link("@401", Shirt, shirt1, asset1),
            Link("@400", Shirt, shirt0, asset0),
        };

        var derived = CofWearables.Derive(Empty(), links, out var changed);

        Assert.True(changed);
        Assert.Equal(2, derived[Shirt].Count);
        Assert.Equal(shirt0, derived[Shirt][0].ItemID);
        Assert.Equal(asset0, derived[Shirt][0].AssetID);
        Assert.Equal(shirt1, derived[Shirt][1].ItemID);
        Assert.Equal(asset1, derived[Shirt][1].AssetID);
    }

    [Fact]
    public void OrderStringIsTypeTimesHundredPlusIndex()
    {
        // build_order_string(WT_SHIRT, 1) == "@401" (llappearancemgr.cpp:3637-3642), and within one type the
        // key orders exactly as the index does.
        Assert.Equal(400, CofWearables.OrderKey("@400"));
        Assert.Equal(401, CofWearables.OrderKey("@401"));
        Assert.Equal(0, CofWearables.OrderOf("@400", Shirt));
        Assert.Equal(1, CofWearables.OrderOf("@401", Shirt));
        Assert.Equal(0, CofWearables.OrderOf("@500", Pants));
        // WearablesOrderComparator sinks a description that carries no ordering info (llappearancemgr.cpp:3657-3668).
        Assert.True(CofWearables.OrderKey("") < 0);
        Assert.True(CofWearables.OrderKey("Broken link") < 0);
        Assert.True(CofWearables.OrderOf("@500", Shirt) < 0);
    }

    [Fact]
    public void ADescriptionlessLinkSinksBelowTheOrderedOnes()
    {
        var ordered = UUID.Random(); var unordered = UUID.Random();
        var links = new[]
        {
            Link("", Shirt, unordered, UUID.Random()),
            Link("@400", Shirt, ordered, UUID.Random()),
        };

        var derived = CofWearables.Derive(Empty(), links, out _);

        Assert.Equal(ordered, derived[Shirt][0].ItemID);
        Assert.Equal(unordered, derived[Shirt][1].ItemID);
    }

    // ------------------------------------------------------------------ 2. the S8 rule must survive

    [Fact]
    public void ATypeNoLinkResolvesForKeepsWhatTheAgentAlreadyWears()
    {
        // S8: an item this region cannot resolve is a statement about the inventory lookup, not about what the
        // avatar is wearing (AvatarFactoryModule.cs:975-989). An unresolvable link cannot be attributed to a
        // wearable type at all, so its type must look untouched and keep its contents rather than go empty.
        var shirt = UUID.Random(); var shirtAsset = UUID.Random();
        var pants = UUID.Random(); var pantsAsset = UUID.Random();
        var existing = With((Shirt, shirt, shirtAsset), (Pants, pants, pantsAsset));

        // Only the shirt link resolved this time; the pants link's target could not be read.
        var derived = CofWearables.Derive(existing, new[] { Link("@400", Shirt, shirt, shirtAsset) }, out var changed);

        Assert.False(changed);
        Assert.Equal(shirt, derived[Shirt][0].ItemID);
        Assert.Equal(1, derived[Pants].Count);
        Assert.Equal(pants, derived[Pants][0].ItemID);
    }

    [Fact]
    public void NoLinksAtAllChangesNothing()
    {
        var shirt = UUID.Random();
        var existing = With((Shirt, shirt, UUID.Random()));

        var derived = CofWearables.Derive(existing, Array.Empty<CofWearableLink>(), out var changed);

        Assert.False(changed);
        Assert.Equal(shirt, derived[Shirt][0].ItemID);
    }

    // ------------------------------------------------------------------ 3. persistence keeps the second slot

    [Fact]
    public void TheAvatarsRecordRoundTripsBothShirtsInIndexOrder()
    {
        var shirt0 = UUID.Random(); var asset0 = UUID.Random();
        var shirt1 = UUID.Random(); var asset1 = UUID.Random();
        var appearance = new AvatarAppearance { Wearables = With((Shirt, shirt0, asset0)) };
        appearance.Wearables[Shirt].Add(shirt1, asset1);

        var data = new AvatarData(appearance);

        // The writer already names the slot "Wearable <type>:<index>" (IAvatarService.cs:199-208).
        Assert.Equal($"{shirt0}:{asset0}", data.Data["Wearable 4:0"]);
        Assert.Equal($"{shirt1}:{asset1}", data.Data["Wearable 4:1"]);

        // Shuffle the rows: a row store hands them back in whatever order it likes, and the index in the key is
        // the only thing that says which shirt is on top.
        var shuffled = new AvatarData { AvatarType = data.AvatarType, Data = new Dictionary<string, string>() };
        foreach (var kvp in data.Data.Reverse()) shuffled.Data[kvp.Key] = kvp.Value;

        var back = shuffled.ToAvatarAppearance();

        Assert.Equal(2, back.Wearables[Shirt].Count);
        Assert.Equal(shirt0, back.Wearables[Shirt][0].ItemID);
        Assert.Equal(shirt1, back.Wearables[Shirt][1].ItemID);
    }

    // ------------------------------------------------------------------ 4. the pixel

    private static RgbaPlanes Flat(byte r, byte g, byte b)
    {
        var p = new RgbaPlanes(64, 64, hasAlpha: false);
        Array.Fill(p.R, r); Array.Fill(p.G, g); Array.Fill(p.B, b); Array.Fill(p.A, (byte)255);
        return p;
    }

    /// <summary>A shirt worn at full coverage, so the whole upper-body region is its colour.</summary>
    private static readonly Dictionary<int, float> FullShirt = new()
    {
        [800] = 1f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 1f, [804] = 1f, [805] = 1f,
    };

    /// <summary>The worn list the bake composites, built from the derived wearables in the order they carry.</summary>
    private static List<WornWearable> Worn(AvatarWearable[] wearables, IReadOnlyDictionary<UUID, RgbaPlanes> shirtTextures)
    {
        var worn = new List<WornWearable>
        {
            new() { Kind = WearableKind.Shape, Label = "Shape", Params = new Dictionary<int, float> { [80] = 0f } },
            new()
            {
                Kind = WearableKind.Skin, Label = "Skin", Params = new Dictionary<int, float> { [111] = 0.5f },
                TextureIds = new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperBodypaint] = UUID.Random() },
                Textures = new Dictionary<TextureSlot, RgbaPlanes> { [TextureSlot.UpperBodypaint] = Flat(200, 150, 120) },
            },
        };
        var slot = wearables[Shirt];
        for (var j = 0; j < slot.Count; j++)
        {
            worn.Add(new WornWearable
            {
                Kind = WearableKind.Shirt, Label = $"Shirt:{j}", Params = FullShirt,
                TextureIds = new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = UUID.Random() },
                Textures = new Dictionary<TextureSlot, RgbaPlanes> { [TextureSlot.UpperShirt] = shirtTextures[slot[j].AssetID] },
            });
        }
        return worn;
    }

    private static (int Red, int Blue) Count(RgbaPlanes img)
    {
        int red = 0, blue = 0;
        for (var i = 0; i < img.R.Length; i++)
        {
            // Saturated red / saturated blue, not an exact colour: the shirt layer is tinted by the wearable's
            // own colour parameters, and the skin underneath it (200,150,120) is neither.
            if (img.R[i] > 150 && img.R[i] > img.G[i] + 80 && img.R[i] > img.B[i] + 80) red++;
            else if (img.B[i] > 150 && img.B[i] > img.G[i] + 80 && img.B[i] > img.R[i] + 80) blue++;
        }
        return (red, blue);
    }

    [Fact]
    public void TheHigherIndexedShirtIsCompositedOverTheLowerOne()
    {
        const int size = 256;
        var red = UUID.Random(); var blue = UUID.Random();
        var textures = new Dictionary<UUID, RgbaPlanes> { [red] = Flat(255, 0, 0), [blue] = Flat(0, 0, 255) };
        var compositor = new TexLayerCompositor();

        // "@400" red, "@401" blue: blue is on top, and nothing red survives it.
        var blueOnTop = CofWearables.Derive(Empty(), new[]
        {
            Link("@400", Shirt, UUID.Random(), red),
            Link("@401", Shirt, UUID.Random(), blue),
        }, out _);
        var (r1, b1) = Count(compositor.Bake(BakeChannel.Upper, Worn(blueOnTop, textures), size).Image);
        Assert.True(b1 > 1000, $"the @401 shirt must cover the upper body; {b1} blue pixels");
        Assert.Equal(0, r1);

        // Swap the two order strings and the bake swaps with them.
        var redOnTop = CofWearables.Derive(Empty(), new[]
        {
            Link("@401", Shirt, UUID.Random(), red),
            Link("@400", Shirt, UUID.Random(), blue),
        }, out _);
        var (r2, b2) = Count(compositor.Bake(BakeChannel.Upper, Worn(redOnTop, textures), size).Image);
        Assert.True(r2 > 1000, $"the @401 shirt must cover the upper body; {r2} red pixels");
        Assert.Equal(0, b2);
    }
}
