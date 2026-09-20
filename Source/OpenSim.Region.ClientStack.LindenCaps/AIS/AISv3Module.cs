using System;
using System.Collections.Generic;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// Region-side host for the AIS v3 inventory cap (Ledger A-D1). Config:
/// <code>
/// [AIS]
///     Enabled = false
/// </code>
/// When enabled it registers <c>InventoryAPIv3</c> for every agent from <c>OnRegisterCaps</c> (tree state T1: the
/// cap must be registered on the agent's Caps under that exact name; the viewer requests the name itself). When
/// disabled it registers nothing, so the viewer never sees the cap and keeps its legacy paths (risk A-R1).
/// <c>LibraryAPIv3</c> is deliberately not registered (Ledger A-D3).
/// </summary>
public class AISv3Module : ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AISv3Module));

    public const string CapName = "InventoryAPIv3";
    /// <summary>The library cap. Same handler, library owner as the agent, mutations refused (John's Phase 1 ruling; supersedes A-D3).</summary>
    public const string LibraryCapName = "LibraryAPIv3";
    public const string ConfigSection = "AIS";

    /// <summary>
    /// AIS caps must be registered as **variable-path** handlers. Every other capability in this tree answers on
    /// its exact URL; AIS answers on paths below it — <c>&lt;capurl&gt;/item/{id}</c>,
    /// <c>&lt;capurl&gt;/category/{id}/children</c>, <c>&lt;capurl&gt;/orphans</c>. The listener keeps exact and
    /// variable-path handlers in different dictionaries and only the latter is matched by prefix
    /// (<c>BaseHttpServer.TryGetSimpleStreamHandler</c>, <c>AddSimpleStreamHandler</c>), so registering the
    /// default way makes every AIS request 404 before the handler is entered. That is the A6 live failure;
    /// see Docs/feature/ais-v3/A6-LIVE-FAILURE.md.
    /// </summary>
    public const bool VarPath = true;

    /// <summary>The grid-wide default from <c>[AIS] Enabled</c>. A region may override it; see <see cref="ResolveEnabled"/>.</summary>
    public bool Enabled { get; private set; }

    /// <summary>The scenes that resolved to enabled, with the handler they subscribed, so RemoveRegion can undo exactly what RegionLoaded did.</summary>
    private readonly Dictionary<Scene, EventManager.RegisterCapsEvent> m_enabledScenes = new();

    public string Name => "AISv3Module";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs[ConfigSection];
        Enabled = config is not null && config.GetBoolean("Enabled", false);
        if (Enabled)
            m_log.LogInformation(
                "[AIS]: [{Section}] Enabled = true: AIS v3 is on for every region this simulator runs. It routes ALL "
                + "of an LL viewer's inventory traffic - fetch, delete, purge, slam and create - and the viewer has "
                + "no fallback for the mutations (spec 1g, risk A-R1). A single region opts out with "
                + "AIS_Enabled = false in its own section.", ConfigSection);
    }

    public void PostInitialise() { }
    public void Close() { }

    public void AddRegion(Scene scene) { }

    /// <summary>
    /// Whether AIS is on for one region. The grid-wide <c>[AIS] Enabled</c> is the default, and a
    /// <c>[&lt;Region Name&gt;]</c> section may override it with <c>AIS_Enabled</c> — the per-region idiom this tree
    /// already uses (<c>AutoBackupModule.cs:400-406</c> reads <c>scene.Config.Configs[regionName]</c> and takes
    /// per-key defaults from the global setting).
    ///
    /// <para>This exists because risk A-R1 makes a grid-wide flip unacceptable: turning AIS on hands the LL
    /// viewer's entire inventory path to this code with no fallback, so it must be possible to try it on exactly
    /// one region. Static and free of <c>Scene</c> so it can be tested with a plain config source.</para>
    /// </summary>
    public static bool ResolveEnabled(bool gridDefault, IConfigSource sceneConfig, string regionName)
    {
        if (sceneConfig is null || string.IsNullOrEmpty(regionName)) return gridDefault;
        IConfig regionConfig = sceneConfig.Configs[regionName];
        return regionConfig is null ? gridDefault : regionConfig.GetBoolean("AIS_Enabled", gridDefault);
    }

    /// <summary>
    /// S12: which config decided this region's flag - <c>"region section"</c> when the region's own section carries
    /// an <c>AIS_Enabled</c> key, <c>"global"</c> otherwise. For the startup line only; see
    /// <c>ServerSideBakingRegion.EnabledSource</c>, which answers the same question for the other lane in the same
    /// words, because the flip verify reads both.
    /// </summary>
    public static string EnabledSource(IConfigSource sceneConfig, string regionName)
    {
        if (sceneConfig is null || string.IsNullOrEmpty(regionName)) return GlobalSource;
        IConfig regionConfig = sceneConfig.Configs[regionName];
        return regionConfig is not null && regionConfig.Contains("AIS_Enabled") ? RegionSource : GlobalSource;
    }

    /// <summary>The two answers <see cref="EnabledSource"/> gives.</summary>
    public const string GlobalSource = "global";
    public const string RegionSource = "region section";

    public void RegionLoaded(Scene scene)
    {
        if (scene is null) return;

        var regionName = scene.RegionInfo?.RegionName;
        var enabled = ResolveEnabled(Enabled, scene.Config, regionName);

        // S12: one line per region, naming which config decided - the same shape the SSB lane logs, so the flip
        // verify reads one console for both.
        m_log.LogInformation("[AIS]: region {Region}: AIS v3 {State} ({Source})",
            scene.Name, enabled ? "ON" : "off", EnabledSource(scene.Config, regionName));

        if (!enabled) return;

        if (scene.InventoryService is null)
        {
            m_log.LogError("[AIS]: region {Region} has no inventory service; no AIS caps registered there", scene.Name);
            return;
        }

        void Handler(UUID agentID, Caps caps) => RegisterCaps(scene, agentID, caps);
        lock (m_enabledScenes)
        {
            if (m_enabledScenes.ContainsKey(scene)) return;
            m_enabledScenes[scene] = Handler;
        }
        scene.EventManager.OnRegisterCaps += Handler;

        var caps = scene.LibraryService is null
            ? CapName
            : CapName + ", " + LibraryCapName;
        m_log.LogInformation(
            "[AIS]: region {Region} advertises {Caps} to every agent. All LL-viewer inventory traffic there - fetch, "
            + "delete, purge, slam and create - goes through AIS with no fallback (spec 1g).", scene.Name, caps);
    }

    public void RemoveRegion(Scene scene)
    {
        if (scene is null) return;
        EventManager.RegisterCapsEvent handler;
        lock (m_enabledScenes)
        {
            if (!m_enabledScenes.Remove(scene, out handler)) return;
        }
        scene.EventManager.OnRegisterCaps -= handler;
    }
    /// <summary>
    /// Both caps are registered together when enabled, and neither when disabled (tree state T1: a cap reaches
    /// the viewer only if it is registered under its exact name and the viewer asked for it; the viewer asks for
    /// both, `llaisapi.cpp:72-76`). LibraryAPIv3 runs the same handler over the library service with the library
    /// owner as its agent id, and refuses every mutation with 405.
    /// </summary>
    private void RegisterCaps(Scene scene, UUID agentID, Caps caps)
    {
        var inventory = scene.InventoryService;
        var library = scene.LibraryService;

        // AIS-SEC-1: the backend is bound to the agent this cap belongs to. The cap URL is unguessable, but a cap
        // that leaked (or a client of the agent's own) could otherwise name any resident's object UUID and the
        // inventory service would serve it, because it resolves by id and ignores the principal.
        var invHandler = new AisHandler("/" + UUID.Random(), agentID, new InventoryServiceBackend(inventory, agentID, TransactionResolverFor(scene), WornAssetObserverFor(scene)));
        caps.RegisterSimpleHandler(CapName, invHandler, varPath: VarPath);
        m_log.LogDebug("[AIS]: registered {Cap} at {Path} for agent {Agent} in {Region}",
            CapName, invHandler.CapPath, agentID, scene.Name);

        if (library is null)
        {
            m_log.LogWarning("[AIS]: region {Region} has no library service; {Cap} not registered", scene.Name, LibraryCapName);
            return;
        }
        var libraryOwner = LibraryOwnerOf(library);
        // COPY reads from the library and writes into the agent's inventory, so the library handler carries both
        // sides: itself as the source, the agent's inventory as the destination. AIS-SEC-1 binds that destination
        // to the agent too, so a COPY cannot be steered into another resident's folder by its Destination header.
        // The source backend is the library's own and stays as it is: it is read-only by construction.
        var libHandler = new AisHandler("/" + UUID.Random(), libraryOwner, new LibraryServiceBackend(library), AisMode.Library,
            new InventoryServiceBackend(inventory, agentID, TransactionResolverFor(scene), WornAssetObserverFor(scene)), agentID);
        caps.RegisterSimpleHandler(LibraryCapName, libHandler, varPath: VarPath);
        m_log.LogDebug("[AIS]: registered {Cap} at {Path} for agent {Agent} in {Region}",
            LibraryCapName, libHandler.CapPath, agentID, scene.Name);
    }

    /// <summary>
    /// Hands a <c>hash_id</c> to the region's asset-transaction module, which is the only thing that knows which
    /// asset a transaction produced (A16). Kept here rather than in the backend so
    /// <see cref="InventoryServiceBackend"/> stays free of <c>Scene</c> (Ledger P-2) and Phase 2 can host it on
    /// Robust unchanged: there it simply has no resolver and the PATCH falls back to <c>asset_id</c>.
    ///
    /// <para>
    /// The two lookups are done per call, not captured: an agent's <see cref="IClientAPI"/> comes and goes with
    /// the connection, and the module is registered on the scene. The call itself is the legacy route's, field
    /// for field (<c>Scene.Inventory.cs:579-582</c>).
    /// </para>
    /// </summary>
    private static InventoryServiceBackend.AssetTransactionResolver TransactionResolverFor(Scene scene)
        => (agentId, transactionId, item) =>
        {
            var transactions = scene.RequestModuleInterface<IAgentAssetTransactions>();
            if (transactions is null)
            {
                m_log.LogWarning("[AIS]: item {Item} carried hash_id {Transaction} but region {Region} has no asset transaction module; the asset was not applied",
                    item.ID, transactionId, scene.Name);
                return AisAssetTransaction.NotResolvable;
            }
            if (!scene.TryGetClient(agentId, out var client) || client is null)
            {
                m_log.LogWarning("[AIS]: item {Item} carried hash_id {Transaction} but agent {Agent} has no client in {Region}; the asset was not applied",
                    item.ID, transactionId, agentId, scene.Name);
                return AisAssetTransaction.NotResolvable;
            }
            // A19: the module's verdict, not an unconditional yes. It is false only when the referenced assets
            // were validated and refused, which is the case the cap has to report rather than answer 200 to.
            bool applied = transactions.HandleItemUpdateFromTransaction(client, transactionId, item);
            if (!applied)
            {
                m_log.LogWarning("[AIS]: item {Item} carried hash_id {Transaction} but the asset transaction module REFUSED the update for agent {Agent} in {Region}; the item still points at its previous asset",
                    item.ID, transactionId, agentId, scene.Name);
                return AisAssetTransaction.Refused;
            }
            return AisAssetTransaction.Applied;
        };

    /// <summary>
    /// S9. Turns "this item's asset changed" into "rebake if it mattered". Kept here, not in the backend, for the
    /// same reason as the transaction resolver: <see cref="InventoryServiceBackend"/> stays free of <c>Scene</c>
    /// (Ledger P-2) and Phase 2 on Robust, which has no presence to update, simply has no observer.
    ///
    /// <para>
    /// Queuing rather than baking is deliberate and is the same ordering the cap uses (Q-16): the save resolves
    /// every worn item to its current asset, persists the result and raises the S5 trigger, and the bake's own
    /// per-channel input hash then decides what is recomputed. An edit that changed nothing visible costs one
    /// hash check per channel.
    /// </para>
    /// </summary>
    private static InventoryServiceBackend.WornAssetObserver WornAssetObserverFor(Scene scene)
        => (agentId, itemId, newAssetId) =>
        {
            ScenePresence sp = scene.GetScenePresence(agentId);
            if (sp is null || sp.IsChildAgent) return;   // S8: a child presence never drives an appearance save

            if (!AisWornAssets.ApplyTo(sp.Appearance, itemId, newAssetId))
                return;                                  // not worn here, or already carrying this asset

            m_log.LogDebug("[AIS]: item {Item} is worn by {Agent} and its asset changed to {Asset}; queueing an appearance save in {Region}",
                itemId, agentId, newAssetId, scene.Name);
            scene.AvatarFactory?.QueueAppearanceSave(agentId);
        };

    /// <summary>
    /// The library's owner, as the tree defines it: <c>ILibraryService.LibraryRootFolder.Owner</c>, set by
    /// <c>LibraryService</c> to <c>Constants.m_MrOpenSimID</c> for the root folder and every library folder and
    /// item (<c>Source/OpenSim.Services.InventoryService/LibraryService.cs:50, 100, 115-116, 176, 199-200</c>).
    /// Read off the service rather than hardcoded, so a grid that supplies its own library owner still works.
    /// </summary>
    public static UUID LibraryOwnerOf(ILibraryService library) => library?.LibraryRootFolder?.Owner ?? UUID.Zero;

    /// <summary>
    /// Phase 1 backend: the region's <c>IInventoryService</c>, <b>scoped to the one resident whose cap this is</b>.
    /// Nothing here knows about scenes (Ledger P-2), so Phase 2 hosts it on Robust unchanged.
    ///
    /// <para><b>AIS-SEC-1: the scoping is this class's job, and nothing below it does any.</b>
    /// <see cref="IAisInventoryBackend"/> has always documented that <c>GetFolder</c> and <c>GetItem</c> return
    /// null for an object that is "not the agent's", and until this was written that promise was not kept: the
    /// class was a pass-through, and <c>XInventoryService</c> resolves by UUID alone and says so — <c>GetItem</c>
    /// queries <c>inventoryID</c> (<c>XInventoryService.cs:633-641</c>), <c>GetFolder</c> queries <c>folderID</c>
    /// (<c>:653-663</c>), <c>GetFolderContent</c> carries the comment <i>"This method doesn't receive a valud
    /// principal id from the connector. So we disregard the principal and look by ID"</i> (<c>:319-323</c>),
    /// <c>DeleteFolders</c> <i>"Ignore principal ID, it's bogus at connector level"</i> (<c>:482-492</c>) and
    /// <c>DeleteItems</c> <i>"Just use the ID... *facepalms*"</i> (<c>:602-631</c>). A valid AIS cap plus another
    /// resident's item or folder UUID could therefore read, rename, delete, purge, slam and create across the
    /// boundary.</para>
    ///
    /// <para><b>Why the fix is here and not in the service.</b> That behaviour is upstream and other connector
    /// paths depend on it — the Robust connector really does pass a principal the service cannot trust, which is
    /// what those comments are about. The cap, by contrast, knows exactly whose it is: it is registered per agent
    /// from <c>OnRegisterCaps</c>, so the owner is a constructor argument and every call is checked against it.</para>
    ///
    /// <para><b>The rule, in one line:</b> a read answers only for an object whose <c>Owner</c> is
    /// <see cref="OwnerId"/>, and a write happens only when the object <i>and</i> its parent folder are the
    /// owner's. The agent id each interface method takes is still checked — it must be the owner — but it is never
    /// <i>trusted</i> as the scope. The scope is the field, and it is the field that is handed to the service.</para>
    ///
    /// <para><b>What the handler sees.</b> A foreign object is indistinguishable from an absent one, so the
    /// handler's pre-existing not-found paths fire and the route answers <b>404</b>. That is deliberate: a 403
    /// would tell a caller that a UUID it guessed belongs to somebody, which is a membership oracle over the whole
    /// inventory keyspace. No status mapping was invented for the security case.</para>
    /// </summary>
    public sealed class InventoryServiceBackend : IAisInventoryBackend
    {
        /// <summary>Hands a transaction id and the item to whatever knows about asset transactions (A16).</summary>
        public delegate AisAssetTransaction AssetTransactionResolver(UUID agentId, UUID transactionId, InventoryItemBase item);

        /// <summary>Told that an item's asset changed, so a worn one can rebake (S9).</summary>
        public delegate void WornAssetObserver(UUID agentId, UUID itemId, UUID newAssetId);

        private readonly IInventoryService m_service;
        private readonly UUID m_ownerId;
        private readonly AssetTransactionResolver m_transactions;
        private readonly WornAssetObserver m_wornAssets;

        /// <param name="ownerId">
        /// The one resident this backend serves. Zero is refused rather than defaulted: a zero owner would scope
        /// nothing, which is exactly the state AIS-SEC-1 fixed, and a quiet guard is how that state would come back.
        /// </param>
        public InventoryServiceBackend(IInventoryService service, UUID ownerId,
            AssetTransactionResolver transactions = null, WornAssetObserver wornAssets = null)
        {
            m_service = service ?? throw new ArgumentNullException(nameof(service));
            if (ownerId.IsZero())
                throw new ArgumentException("an AIS inventory backend must be bound to a non-zero owner", nameof(ownerId));
            m_ownerId = ownerId;
            m_transactions = transactions;
            m_wornAssets = wornAssets;
        }

        /// <summary>The resident whose inventory this is, and the only principal this class ever passes down.</summary>
        public UUID OwnerId => m_ownerId;

        private bool IsCaller(UUID agentId) => agentId == m_ownerId;
        private bool IsOwned(InventoryFolderBase folder) => folder is not null && folder.Owner == m_ownerId;
        private bool IsOwned(InventoryItemBase item) => item is not null && item.Owner == m_ownerId;

        // ---------------- reads: nothing unless the caller is the owner AND the row is theirs ----------------

        public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type)
        {
            if (!IsCaller(agentId)) return null;
            var folder = m_service.GetFolderForType(m_ownerId, type);
            return IsOwned(folder) ? folder : null;
        }

        public InventoryFolderBase GetFolder(UUID agentId, UUID folderId)
        {
            if (!IsCaller(agentId)) return null;
            var folder = m_service.GetFolder(m_ownerId, folderId);
            return IsOwned(folder) ? folder : null;
        }

        public InventoryItemBase GetItem(UUID agentId, UUID itemId)
        {
            if (!IsCaller(agentId)) return null;
            var item = m_service.GetItem(m_ownerId, itemId);
            return IsOwned(item) ? item : null;
        }

        /// <summary>
        /// The folder itself must pass <see cref="GetFolder"/>, and the contents are filtered as well: a row whose
        /// parent is the owner's folder but whose own <c>Owner</c> is somebody else is a data fault, and it is not
        /// this cap's to hand out. The collection is rebuilt rather than edited so the caller is never handed the
        /// service's own lists.
        /// </summary>
        public InventoryCollection GetFolderContent(UUID agentId, UUID folderId)
        {
            if (GetFolder(agentId, folderId) is null) return null;
            var content = m_service.GetFolderContent(m_ownerId, folderId);
            if (content is null) return null;

            var folders = new List<InventoryFolderBase>();
            if (content.Folders is not null)
                foreach (var folder in content.Folders) if (IsOwned(folder)) folders.Add(folder);
            var items = new List<InventoryItemBase>();
            if (content.Items is not null)
                foreach (var item in content.Items) if (IsOwned(item)) items.Add(item);

            return new InventoryCollection
            {
                OwnerID = m_ownerId,
                FolderID = folderId,
                Version = content.Version,
                Descendents = folders.Count + items.Count,
                Folders = folders,
                Items = items,
            };
        }

        public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId)
        {
            var content = GetFolderContent(agentId, folderId);
            return content?.Folders ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>();
        }

        /// <summary>
        /// <c>GetMultipleItems</c> returns one slot per requested id and <c>null</c> where the id is unknown
        /// (<c>XInventoryService.cs:643-651</c>), so this drops nulls as well as foreign rows. The count that comes
        /// back is therefore meaningful, which is what <see cref="DeleteItems"/> relies on.
        /// </summary>
        public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds)
        {
            if (!IsCaller(agentId) || itemIds is null || itemIds.Count == 0) return Array.Empty<InventoryItemBase>();
            var ids = new UUID[itemIds.Count];
            for (var i = 0; i < ids.Length; i++) ids[i] = itemIds[i];
            var found = m_service.GetMultipleItems(m_ownerId, ids);
            if (found is null) return Array.Empty<InventoryItemBase>();
            var owned = new List<InventoryItemBase>(found.Length);
            foreach (var item in found) if (IsOwned(item)) owned.Add(item);
            return owned;
        }

        public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId)
        {
            if (!IsCaller(agentId)) return Array.Empty<InventoryFolderBase>();
            var skeleton = m_service.GetInventorySkeleton(m_ownerId);
            if (skeleton is null) return Array.Empty<InventoryFolderBase>();
            var owned = new List<InventoryFolderBase>(skeleton.Count);
            foreach (var folder in skeleton) if (IsOwned(folder)) owned.Add(folder);
            return owned;
        }

        // ---------------- creates: the new object and its parent must both be the owner's ----------------

        public bool AddFolder(InventoryFolderBase folder)
        {
            if (!IsOwned(folder) || folder.ParentID.IsZero()) return false;
            if (GetFolder(m_ownerId, folder.ParentID) is null) return false;
            return m_service.AddFolder(folder);
        }

        public bool AddItem(InventoryItemBase item)
        {
            if (!IsOwned(item) || item.Folder.IsZero()) return false;
            if (GetFolder(m_ownerId, item.Folder) is null) return false;
            return m_service.AddItem(item);
        }

        // ---------------- updates: the row must exist, be the owner's, and land in the owner's folder ----------------

        public bool UpdateItem(InventoryItemBase item)
        {
            if (!IsOwned(item)) return false;
            if (GetItem(m_ownerId, item.ID) is null) return false;
            if (GetFolder(m_ownerId, item.Folder) is null) return false;
            return m_service.UpdateItem(item);
        }

        /// <summary>
        /// The parent is checked only when <c>ParentID</c> is non-zero: the agent's own inventory root legitimately
        /// has none, and refusing that would refuse a rename of the root.
        /// </summary>
        public bool UpdateFolder(InventoryFolderBase folder)
        {
            if (!IsOwned(folder)) return false;
            if (GetFolder(m_ownerId, folder.ID) is null) return false;
            if (folder.ParentID.IsNotZero() && GetFolder(m_ownerId, folder.ParentID) is null) return false;
            return m_service.UpdateFolder(folder);
        }

        // ---------------- deletes ----------------

        /// <summary>
        /// The whole batch is refused unless every id resolves to an item the owner holds, and the check is
        /// <b>one</b> call: <see cref="GetItems"/> wraps <c>GetMultipleItems</c>, so a slam removing ~20 links
        /// costs a single round trip to Robust rather than twenty. The returned count equalling the requested count
        /// is exactly the "all of them, and all mine" test, because the service returns one slot per id.
        /// </summary>
        public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds)
        {
            if (!IsCaller(agentId) || itemIds is null) return false;
            if (GetItems(m_ownerId, itemIds).Count != itemIds.Count) return false;
            return m_service.DeleteItems(m_ownerId, new List<UUID>(itemIds));
        }

        /// <summary>Per id rather than batched: a folder delete carries one or two ids, never a slam's twenty.</summary>
        public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash)
        {
            if (!IsCaller(agentId) || folderIds is null) return false;
            foreach (var id in folderIds)
                if (GetFolder(m_ownerId, id) is null) return false;
            return m_service.DeleteFolders(m_ownerId, new List<UUID>(folderIds), onlyIfTrash);
        }

        public bool PurgeFolder(InventoryFolderBase folder)
        {
            if (!IsOwned(folder)) return false;
            if (GetFolder(m_ownerId, folder.ID) is null) return false;
            return m_service.PurgeFolder(folder);
        }

        /// <summary>
        /// Only a region with a transaction module and a connected client can resolve one; see the remarks on the
        /// interface. AIS-SEC-1 makes it <b>fail closed</b>: an item that is not the owner's, or that is not in the
        /// store, answers <c>Refused</c> rather than <c>NotResolvable</c>, because <c>NotResolvable</c> still
        /// yields a 200 and applying a stranger's upload is not something to be relaxed about. A region with no
        /// resolver at all still answers <c>NotResolvable</c> for the owner's own item, which is the documented
        /// Phase 2 / library behaviour.
        /// </summary>
        public AisAssetTransaction ApplyAssetTransaction(UUID agentId, UUID transactionId, InventoryItemBase item)
        {
            if (!IsCaller(agentId) || !IsOwned(item) || GetItem(m_ownerId, item.ID) is null)
                return AisAssetTransaction.Refused;
            return m_transactions is null ? AisAssetTransaction.NotResolvable : m_transactions(m_ownerId, transactionId, item);
        }

        /// <inheritdoc/>
        public void OnItemAssetChanged(UUID agentId, UUID itemId, UUID newAssetId)
        {
            if (m_wornAssets is null) return;
            if (!IsCaller(agentId) || GetItem(m_ownerId, itemId) is null) return;
            m_wornAssets(m_ownerId, itemId, newAssetId);
        }
    }

    /// <summary>
    /// The LibraryAPIv3 backend: the shared library over <see cref="ILibraryService"/>, which holds the whole
    /// tree in memory (<c>GetAllFolders</c>, <c>InventoryFolderImpl.RequestListOfFolders/RequestListOfItems</c>).
    /// Read-only by construction — every mutator returns false and the handler answers 405 before reaching them
    /// (John's Phase 1 ruling). The agent id is the library owner, so the same handler code needs no library
    /// special case beyond its mode.
    /// </summary>
    public sealed class LibraryServiceBackend : IAisInventoryBackend
    {
        private readonly ILibraryService m_library;
        public LibraryServiceBackend(ILibraryService library) { m_library = library ?? throw new ArgumentNullException(nameof(library)); }

        private InventoryFolderImpl Folder(UUID folderId)
        {
            var root = m_library.LibraryRootFolder;
            if (root is null) return null;
            if (root.ID.Equals(folderId)) return root;
            return m_library.GetAllFolders().TryGetValue(folderId, out var folder) ? folder : null;
        }

        /// <summary>The library has no per-agent system folders; only its root is addressable by type.</summary>
        public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type)
            => type == FolderType.Root ? m_library.LibraryRootFolder : null;

        public InventoryFolderBase GetFolder(UUID agentId, UUID folderId) => Folder(folderId);

        public InventoryCollection GetFolderContent(UUID agentId, UUID folderId)
        {
            var folder = Folder(folderId);
            if (folder is null) return null;
            return new InventoryCollection
            {
                OwnerID = folder.Owner,
                FolderID = folder.ID,
                Version = folder.Version,
                Folders = folder.RequestListOfFolders(),
                Items = folder.RequestListOfItems(),
            };
        }

        public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId)
            => Folder(folderId)?.RequestListOfFolders() ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>();

        public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId)
        {
            var all = m_library.GetAllFolders();
            var list = new List<InventoryFolderBase>(all.Count);
            foreach (var folder in all.Values) list.Add(folder);
            return list;
        }

        public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds)
        {
            var ids = new UUID[itemIds.Count];
            for (var i = 0; i < ids.Length; i++) ids[i] = itemIds[i];
            return m_library.GetMultipleItems(ids) ?? Array.Empty<InventoryItemBase>();
        }

        public InventoryItemBase GetItem(UUID agentId, UUID itemId) => m_library.GetItem(itemId);

        // read-only: the handler answers 405 for every mutation before it reaches these (AisMode.Library)
        public bool AddFolder(InventoryFolderBase folder) => false;
        public bool AddItem(InventoryItemBase item) => false;
        public bool UpdateItem(InventoryItemBase item) => false;
        public bool UpdateFolder(InventoryFolderBase folder) => false;
        public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds) => false;
        public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash) => false;
        public bool PurgeFolder(InventoryFolderBase folder) => false;
        /// <summary>The library is read-only and has no asset transactions.</summary>
        public AisAssetTransaction ApplyAssetTransaction(UUID agentId, UUID transactionId, InventoryItemBase item) => AisAssetTransaction.NotResolvable;
        /// <summary>Nothing in the library is worn.</summary>
        public void OnItemAssetChanged(UUID agentId, UUID itemId, UUID newAssetId) { }
    }
}
