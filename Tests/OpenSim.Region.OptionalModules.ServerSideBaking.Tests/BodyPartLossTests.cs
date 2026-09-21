using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S8. A bake made from a wearable set that has lost a body part is not a bake of a different outfit - it is a
/// bake of nothing, and storing it is destructive, because a new bake supersedes (deletes) the asset it replaces
/// (ADR-004). Baking again cannot undo it.
///
/// <para><b>The live loss.</b> 2026-09-05, 18:06:51: four unresolvable item ids had just emptied wearable slots
/// 1-4, and the <c>reason=CofChanged</c> bake that followed reported "no Skin worn / no Eyes worn / no Hair worn",
/// stored 4 channels and superseded 4 - the good ones. The agent's Current Outfit folder still linked valid skin,
/// eyes and hair items the whole time.</para>
///
/// <para>The guard is deliberately narrow. It fires only on the four body-part slots, and only on
/// present-to-absent: a resident cannot take off their skin, so that transition is always a failure upstream of
/// the bake. Everything a resident can actually do - change clothes, strip to underwear, swap a shape - either
/// leaves the body parts populated or replaces them, and goes through untouched.</para>
/// </summary>
public class BodyPartLossTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    private static ServerSideBakingRegion Region() => new(enabled: true, handshake: new CofHandshake());

    /// <summary>A wearable set with something in each named slot and nothing anywhere else.</summary>
    private static AvatarWearable[] Worn(params WearableType[] slots)
    {
        var w = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < w.Length; i++) w[i] = new AvatarWearable();
        foreach (var slot in slots) w[(int)slot] = new AvatarWearable(UUID.Random(), UUID.Random());
        return w;
    }

    private static readonly WearableType[] AllBodyParts =
        { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes };

    // ---------------- the refusal ----------------

    [Theory]
    [InlineData(WearableType.Skin)]
    [InlineData(WearableType.Shape)]
    [InlineData(WearableType.Hair)]
    [InlineData(WearableType.Eyes)]
    public void Losing_a_body_part_since_the_last_bake_refuses_the_next_one(WearableType lost)
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(AllBodyParts));

        var remaining = new System.Collections.Generic.List<WearableType>(AllBodyParts);
        remaining.Remove(lost);

        var refusal = region.RefusalForBodyPartLoss(Agent, Worn(remaining.ToArray()));

        Assert.NotNull(refusal);
        Assert.Contains(lost.ToString(), refusal);
        Assert.Contains("nothing is baked and nothing is superseded", refusal);
    }

    /// <summary>The live case exactly: skin, hair and eyes gone at once, shape still there.</summary>
    [Fact]
    public void The_2026_09_05_loss_is_refused()
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes, WearableType.Shirt));

        var refusal = region.RefusalForBodyPartLoss(Agent, Worn(WearableType.Shape));

        Assert.NotNull(refusal);
        Assert.Contains("Skin", refusal);
        Assert.Contains("Hair", refusal);
        Assert.Contains("Eyes", refusal);
    }

    // ---------------- what must still go through ----------------

    [Fact]
    public void The_first_bake_of_a_session_always_proceeds()
    {
        // Nothing to compare with. Refusing here would leave a new arrival unbaked forever.
        Assert.Null(Region().RefusalForBodyPartLoss(Agent, Worn(AllBodyParts)));
    }

    [Fact]
    public void An_ordinary_outfit_change_proceeds()
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes, WearableType.Shirt, WearableType.Pants));

        // took the shirt and pants off; every body part still worn
        Assert.Null(region.RefusalForBodyPartLoss(Agent, Worn(AllBodyParts)));
    }

    [Fact]
    public void Gaining_a_body_part_proceeds()
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(WearableType.Shape, WearableType.Skin));
        Assert.Null(region.RefusalForBodyPartLoss(Agent, Worn(AllBodyParts)));
    }

    [Fact]
    public void Replacing_a_body_part_with_a_different_item_proceeds()
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(AllBodyParts));
        Assert.Null(region.RefusalForBodyPartLoss(Agent, Worn(AllBodyParts)));   // fresh random ids, same slots
    }

    // ---------------- the baseline is only ever a good set ----------------

    /// <summary>
    /// A refusal must not become the baseline. If it did, the retry a few seconds later would see no loss, bake
    /// from the empty set and supersede the good bakes anyway - the guard would delay the damage, not prevent it.
    /// </summary>
    [Fact]
    public void A_refused_set_does_not_become_the_new_baseline()
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(AllBodyParts));
        var damaged = Worn(WearableType.Shape);

        Assert.NotNull(region.RefusalForBodyPartLoss(Agent, damaged));
        Assert.NotNull(region.RefusalForBodyPartLoss(Agent, damaged));   // and again, and again
        Assert.NotNull(region.RefusalForBodyPartLoss(Agent, damaged));
    }

    [Fact]
    public void Forgetting_an_agent_clears_the_baseline()
    {
        var region = Region();
        region.RecordGoodBodyParts(Agent, Worn(AllBodyParts));
        region.Forget(Agent);
        Assert.Null(region.RefusalForBodyPartLoss(Agent, Worn(WearableType.Shape)));
    }

    // ---------------- the slot set itself ----------------

    [Fact]
    public void The_guarded_slots_are_the_four_body_parts()
        => Assert.Equal(
            new[] { (int)WearableType.Shape, (int)WearableType.Skin, (int)WearableType.Hair, (int)WearableType.Eyes },
            ServerSideBakingRegion.BodyPartSlots);

    [Fact]
    public void Presence_is_read_per_slot_from_the_wearable_array()
    {
        Assert.Equal(
            new[] { (int)WearableType.Skin, (int)WearableType.Eyes },
            ServerSideBakingRegion.BodyPartsPresent(Worn(WearableType.Skin, WearableType.Eyes, WearableType.Shirt)));
    }
}
