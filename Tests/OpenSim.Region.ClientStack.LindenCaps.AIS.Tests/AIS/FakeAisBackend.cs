using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// An in-memory inventory for the AIS tests: folders and items keyed by id, with the same rules the region
/// backend has — a folder's contents are the rows whose parent is that folder, links are items with
/// <c>AssetType.Link</c>, and everything is scoped to one owner. Records what was asked of it so a test can prove
/// the handler resolved link targets with one batched call rather than N single ones.
/// </summary>
public sealed class FakeAisBackend : IAisInventoryBackend
{
    public readonly Dictionary<UUID, InventoryFolderBase> Folders = new();
    public readonly Dictionary<UUID, InventoryItemBase> Items = new();
    public readonly List<string> Calls = new();

    /// <summary>
    /// AIS-SEC-3. Invoked at the start of every backend call with the same label <see cref="Calls"/> records, and
    /// <b>before the call touches the store</b>. A test blocks in here on a <c>ManualResetEventSlim</c> to pin an
    /// interleaving exactly — a sleep-based race test passes or fails on machine load, which is worse than no test.
    ///
    /// <para>It fires outside every lock this class takes, deliberately. A thread parked in here holds nothing, so
    /// the other thread can run to completion — which is what lets the concurrency tests produce a deterministic
    /// interleaving with no two threads ever inside the store at once, and is also why the "different folders must
    /// not block each other" test cannot pass for the wrong reason.</para>
    /// </summary>
    public Action<string> BeforeCall;

    /// <summary>
    /// AIS-SEC-5. Return an exception from here to make a named backend call <b>throw</b>, which is the only way
    /// to reach <c>AisHandler.Dispatch</c>'s unexpected-exception path. The existing gates all return <c>bool</c>
    /// and model a refusal, not a fault - a database or connector error is a fault, and that is the path whose
    /// error hygiene AIS-SEC-5 is about.
    /// </summary>
    public Func<string, Exception> ThrowOn;

    /// <summary>Records the call, then gives the hooks a chance to park or fault this thread.</summary>
    private void Record(string label)
    {
        lock (Calls) Calls.Add(label);   // two threads append in the concurrency tests
        BeforeCall?.Invoke(label);
        Exception fault = ThrowOn?.Invoke(label);
        if (fault is not null) throw fault;
    }

    /// <summary>A snapshot of <see cref="Calls"/> safe to take while another thread may still be recording.</summary>
    public IReadOnlyList<string> CallSnapshot() { lock (Calls) return Calls.ToList(); }

    /// <summary>
    /// The subset of <see cref="Calls"/> that could change inventory — every backend member that writes, whether
    /// or not it went on to succeed, because the member being <i>reached at all</i> is what AIS-SEC-2 is about.
    /// A malformed body must leave this empty: not "a write that failed", but no write attempted.
    /// </summary>
    public IReadOnlyList<string> Writes => Calls.Where(c =>
        c.StartsWith("AddFolder(") || c.StartsWith("AddItem(") ||
        c.StartsWith("UpdateItem(") || c.StartsWith("UpdateFolder(") ||
        c.StartsWith("DeleteItems[") || c.StartsWith("DeleteFolders[") ||
        c.StartsWith("PurgeFolder(") || c.StartsWith("ApplyAssetTransaction(")).ToList();

    public UUID Owner;
    public UUID CurrentOutfitId = UUID.Zero;

    public FakeAisBackend(UUID owner) { Owner = owner; }

    // ---------------- building ----------------

    public InventoryFolderBase AddFolder(UUID id, UUID parent, string name, int version = 1, short type = -1)
    {
        var folder = new InventoryFolderBase(id, name, Owner, type, parent, (ushort)version);
        Folders[id] = folder;
        return folder;
    }

    public InventoryItemBase AddItem(UUID id, UUID folder, string name, int assetType = (int)AssetType.Clothing, UUID assetId = default)
    {
        var item = new InventoryItemBase(id, Owner)
        {
            Folder = folder,
            Name = name,
            Description = "",
            AssetType = assetType,
            InvType = (int)InventoryType.Wearable,
            AssetID = assetId.IsZero() ? UUID.Random() : assetId,
            CreationDate = 1756900000,
            Flags = 0,
            CreatorId = Owner.ToString(),
            BasePermissions = 0x7fffffff,
            CurrentPermissions = 0x7fffffff,
            NextPermissions = 532480,
            SalePrice = 0,
            SaleType = 0,
        };
        Items[id] = item;
        return item;
    }

    /// <summary>A link row: an item of <c>AssetType.Link</c> whose asset id is the target item's id.</summary>
    public InventoryItemBase AddLink(UUID id, UUID folder, string name, UUID target)
        => AddItem(id, folder, name, (int)AssetType.Link, target);

    // ---------------- IAisInventoryBackend ----------------

    public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type)
    {
        Record($"GetFolderForType({type})");
        if (agentId != Owner) return null;
        if (type == FolderType.CurrentOutfit && !CurrentOutfitId.IsZero())
            return Folders.TryGetValue(CurrentOutfitId, out var cof) ? cof : null;
        return Folders.Values.FirstOrDefault(f => f.Type == (short)type);
    }

    public InventoryFolderBase GetFolder(UUID agentId, UUID folderId)
    {
        Record($"GetFolder({folderId})");
        if (agentId != Owner) return null;
        return Folders.TryGetValue(folderId, out var folder) ? folder : null;
    }

    public InventoryCollection GetFolderContent(UUID agentId, UUID folderId)
    {
        Record($"GetFolderContent({folderId})");
        if (agentId != Owner || !Folders.TryGetValue(folderId, out var folder)) return null;
        return new InventoryCollection
        {
            OwnerID = Owner,
            FolderID = folderId,
            Version = folder.Version,
            Folders = Folders.Values.Where(f => f.ParentID == folderId).ToList(),
            Items = Items.Values.Where(i => i.Folder == folderId).ToList(),
        };
    }

    public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId)
    {
        Record($"GetSubFolders({folderId})");
        if (agentId != Owner || !Folders.ContainsKey(folderId)) return Array.Empty<InventoryFolderBase>();
        return Folders.Values.Where(f => f.ParentID == folderId).ToList();
    }

    public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId)
    {
        Record("GetInventorySkeleton");
        return agentId != Owner ? Array.Empty<InventoryFolderBase>() : Folders.Values.ToList();
    }

    public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds)
    {
        Record($"GetItems[{itemIds.Count}]");
        if (agentId != Owner) return Array.Empty<InventoryItemBase>();
        var found = new List<InventoryItemBase>();
        foreach (var id in itemIds) if (Items.TryGetValue(id, out var item)) found.Add(item);
        return found;
    }

    public InventoryItemBase GetItem(UUID agentId, UUID itemId)
    {
        Record($"GetItem({itemId})");
        if (agentId != Owner) return null;
        return Items.TryGetValue(itemId, out var item) ? item : null;
    }

    // ---------------- mutators (A2) ----------------

    /// <summary>Set to false to make the service refuse writes, as XInventoryService does when AllowDelete is off.</summary>
    public bool AllowWrite = true;

    /// <summary>
    /// Set to true to reproduce this tree's real DeleteFolders behaviour: the two-argument overload on
    /// IInventoryService is onlyIfTrash = true, so a folder outside Trash is silently skipped and true is still
    /// returned (XInventoryService.cs:459-478).
    /// </summary>
    public bool DeleteFoldersOnlyIfTrash = false;

    /// <summary>Fault injection: return false to make this AddItem fail. Null means every add succeeds.</summary>
    public Func<InventoryItemBase, bool> AddItemGate;

    /// <summary>
    /// Fault injection: return false to make this AddFolder fail. Null means every add succeeds. Added for
    /// AIS-SEC-4, which needs a create to fail on the <i>second</i> of three categories - the whole point being
    /// what the response says about the first one.
    /// </summary>
    public Func<InventoryFolderBase, bool> AddFolderGate;

    /// <summary>Fault injection: return false to make this PurgeFolder fail. Null means it succeeds.</summary>
    public Func<InventoryFolderBase, bool> PurgeFolderGate;

    /// <summary>Fault injection: return false to make this DeleteFolders fail. Null means it succeeds.</summary>
    public Func<IReadOnlyList<UUID>, bool> DeleteFoldersGate;

    /// <summary>Fault injection: return false to make this DeleteItems fail. Null means every delete succeeds.</summary>
    public Func<IReadOnlyList<UUID>, bool> DeleteItemsGate;

    /// <summary>Runs after every successful write, so a test can change the store underneath the handler.</summary>
    public Action OnWrite;

    /// <summary>The data layer bumps a folder's version on every store or delete of its contents (S0a V6).</summary>
    private void Bump(UUID folderId)
    {
        if (Folders.TryGetValue(folderId, out var folder)) folder.Version = (ushort)(folder.Version + 1);
    }

    public bool AddFolder(InventoryFolderBase folder)
    {
        Record($"AddFolder({folder.ID})");
        if (!AllowWrite) return false;
        if (AddFolderGate is not null && !AddFolderGate(folder)) return false;
        Folders[folder.ID] = folder;
        Bump(folder.ParentID);
        OnWrite?.Invoke();
        return true;
    }

    public bool AddItem(InventoryItemBase item)
    {
        Record($"AddItem({item.Name})");
        if (!AllowWrite) return false;
        if (AddItemGate is not null && !AddItemGate(item)) return false;
        Items[item.ID] = item;
        Bump(item.Folder);
        OnWrite?.Invoke();
        return true;
    }

    /// <summary>
    /// Asset transactions the region has completed: transaction id -> the asset it uploaded. A16 —
    /// <see cref="ApplyAssetTransaction"/> stands in for the region's asset-transaction module, whose real
    /// behaviour is to set the item's asset and store the item itself (<c>AssetXferUploader.cs:425-430</c>).
    /// </summary>
    public readonly Dictionary<UUID, UUID> Transactions = new();

    /// <summary>False when this backend cannot resolve transactions at all, as the library backend cannot.</summary>
    public bool ResolvesTransactions = true;

    /// <summary>
    /// A19: transactions the region's validator REFUSES - the uploaded asset referenced something the resident may
    /// not use, so nothing is stored and the item keeps the asset it had. Distinct from a transaction this backend
    /// cannot resolve at all, which is not a failure.
    /// </summary>
    public readonly HashSet<UUID> RefusedTransactions = new();

    public AisAssetTransaction ApplyAssetTransaction(UUID agentId, UUID transactionId, InventoryItemBase item)
    {
        Record($"ApplyAssetTransaction({transactionId})");
        if (!ResolvesTransactions || agentId != Owner) return AisAssetTransaction.NotResolvable;
        if (RefusedTransactions.Contains(transactionId)) return AisAssetTransaction.Refused;
        // An unknown transaction opens a pending uploader and the asset lands with the xfer; nothing is stored yet.
        if (!Transactions.TryGetValue(transactionId, out var assetId)) return AisAssetTransaction.Applied;
        item.AssetID = assetId;
        UpdateItem(item);
        return AisAssetTransaction.Applied;
    }

    /// <summary>S9: every (item, newAsset) the handler reported as an asset change, in order.</summary>
    public readonly List<(UUID Item, UUID Asset)> AssetChanges = new();

    public void OnItemAssetChanged(UUID agentId, UUID itemId, UUID newAssetId)
    {
        Record($"OnItemAssetChanged({itemId})");
        if (agentId == Owner) AssetChanges.Add((itemId, newAssetId));
    }

    public bool UpdateItem(InventoryItemBase item)
    {
        Record($"UpdateItem({item.ID})");
        if (!AllowWrite || !Items.ContainsKey(item.ID)) return false;
        Items[item.ID] = item;
        Bump(item.Folder);
        OnWrite?.Invoke();
        return true;
    }

    public bool UpdateFolder(InventoryFolderBase folder)
    {
        Record($"UpdateFolder({folder.ID})");
        if (!AllowWrite || !Folders.ContainsKey(folder.ID)) return false;
        Folders[folder.ID] = folder;
        Bump(folder.ParentID);
        OnWrite?.Invoke();
        return true;
    }

    public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds)
    {
        Record($"DeleteItems[{itemIds.Count}]");
        if (!AllowWrite || agentId != Owner) return false;
        if (DeleteItemsGate is not null && !DeleteItemsGate(itemIds)) return false;
        foreach (var id in itemIds)
            if (Items.TryGetValue(id, out var item)) { Items.Remove(id); Bump(item.Folder); }
        OnWrite?.Invoke();
        return true;
    }

    /// <summary>Recursive, and with the real service's trash gate available for a test to switch on.</summary>
    public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash)
    {
        Record($"DeleteFolders[{folderIds.Count}, onlyIfTrash={onlyIfTrash}]");
        if (!AllowWrite || agentId != Owner) return false;
        if (DeleteFoldersGate is not null && !DeleteFoldersGate(folderIds)) return false;
        foreach (var id in folderIds)
        {
            if (!Folders.TryGetValue(id, out var folder)) continue;
            if (onlyIfTrash && DeleteFoldersOnlyIfTrash && !UnderTrash(id)) continue;   // as the real service does
            Purge(id);
            Folders.Remove(id);
            Bump(folder.ParentID);
        }
        OnWrite?.Invoke();
        return true;
    }

    public bool PurgeFolder(InventoryFolderBase folder)
    {
        Record($"PurgeFolder({folder.ID})");
        if (!AllowWrite) return false;
        if (PurgeFolderGate is not null && !PurgeFolderGate(folder)) return false;
        Purge(folder.ID);
        Bump(folder.ID);
        OnWrite?.Invoke();
        return true;
    }

    /// <summary>Everything under a folder, depth first.</summary>
    private void Purge(UUID folderId)
    {
        foreach (var child in Folders.Values.Where(f => f.ParentID == folderId).Select(f => f.ID).ToList())
        {
            Purge(child);
            Folders.Remove(child);
        }
        foreach (var item in Items.Values.Where(i => i.Folder == folderId).Select(i => i.ID).ToList())
            Items.Remove(item);
    }

    /// <summary>The Trash / Lost And Found test the real service applies before it will delete a folder.</summary>
    public UUID TrashId = UUID.Zero;
    private bool UnderTrash(UUID folderId)
    {
        var id = folderId;
        for (var guard = 0; guard < 64 && Folders.TryGetValue(id, out var folder); guard++)
        {
            if (folder.ParentID == TrashId && !TrashId.IsZero()) return true;
            id = folder.ParentID;
        }
        return false;
    }
}
