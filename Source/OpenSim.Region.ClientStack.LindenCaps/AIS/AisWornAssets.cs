using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// S9. Editing a worn wearable changes the item's asset and nothing else the region watches: the worn SET is
/// unchanged (the viewer keeps the item id - <c>llagentwearables.cpp:319</c> <c>setItemID(old_item_id)</c>), so no
/// <c>AgentIsNowWearing</c> follows, and while the viewer does schedule an <c>UpdateAvatarAppearance</c> POST it is
/// deferred behind pending uploads (<c>llappearancemgr.cpp:3849</c>) and can arrive minutes later carrying a
/// <c>cof_version</c> the COF has already moved past - which is what was observed on 2026-09-05, one POST at
/// 20:57:40 refused as stale for four edits between 20:30 and 20:53.
///
/// <para>
/// So the region learns of the change at exactly one reliable moment: the AIS <c>UpdateItem</c> that carries the
/// new asset. This is the rule applied there. It is a pure function of the appearance and the item so it can be
/// tested without a scene; the module wires it to <c>QueueAppearanceSave</c>.
/// </para>
/// </summary>
public static class AisWornAssets
{
    /// <summary>
    /// Point a worn wearable at its new asset. Returns true when the item is worn <b>and</b> the asset actually
    /// differs - the only case that is worth a save, and therefore the only case that can cost a bake.
    ///
    /// <para>
    /// An item that is not worn changes nothing here: an edit to something in a drawer must not queue an
    /// appearance save. An item that is worn but already carries this asset changes nothing either, so a repeated
    /// or replayed PATCH is free.
    /// </para>
    /// </summary>
    public static bool ApplyTo(AvatarAppearance appearance, UUID itemId, UUID newAssetId)
    {
        if (appearance is null || itemId.IsZero() || newAssetId.IsZero()) return false;

        AvatarWearable[] worn = appearance.Wearables;
        if (worn is null) return false;

        for (var slot = 0; slot < worn.Length; slot++)
        {
            AvatarWearable w = worn[slot];
            if (w is null) continue;
            for (var j = 0; j < w.Count; j++)
            {
                if (w[j].ItemID != itemId) continue;
                if (w[j].AssetID == newAssetId) return false;   // already current
                w.Add(itemId, newAssetId);                      // Add updates in place for a known item
                return true;
            }
        }
        return false;
    }
}
