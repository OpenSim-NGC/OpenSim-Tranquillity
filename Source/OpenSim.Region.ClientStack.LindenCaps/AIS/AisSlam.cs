using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>One link as the viewer's slam body describes it.</summary>
public sealed record SlamLink(string Name, string Desc, UUID LinkedId, int AssetType);

/// <summary>
/// What a slam did, so the handler can build the envelope and report failure honestly.
/// </summary>
/// <param name="Created">The link rows created, in body order.</param>
/// <param name="Removed">The ids of the links that were there before and have now gone.</param>
/// <param name="Failure">Null on success; otherwise what went wrong, already compensated for.</param>
/// <param name="CompensationFailed">
/// True when the rollback after a failure could not itself be completed. The folder then holds **more** links
/// than it started with — never fewer — and the ones in <see cref="Created"/> that could not be removed are named
/// in <see cref="Leftover"/>.
/// </param>
public sealed record SlamOutcome(
    IReadOnlyList<InventoryItemBase> Created,
    IReadOnlyList<UUID> Removed,
    string Failure,
    bool CompensationFailed,
    IReadOnlyList<UUID> Leftover)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// PUT /category/{id}/links — replace a folder's links.
///
/// <para><b>The body</b> is a bare LLSD <b>array</b> of link maps, each carrying exactly <c>name</c>, <c>desc</c>,
/// <c>linked_id</c> and <c>type</c> (<c>AT_LINK</c> = 24, or <c>AT_LINK_FOLDER</c> = 25). Both builders agree:
/// <c>LLAppearanceMgr::updateCOF</c> (<c>llappearancemgr.cpp:2209-2245</c>) and
/// <c>LLAppearanceMgr::slamCategoryLinks</c> (<c>:1795-1833</c>). A0's spec guessed a <c>{"links": [...]}</c>
/// wrapper and marked it UNVERIFIED; the real body has no wrapper. This parser accepts the array and, tolerantly,
/// a map carrying the array under <c>links</c> or <c>contents</c>.</para>
///
/// <para><b>Atomicity.</b> There is none available: <c>IInventoryService</c> has no transaction and no batch write
/// (tree state T5), so a slam is several independent calls. What this implements instead is an ordering chosen so
/// that the dangerous failure cannot happen — see <see cref="Run"/>.</para>
/// </summary>
public static class AisSlam
{
    /// <summary>
    /// Parses a slam body. Returns null when it is not a shape we recognise, and the caller answers 400.
    ///
    /// <para><b>AIS-SEC-2: the guarantee is that a body which fails validation produces zero inventory writes.</b>
    /// Validation completes before <see cref="Run"/> is entered, so a rejected body never reaches an
    /// <c>AddItem</c> or a <c>DeleteItems</c> at all. It is not a write that failed; it is a write not attempted.</para>
    ///
    /// <para><b>An empty MAP is not an empty slam.</b> That branch used to exist and it is what made the defect
    /// reachable: <c>AisHandler</c> produced an empty <c>OSDMap</c> both for a body it could not parse and for no
    /// body at all, so a truncated <c>PUT</c> — a dropped connection is enough — was read as "replace every link
    /// with none" and emptied the wearer's Current Outfit. The viewer sends a bare LLSD <b>array</b> and never
    /// <c>{}</c> (spec A-Q3, <c>llappearancemgr.cpp:2209-2245</c>, <c>:1795-1833</c>), so <c>{}</c> can only be a
    /// client we do not know or a body that arrived damaged, and under replacement semantics the safe reading of
    /// both is "refuse". An empty <b>array</b> stays an intentional empty slam: that is how the viewer takes off
    /// the last garment.</para>
    ///
    /// <para><b>All-or-nothing.</b> One bad entry rejects the whole body rather than being skipped. Skipping was
    /// the old behaviour and it is the same outfit loss by a quieter route — a slam replaces, so dropping an entry
    /// the viewer meant to keep deletes the link it was asking to preserve. A link must carry a non-zero
    /// <c>linked_id</c> and a type of <c>AT_LINK</c> or <c>AT_LINK_FOLDER</c>, the only two either builder emits;
    /// anything else would be stored as a link row no fetch route knows how to present.</para>
    /// </summary>
    public static IReadOnlyList<SlamLink> ParseBody(OSD body)
    {
        var array = body as OSDArray;
        if (array is null && body is OSDMap map)
        {
            if (map["links"] is OSDArray fromLinks) array = fromLinks;
            else if (map["contents"] is OSDArray fromContents) array = fromContents;
        }
        if (array is null) return null;

        var links = new List<SlamLink>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not OSDMap m) return null;

            var type = m.ContainsKey("type") ? m["type"].AsInteger() : (int)OpenMetaverse.AssetType.Link;
            if (type != (int)OpenMetaverse.AssetType.Link && type != (int)OpenMetaverse.AssetType.LinkFolder)
                return null;

            var linkedId = m["linked_id"].AsUUID();
            if (linkedId.IsZero()) return null;

            links.Add(new SlamLink(
                m["name"].AsString() ?? "",
                m["desc"].AsString() ?? "",
                linkedId,
                type));
        }
        return links;
    }

    /// <summary>
    /// Replace <paramref name="folderId"/>'s links with <paramref name="wanted"/>.
    ///
    /// <para><b>The guarantee, in plain terms.</b> This is <b>not</b> atomic and does not claim to be. The new
    /// links are created <b>first</b> and the old ones removed <b>second</b>, so the folder never passes through a
    /// state with fewer links than it started with — the failure that would strip an avatar's Current Outfit
    /// cannot occur. Concretely:</para>
    /// <list type="bullet">
    ///   <item>If a creation fails, every link created so far is deleted again and the folder is left exactly as
    ///   it was. The response is an error.</item>
    ///   <item>If that rollback itself fails, the folder keeps the links that could not be removed: it holds
    ///   <b>more</b> than it started with, never fewer, and the outcome says so. The avatar shows duplicates until
    ///   the next slam, which is recoverable; a stripped outfit would not be.</item>
    ///   <item>If a removal fails after every creation succeeded, the folder holds the old links as well as the
    ///   new. The response is an error and the client's next slam corrects it. Nothing is lost.</item>
    /// </list>
    /// <para><b>The window that remains:</b> between the last creation and the last removal the folder holds both
    /// sets, so a bake or a fetch racing the slam sees duplicates. Closing that needs a transactional or batch
    /// write on <c>IInventoryService</c>, which does not exist (Ledger A-R2).</para>
    /// </summary>
    public static SlamOutcome Run(IAisInventoryBackend backend, UUID agentId, UUID folderId,
        IReadOnlyList<InventoryItemBase> existingLinks, IReadOnlyList<SlamLink> wanted)
    {
        var created = new List<InventoryItemBase>(wanted.Count);

        // resolve the targets once, so a link carries its target's inventory type
        var targetIds = new List<UUID>();
        foreach (var link in wanted)
            if (!link.LinkedId.IsZero() && !targetIds.Contains(link.LinkedId)) targetIds.Add(link.LinkedId);
        var targets = new Dictionary<UUID, InventoryItemBase>();
        if (targetIds.Count > 0)
            foreach (var item in backend.GetItems(agentId, targetIds) ?? Array.Empty<InventoryItemBase>())
                if (item is not null) targets[item.ID] = item;

        // 1. create everything the body asked for
        foreach (var link in wanted)
        {
            var row = new InventoryItemBase(UUID.Random(), agentId)
            {
                Folder = folderId,
                Name = link.Name,
                Description = link.Desc,
                AssetID = link.LinkedId,
                AssetType = link.AssetType,
                InvType = targets.TryGetValue(link.LinkedId, out var target) ? target.InvType : 0,
                CreatorId = agentId.ToString(),
                CreationDate = (int)Util.UnixTimeSinceEpoch(),
                BasePermissions = (uint)OpenSim.Framework.PermissionMask.All,
                CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
                EveryOnePermissions = 0,
                NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
                GroupPermissions = 0,
                Flags = 0,
            };

            if (backend.AddItem(row)) { created.Add(row); continue; }

            // 2. a creation failed: undo the ones already made and leave the folder as it was
            var leftover = Rollback(backend, agentId, created);
            return new SlamOutcome(Array.Empty<InventoryItemBase>(), Array.Empty<UUID>(),
                $"could not create the link to {link.LinkedId}; the folder was left unchanged", leftover.Count > 0, leftover);
        }

        // 3. every creation succeeded: now remove what was there before
        var oldIds = new List<UUID>(existingLinks.Count);
        foreach (var link in existingLinks) oldIds.Add(link.ID);
        if (oldIds.Count > 0 && !backend.DeleteItems(agentId, oldIds))
            return new SlamOutcome(created, Array.Empty<UUID>(),
                "the new links were created but the previous ones could not be removed; the folder holds both sets",
                false, Array.Empty<UUID>());

        return new SlamOutcome(created, oldIds, null, false, Array.Empty<UUID>());
    }

    /// <summary>Deletes the links a failed slam had already created. Returns the ones it could not remove.</summary>
    private static IReadOnlyList<UUID> Rollback(IAisInventoryBackend backend, UUID agentId, List<InventoryItemBase> created)
    {
        if (created.Count == 0) return Array.Empty<UUID>();
        var ids = new List<UUID>(created.Count);
        foreach (var row in created) ids.Add(row.ID);
        return backend.DeleteItems(agentId, ids) ? Array.Empty<UUID>() : ids;
    }
}
