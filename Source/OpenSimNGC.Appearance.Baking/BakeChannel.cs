namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The eleven baked-texture output channels an avatar can carry.
/// The first six are the legacy (pre-Bakes-on-Mesh) slots; the last five are the
/// extended slots signalled by RegionProtocols bit 63.
/// </summary>
public enum BakeChannel
{
    /// <summary>Head bake (TextureEntry face 8, <c>TEX_HEAD_BAKED</c>).</summary>
    Head,
    /// <summary>Upper-body bake (face 9, <c>TEX_UPPER_BAKED</c>).</summary>
    Upper,
    /// <summary>Lower-body bake (face 10, <c>TEX_LOWER_BAKED</c>).</summary>
    Lower,
    /// <summary>Eyes bake (face 11, <c>TEX_EYES_BAKED</c>).</summary>
    Eyes,
    /// <summary>Skirt bake (face 19, <c>TEX_SKIRT_BAKED</c>).</summary>
    Skirt,
    /// <summary>Hair bake (face 20, <c>TEX_HAIR_BAKED</c>).</summary>
    Hair,
    /// <summary>Left-arm bake (face 21, <c>TEX_LEFT_ARM_BAKED</c>).</summary>
    LeftArm,
    /// <summary>Left-leg bake (face 22, <c>TEX_LEFT_LEG_BAKED</c>).</summary>
    LeftLeg,
    /// <summary>Auxiliary bake 1 (face 23, <c>TEX_AUX1_BAKED</c>).</summary>
    Aux1,
    /// <summary>Auxiliary bake 2 (face 24, <c>TEX_AUX2_BAKED</c>).</summary>
    Aux2,
    /// <summary>Auxiliary bake 3 (face 25, <c>TEX_AUX3_BAKED</c>).</summary>
    Aux3,
}
