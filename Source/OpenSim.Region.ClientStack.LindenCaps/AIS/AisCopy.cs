using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>What a library copy created at the destination.</summary>
public sealed record CopyOutcome(
    IReadOnlyList<InventoryFolderBase> Categories,
    IReadOnlyList<InventoryItemBase> Items,
    string Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// COPY /category/{sourceId} on the library cap, with the destination folder in the <c>Destination</c> header
/// (A1: <c>headers-&gt;append(HTTP_OUT_HEADER_DESTINATION, dest)</c>, <c>llcorehttputil.cpp:1135</c>).
///
/// <para><b>Permissions are the tree's existing library-copy rule, reused rather than reinvented.</b>
/// <c>Scene.Inventory.CopyInventoryItem</c> finds the item in <c>LibraryService.LibraryRootFolder</c> first, and
/// when the source owner is the library owner it creates the copy with the source's **own** Base, Current,
/// EveryOne, Next and Group permissions — not degraded through NextPermissions, which is what the *other* branch
/// does for a copy between two residents (<c>Source/OpenSim.Region.Framework/Scenes/Scene.Inventory.cs:1053-1064</c>
/// versus <c>:1066-1078</c>). A library item is copyable by construction, so there is no
/// <c>PermissionMask.Copy</c> check on that path either (<c>:1046-1048</c> guards only the non-library case).
/// This copies that rule field for field. It cannot call <c>CreateNewInventoryItem</c> itself: that takes an
/// <c>IClientAPI</c>, and nothing in this handler may touch a scene (Ledger P-2).</para>
///
/// <para><b>No rollback.</b> Like a create and unlike a slam, a copy is purely additive: a failure part-way leaves
/// the folders and items already made, and nothing that existed before is at risk. The response reports the
/// failure and names what was made, so a client can retry or clean up. Rolling back would mean deleting objects a
/// concurrent operation may already have touched.</para>
/// </summary>
public static class AisCopy
{
    /// <summary>
    /// Copy <paramref name="sourceId"/> and everything under it from <paramref name="library"/> into
    /// <paramref name="destinationParent"/> in the agent's inventory. <paramref name="copySubfolders"/> is false
    /// when the viewer appended <c>,depth=0</c> to the tid (<c>llaisapi.cpp:278</c>).
    /// </summary>
    public static CopyOutcome Run(IAisInventoryBackend library, IAisInventoryBackend destination,
        UUID libraryOwner, UUID agentId, UUID sourceId, UUID destinationParent, bool copySubfolders)
    {
        var source = library.GetFolder(libraryOwner, sourceId);
        if (source is null) return new CopyOutcome(Array.Empty<InventoryFolderBase>(), Array.Empty<InventoryItemBase>(),
            $"no library category {sourceId}");

        var categories = new List<InventoryFolderBase>();
        var items = new List<InventoryItemBase>();
        var failure = CopyFolder(library, destination, libraryOwner, agentId, source, destinationParent,
            copySubfolders, categories, items);
        return new CopyOutcome(categories, items, failure);
    }

    /// <summary>Copies one folder and its contents, recursing into sub-folders only when asked.</summary>
    private static string CopyFolder(IAisInventoryBackend library, IAisInventoryBackend destination,
        UUID libraryOwner, UUID agentId, InventoryFolderBase source, UUID parentId, bool copySubfolders,
        List<InventoryFolderBase> categories, List<InventoryItemBase> items)
    {
        var copy = new InventoryFolderBase(UUID.Random(), source.Name, agentId, source.Type, parentId, 1);
        if (!destination.AddFolder(copy)) return $"could not create the folder {source.Name}";
        categories.Add(copy);

        var contents = library.GetFolderContent(libraryOwner, source.ID);
        if (contents?.Items is not null)
        {
            foreach (var item in contents.Items)
            {
                var itemCopy = CopyItem(item, agentId, copy.ID);
                if (!destination.AddItem(itemCopy)) return $"could not create the item {item.Name}";
                items.Add(itemCopy);
            }
        }

        if (!copySubfolders || contents?.Folders is null) return null;
        foreach (var child in contents.Folders)
        {
            var error = CopyFolder(library, destination, libraryOwner, agentId, child, copy.ID, true, categories, items);
            if (error is not null) return error;
        }
        return null;
    }

    /// <summary>
    /// One item, with the library-copy permission rule of <c>Scene.Inventory.cs:1053-1064</c>: creator and asset
    /// preserved, owner becomes the agent, and every permission mask is carried over unchanged.
    /// </summary>
    private static InventoryItemBase CopyItem(InventoryItemBase source, UUID agentId, UUID folderId)
        => new(UUID.Random(), agentId)
        {
            Folder = folderId,
            Name = source.Name,
            Description = source.Description,
            AssetID = source.AssetID,
            AssetType = source.AssetType,
            InvType = source.InvType,
            Flags = source.Flags,
            CreatorId = source.CreatorId,
            CreatorData = source.CreatorData,
            CreationDate = Util.UnixTimeSinceEpoch(),
            BasePermissions = source.BasePermissions,
            CurrentPermissions = source.CurrentPermissions,
            EveryOnePermissions = source.EveryOnePermissions,
            NextPermissions = source.NextPermissions,
            GroupPermissions = source.GroupPermissions,
            SalePrice = source.SalePrice,
            SaleType = source.SaleType,
        };
}
