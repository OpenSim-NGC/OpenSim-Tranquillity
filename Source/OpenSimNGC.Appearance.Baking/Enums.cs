namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// Wearable types with the wire values every viewer and OpenSim use (the same numbers as
/// <c>OpenMetaverse.WearableType</c>; kept local so the library does not depend on the client library).
/// </summary>
public enum WearableKind
{
    Shape = 0, Skin = 1, Hair = 2, Eyes = 3, Shirt = 4, Pants = 5, Shoes = 6, Socks = 7, Jacket = 8, Gloves = 9,
    Undershirt = 10, Underpants = 11, Skirt = 12, Alpha = 13, Tattoo = 14, Physics = 15, Universal = 16,
    Invalid = 255,
}

/// <summary>
/// Avatar texture slots (the viewer's <c>ETextureIndex</c>, the same numbers as <c>OpenMetaverse.AvatarTextureIndex</c>).
/// Wearable assets key their textures by these numbers; bakes land in the <c>*Baked</c> slots.
/// </summary>
public enum TextureSlot
{
    Unknown = -1,
    HeadBodypaint = 0, UpperShirt = 1, LowerPants = 2, EyesIris = 3, Hair = 4, UpperBodypaint = 5, LowerBodypaint = 6, LowerShoes = 7,
    HeadBaked = 8, UpperBaked = 9, LowerBaked = 10, EyesBaked = 11,
    LowerSocks = 12, UpperJacket = 13, LowerJacket = 14, UpperGloves = 15, UpperUndershirt = 16, LowerUnderpants = 17, Skirt = 18,
    SkirtBaked = 19, HairBaked = 20,
    LowerAlpha = 21, UpperAlpha = 22, HeadAlpha = 23, EyesAlpha = 24, HairAlpha = 25,
    HeadTattoo = 26, UpperTattoo = 27, LowerTattoo = 28,
    HeadUniversalTattoo = 29, UpperUniversalTattoo = 30, LowerUniversalTattoo = 31, SkirtTattoo = 32, HairTattoo = 33, EyesTattoo = 34,
    LeftArmTattoo = 35, LeftLegTattoo = 36, Aux1Tattoo = 37, Aux2Tattoo = 38, Aux3Tattoo = 39,
    LeftArmBaked = 40, LeftLegBaked = 41, Aux1Baked = 42, Aux2Baked = 43, Aux3Baked = 44,
}

/// <summary>Well-known texture ids the bake pipeline treats specially.</summary>
public static class BakeConstants
{
    /// <summary>The "no texture" placeholder a wearable carries for a slot it does not paint (<c>IMG_DEFAULT_AVATAR</c>).</summary>
    public static readonly OpenMetaverse.UUID DefaultAvatarTexture = new("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");
    /// <summary>An alpha wearable carrying this texture hides the whole region (<c>IMG_INVISIBLE</c>).</summary>
    public static readonly OpenMetaverse.UUID InvisibleTexture = new("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");

    /// <summary>The TextureEntry face each bake channel is written to.</summary>
    public static TextureSlot BakedSlotOf(BakeChannel ch) => ch switch
    {
        BakeChannel.Head => TextureSlot.HeadBaked, BakeChannel.Upper => TextureSlot.UpperBaked, BakeChannel.Lower => TextureSlot.LowerBaked,
        BakeChannel.Eyes => TextureSlot.EyesBaked, BakeChannel.Skirt => TextureSlot.SkirtBaked, BakeChannel.Hair => TextureSlot.HairBaked,
        BakeChannel.LeftArm => TextureSlot.LeftArmBaked, BakeChannel.LeftLeg => TextureSlot.LeftLegBaked,
        BakeChannel.Aux1 => TextureSlot.Aux1Baked, BakeChannel.Aux2 => TextureSlot.Aux2Baked, BakeChannel.Aux3 => TextureSlot.Aux3Baked,
        _ => TextureSlot.Unknown,
    };
}

public static class WearableKinds
{
    /// <summary>The wearable type name avatar_lad.xml uses in <c>wearable=</c> attributes and LLWearable files.</summary>
    public static string TypeName(WearableKind t) => t switch
    {
        WearableKind.Shape => "shape", WearableKind.Skin => "skin", WearableKind.Hair => "hair", WearableKind.Eyes => "eyes", WearableKind.Shirt => "shirt",
        WearableKind.Pants => "pants", WearableKind.Shoes => "shoes", WearableKind.Socks => "socks", WearableKind.Jacket => "jacket", WearableKind.Gloves => "gloves",
        WearableKind.Undershirt => "undershirt", WearableKind.Underpants => "underpants", WearableKind.Skirt => "skirt", WearableKind.Alpha => "alpha",
        WearableKind.Tattoo => "tattoo", WearableKind.Physics => "physics", WearableKind.Universal => "universal", _ => "",
    };

    public static WearableKind? FromName(string name)
    {
        foreach (WearableKind t in Enum.GetValues<WearableKind>())
            if (t != WearableKind.Invalid && string.Equals(TypeName(t), name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    public static bool IsBodyPart(WearableKind t) => t is WearableKind.Shape or WearableKind.Skin or WearableKind.Hair or WearableKind.Eyes;
}
