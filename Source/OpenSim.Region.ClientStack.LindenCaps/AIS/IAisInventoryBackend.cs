using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// Everything the AIS v3 routes need from inventory, and nothing else. The handler is written against this
/// interface only (Ledger P-2): no Scene, no ScenePresence. Phase 1 implements it over the region's
/// <c>IInventoryService</c>; Phase 2 hosts the same handler on Robust over the service directly.
///
/// A0 defines the surface; every member is implemented in A1+. Links are ordinary <see cref="InventoryItemBase"/>
/// rows with <c>AssetType.Link</c> / <c>AssetType.LinkFolder</c>; the handler splits them out of item lists into
/// the <c>_embedded.links</c> collection (spec §1c), and resolves their targets with <see cref="GetItems"/>
/// (tree state T5: the service has no link-aware fetch).
/// </summary>
public interface IAisInventoryBackend
{
    /// <summary>The agent's folder of a system type (spec §1b: "current" = <c>FolderType.CurrentOutfit</c>); null if absent.</summary>
    InventoryFolderBase GetFolderForType(UUID agentId, FolderType type);

    /// <summary>A folder with its current <c>Version</c> freshly read (tree state T4); null if absent or not the agent's.</summary>
    InventoryFolderBase GetFolder(UUID agentId, UUID folderId);

    /// <summary>A folder's direct children: sub-folders and items (links included in <c>Items</c>), with the folder's version.</summary>
    InventoryCollection GetFolderContent(UUID agentId, UUID folderId);

    /// <summary>A folder's direct sub-folders only (GET /category/{id}/categories). Empty when the folder is absent.</summary>
    IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId);

    /// <summary>
    /// Every folder the agent owns, parents included — the inventory skeleton
    /// (<c>IInventoryService.GetInventorySkeleton</c>). Used only to find folders whose parent no longer exists
    /// (GET /orphans); there is no cheaper orphan query in the service.
    /// </summary>
    IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId);

    /// <summary>Items by id, e.g. link targets; absent ids are simply missing from the result.</summary>
    IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds);

    /// <summary>One item (or link) by id; null if absent or not the agent's.</summary>
    InventoryItemBase GetItem(UUID agentId, UUID itemId);

    /// <summary>Create a folder under its <c>ParentID</c>. The data layer bumps the parent's version (S0a V6).</summary>
    bool AddFolder(InventoryFolderBase folder);

    /// <summary>Create an item or link under its <c>Folder</c>. Bumps the parent's version (S0a V6).</summary>
    bool AddItem(InventoryItemBase item);

    /// <summary>Update an item's mutable fields (name, description, flags, asset, permissions).</summary>
    bool UpdateItem(InventoryItemBase item);

    /// <summary>
    /// Resolve a <c>hash_id</c> — an asset transaction id — to the asset that transaction uploaded, apply it to
    /// <paramref name="item"/> and store the item (A16).
    ///
    /// <para>
    /// This is the one thing the AIS routes cannot do through <c>IInventoryService</c> alone. A wearable save
    /// uploads its asset over the xfer protocol under a transaction id and then PATCHes the item, and the viewer
    /// sends the transaction id rather than the asset id: <c>LLViewerInventoryItem::updateServer</c>
    /// (<c>llviewerinventory.cpp:435-454</c>) erases <c>asset_id</c> and <c>shadow_id</c> from the body and puts
    /// <c>hash_id</c> in their place. Only the region's asset-transaction module knows which asset that
    /// transaction produced, so the region backend hands the pair to it, exactly as the legacy UDP path does
    /// (<c>Scene.Inventory.cs:579-582</c>).
    /// </para>
    ///
    /// <para>
    /// A19: the answer is a three-way <see cref="AisAssetTransaction"/>, not a bool. <c>NotResolvable</c> is what
    /// a backend that resolves no transactions at all returns — the library, or a region with no transaction
    /// module or no connected client for the agent — and the item's asset is simply left alone. <c>Applied</c>
    /// means the transaction was handed over; the module stores the item itself once the xfer completes, so the
    /// caller must re-read the item rather than trust the copy it passed in. <c>Refused</c> means the region
    /// validated the uploaded asset and said no, which is a <b>failed save</b>.
    /// </para>
    /// </summary>
    AisAssetTransaction ApplyAssetTransaction(UUID agentId, UUID transactionId, InventoryItemBase item);

    /// <summary>
    /// A worn wearable's asset just changed (S9). The region points the presence's wearable at the new asset and
    /// queues an appearance save; the save resolves every worn item afresh, persists the result and raises the
    /// S5 change trigger, and the bake's own input hash then decides whether anything is recomputed - so an edit
    /// that changed nothing visible costs one hash check per channel and no compositing.
    ///
    /// <para>
    /// This exists because it is the only reliable signal. Editing a wearable leaves the worn set unchanged, so
    /// no <c>AgentIsNowWearing</c> follows, and the viewer's <c>UpdateAvatarAppearance</c> POST is deferred behind
    /// pending uploads and can arrive stale. Backends with no presence to update - the library, Phase 2 on
    /// Robust - do nothing.
    /// </para>
    /// </summary>
    void OnItemAssetChanged(UUID agentId, UUID itemId, UUID newAssetId);

    /// <summary>Update a folder's mutable fields (name, type, parent on move).</summary>
    bool UpdateFolder(InventoryFolderBase folder);

    /// <summary>Delete items (and links) by id. Bumps each parent's version (S0a V6).</summary>
    bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds);

    /// <summary>
    /// Delete folders by id, recursively. <paramref name="onlyIfTrash"/> is the inventory service's Trash
    /// restriction: with it true a folder outside Trash or Lost And Found is silently skipped and the call still
    /// succeeds. AIS passes **false** — the LL viewer deletes any non-protected folder wherever it sits — which is
    /// why <c>IInventoryService</c> gained the three-argument overload in A2b (Ledger A-Q9).
    /// </summary>
    bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash);

    /// <summary>Delete a folder's contents but keep the folder (AIS PurgeDescendents).</summary>
    bool PurgeFolder(InventoryFolderBase folder);
}

/// <summary>
/// A19. What became of a <c>hash_id</c>. The three states have to be distinct because two of them are fine and
/// one is a failed save, and the bool this replaced could not tell them apart - which is how a refused wearable
/// update came to be answered <c>200</c>.
/// </summary>
public enum AisAssetTransaction
{
    /// <summary>The asset was applied to the item, or the xfer is still in flight and will apply it when it lands.</summary>
    Applied,

    /// <summary>
    /// Nothing to apply and nothing refused: this backend resolves no transactions at all (the library, or a
    /// region with no asset-transaction module), or the agent has no client here. The rest of the PATCH stands
    /// and the cap still answers <c>200</c> - an unknown transaction id is not an error, the uploader opens a
    /// pending xfer for it (<c>AgentAssetsTransactions.cs:68-90</c>).
    /// </summary>
    NotResolvable,

    /// <summary>
    /// The region validated the uploaded asset and <b>refused</b> it, so the asset was not stored and the item
    /// still points where it did. This is a failed save and the cap must say so.
    /// </summary>
    Refused,
}
