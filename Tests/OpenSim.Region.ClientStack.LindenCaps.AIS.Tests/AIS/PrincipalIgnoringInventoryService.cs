using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// An <see cref="IInventoryService"/> that reproduces the one behaviour of <c>XInventoryService</c> the AIS backend
/// has to defend against: <b>it looks objects up by UUID and disregards the principal it was handed</b>. Every read
/// and every delete here answers for any row in the store regardless of who owns it, exactly as the real service
/// does - <c>GetItem</c> queries <c>inventoryID</c> alone (<c>XInventoryService.cs:633-641</c>), <c>GetFolder</c>
/// queries <c>folderID</c> alone (<c>:653-663</c>), <c>GetFolderContent</c> says so in a comment (<i>"This method
/// doesn't receive a valud principal id from the connector. So we disregard the principal and look by ID"</i>,
/// <c>:319-323</c>), <c>DeleteFolders</c> says <i>"Ignore principal ID, it's bogus at connector level"</i>
/// (<c>:482-492</c>) and <c>DeleteItems</c> says <i>"Just use the ID... *facepalms*"</i> (<c>:602-631</c>).
///
/// <para>
/// This is the opposite of <see cref="FakeAisBackend"/>, which enforces owner scoping itself and therefore cannot
/// see the AIS-SEC-1 defect at all. Running the <b>real</b> <c>AISv3Module.InventoryServiceBackend</c> over this
/// double is what makes the cross-user cases in <c>AisCrossUserHttpTests</c> mean anything.
/// </para>
///
/// <para><b>Two deliberate over-approximations</b>, recorded so nobody reads this file as a description of the real
/// service:</para>
/// <list type="bullet">
///   <item><c>GetInventorySkeleton</c> here returns <b>every</b> folder in the store. The real one does filter by
///   <c>agentID</c> (<c>XInventoryService.cs:233-237</c>). The double is harsher on purpose: it makes the backend
///   filter the skeleton itself rather than inherit a guarantee from the connector, which is the same assumption
///   the defect was made of.</item>
///   <item><c>GetFolderForType</c> here returns the first folder of that type owned by anyone. The real one
///   resolves through <c>GetRootFolder(principalID)</c> (<c>:277-292</c>) and so is scoped. Same reason.</item>
/// </list>
///
/// <para>Mutations store whatever they are given, without an ownership opinion - again as the real service does.
/// Members the AIS backend never calls throw <see cref="NotImplementedException"/> rather than pretend.</para>
/// </summary>
public sealed class PrincipalIgnoringInventoryService : IInventoryService
{
    public readonly Dictionary<UUID, InventoryFolderBase> Folders = new();
    public readonly Dictionary<UUID, InventoryItemBase> Items = new();

    // ---------------- seeding, by owner, straight into the store ----------------

    public InventoryFolderBase Seed(UUID id, UUID owner, UUID parent, string name, short type, int version = 1)
    {
        var folder = new InventoryFolderBase(id, name, owner, type, parent, (ushort)version);
        Folders[id] = folder;
        return folder;
    }

    public InventoryItemBase SeedItem(UUID id, UUID owner, UUID folder, string name,
        int assetType = (int)AssetType.Clothing, UUID assetId = default)
    {
        var item = new InventoryItemBase(id, owner)
        {
            Folder = folder,
            Name = name,
            Description = "",
            AssetType = assetType,
            InvType = (int)InventoryType.Wearable,
            AssetID = assetId.IsZero() ? UUID.Random() : assetId,
            CreationDate = 1756900000,
            Flags = 0,
            CreatorId = owner.ToString(),
            BasePermissions = 0x7fffffff,
            CurrentPermissions = 0x7fffffff,
            NextPermissions = 532480,
        };
        Items[id] = item;
        return item;
    }

    /// <summary>A link row: an item of <c>AssetType.Link</c> whose asset id is the target item's id.</summary>
    public InventoryItemBase SeedLink(UUID id, UUID owner, UUID folder, string name, UUID target)
        => SeedItem(id, owner, folder, name, (int)AssetType.Link, target);

    // ---------------- the reads that disregard the principal ----------------

    /// <summary>By id alone (<c>XInventoryService.cs:633-641</c>).</summary>
    public InventoryItemBase GetItem(UUID userID, UUID itemID)
        => Items.TryGetValue(itemID, out var item) ? item : null;

    /// <summary>By id alone (<c>:653-663</c>).</summary>
    public InventoryFolderBase GetFolder(UUID userID, UUID folderID)
        => Folders.TryGetValue(folderID, out var folder) ? folder : null;

    /// <summary>One slot per requested id, <c>null</c> where the id is unknown (<c>:643-651</c>).</summary>
    public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] ids)
    {
        var found = new InventoryItemBase[ids.Length];
        for (var i = 0; i < ids.Length; i++) found[i] = GetItem(userID, ids[i]);
        return found;
    }

    /// <summary>By parent id alone; the owner and version come off the folder row (<c>:319-361</c>).</summary>
    public InventoryCollection GetFolderContent(UUID userID, UUID folderID)
    {
        var collection = new InventoryCollection
        {
            OwnerID = userID,
            FolderID = folderID,
            Folders = Folders.Values.Where(f => f.ParentID == folderID).ToList(),
            Items = Items.Values.Where(i => i.Folder == folderID).ToList(),
        };
        if (Folders.TryGetValue(folderID, out var folder))
        {
            collection.Version = folder.Version;
            collection.OwnerID = folder.Owner;
        }
        return collection;
    }

    /// <summary>Every folder in the store - see the over-approximation note on the class.</summary>
    public List<InventoryFolderBase> GetInventorySkeleton(UUID userId) => Folders.Values.ToList();

    /// <summary>The first folder of that type owned by anyone - see the over-approximation note on the class.</summary>
    public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        => Folders.Values.FirstOrDefault(f => f.Type == (short)type);

    // ---------------- the deletes that disregard the principal ----------------

    /// <summary>By id alone, and true whatever happened (<c>:602-631</c>).</summary>
    public bool DeleteItems(UUID userID, List<UUID> itemIDs)
    {
        foreach (var id in itemIDs) Items.Remove(id);
        return true;
    }

    public bool DeleteFolders(UUID userID, List<UUID> folderIDs) => DeleteFolders(userID, folderIDs, true);

    /// <summary>By id alone, purging each folder first, and true whatever happened (<c>:482-503</c>).</summary>
    public bool DeleteFolders(UUID userID, List<UUID> folderIDs, bool onlyIfTrash)
    {
        foreach (var id in folderIDs)
        {
            if (!Folders.TryGetValue(id, out var folder)) continue;
            if (onlyIfTrash && !ParentIsTrashOrLost(folder)) continue;
            PurgeFolder(folder);
            Folders.Remove(id);
        }
        return true;
    }

    /// <summary>
    /// By id alone, recursively. The trash restriction the real one-argument form applies (<c>:503-528</c>) is
    /// deliberately not reproduced here: it would only mask the ownership question this double exists to ask.
    /// </summary>
    public bool PurgeFolder(InventoryFolderBase folder)
    {
        foreach (var child in Folders.Values.Where(f => f.ParentID == folder.ID).ToList())
        {
            PurgeFolder(child);
            Folders.Remove(child.ID);
        }
        foreach (var item in Items.Values.Where(i => i.Folder == folder.ID).ToList())
            Items.Remove(item.ID);
        return true;
    }

    private bool ParentIsTrashOrLost(InventoryFolderBase folder)
        => Folders.TryGetValue(folder.ParentID, out var parent)
           && (parent.Type == (short)FolderType.Trash || parent.Type == (short)FolderType.LostAndFound);

    // ---------------- the mutations: store whatever you are given ----------------

    public bool AddFolder(InventoryFolderBase folder) { Folders[folder.ID] = folder; return true; }
    public bool UpdateFolder(InventoryFolderBase folder) { Folders[folder.ID] = folder; return true; }
    public bool AddItem(InventoryItemBase item) { Items[item.ID] = item; return true; }
    public bool UpdateItem(InventoryItemBase item) { Items[item.ID] = item; return true; }

    // ---------------- never called by the AIS backend ----------------

    public bool CreateUserInventory(UUID user) => throw new NotImplementedException();
    public InventoryFolderBase GetRootFolder(UUID userID) => throw new NotImplementedException();
    public InventoryCollection[] GetMultipleFoldersContent(UUID userID, UUID[] folderIDs) => throw new NotImplementedException();
    public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID) => throw new NotImplementedException();
    public bool MoveFolder(InventoryFolderBase folder) => throw new NotImplementedException();
    public bool MoveItems(UUID ownerID, List<InventoryItemBase> items) => throw new NotImplementedException();
    public bool HasInventoryForUser(UUID userID) => throw new NotImplementedException();
    public List<InventoryItemBase> GetActiveGestures(UUID userId) => throw new NotImplementedException();
    public int GetAssetPermissions(UUID userID, UUID assetID) => throw new NotImplementedException();
}
