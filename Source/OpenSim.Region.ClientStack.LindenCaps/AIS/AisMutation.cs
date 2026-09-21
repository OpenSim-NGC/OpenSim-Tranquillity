using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// Applying a PATCH body to an item or a folder, and building the delta envelope the viewer applies
/// (AIS-V3-SPEC.md §1d-bis). Pure: no backend, no I/O, so the field rules are unit-testable on their own.
/// </summary>
public static class AisMutation
{
    /// <summary>
    /// Fields of an item PATCH this tree can store. The viewer sends the item's **whole** <c>asLLSD()</c> map with
    /// <c>asset_id</c>/<c>shadow_id</c> swapped for <c>hash_id</c> (A-Q3, <c>llviewerinventory.cpp:435-454</c> and
    /// <c>:1399-1422</c>), so most keys in a body are just the item as the viewer already had it. Anything not
    /// listed here is ignored rather than refused — refusing would fail every ordinary rename.
    ///
    /// <para>
    /// <c>hash_id</c> is listed but is not applied here: it is a transaction id, and only the region's
    /// asset-transaction module knows which asset it produced. <see cref="ApplyToItem"/> reports it and the
    /// handler hands it to <see cref="IAisInventoryBackend.ApplyAssetTransaction"/> (A16).
    /// </para>
    ///
    /// <para>
    /// <c>shadow_id</c> is deliberately absent. It is the obfuscated form <c>asLLSD</c> emits instead of
    /// <c>asset_id</c> for a restricted-permission item (<c>llinventory.cpp:952-964</c>), and both of the two
    /// callers that build an update body erase it before sending (<c>llviewerinventory.cpp:445-452</c>,
    /// <c>:1414-1421</c>), so no PATCH this tree can receive from a stock viewer carries one.
    /// </para>
    /// </summary>
    public static readonly string[] ItemFields = { "name", "desc", "sale_info", "flags", "asset_id", "hash_id", "permissions" };

    /// <summary>
    /// Fields of a category PATCH this tree can store. The viewer sends the category's whole <c>asLLSD()</c> for a
    /// rename or a type change, or a single-key <c>{thumbnail}</c> / <c>{favorite}</c> map for a protected folder
    /// (<c>llviewerinventory.cpp:651-665</c>, <c>:866-884</c>, <c>:1436-1457</c>). <c>thumbnail</c> and
    /// <c>favorite</c> have no column in this tree's inventory, so they are accepted and dropped.
    /// </summary>
    public static readonly string[] CategoryFields = { "name" };

    /// <summary>What a patch actually changed, for the log and for deciding whether to write at all.</summary>
    /// <param name="Transaction">
    /// The body's <c>hash_id</c>, if it carried one. Not a field this class can apply — resolving a transaction
    /// id to the asset it uploaded needs the region's asset-transaction module — so it is reported for the
    /// handler to hand to <see cref="IAisInventoryBackend.ApplyAssetTransaction"/> (A16).
    /// </param>
    public sealed record Applied(IReadOnlyList<string> Changed, IReadOnlyList<string> Ignored, UUID Transaction = default)
    {
        public bool Any => Changed.Count > 0;
    }

    /// <summary>
    /// The permission masks a client may set, each first masked by the item's own <c>BasePermissions</c>. Base
    /// and current are never taken from a request body. This is the tree's existing rule for a client-supplied
    /// item update, lifted from the legacy UDP path (<c>Scene.Inventory.cs:497-548</c>) so both routes agree.
    ///
    /// <para>
    /// One simplification against that path, deliberate: it also forces full next-owner permissions when the
    /// everyone mask gains <c>Export</c>, and denies the change outright for a non-creator. Here the export bit
    /// is simply preserved as it stands — masking by base already prevents granting an export the item does not
    /// have, and preserving it prevents removing one through a route that was never asked to manage exports.
    /// </para>
    /// </summary>
    private static bool ApplyPermissions(OSDMap perms, InventoryItemBase item)
    {
        const uint Export = (uint)OpenSim.Framework.PermissionMask.Export;   // Scene.Inventory.cs uses this one
        var next = perms.ContainsKey("next_owner_mask") ? (uint)perms["next_owner_mask"].AsInteger() : item.NextPermissions;
        var everyone = perms.ContainsKey("everyone_mask") ? (uint)perms["everyone_mask"].AsInteger() : item.EveryOnePermissions;
        var group = perms.ContainsKey("group_mask") ? (uint)perms["group_mask"].AsInteger() : item.GroupPermissions;

        next &= item.BasePermissions;
        everyone &= item.BasePermissions;
        group &= item.BasePermissions;
        everyone = (everyone & ~Export) | (item.EveryOnePermissions & Export);   // the export bit stands as it is

        if (next == item.NextPermissions && everyone == item.EveryOnePermissions && group == item.GroupPermissions)
            return false;

        item.NextPermissions = next;
        item.EveryOnePermissions = everyone;
        item.GroupPermissions = group;
        return true;
    }

    /// <summary>
    /// Applies the storable fields of <paramref name="body"/> to <paramref name="item"/> and reports what it did.
    /// <c>parent_id</c> is deliberately **not** applied: a move changes two folders' versions and is not part of
    /// A2 (see the session's decisions), so a body whose parent differs is treated as an unchanged parent.
    /// </summary>
    public static Applied ApplyToItem(OSDMap body, InventoryItemBase item)
    {
        var changed = new List<string>();
        var ignored = new List<string>();
        var transaction = UUID.Zero;
        foreach (var key in body.Keys)
        {
            switch (key)
            {
                case "name":
                    var name = body["name"].AsString() ?? "";
                    if (name != item.Name) { item.Name = name; changed.Add(key); }
                    break;
                case "desc":
                    var desc = body["desc"].AsString() ?? "";
                    if (desc != item.Description) { item.Description = desc; changed.Add(key); }
                    break;
                case "flags":
                    var flags = (uint)body["flags"].AsInteger();
                    if (flags != item.Flags) { item.Flags = flags; changed.Add(key); }
                    break;
                case "asset_id":
                    // The asset the viewer wants this item to point at. A16: dropping this answered 200 and left
                    // the wearable pointing at its previous asset, so an outfit edit survived only in the
                    // viewer's cache until it re-read the item.
                    var assetId = body["asset_id"].AsUUID();
                    if (assetId != item.AssetID) { item.AssetID = assetId; changed.Add(key); }
                    break;
                case "hash_id":
                    // A transaction id, not an asset id (llviewerinventory.cpp:435-454). Reported, not applied.
                    transaction = body["hash_id"].AsUUID();
                    break;
                case "permissions":
                    if (body["permissions"] is OSDMap perms && ApplyPermissions(perms, item)) changed.Add(key);
                    break;
                case "sale_info":
                    if (body["sale_info"] is OSDMap sale)
                    {
                        var price = sale.ContainsKey("sale_price") ? sale["sale_price"].AsInteger() : item.SalePrice;
                        var type = sale.ContainsKey("sale_type") ? (byte)sale["sale_type"].AsInteger() : item.SaleType;
                        if (price != item.SalePrice || type != item.SaleType)
                        {
                            item.SalePrice = price;
                            item.SaleType = type;
                            changed.Add(key);
                        }
                    }
                    break;
                default:
                    ignored.Add(key);
                    break;
            }
        }
        return new Applied(changed, ignored, transaction);
    }

    /// <summary>Applies the storable fields of a category PATCH. <c>type_default</c> is ignored: see the decisions.</summary>
    public static Applied ApplyToFolder(OSDMap body, InventoryFolderBase folder)
    {
        var changed = new List<string>();
        var ignored = new List<string>();
        foreach (var key in body.Keys)
        {
            if (key == "name")
            {
                var name = body["name"].AsString() ?? "";
                if (name != folder.Name) { folder.Name = name; changed.Add(key); }
            }
            else ignored.Add(key);
        }
        return new Applied(changed, ignored);
    }

    // ------------------------------------------------------------------ the delta envelope

    public const string CategoriesRemoved = "_categories_removed";
    public const string RemovedItems = "_removed_items";
    public const string UpdatedCategoryVersions = "_updated_category_versions";
    public const string CreatedItems = "_created_items";
    public const string CreatedCategories = "_created_categories";
    public const string CategoryItemsRemoved = "_category_items_removed";
    public const string BrokenLinksRemoved = "_broken_links_removed";

    /// <summary>
    /// Every delta key of spec §1d-bis, in the order a reader wants them: what was made, then what went, then the
    /// versions that gate both. The response logging walks this, so the log cannot drift from the contract.
    /// </summary>
    public static readonly string[] DeltaKeys =
    {
        CreatedCategories, CreatedItems,
        CategoriesRemoved, RemovedItems, CategoryItemsRemoved, BrokenLinksRemoved,
        UpdatedCategoryVersions,
    };

    /// <summary>
    /// A one-line rendering of a mutation response's deltas, for the DEBUG log. Absent keys are omitted, so a
    /// response that reported nothing prints as <c>no deltas</c> — which is the interesting case, because a
    /// mutation that changed a folder and said nothing is exactly how a viewer's model goes stale (§1d-bis).
    ///
    /// <para><b>Only ever call this inside an <c>IsEnabled(LogLevel.Debug)</c> guard.</b> It allocates a builder
    /// and walks the whole envelope; with DEBUG off none of it should run.</para>
    /// </summary>
    public static string SummariseDeltas(OSDMap body)
    {
        if (body is null || body.Count == 0) return "empty body";

        var sb = new StringBuilder();

        // the content object a response carries at top level, when it has one
        foreach (var idKey in new[] { "category_id", "item_id" })
            if (body.TryGetValue(idKey, out var id))
                sb.Append(idKey).Append('=').Append(id.AsUUID()).Append(' ');

        foreach (var key in DeltaKeys)
        {
            if (!body.TryGetValue(key, out var value)) continue;
            sb.Append(key).Append('=');
            switch (value)
            {
                case OSDArray array:
                    sb.Append('[');
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(array[i].AsUUID());
                    }
                    sb.Append(']');
                    break;
                case OSDMap map:      // _updated_category_versions: folder -> version
                    sb.Append('{');
                    var first = true;
                    foreach (string folder in map.Keys)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(folder).Append(':').Append(map[folder].AsInteger());
                    }
                    sb.Append('}');
                    break;
                default:
                    sb.Append(value.AsString());
                    break;
            }
            sb.Append(' ');
        }

        return sb.Length == 0 ? "no deltas" : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Adds <paramref name="folder"/>'s freshly read version to the response's
    /// <c>_updated_category_versions</c>. Every mutation must list every folder whose contents it changed, and the
    /// zero-delta entries the viewer creates for an update are discarded unless the folder is listed
    /// (<c>llaisapi.cpp:1625-1629</c>). Never list a folder the viewer may not have: that is a null dereference in
    /// <c>doUpdate</c> (<c>:1760-1762</c>, Ledger A-R6).
    /// </summary>
    public static void ReportVersion(OSDMap response, InventoryFolderBase folder)
    {
        if (folder is null) return;
        if (response[UpdatedCategoryVersions] is not OSDMap versions)
        {
            versions = new OSDMap();
            response[UpdatedCategoryVersions] = versions;
        }
        versions[folder.ID.ToString()] = (int)folder.Version;
    }

    /// <summary>Adds an id to one of the removal arrays, creating it on first use.</summary>
    public static void ReportRemoved(OSDMap response, string key, UUID id)
    {
        if (response[key] is not OSDArray array)
        {
            array = new OSDArray();
            response[key] = array;
        }
        array.Add(OSD.FromUUID(id));
    }
}
