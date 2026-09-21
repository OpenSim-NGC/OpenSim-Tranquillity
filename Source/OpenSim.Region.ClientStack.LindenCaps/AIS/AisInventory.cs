using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>One folder's direct contents, with links already split out of the item list (spec §1c).</summary>
public sealed record AisFolderContents(
    InventoryFolderBase Folder,
    IReadOnlyList<InventoryFolderBase> SubFolders,
    IReadOnlyList<InventoryItemBase> Items,
    IReadOnlyList<InventoryItemBase> Links);

/// <summary>Folders whose parent no longer exists (GET /orphans).</summary>
public sealed record AisOrphans(IReadOnlyList<InventoryFolderBase> Folders, IReadOnlyList<InventoryItemBase> Items);

/// <summary>
/// The composition the fetch routes need on top of the backend primitives: split links out of an item list,
/// resolve link targets, walk a folder to a depth, find orphans. Pure with respect to the backend — no Scene, no
/// ScenePresence (Ledger P-2) — so both the region host and Phase 2's Robust host share exactly this behaviour
/// rather than each re-deriving it.
/// </summary>
public static class AisInventory
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AisInventory));

    /// <summary>A folder's contents with links separated from items; null when the folder is not the agent's.</summary>
    public static AisFolderContents GetContents(IAisInventoryBackend backend, UUID agentId, UUID folderId)
    {
        var folder = backend.GetFolder(agentId, folderId);
        if (folder is null) return null;
        var collection = backend.GetFolderContent(agentId, folderId);
        var items = new List<InventoryItemBase>();
        var links = new List<InventoryItemBase>();
        if (collection?.Items is not null)
            foreach (var item in collection.Items)
                (AisEnvelope.IsLink(item) ? links : items).Add(item);
        var folders = (IReadOnlyList<InventoryFolderBase>)(collection?.Folders ?? new List<InventoryFolderBase>());
        return new AisFolderContents(folder, folders, items, links);
    }

    /// <summary>
    /// The items a set of links points at. There is no link-aware fetch in <c>IInventoryService</c> (tree state
    /// T5), so this does what the existing descendents cap does: collect the <c>AssetType.Link</c> rows' asset ids
    /// and resolve them with one <c>GetMultipleItems</c>
    /// (<c>Source/OpenSim.Capabilities.Handlers/FetchInventory/FetchInvDescHandler.cs:424-460</c>,
    /// <c>ProcessLinks</c>). Links to links are dropped for the same reason that handler drops them (:454-457):
    /// they are not observed in practice and following them invites cycles. Broken links simply resolve to
    /// nothing.
    /// </summary>
    public static IReadOnlyList<InventoryItemBase> ResolveLinkTargets(IAisInventoryBackend backend, UUID agentId, IEnumerable<InventoryItemBase> links)
    {
        var ids = new List<UUID>();
        foreach (var link in links ?? Enumerable.Empty<InventoryItemBase>())
            if (link.AssetType == (int)AssetType.Link && !link.AssetID.IsZero() && !ids.Contains(link.AssetID))
                ids.Add(link.AssetID);
        if (ids.Count == 0) return System.Array.Empty<InventoryItemBase>();

        var resolved = backend.GetItems(agentId, ids) ?? (IReadOnlyList<InventoryItemBase>)System.Array.Empty<InventoryItemBase>();
        var targets = new List<InventoryItemBase>(resolved.Count);
        foreach (var item in resolved)
            if (item is not null && item.AssetType != (int)AssetType.Link) targets.Add(item);
        return targets;
    }

    /// <summary>The viewer's own ceiling on a requested depth: MAX_FOLDER_DEPTH_REQUEST (llaisapi.cpp:58).</summary>
    public const int MaxDepth = 50;
    /// <summary>
    /// A folder and its descendants to <paramref name="depth"/> levels below it. <c>depth = 0</c> expands the
    /// requested folder only: its own <c>categories</c>, <c>items</c> and <c>links</c> are listed, and each child
    /// category appears as a bare map with no <c>_embedded</c>. Each further level of depth expands one more
    /// generation. Returned in breadth-first order with the requested folder first.
    ///
    /// <para>Settled in A2b from the viewer's own fetch path (spec §1c-bis): the viewer parses the requested
    /// folder at the depth it asked for and each `_embedded` level one lower (`llaisapi.cpp:1205`, `:1461-1464`),
    /// and versions a category only while that depth is still &gt;= 0 and its descendent count is known
    /// (`:1380-1407`). So N licenses exactly N generations below the requested folder — deeper is wasted work the
    /// viewer will not version, shallower is safe because it re-queues every descendant regardless
    /// (`llinventorymodelbackgroundfetch.cpp:610`). This implementation is that rule, with no off-by-one.</para>
    /// </summary>

    public static IReadOnlyList<AisFolderContents> Walk(IAisInventoryBackend backend, UUID agentId, UUID rootId, int depth)
    {
        var expanded = new List<AisFolderContents>();
        var root = GetContents(backend, agentId, rootId);
        if (root is null) return expanded;
        expanded.Add(root);

        // AIS-SEC-5: a visited set ALONGSIDE the depth cap, not instead of it. The cap bounds legitimate deep
        // trees and is what the viewer's own MAX_FOLDER_DEPTH_REQUEST means; the visited set bounds a *cyclic*
        // graph, which the cap previously only limited the damage of. A cycle A->B->A was re-walked once per
        // level to the cap - 51 entries for two folders - and every one of those is a GetFolderContent, which on
        // a grid deployment is a round trip to Robust. It is now two.
        var visited = new HashSet<UUID> { root.Folder.ID };

        var frontier = new List<AisFolderContents> { root };
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<AisFolderContents>();
            foreach (var parent in frontier)
                foreach (var child in parent.SubFolders)
                {
                    if (!visited.Add(child.ID)) continue;   // already expanded: a cycle, or a repeated row
                    var contents = GetContents(backend, agentId, child.ID);
                    if (contents is null) continue;
                    expanded.Add(contents);
                    next.Add(contents);
                }
            frontier = next;
        }
        return expanded;
    }

    /// <summary>The agent's Current Outfit folder (spec §1b, tree state T2); null when the agent has none.</summary>
    public static InventoryFolderBase GetCurrentOutfit(IAisInventoryBackend backend, UUID agentId)
        => GetSystemFolder(backend, agentId, FolderType.CurrentOutfit);

    /// <summary>
    /// The agent's folder of a system type, resolved **deterministically** even when the agent owns more than one
    /// folder of that type.
    ///
    /// <para>The service's own resolution is a coin flip: <c>XInventoryService.GetSystemFolderForType</c> returns
    /// <c>folders[0]</c> from a query with no <c>ORDER BY</c> and no <c>LIMIT</c>
    /// (<c>MySQLGenericTableHandler.Get</c> passes an empty <c>options</c>), and nothing in the schema forbids
    /// duplicates — <c>inventoryfolders</c> has no unique key on <c>(agentID, type)</c>. Where an agent does
    /// carry two folders of a type, picking the wrong one silently writes an outfit change into a folder no
    /// viewer reads, which is how this was found live.</para>
    ///
    /// <para>The rule is **highest <c>Version</c>, lowest id on a tie**. A folder's version is incremented on
    /// every child add or remove and never decreases, so the folder the viewer has been writing to is the folder
    /// whose version climbed — which is exactly the ground truth we have to match. Creation order cannot be used:
    /// the table has no timestamp. Descendant count cannot be used either: a legitimately emptied Current Outfit
    /// has none, and emptying it is what "take off the last garment" does.</para>
    ///
    /// <para>Candidates come from the skeleton rather than from <see cref="IAisInventoryBackend.GetFolderForType"/>
    /// because the skeleton is queried by agent alone and so sees every folder the viewer sees, including one that
    /// is not a direct child of the root — which the service's own query structurally cannot return. When the
    /// skeleton is unavailable, or holds no folder of this type, the backend's own answer is used unchanged.</para>
    /// </summary>
    public static InventoryFolderBase GetSystemFolder(IAisInventoryBackend backend, UUID agentId, FolderType type)
    {
        var skeleton = backend.GetInventorySkeleton(agentId);
        if (skeleton is null || skeleton.Count == 0)
            return backend.GetFolderForType(agentId, type);

        List<InventoryFolderBase> candidates = null;
        foreach (var folder in skeleton)
            if (folder.Type == (short)type)
                (candidates ??= new List<InventoryFolderBase>()).Add(folder);

        if (candidates is null)
            return backend.GetFolderForType(agentId, type);
        if (candidates.Count == 1)
            return candidates[0];

        var chosen = candidates[0];
        foreach (var folder in candidates)
            if (folder.Version > chosen.Version ||
                (folder.Version == chosen.Version && folder.ID.CompareTo(chosen.ID) < 0))
                chosen = folder;

        // An operator has to be able to see this without opening the database, because the visible symptom is an
        // outfit change that quietly does not stick.
        m_log.LogWarning(
            "[AIS]: agent {Agent} has {Count} folders of type {Type} ({Candidates}); using {Chosen} version {Version}. "
            + "Duplicate system folders DIRECTLY UNDER THE ROOT are a data fault, not an AIS one. A second "
            + "folder of this type inside My Suitcase is EXPECTED (HGSuitcaseInventoryService.CreateSystemFolders "
            + "builds a full set there) and is not a fault: on the grid where this was first investigated every "
            + "apparent duplicate Current Outfit turned out to be the suitcase one",
            agentId, candidates.Count, type,
            string.Join(", ", candidates.Select(f => $"{f.ID} v{f.Version}")),
            chosen.ID, chosen.Version);

        return chosen;
    }

    /// <summary>
    /// Folders whose <c>ParentID</c> names a folder that is not in the agent's skeleton — the only orphan class
    /// this tree can find without walking every folder's contents. Orphaned **items** are not reported: the
    /// inventory service has no query for them and finding them would mean listing every folder
    /// (<c>IInventoryService</c> surface, tree state T5). An empty result is therefore "no orphan folders", not
    /// "no orphans of any kind", and the route says so in its own documentation.
    /// </summary>
    public static AisOrphans FindOrphans(IAisInventoryBackend backend, UUID agentId)
    {
        var skeleton = backend.GetInventorySkeleton(agentId);
        if (skeleton is null || skeleton.Count == 0)
            return new AisOrphans(System.Array.Empty<InventoryFolderBase>(), System.Array.Empty<InventoryItemBase>());

        var known = new HashSet<UUID>();
        foreach (var folder in skeleton) known.Add(folder.ID);
        var orphans = new List<InventoryFolderBase>();
        foreach (var folder in skeleton)
            if (!folder.ParentID.IsZero() && !known.Contains(folder.ParentID)) orphans.Add(folder);
        return new AisOrphans(orphans, System.Array.Empty<InventoryItemBase>());
    }
}
