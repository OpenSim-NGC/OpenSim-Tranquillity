using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>What a purge removed, and what it could not.</summary>
/// <param name="RemovedCategories">Direct sub-folders that are gone.</param>
/// <param name="RemovedItems">Direct items (links included) that are gone.</param>
/// <param name="Survivors">Direct children still present afterwards; empty on success.</param>
public sealed record PurgeOutcome(
    IReadOnlyList<UUID> RemovedCategories,
    IReadOnlyList<UUID> RemovedItems,
    IReadOnlyList<UUID> Survivors)
{
    public bool Ok => Survivors.Count == 0;
}

/// <summary>
/// DELETE /category/{id}/children — empty a folder, keeping the folder.
///
/// <para><b>Who calls it.</b> `purge_descendents_of` (<c>llviewerinventory.cpp:1590-1643</c>), reached from
/// <c>LLInventoryModel::callbackEmptyFolderType</c> and <c>emptyFolderType</c>
/// (<c>llinventorymodel.cpp:4125-4131</c>, <c>:4136-4158</c>) — Empty Trash and Empty Lost and Found.</para>
///
/// <para><b>The deltas must be enumerated, unlike a folder delete.</b> A2 established that
/// <c>DELETE /category/{id}</c> need only name the folder, because
/// <c>LLInventoryModel::onObjectDeletedFromServer</c> calls <c>onDescendentsPurgedFromServer</c> for a category
/// and the viewer purges the children locally. That does **not** apply here: the only caller of
/// <c>onDescendentsPurgedFromServer</c> is that one line in <c>onObjectDeletedFromServer</c>
/// (<c>llinventorymodel.cpp:2023</c>), and nothing in <c>llaisapi.cpp</c> calls it. A purge does not delete the
/// folder, so nothing triggers the local sweep and the viewer would keep every child it already had. **The
/// response must therefore list the purged children itself**: direct sub-folders in <c>_categories_removed</c>
/// and direct items in <c>_removed_items</c>. Only the *direct* children need listing — each removed sub-folder
/// goes through <c>onObjectDeletedFromServer</c> in turn, which purges *its* descendents locally.</para>
///
/// <para><b>The protected-folder rule does not apply</b>, for the same reason it does not apply to a slam (A3):
/// <c>lookupIsProtectedType</c> governs moving, deleting and retyping a folder — *"you can't move, deleted, or
/// change certain properties such as their type"* (<c>llfoldertype.cpp:151-153</c>) — not emptying it. Trash and
/// Lost and Found are both protected (<c>:97</c>, <c>:99</c>) and are the only folders the verified callers purge.
/// Refusing them would refuse the operation's entire purpose.</para>
/// </summary>
public static class AisPurge
{
    /// <summary>
    /// Empty <paramref name="folder"/>, and report what actually went by re-reading afterwards.
    ///
    /// <para><b>What is composed, and why.</b> <c>IInventoryService.PurgeFolder(folder)</c> is the one-argument
    /// form, i.e. <c>onlyIfTrash = true</c>. Its gate is <c>ParentIsTrashOrLost</c>, which returns true when the
    /// folder **itself** is Trash or Lost and Found (<c>XInventoryService.cs:522-529</c>), so it covers the two
    /// callers the viewer actually has. For any other folder it refuses, so this falls back to composing the purge
    /// from what A2b already exposed: <c>DeleteItems</c> for the direct items and
    /// <c>DeleteFolders(..., onlyIfTrash: false)</c> for the direct sub-folders, which itself purges each
    /// sub-folder recursively before removing it (<c>XInventoryService.cs:459-478</c>).</para>
    ///
    /// <para><b>The failure window.</b> A purge is destructive by intent, so there is nothing to roll back to —
    /// unlike a slam, a partially purged folder cannot be restored, and pretending otherwise would be a lie. What
    /// this does instead is re-read and tell the truth: every child that survived is named in
    /// <see cref="PurgeOutcome.Survivors"/> and the route answers an error. A purge is idempotent, so re-issuing it
    /// finishes the job; that is the recovery, and it is the client's to make.</para>
    /// </summary>
    public static PurgeOutcome Run(IAisInventoryBackend backend, UUID agentId, InventoryFolderBase folder)
    {
        var before = AisInventory.GetContents(backend, agentId, folder.ID);
        var categoriesBefore = new List<UUID>();
        var itemsBefore = new List<UUID>();
        if (before is not null)
        {
            foreach (var child in before.SubFolders) categoriesBefore.Add(child.ID);
            foreach (var item in before.Items) itemsBefore.Add(item.ID);
            foreach (var link in before.Links) itemsBefore.Add(link.ID);
        }

        // 1. the service's own purge, which covers Trash and Lost and Found
        backend.PurgeFolder(folder);

        // 2. whatever it declined to touch, remove with the operations A2b exposed
        var after = AisInventory.GetContents(backend, agentId, folder.ID);
        if (after is not null && (after.SubFolders.Count > 0 || after.Items.Count > 0 || after.Links.Count > 0))
        {
            var remainingItems = new List<UUID>();
            foreach (var item in after.Items) remainingItems.Add(item.ID);
            foreach (var link in after.Links) remainingItems.Add(link.ID);
            if (remainingItems.Count > 0) backend.DeleteItems(agentId, remainingItems);

            var remainingFolders = new List<UUID>();
            foreach (var child in after.SubFolders) remainingFolders.Add(child.ID);
            if (remainingFolders.Count > 0) backend.DeleteFolders(agentId, remainingFolders, onlyIfTrash: false);
        }

        // 3. report only what really went
        var final = AisInventory.GetContents(backend, agentId, folder.ID);
        var stillThere = new HashSet<UUID>();
        if (final is not null)
        {
            foreach (var child in final.SubFolders) stillThere.Add(child.ID);
            foreach (var item in final.Items) stillThere.Add(item.ID);
            foreach (var link in final.Links) stillThere.Add(link.ID);
        }

        var removedCategories = new List<UUID>();
        foreach (var id in categoriesBefore) if (!stillThere.Contains(id)) removedCategories.Add(id);
        var removedItems = new List<UUID>();
        foreach (var id in itemsBefore) if (!stillThere.Contains(id)) removedItems.Add(id);

        return new PurgeOutcome(removedCategories, removedItems, new List<UUID>(stillThere));
    }
}
