using System;
using System.Collections.Generic;

namespace OpenSim.Services.Interfaces;

/// <summary>
/// The bake channel tokens the LL viewer puts in an <c>agent_appearance_service</c> URL, and the
/// <see cref="AvatarDataKeys"/> index names they resolve to.
///
/// <para>
/// <b>Established from the viewer, not assumed.</b> <c>LLVOAvatar::getImageURL</c> builds
/// <c>appearance_service_url + "texture/" + agent + "/" + texture_entry-&gt;mDefaultImageName + "/" + uuid</c>
/// (<c>indra/newview/llvoavatar.cpp:5912</c>). So the channel token is neither a number nor the enum name — it is
/// the <c>mDefaultImageName</c> of the baked <c>TextureEntry</c>, which is the fourth constructor argument
/// (<c>indra/llappearance/llavatarappearancedefines.h:162-167</c>,
/// <c>llavatarappearancedefines.cpp:202-215</c>) and is given for the eleven baked entries at
/// <c>llavatarappearancedefines.cpp:81-91</c>:
/// </para>
///
/// <code>
/// TEX_HEAD_BAKED     -&gt; "head"       TEX_SKIRT_BAKED    -&gt; "skirt"
/// TEX_UPPER_BAKED    -&gt; "upper"      TEX_LEFT_ARM_BAKED -&gt; "leftarm"
/// TEX_LOWER_BAKED    -&gt; "lower"      TEX_LEFT_LEG_BAKED -&gt; "leftleg"
/// TEX_EYES_BAKED     -&gt; "eyes"       TEX_AUX1_BAKED     -&gt; "aux1"
/// TEX_HAIR_BAKED     -&gt; "hair"       TEX_AUX2_BAKED     -&gt; "aux2"
///                                    TEX_AUX3_BAKED     -&gt; "aux3"
/// </code>
///
/// <para>
/// All lower case, and <c>leftarm</c>/<c>leftleg</c> carry no separator. They coincide exactly with the bake
/// library's <c>BakeChannel</c> names lower-cased — which is also the token in a stored bake's asset name,
/// <c>bake:&lt;agent&gt;:&lt;channel&gt;</c> — but that is a coincidence worth pinning rather than relying on, and a
/// test pins it. This type is the authority for the wire, and it deliberately does not reference the bake library:
/// the Robust deployment does not carry it.
/// </para>
/// </summary>
public static class AppearanceChannels
{
    /// <summary>Token as the viewer writes it, paired with the index name <c>Bake:&lt;name&gt;</c> uses.</summary>
    private static readonly (string Token, string IndexName)[] Channels =
    {
        ("head", "Head"),
        ("upper", "Upper"),
        ("lower", "Lower"),
        ("eyes", "Eyes"),
        ("skirt", "Skirt"),
        ("hair", "Hair"),
        ("leftarm", "LeftArm"),
        ("leftleg", "LeftLeg"),
        ("aux1", "Aux1"),
        ("aux2", "Aux2"),
        ("aux3", "Aux3"),
    };

    /// <summary>Every token the viewer can send, in baked-texture-index order.</summary>
    public static IReadOnlyList<string> Tokens
    {
        get
        {
            var list = new List<string>(Channels.Length);
            foreach (var c in Channels) list.Add(c.Token);
            return list;
        }
    }

    /// <summary>
    /// The avatar-service key that holds the bake for a viewer channel token, or null when the token is not one
    /// of the eleven. Matching is case-insensitive so a viewer or proxy that upper-cases the path still resolves;
    /// nothing else is accepted, and in particular a numeric token is not — the viewer never sends one.
    /// </summary>
    public static string BakeKeyFor(string channelToken)
    {
        if (string.IsNullOrEmpty(channelToken)) return null;
        foreach (var c in Channels)
            if (string.Equals(c.Token, channelToken, StringComparison.OrdinalIgnoreCase))
                return AvatarDataKeys.BakeIndexPrefix + ":" + c.IndexName;
        return null;
    }

    /// <summary>The index name for a token (<c>"leftarm"</c> → <c>"LeftArm"</c>), or null.</summary>
    public static string IndexNameFor(string channelToken)
    {
        if (string.IsNullOrEmpty(channelToken)) return null;
        foreach (var c in Channels)
            if (string.Equals(c.Token, channelToken, StringComparison.OrdinalIgnoreCase))
                return c.IndexName;
        return null;
    }
}
