using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// One Current Outfit Folder link that names a wearable, already resolved against this region's inventory.
/// </summary>
/// <param name="LinkItemId">The link item's own id, in the COF. Only used for logging.</param>
/// <param name="Description">
/// The link item's description — the viewer's ordering information for same-type wearables. See
/// <see cref="CofWearables.OrderKey"/>.
/// </param>
/// <param name="TargetItemId">The linked-to inventory item: the id that goes into <see cref="AvatarWearable"/>.</param>
/// <param name="WearableType">The target's wearable type (the index into <see cref="AvatarAppearance.Wearables"/>).</param>
/// <param name="AssetId">The target's asset id, or <see cref="UUID.Zero"/> when this region has not resolved it yet.</param>
public sealed record CofWearableLink(UUID LinkItemId, string Description, UUID TargetItemId, int WearableType, UUID AssetId);

/// <summary>
/// A Current Outfit link whose target this region could not resolve, as the inventory stored the link itself.
/// </summary>
/// <param name="Description">The link item's description; for a wearable link, the viewer's <c>"@NNN"</c> ordering string.</param>
/// <param name="InvType">The link item's inventory type, which the viewer sets from its target when it creates the link.</param>
public sealed record CofUnresolvedLink(string Description, int InvType);

/// <summary>
/// Turns the Current Outfit Folder's wearable links into <see cref="AvatarAppearance.Wearables"/>, keeping
/// every link of a type and putting them in the order the viewer layers them.
///
/// <para>
/// <b>The order is the viewer's, and the viewer writes it into the COF.</b> <c>LLAppearanceMgr</c> stores each
/// clothing link's position in the link item's description as <c>"@" + (type * 100 + index)</c>
/// (<c>build_order_string</c>, llappearancemgr.cpp:3637-3642, written by
/// <c>getWearableOrderingDescUpdates</c> :3676-3702 and pushed to the server by <c>updateClothingOrderingInfo</c>
/// :3733), and sorts by that description before wearing (<c>sortItemsByActualDescription</c> :4346-4351, called
/// at :2683). The bake must layer by the same key: <c>LLTexLayerTemplate::render</c> walks the wearables of a
/// type from index 0 upwards and draws each over the last (lltexlayer.cpp:1659-1689 over the cache built at
/// :1615-1638), so the higher index is on top.
/// </para>
///
/// <para>
/// Two rules are inherited and must not regress. <b>Keep</b>: a body part type this COF read says nothing about
/// keeps what the agent already wears, rather than being emptied by a partial view of the outfit. Clothing is the
/// other way round: the Current Outfit is the authority, so a clothing type with no link is not worn. <b>Unresolvable</b>: a link whose
/// target this region cannot resolve is a statement about the inventory lookup, not about what the avatar is
/// wearing (<c>AvatarFactoryModule.SetAppearanceAssets</c>, AvatarFactoryModule.cs:975-989) — such a link never
/// reaches here. The caller drops it and passes its description; the type the description names keeps its contents,
/// and a link with no readable type makes the read empty no clothing type. The caller must not pass an
/// unresolvable link with a guessed type.
/// </para>
///
/// <para>Pure, and free of <c>Scene</c> and <c>IInventoryService</c>, so every rule above is a plain unit test.</para>
/// </summary>
public static class CofWearables
{
    /// <summary>
    /// The viewer's sort key for a COF link's description, or -1 when the description carries no ordering
    /// information. <c>WearablesOrderComparator</c> (llappearancemgr.cpp:3644-3674) accepts a description of
    /// exactly the width <c>build_order_string</c> produces and beginning with <c>'@'</c>, compares those
    /// lexicographically, and sinks everything else below them (an empty description, "Broken link", a link the
    /// viewer has not numbered yet). Same width and the same leading <c>'@'</c> makes the lexicographic compare
    /// a numeric one, which is what this returns.
    ///
    /// <para>
    /// The number is <c>type * 100 + index</c>, so within one wearable type the key orders exactly as the index
    /// does — which is all <see cref="Derive"/> needs, and it means a link numbered for another type still sorts
    /// deterministically instead of being dropped, as it does in the viewer before it renumbers it.
    /// </para>
    /// </summary>
    public static int OrderKey(string description)
    {
        if (string.IsNullOrEmpty(description) || description[0] != ORDER_NUMBER_SEPARATOR) return -1;
        return int.TryParse(description.AsSpan(1), out var n) && n >= 0 ? n : -1;
    }

    /// <summary><c>ORDER_NUMBER_SEPARATOR</c>, llappearancemgr.cpp:111.</summary>
    private const char ORDER_NUMBER_SEPARATOR = '@';

    /// <summary>
    /// The index <c>build_order_string</c> encoded for a link of this type, or -1 when the description carries no
    /// ordering information for it. Diagnostic: <see cref="Derive"/> sorts on <see cref="OrderKey"/>, which needs
    /// no type.
    /// </summary>
    public static int OrderOf(string description, int wearableType)
    {
        var key = OrderKey(description);
        return key >= 0 && key / 100 == wearableType ? key % 100 : -1;
    }

    /// <summary>
    /// Apply a COF read to an existing wearable set.
    /// </summary>
    /// <remarks>
    /// Neither argument is mutated; the result is a new array, safe to assign to
    /// <see cref="AvatarAppearance.Wearables"/>. Rules:
    /// <list type="bullet">
    /// <item>A type that any link names is replaced by exactly that type's links, ordered by
    /// <see cref="OrderKey"/> ascending, with unnumbered links kept in the order given and sunk below the
    /// numbered ones. Asset ids come from the links; a link may carry <see cref="UUID.Zero"/> and be resolved
    /// later by <c>SetAppearanceAssets</c>, exactly as an <c>AgentIsNowWearing</c> item is.</item>
    /// <item>A body part type (shape, skin, hair, eyes) no link names keeps its current items.</item>
    /// <item>A clothing type no link names is emptied: on a server-baking region the Current Outfit decides
    /// which clothing is worn, and the viewer drops a clothing link when the item is taken off or the outfit is
    /// replaced (llappearancemgr.cpp:2172-2179, :4228). Exceptions: a type named only by an unresolvable link keeps
    /// what the agent wears (the link's description gives the type, see <paramref name="unresolvedLinks"/>); and
    /// when an unresolvable link's type cannot be read, or the read found no wearable link at all, nothing is
    /// emptied. An unresolvable link whose own inventory type is a known non-wearable type (<see cref="IsKnownNonWearable"/>)
    /// is ignored.</item>
    /// <item><see cref="AvatarWearable.Add"/> caps a type at five items and ignores <see cref="UUID.Zero"/> item
    /// ids, so a sixth link of a type and a link to nothing are both dropped here, as they are on every other
    /// path into the wearable table.</item>
    /// </list>
    /// </remarks>
    /// <param name="existing">The agent's current wearables. May be null (treated as empty).</param>
    /// <param name="links">The COF's resolved wearable links, in any order.</param>
    /// <param name="unresolvedLinks">
    /// The descriptions of the links the caller had to drop because their target could not be resolved. A
    /// description of the viewer's form <c>"@NNN"</c> names the type <c>NNN / 100</c> (see <see cref="OrderKey"/>).
    /// A link whose own inventory type is a known non-wearable type is ignored.
    /// </param>
    /// <param name="changed">True when the result differs from <paramref name="existing"/> in any item id or order.</param>
    public static AvatarWearable[] Derive(AvatarWearable[] existing, IReadOnlyList<CofWearableLink> links, IReadOnlyList<CofUnresolvedLink> unresolvedLinks, out bool changed)
    {
        existing ??= Array.Empty<AvatarWearable>();
        links ??= Array.Empty<CofWearableLink>();

        var byType = new Dictionary<int, List<CofWearableLink>>();
        var length = existing.Length;
        foreach (var link in links)
        {
            if (link is null || link.WearableType < 0) continue;
            if (!byType.TryGetValue(link.WearableType, out var list)) byType[link.WearableType] = list = new List<CofWearableLink>();
            list.Add(link);
            if (link.WearableType >= length) length = link.WearableType + 1;
        }

        // A read is a complete statement of the outfit only when it found wearable links. A link this region could not
        // resolve says something about the inventory lookup, not about what is worn, but its own description usually
        // still carries the viewer's ordering number, which names its type: that type keeps what the agent wears, and
        // every other type follows the normal rule. A broken link whose type cannot be read leaves no way to tell
        // which type it was, so the read then empties no clothing type.
        var brokenTypes = new HashSet<int>();
        var typeUnknown = false;
        if (unresolvedLinks is not null)
            foreach (var broken in unresolvedLinks)
            {
                if (broken is null || IsKnownNonWearable(broken.InvType)) continue;
                var key = OrderKey(broken.Description);
                if (key >= 0 && key / 100 < AvatarWearable.MAX_WEARABLES) brokenTypes.Add(key / 100);
                else typeUnknown = true;
            }
        var outfitIsComplete = !typeUnknown && byType.Count > 0;

        var derived = new AvatarWearable[length];
        for (var type = 0; type < length; type++)
        {
            derived[type] = new AvatarWearable();
            if (byType.TryGetValue(type, out var list))
            {
                // A stable sort on the viewer's key: numbered links ascending, unnumbered ones after them in the
                // order the folder gave, which is what WearablesOrderComparator's "sink the invalid ones" does.
                var ordered = new List<CofWearableLink>(list);
                ordered.Sort((a, b) =>
                {
                    int ka = OrderKey(a.Description), kb = OrderKey(b.Description);
                    if (ka == kb) return list.IndexOf(a).CompareTo(list.IndexOf(b));
                    if (ka < 0) return 1;
                    if (kb < 0) return -1;
                    return ka.CompareTo(kb);
                });
                foreach (var link in ordered) derived[type].Add(link.TargetItemId, link.AssetId);
                continue;
            }

            // No link of this type. The Current Outfit decides clothing, so a clothing type with no link is not worn;
            // see the remarks on Derive for when this read is not a complete statement and the type is kept.
            if (outfitIsComplete && IsClothing(type) && !brokenTypes.Contains(type)) continue;

            // Untouched by this read: keep what the agent already wears.
            if (type < existing.Length && existing[type] is not null)
                for (var j = 0; j < existing[type].Count; j++)
                    derived[type].Add(existing[type][j].ItemID, existing[type][j].AssetID);
        }

        changed = !SameItems(existing, derived);
        return derived;
    }

    /// <summary>
    /// Shape, skin, hair and eyes (wearable types 0-3). The viewer never removes a body part link: taking an item off
    /// skips them (llappearancemgr.cpp:4224-4227) and an outfit replacement keeps the old one when the new folder has
    /// none (:2163-2170). Every other wearable type is clothing.
    /// </summary>
    private static bool IsClothing(int wearableType) => wearableType > (int)WearableType.Eyes;

    /// <summary>
    /// True when a link's own inventory type says its target was not a wearable (an object, an attachment, a
    /// gesture, a sound and so on), so a broken one says nothing about clothing. The viewer sets a link's inventory
    /// type from its target when it creates the link (<c>link_inventory_array</c>, llviewerinventory.cpp:1330 and
    /// :1355). Wearable is not in this set, and neither is Texture (0), which is also what an unset type reads as,
    /// nor any value that is not an inventory type at all: those stay unknown.
    /// </summary>
    private static bool IsKnownNonWearable(int invType) =>
        invType != (int)InventoryType.Texture && invType != (int)InventoryType.Wearable
        && invType is >= 0 and <= 127 && Enum.IsDefined((InventoryType)invType);

    /// <summary>Item ids, per type, in order: the comparison that decides whether a save is worth making.</summary>
    private static bool SameItems(AvatarWearable[] a, AvatarWearable[] b)
    {
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var ca = i < a.Length && a[i] is not null ? a[i].Count : 0;
            var cb = i < b.Length && b[i] is not null ? b[i].Count : 0;
            if (ca != cb) return false;
            for (var j = 0; j < ca; j++)
                if (a[i][j].ItemID != b[i][j].ItemID) return false;
        }
        return true;
    }
}
