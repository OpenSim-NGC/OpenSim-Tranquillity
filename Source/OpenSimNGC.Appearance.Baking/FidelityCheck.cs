using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The fidelity gate's evidence. Names everything in an outfit the compositor does not reproduce faithfully;
/// an empty list means every wearable type, texture slot and bundled resource the requested channels need is
/// one the compositor handles. The library only reports; whether to refuse is the caller's decision (the
/// web-viewer gateway refuses on any reason; the simulator decides per ADR-005).
/// </summary>
public static class FidelityCheck
{
    /// <summary>Wearable types whose textures and parameters the compositor handles.</summary>
    public static readonly WearableKind[] SupportedKinds =
    {
        WearableKind.Shape, WearableKind.Skin, WearableKind.Hair, WearableKind.Eyes, WearableKind.Shirt, WearableKind.Pants, WearableKind.Shoes,
        WearableKind.Socks, WearableKind.Jacket, WearableKind.Gloves, WearableKind.Undershirt, WearableKind.Underpants, WearableKind.Skirt,
        WearableKind.Alpha, WearableKind.Tattoo, WearableKind.Physics, WearableKind.Universal,
    };

    public sealed record WornSummary(WearableKind Kind, string Label, IReadOnlyDictionary<TextureSlot, UUID> Textures);

    /// <summary>Every reason the outfit cannot be baked faithfully for the given channels; empty means supported.</summary>
    public static List<string> Check(IReadOnlyList<WornSummary> worn, TexLayerCompositor compositor, IEnumerable<BakeChannel> channels)
    {
        var reasons = new List<string>();
        var channelList = channels.ToList();
        var slotsInScope = channelList.SelectMany(compositor.SlotsOf).ToHashSet();
        // Several wearables of one type are layered in wear order and the five Bakes-on-Mesh extra bakes are made from
        // their layer sets; neither is a refusal. Body parts are still one each: a second shape, skin, hair or eyes would
        // be silently ignored by a viewer, so refuse.
        foreach (var group in worn.GroupBy(w => w.Kind))
            if (group.Count() > 1 && WearableKinds.IsBodyPart(group.Key))
                reasons.Add($"{group.Count()} {group.Key} wearables worn at once; a body part can only be worn once");
        foreach (var w in worn)
        {
            if (!SupportedKinds.Contains(w.Kind)) { reasons.Add($"{w.Label}: wearable type {w.Kind} is not composited"); continue; }
            foreach (var (idx, id) in w.Textures)
            {
                if (id == UUID.Zero || id == BakeConstants.DefaultAvatarTexture) continue;
                if (TexLayerCompositor.WearableOf(idx) == WearableKind.Invalid) { reasons.Add($"{w.Label}: texture slot {idx} is unknown to the compositor"); continue; }
                if (!slotsInScope.Contains(idx) && idx != TextureSlot.Skirt && idx != TextureSlot.SkirtTattoo)
                    reasons.Add($"{w.Label}: texture slot {idx} is not drawn by any of the bakes being made");
            }
        }
        foreach (var file in channelList.SelectMany(compositor.ResourceFilesOf).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!compositor.Resources.Exists(file))
                reasons.Add($"bundled resource {file} is missing or unreadable");
        return reasons;
    }
}
