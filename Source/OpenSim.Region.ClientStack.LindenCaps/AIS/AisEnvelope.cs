using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// Builds the LLSD maps the viewer parses: items, links, categories and the <c>_embedded</c> collections
/// (AIS-V3-SPEC.md §1c/§1d).
///
/// <para><b>Field set (A-Q1, resolved A1).</b> <c>LLInventoryItem::fromLLSD</c>
/// (<c>indra/llinventory/llinventory.cpp:984-1183</c>) reads exactly: <c>item_id</c> (:1004),
/// <c>parent_id</c> (:1010), <c>thumbnail</c>{<c>asset_id</c>} (:1016) or <c>thumbnail_id</c> (:1037),
/// <c>favorite</c>{<c>toggled</c>} (:1043), <c>permissions</c> (:1054), <c>sale_info</c> (:1060),
/// <c>shadow_id</c> (:1087), <c>asset_id</c> (:1094), <c>linked_id</c> (:1100, also into the asset id),
/// <c>type</c> (:1106), <c>inv_type</c> (:1122), <c>flags</c> (:1137), <c>name</c> (:1150), <c>desc</c> (:1156)
/// and <c>created_at</c> (:1162). <c>LLInventoryCategory::fromLLSD</c> (:1289-1352) reads <c>category_id</c>
/// (:1293, the label constant <c>INV_FOLDER_ID_LABEL_WS</c> = "category_id", :67), <c>parent_id</c> (:1297),
/// <c>thumbnail</c>/<c>thumbnail_id</c> (:1303-1318), <c>favorite</c>{<c>toggled</c>} (:1321),
/// <c>type</c> **or** <c>type_default</c> as an integer folder type (:1333-1344, <c>INV_ASSET_TYPE_LABEL_WS</c>
/// = "type_default", :66) and <c>name</c> (:1346). It reads neither <c>version</c> nor a descendent count —
/// <c>llaisapi.cpp</c> reads those itself (spec §1d, §1e).</para>
///
/// <para><b>Types are emitted as integers.</b> <c>fromLLSD</c> accepts a string or an integer for <c>type</c>
/// and <c>inv_type</c> (:1108-1119, :1124-1135); integers are what this tree's own shipped LLSD inventory shape
/// uses (<c>Source/OpenSim.Capabilities/LLSDInventoryItem.cs:33-68</c>), which the LL viewer consumes today over
/// FetchInventoryDescendents2, so they are both authority-legal and already proven on the wire.</para>
///
/// <para><b>UNVERIFIED.</b> The inner key names of <c>permissions</c> and <c>sale_info</c> are read by
/// <c>LLPermissions::importLLSD</c> and <c>LLSaleInfo::fromLLSD</c> in <c>llpermissions.cpp</c> /
/// <c>llsaleinfo.cpp</c>, which are not permitted reads. This file emits the key set of
/// <c>LLSDInventoryItem.cs</c> for the same reason as above: it is what the region already sends and the viewer
/// already accepts. That <c>LLViewerInventoryItem::unpackMessage(const LLSD&amp;)</c> (called at
/// <c>llaisapi.cpp:1223</c>) delegates to <c>fromLLSD</c> is likewise UNVERIFIED — <c>llviewerinventory.cpp</c>
/// is not permitted this session.</para>
/// </summary>
public static class AisEnvelope
{
    public const string Categories = "categories";
    public const string Items = "items";
    public const string Links = "links";
    public const string Embedded = "_embedded";

    /// <summary>True when the item is a link rather than a real item (links live in their own collection, §1c).</summary>
    public static bool IsLink(InventoryItemBase item)
        => item.AssetType == (int)AssetType.Link || item.AssetType == (int)AssetType.LinkFolder;

    /// <summary>The permissions sub-map, in the shape this tree already sends (LLSDInventoryItem.cs:51-62).</summary>
    private static OSDMap Permissions(InventoryItemBase item) => new()
    {
        ["creator_id"] = item.CreatorIdAsUuid,
        ["owner_id"] = item.Owner,
        ["group_id"] = item.GroupID,
        ["base_mask"] = (int)item.BasePermissions,
        ["owner_mask"] = (int)item.CurrentPermissions,
        ["group_mask"] = (int)item.GroupPermissions,
        ["everyone_mask"] = (int)item.EveryOnePermissions,
        ["next_owner_mask"] = (int)item.NextPermissions,
        ["is_owner_group"] = item.GroupOwned,
    };

    private static OSDMap SaleInfo(InventoryItemBase item) => new()
    {
        ["sale_price"] = item.SalePrice,
        ["sale_type"] = (int)item.SaleType,
    };

    /// <summary>
    /// One item. <paramref name="agentId"/> is emitted as <c>agent_id</c>: the viewer's item parse does not read
    /// it, but the fixtures and SL both carry it and it costs nothing.
    /// </summary>
    public static OSDMap Item(InventoryItemBase item, UUID agentId)
    {
        var map = new OSDMap
        {
            ["item_id"] = item.ID,
            ["parent_id"] = item.Folder,
            ["agent_id"] = agentId,
            ["asset_id"] = item.AssetID,
            ["name"] = item.Name ?? "",
            ["desc"] = item.Description ?? "",
            ["type"] = item.AssetType,
            ["inv_type"] = item.InvType,
            ["flags"] = (int)item.Flags,
            ["created_at"] = item.CreationDate,
            ["permissions"] = Permissions(item),
            ["sale_info"] = SaleInfo(item),
        };
        return map;
    }

    /// <summary>
    /// One link. Selected by <c>linked_id</c> + <c>parent_id</c> (spec §1d, <c>llaisapi.cpp:1185</c>); the target
    /// id is the link row's asset id. The viewer overwrites a link's permissions and sale info with defaults
    /// (<c>llaisapi.cpp:1278-1283</c>, <c>:1303-1307</c>), so they are not emitted. The remaining fields match the
    /// link maps the viewer itself builds for SlamFolder — <c>name</c>, <c>desc</c>, <c>linked_id</c>, <c>type</c>
    /// (<c>llappearancemgr.cpp:2230-2234</c>, A-Q3).
    /// </summary>
    public static OSDMap Link(InventoryItemBase link, UUID agentId) => new()
    {
        ["item_id"] = link.ID,
        ["parent_id"] = link.Folder,
        ["agent_id"] = agentId,
        ["linked_id"] = link.AssetID,
        ["name"] = link.Name ?? "",
        ["desc"] = link.Description ?? "",
        ["type"] = link.AssetType,
        ["inv_type"] = link.InvType,
        ["flags"] = (int)link.Flags,
        ["created_at"] = link.CreationDate,
    };

    /// <summary>
    /// One category. <c>version</c> is the folder's freshly read version (tree state T4);
    /// <paramref name="embedded"/> is attached only when this category's contents are being expanded — a category
    /// must carry all three collections or none, because the viewer derives its descendent count from having all
    /// three and refuses to version a folder without one (spec §1c/§1e, risk A-R3).
    /// </summary>
    public static OSDMap Category(InventoryFolderBase folder, UUID agentId, OSDMap embedded = null)
    {
        var map = new OSDMap
        {
            ["category_id"] = folder.ID,
            ["parent_id"] = folder.ParentID,
            ["agent_id"] = agentId,
            ["name"] = folder.Name ?? "",
            ["type_default"] = (int)folder.Type,
            ["version"] = (int)folder.Version,
        };
        if (embedded is not null) map[Embedded] = embedded;
        return map;
    }

    /// <summary>
    /// An <c>_embedded</c> map. Always all three collections, even when empty (risk A-R3): a category returned
    /// without one never gets a descendent count on the viewer and is re-fetched forever.
    /// </summary>
    public static OSDMap EmbeddedMap(OSDMap categories, OSDMap items, OSDMap links) => new()
    {
        [Categories] = categories ?? new OSDMap(),
        [Items] = items ?? new OSDMap(),
        [Links] = links ?? new OSDMap(),
    };

    /// <summary>Items keyed by item id, links excluded — callers split them first.</summary>
    public static OSDMap ItemsMap(IEnumerable<InventoryItemBase> items, UUID agentId)
    {
        var map = new OSDMap();
        if (items is null) return map;
        foreach (var item in items) map[item.ID.ToString()] = Item(item, agentId);
        return map;
    }

    /// <summary>Links keyed by the link's own id (not the target's).</summary>
    public static OSDMap LinksMap(IEnumerable<InventoryItemBase> links, UUID agentId)
    {
        var map = new OSDMap();
        if (links is null) return map;
        foreach (var link in links) map[link.ID.ToString()] = Link(link, agentId);
        return map;
    }
}
