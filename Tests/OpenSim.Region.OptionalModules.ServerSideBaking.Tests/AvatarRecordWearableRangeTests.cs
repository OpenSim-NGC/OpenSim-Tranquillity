using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S11. The <c>Avatars</c> writer stops at wearable type 14, so Physics (15) and Universal (16) are never
/// persisted — however faithfully the rest of the stack carries them.
///
/// <para>
/// Observed on Ebony (1.1.258): Truly wears a Ruth2 v4 Physics Default and, since 2026-09-06 20:19 UTC, a
/// Universal. Both are linked in the COF, both are derived into the presence's wearables by S10, and both reach
/// the bake — the 20:27:33 line has <c>LeftArm=Baked</c> and <c>Aux1=Baked</c>, which only a Universal feeds.
/// The record for <c>a7d2ff2e-dc32-44d8-aa61-3d22070a4964</c> has never held a <c>Wearable 15:*</c> or
/// <c>Wearable 16:*</c> row, across several deferred saves.
/// </para>
///
/// <para>
/// S10 fixed the reader for types at or beyond the initial array length; this is the write-side counterpart it
/// never had.
/// </para>
/// </summary>
public class AvatarRecordWearableRangeTests
{
    private const int Shirt     = 4;    // WearableType.Shirt
    private const int Physics   = 15;   // AvatarWearable.PHYSICS / LLWearableType::WT_PHYSICS
    private const int Universal = 16;   // AvatarWearable.UNIVERSAL / LLWearableType::WT_UNIVERSAL

    private static AvatarWearable[] FullWidth()
    {
        var w = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < w.Length; i++) w[i] = new AvatarWearable();
        return w;
    }

    [Fact]
    public void TheRecordCarriesEveryWornTypeIncludingPhysicsAndUniversal()
    {
        var shirt0 = UUID.Random(); var shirt0Asset = UUID.Random();
        var shirt1 = UUID.Random(); var shirt1Asset = UUID.Random();
        var phys   = UUID.Random(); var physAsset   = UUID.Random();
        var uni    = new UUID("32f07ed9-0000-4000-8000-000000000001"); var uniAsset = UUID.Random();

        var wearables = FullWidth();
        wearables[Shirt].Add(shirt0, shirt0Asset);
        wearables[Shirt].Add(shirt1, shirt1Asset);
        wearables[Physics].Add(phys, physAsset);
        wearables[Universal].Add(uni, uniAsset);

        var appearance = new AvatarAppearance { Wearables = wearables };

        var data = new AvatarData(appearance);

        // The writer's own rows: every worn type, keyed "Wearable <type>:<index>".
        Assert.Equal($"{shirt0}:{shirt0Asset}", data.Data["Wearable 4:0"]);
        Assert.Equal($"{shirt1}:{shirt1Asset}", data.Data["Wearable 4:1"]);
        Assert.True(data.Data.ContainsKey("Wearable 15:0"), "no Wearable 15:0 row: the Physics layer was not persisted");
        Assert.True(data.Data.ContainsKey("Wearable 16:0"), "no Wearable 16:0 row: the Universal layer was not persisted");
        Assert.Equal($"{phys}:{physAsset}", data.Data["Wearable 15:0"]);
        Assert.Equal($"{uni}:{uniAsset}", data.Data["Wearable 16:0"]);

        // Exactly the four worn slots, and nothing invented for the empty ones.
        Assert.Equal(4, data.Data.Keys.Count(k => k.StartsWith("Wearable ")));

        // And it reads back, in index order, at every type (the S10 reader).
        var back = data.ToAvatarAppearance();

        Assert.Equal(2, back.Wearables[Shirt].Count);
        Assert.Equal(shirt0, back.Wearables[Shirt][0].ItemID);
        Assert.Equal(shirt1, back.Wearables[Shirt][1].ItemID);
        Assert.True(back.Wearables.Length > Universal, $"the read-back set is only {back.Wearables.Length} slots wide");
        Assert.Equal(1, back.Wearables[Physics].Count);
        Assert.Equal(phys, back.Wearables[Physics][0].ItemID);
        Assert.Equal(physAsset, back.Wearables[Physics][0].AssetID);
        Assert.Equal(1, back.Wearables[Universal].Count);
        Assert.Equal(uni, back.Wearables[Universal][0].ItemID);
        Assert.Equal(uniAsset, back.Wearables[Universal][0].AssetID);
    }

    [Fact]
    public void ARecordOfTheLegacyWidthStillRoundTrips()
    {
        // A 15-slot appearance is what AvatarAppearance's own constructor and ClearWearables produce
        // (AvatarAppearance.cs:320-325), and the writer must not walk off the end of one.
        var wearables = new AvatarWearable[AvatarWearable.LEGACY_VERSION_MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();
        var tattoo = UUID.Random(); var tattooAsset = UUID.Random();
        wearables[14].Add(tattoo, tattooAsset);

        var data = new AvatarData(new AvatarAppearance { Wearables = wearables });

        Assert.Equal($"{tattoo}:{tattooAsset}", data.Data["Wearable 14:0"]);
        Assert.Equal(1, data.Data.Keys.Count(k => k.StartsWith("Wearable ")));
        Assert.Equal(tattoo, data.ToAvatarAppearance().Wearables[14][0].ItemID);
    }
}
