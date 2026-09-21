using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>Which cap this handler is mounted as. The routes are the same; the library one is read-only.</summary>
public enum AisMode
{
    /// <summary>InventoryAPIv3: the agent's own inventory.</summary>
    Inventory,
    /// <summary>LibraryAPIv3: the shared library, owned by the library owner, read-only (mutations answer 405).</summary>
    Library,
}

/// <summary>
/// The InventoryAPIv3 / LibraryAPIv3 cap handler: one per agent per cap, mounted at a random cap path. Parses the
/// request with <see cref="AisRouter"/> and dispatches on <see cref="AisOperation"/>. Holds only an agent id, a
/// backend and its cap path (Ledger P-2) so the same class can be mounted on Robust in Phase 2.
///
/// <para>A1 implemented the **read** surface: <c>GET /item</c>, <c>/category/{id}/children</c> (whole and subset),
/// <c>/categories</c>, <c>/links</c>, <c>/category/current/links</c> and <c>/orphans</c>. A2 adds the
/// **single-object mutations**: <c>PATCH /item</c>, <c>PATCH /category</c>, <c>DELETE /item</c> and
/// <c>DELETE /category</c>, each answering with the delta envelope of §1d-bis. SlamFolder, PurgeDescendents,
/// CreateInventory and COPY are still 501 (A3/A4). Every mutation is 405 on the library cap.</para>
///
/// <para><b>Which collections a response carries.</b> The viewer derives a folder's descendent count only when
/// <c>_embedded</c> has all three of <c>categories</c>, <c>items</c>, <c>links</c> — or, for a Current Outfit or
/// Outfit folder, from <c>links</c> alone (spec §1c, <c>llaisapi.cpp:1466-1482</c>). It then uses that count to
/// accept the folder's <c>version</c>. So a route that returns a folder's **complete** contents
/// (<c>/children</c>) emits all three, and a route that returns a **partial** view (<c>/categories</c>,
/// <c>/links</c>, a subset) emits only the collection it was asked for — emitting empty siblings there would make
/// the viewer compute a wrong descendent count and version a folder it has not actually seen. This refines risk
/// A-R3, which said "always all three".</para>
/// </summary>
public sealed class AisHandler : SimpleStreamHandler
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AisHandler));

    private readonly UUID m_agentId;
    private readonly IAisInventoryBackend m_backend;
    private readonly string m_capPath;
    private readonly AisMode m_mode;

    /// <summary>
    /// Where a library COPY writes, and as whom. Only the library cap has these: its own <see cref="AgentId"/> is
    /// the library owner, so it needs the viewing agent's inventory to copy *into*. Null on the inventory cap,
    /// where COPY is not an operation the viewer sends.
    /// </summary>
    private readonly IAisInventoryBackend m_destination;
    private readonly UUID m_destinationAgentId;

    public AisHandler(string capPath, UUID agentId, IAisInventoryBackend backend, AisMode mode = AisMode.Inventory,
        IAisInventoryBackend destination = null, UUID destinationAgentId = default)
        : base(capPath, mode == AisMode.Library ? AISv3Module.LibraryCapName : AISv3Module.CapName)
    {
        m_capPath = capPath;
        m_agentId = agentId;
        m_backend = backend ?? throw new ArgumentNullException(nameof(backend));
        m_mode = mode;
        m_destination = destination;
        m_destinationAgentId = destinationAgentId;
    }

    public UUID AgentId => m_agentId;
    public string CapPath => m_capPath;
    public AisMode Mode => m_mode;

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var route = AisRouter.Parse(httpRequest.HttpMethod, httpRequest.RawUrl ?? httpRequest.UriPath, m_capPath);
        // A6: without this a request that arrives and fails is indistinguishable in the log from one that never
        // arrived, which is exactly what made the first live run take a code read to diagnose.
        if (m_log.IsEnabled(LogLevel.Debug))
            m_log.LogDebug("[AIS]: {Verb} {Url} -> {Operation} (cap {Mode}, agent {Agent})",
                httpRequest.HttpMethod, httpRequest.RawUrl ?? httpRequest.UriPath, route.Operation, m_mode, m_agentId);
        Dispatch(route, httpRequest, httpResponse);
    }

    /// <summary>Dispatch a parsed route. Public so the HTTP-level tests can drive it without a scene.</summary>
    public void Dispatch(AisRoute route, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var read = ReadBody(httpRequest);

        // AIS-SEC-2. A mutating route never runs on a body we could not read. The fetch routes are untouched:
        // they ignore the body entirely, and refusing one over a body nobody looks at would break clients that
        // send a stray one. Checked here, before the switch, so no handler can see a body it should not.
        //
        // The library cap is the exception, and the ordering matters: there every mutation but COPY is refused
        // 405 because the cap is read-only, and that answer is true whatever the body says. Letting the body
        // gate run first would turn "this cap does not do mutations" into "your body was malformed", which is
        // both less informative and wrong about the reason. COPY keeps the gate - it is a genuine library
        // operation, so its body is worth bounding.
        var refusedAsReadOnly = m_mode == AisMode.Library && route.Operation != AisOperation.CopyCategory;
        if (AisOperations.IsMutation(route.Operation) && !refusedAsReadOnly)
        {
            switch (read.Status)
            {
                case AisBodyStatus.TooLarge:
                    m_log.LogWarning("[AIS]: {Operation} on {Path} from agent {Agent} sent a body over the {Limit} byte limit ({Bytes} bytes and still coming); refused unread",
                        route.Operation, route.Path, m_agentId, MaxBodyBytes, read.Bytes);
                    WriteError(httpResponse, HttpStatusCode.RequestEntityTooLarge,
                        $"the request body exceeds the {MaxBodyBytes} byte limit", route);
                    return;

                case AisBodyStatus.ParseFailure:
                    // Byte count only, never the body: it is attacker-controlled and may carry anything.
                    m_log.LogWarning("[AIS]: {Operation} on {Path} from agent {Agent} sent a body that is not LLSD ({Bytes} bytes); refused without writing",
                        route.Operation, route.Path, m_agentId, read.Bytes);
                    WriteError(httpResponse, HttpStatusCode.BadRequest, "malformed LLSD body", route);
                    return;

                // A slam REPLACES a folder's links, so an absent body cannot be read as "replace them with
                // nothing" - that is the defect this session closed. The other mutating routes keep today's
                // empty-map behaviour deliberately: their semantics are not in scope here.
                case AisBodyStatus.NoBody when route.Operation == AisOperation.SlamFolder:
                    m_log.LogWarning("[AIS]: SlamFolder on {Path} from agent {Agent} arrived with no body; refused rather than read as an empty slam",
                        route.Path, m_agentId);
                    WriteError(httpResponse, HttpStatusCode.BadRequest, "missing body", route);
                    return;
            }
        }

        var raw = read.Value ?? new OSDMap();
        var body = raw as OSDMap ?? new OSDMap();
        try
        {
            switch (route.Operation)
            {
                case AisOperation.Unknown:
                    WriteError(httpResponse, HttpStatusCode.NotFound, "no such AIS v3 route", route);
                    return;

                case AisOperation.FetchItem: FetchItem(route, httpResponse); return;
                case AisOperation.FetchCategoryChildren: FetchChildren(route, httpResponse); return;
                case AisOperation.FetchCategorySubset: FetchSubset(route, httpResponse); return;
                case AisOperation.FetchCategoryCategories: FetchCategories(route, httpResponse); return;
                case AisOperation.FetchCategoryLinks:
                case AisOperation.FetchCOF: FetchLinks(route, httpResponse); return;
                case AisOperation.FetchOrphans: FetchOrphans(route, httpResponse); return;
                case AisOperation.CopyCategory: CopyCategory(route, httpRequest, httpResponse); return;

                case AisOperation.UpdateItem:
                case AisOperation.UpdateCategory:
                case AisOperation.RemoveItem:
                case AisOperation.RemoveCategory:
                case AisOperation.SlamFolder:
                case AisOperation.CreateInventory:
                case AisOperation.PurgeDescendents:
                    // COPY is the exception: it is a library-cap operation by design (the viewer sends it to
                    // {lib}, spec 1a row 5), and it writes into the *agent's* inventory, not the library.
                    if (m_mode == AisMode.Library)
                    {
                        WriteError(httpResponse, HttpStatusCode.MethodNotAllowed, $"{route.Operation} is not allowed on the library: LibraryAPIv3 is read-only", route);
                        return;
                    }
                    switch (route.Operation)
                    {
                        case AisOperation.UpdateItem: UpdateItem(route, body, httpResponse); return;
                        case AisOperation.UpdateCategory: UpdateCategory(route, body, httpResponse); return;
                        case AisOperation.RemoveItem: RemoveItem(route, httpResponse); return;
                        case AisOperation.SlamFolder: SlamFolder(route, raw, httpResponse); return;
                        case AisOperation.CreateInventory: CreateInventory(route, body, httpResponse); return;
                        case AisOperation.PurgeDescendents: PurgeDescendents(route, httpResponse); return;
                        default: RemoveCategory(route, httpResponse); return;
                    }

                default:
                    // Every mutation. On the library cap they are refused outright; on the inventory cap they are
                    // not implemented yet (A2). Both bodies are flat maps the viewer's update parser ignores (§1f).
                    if (m_mode == AisMode.Library)
                        WriteError(httpResponse, HttpStatusCode.MethodNotAllowed, $"{route.Operation} is not allowed on the library: LibraryAPIv3 is read-only", route);
                    else
                        WriteError(httpResponse, HttpStatusCode.NotImplemented, $"{route.Operation} is not implemented", route);
                    return;
            }
        }
        catch (Exception ex)
        {
            // AIS-SEC-5. The exception goes to the LOG, never to the client. It used to be the other way round:
            // `WriteError(..., ex.Message, ...)` with no logging at all, so a connector or database fault
            // travelled to an untrusted client as text - credentials, host names, internal type names, whatever
            // the message happened to carry - while the operator who needs the stack got nothing. Both halves
            // were the same line.
            //
            // The client gets a fixed string. There is nothing useful it could do with the detail, the viewer
            // does not read the message (spec 1f: nothing in the permitted files reads `message`), and the route
            // and verb it already knows are echoed by ErrorBody anyway.
            m_log.LogError(ex,
                "[AIS]: {Operation} on {Path} for agent {Agent} failed with an unhandled exception; answered 500",
                route.Operation, route.Path, m_agentId);
            WriteError(httpResponse, HttpStatusCode.InternalServerError,
                "the request could not be completed", route);
        }
    }

    // ------------------------------------------------------------------ the read routes

    /// <summary>GET /item/{id} — an item, or a link map when the row is a link (§1c: <c>linked_id</c> selects parseLink).</summary>
    private void FetchItem(AisRoute route, IOSHttpResponse response)
    {
        var item = m_backend.GetItem(m_agentId, route.Id);
        if (item is null) { WriteError(response, HttpStatusCode.NotFound, $"no item {route.Id}", route); return; }
        var body = AisEnvelope.IsLink(item) ? AisEnvelope.Link(item, m_agentId) : AisEnvelope.Item(item, m_agentId);
        Write(response, body, route);
    }

    /// <summary>
    /// GET /category/{id}/children?depth=N — the folder with its complete contents, expanded N generations
    /// (<see cref="AisInventory.Walk"/>). All three collections at every expanded level.
    /// </summary>
    private void FetchChildren(AisRoute route, IOSHttpResponse response)
    {
        // MAX_FOLDER_DEPTH_REQUEST (llaisapi.cpp:58): the viewer clamps every depth it sends to 50, so anything
        // above that is a client we do not know asking the region to walk further than any viewer would use.
        var depth = System.Math.Clamp(route.Depth, 0, AisInventory.MaxDepth);
        var walked = AisInventory.Walk(m_backend, m_agentId, route.Id, depth);
        if (walked.Count == 0) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

        var expanded = new Dictionary<UUID, AisFolderContents>();
        foreach (var c in walked) expanded[c.Folder.ID] = c;
        // AIS-SEC-5: one visited set for the whole expansion, replacing the per-level dictionary clone.
        Write(response, Expand(walked[0], expanded, new HashSet<UUID>()), route);
    }

    /// <summary>
    /// A folder as a category map with all three collections; each sub-folder is expanded in turn when the walk
    /// reached it, and appears as a bare category map (no <c>_embedded</c>) when it did not.
    /// </summary>
    /// <summary>
    /// AIS-SEC-5: <paramref name="visited"/> replaces the per-level dictionary clone this used to make.
    ///
    /// <para><b>Why that substitution is equivalence and not an approximation.</b> The old <c>Without()</c>
    /// copied the whole expanded map minus the current folder and handed the <i>same</i> copy to every sibling,
    /// so it implemented <b>ancestor-path exclusion</b>: a folder excluded down one branch was still available
    /// to a sibling branch. A single shared set is stronger - <b>global once-only</b>. The two disagree exactly
    /// when a folder is reachable by two distinct paths, i.e. a diamond.
    /// <c>InventoryFolderBase.ParentID</c> is a single scalar and <c>GetFolderContent</c> selects children by
    /// <c>ParentID == folderId</c>, so every folder is the child of exactly one parent: the graph is a forest
    /// plus possible cycles, and a diamond cannot occur. <c>AisErrorHygieneTraversalTests</c> pins both the
    /// cyclic output shape and that data-model property, so if multi-parenting ever arrives the assumption
    /// fails loudly rather than silently.</para>
    ///
    /// <para>The clone was correct; it was just O(folders) of allocation at every level, which a deep tree paid
    /// all the way down. The set is one allocation for the whole response.</para>
    /// </summary>
    private OSDMap Expand(AisFolderContents contents, Dictionary<UUID, AisFolderContents> expanded, HashSet<UUID> visited)
    {
        visited.Add(contents.Folder.ID);
        var categories = new OSDMap();
        foreach (var child in contents.SubFolders)
        {
            categories[child.ID.ToString()] = !visited.Contains(child.ID) && expanded.TryGetValue(child.ID, out var childContents)
                ? Expand(childContents, expanded, visited)
                : AisEnvelope.Category(child, m_agentId);
        }
        var embedded = AisEnvelope.EmbeddedMap(categories, AisEnvelope.ItemsMap(contents.Items, m_agentId), AisEnvelope.LinksMap(contents.Links, m_agentId));
        return AisEnvelope.Category(contents.Folder, m_agentId, embedded);
    }

    /// <summary>
    /// GET /category/{id}/children?depth=N&amp;children=a,b,... — only the named children. The viewer ignores the
    /// top-level category for a subset and parses <c>_embedded</c> one level shallower (§1c,
    /// <c>llaisapi.cpp:1194-1202</c>), so the folder is still the envelope but its collections carry only what was
    /// asked for. A named child that does not exist is simply absent: the viewer asked for it, so its absence is
    /// the answer, and failing the whole request would lose the children that do exist.
    /// </summary>
    private void FetchSubset(AisRoute route, IOSHttpResponse response)
    {
        var contents = AisInventory.GetContents(m_backend, m_agentId, route.Id);
        if (contents is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

        var wanted = new HashSet<UUID>(route.Children);
        var categories = new OSDMap();
        foreach (var child in contents.SubFolders)
            if (wanted.Contains(child.ID)) categories[child.ID.ToString()] = AisEnvelope.Category(child, m_agentId);
        var items = new OSDMap();
        foreach (var item in contents.Items)
            if (wanted.Contains(item.ID)) items[item.ID.ToString()] = AisEnvelope.Item(item, m_agentId);
        var links = new OSDMap();
        foreach (var link in contents.Links)
            if (wanted.Contains(link.ID)) links[link.ID.ToString()] = AisEnvelope.Link(link, m_agentId);

        Write(response, AisEnvelope.Category(contents.Folder, m_agentId, AisEnvelope.EmbeddedMap(categories, items, links)), route);
    }

    /// <summary>GET /category/{id}/categories — sub-folders only, so <c>_embedded</c> carries <c>categories</c> alone.</summary>
    private void FetchCategories(AisRoute route, IOSHttpResponse response)
    {
        var folder = m_backend.GetFolder(m_agentId, route.Id);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }
        var categories = new OSDMap();
        foreach (var child in m_backend.GetSubFolders(m_agentId, route.Id) ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>())
            categories[child.ID.ToString()] = AisEnvelope.Category(child, m_agentId);
        var embedded = new OSDMap { [AisEnvelope.Categories] = categories };
        Write(response, AisEnvelope.Category(folder, m_agentId, embedded), route);
    }

    /// <summary>
    /// GET /category/{id}/links and GET /category/current/links — the folder and everything its links point to.
    /// <c>_embedded</c> carries <c>links</c> (the link rows) and, so the viewer has the targets it will need,
    /// <c>items</c> holding the **link targets** — the items the links resolve to, not the links themselves. That
    /// is what the existing descendents cap sends for the same reason ("viewers are lasy and want a copy of the
    /// linked item sent before the link to it", <c>FetchInvDescHandler.cs:429</c>), and what
    /// <see cref="AisInventory.ResolveLinkTargets"/> gathers.
    ///
    /// <para>For a Current Outfit or Outfit folder the viewer takes the descendent count from <c>links</c> alone
    /// (§1c), which is exactly this shape.</para>
    /// </summary>
    private void FetchLinks(AisRoute route, IOSHttpResponse response)
    {
        var folderId = route.Id;
        if (route.Operation == AisOperation.FetchCOF)
        {
            var cof = AisInventory.GetCurrentOutfit(m_backend, m_agentId);
            if (cof is null) { WriteError(response, HttpStatusCode.NotFound, "the agent has no Current Outfit folder", route); return; }
            folderId = cof.ID;
            // A11: the resolution, per request. The A7 WARN only fires when there is more than one candidate, so
            // in the ordinary case nothing recorded which folder "current" meant — which is what made A10 have to
            // infer it from the mutation URLs.
            if (m_log.IsEnabled(LogLevel.Debug))
                m_log.LogDebug("[AIS]: FetchCOF resolved \"current\" to {Folder} version {Version} for agent {Agent}",
                    cof.ID, cof.Version, m_agentId);
        }

        var contents = AisInventory.GetContents(m_backend, m_agentId, folderId);
        if (contents is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {folderId}", route); return; }

        var targets = AisInventory.ResolveLinkTargets(m_backend, m_agentId, contents.Links);
        var embedded = new OSDMap
        {
            [AisEnvelope.Links] = AisEnvelope.LinksMap(contents.Links, m_agentId),
            [AisEnvelope.Items] = AisEnvelope.ItemsMap(targets, m_agentId),
        };
        Write(response, AisEnvelope.Category(contents.Folder, m_agentId, embedded), route);
    }

    /// <summary>
    /// GET /orphans — folders whose parent no longer exists. No <c>category_id</c>/<c>item_id</c> at top level, so
    /// the viewer parses <c>_embedded</c> straight (§1c). Orphaned items are not reported; see
    /// <see cref="AisInventory.FindOrphans"/> for why.
    /// </summary>
    private void FetchOrphans(AisRoute route, IOSHttpResponse response)
    {
        var orphans = AisInventory.FindOrphans(m_backend, m_agentId);
        var categories = new OSDMap();
        foreach (var folder in orphans.Folders) categories[folder.ID.ToString()] = AisEnvelope.Category(folder, m_agentId);
        var embedded = new OSDMap
        {
            [AisEnvelope.Categories] = categories,
            [AisEnvelope.Items] = AisEnvelope.ItemsMap(orphans.Items, m_agentId),
        };
        Write(response, new OSDMap { [AisEnvelope.Embedded] = embedded }, route);
    }

    // ------------------------------------------------------------------ AIS-SEC-3: the folder mutation lock

    /// <summary>
    /// Runs <paramref name="body"/> holding the <c>(agent, folder)</c> lock, or answers 503 and runs nothing.
    ///
    /// <para>Every mutating route takes exactly <b>one</b> key, so there is no lock-ordering problem to solve and
    /// deadlock is structurally impossible rather than merely avoided. That holds because AIS-SEC-2 made
    /// <c>POST /category/{parent}</c> refuse a body whose <c>parent_id</c> disagrees with the URL (400), so a
    /// create can only ever write into the folder it addressed. <b>If a future route genuinely needs two
    /// folders</b> - a create whose categories carry links destined for a child, say - take them in ascending
    /// <c>UUID</c> string order and say so at the call site; that total order is what keeps it deadlock-free.</para>
    ///
    /// <para><b>503 and not 409.</b> 409 Conflict says the request disagrees with the current state and the client
    /// must resolve it - re-fetch, merge, decide. Nothing is wrong with this request: it is valid and would
    /// succeed, and the server simply declined to queue behind another change any longer. That is "temporarily
    /// unavailable", which is 503, and it is the status that carries <c>Retry-After</c>. The viewer treats any
    /// non-2xx here alike (<c>llaisapi.cpp:851-951</c>), so the header is for well-behaved clients and the
    /// operator reading the log.</para>
    /// </summary>
    private void WithFolderLock(UUID folderId, AisRoute route, IOSHttpResponse response, Action body)
        => WithFolderLock(m_agentId, folderId, route, response, body);

    /// <summary>
    /// As above, for the one route that writes as somebody other than this cap's owner: a library COPY writes into
    /// the <i>viewing</i> agent's inventory, so its key is that agent and the destination folder. Keying it on the
    /// library owner would order library copies against each other and not against the resident's own slams into
    /// the same folder, which is the pairing that actually races.
    /// </summary>
    private void WithFolderLock(UUID lockAgentId, UUID folderId, AisRoute route, IOSHttpResponse response, Action body)
    {
        if (!AisFolderLocks.TryEnter(lockAgentId, folderId))
        {
            m_log.LogWarning(
                "[AIS]: {Operation} on folder {Folder} for agent {Agent} waited {Seconds}s for the folder lock and "
                + "gave up; answered 503 rather than mutating unserialised", route.Operation, folderId, lockAgentId,
                AisFolderLocks.Timeout.TotalSeconds);
            response.AddHeader("Retry-After", "2");
            WriteError(response, HttpStatusCode.ServiceUnavailable,
                $"another change to category {folderId} is already in progress; retry", route);
            return;
        }

        try { body(); }
        finally { AisFolderLocks.Exit(lockAgentId, folderId); }
    }

    // ------------------------------------------------------------------ the mutation routes (A2)

    /// <summary>
    /// The largest AIS request body this handler will read. A real slam is a few kilobytes — the viewer sends one
    /// link map per worn item — so a megabyte is far above anything legitimate and still small enough that
    /// refusing it costs nothing. Public so the tests assert against the same number the handler enforces.
    /// </summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>What became of the request body. Four states, because three of them must not reach a handler.</summary>
    private enum AisBodyStatus
    {
        /// <summary>The request carried no body at all.</summary>
        NoBody,
        /// <summary>The body parsed as LLSD.</summary>
        Parsed,
        /// <summary>There was a body and it is not LLSD.</summary>
        ParseFailure,
        /// <summary>The body exceeded <see cref="MaxBodyBytes"/> and was not read to the end.</summary>
        TooLarge,
    }

    private readonly record struct AisBody(AisBodyStatus Status, OSD Value, long Bytes)
    {
        public static AisBody None() => new(AisBodyStatus.NoBody, null, 0);
        public static AisBody Ok(OSD value, long bytes) => new(AisBodyStatus.Parsed, value, bytes);
        public static AisBody Failure(long bytes) => new(AisBodyStatus.ParseFailure, null, bytes);
        public static AisBody Oversize(long bytes) => new(AisBodyStatus.TooLarge, null, bytes);
    }

    /// <summary>
    /// The request body as LLSD, whatever its top-level type. A slam body is a bare **array**
    /// (<c>llappearancemgr.cpp:2209-2245</c>, <c>:1795-1833</c>), so it cannot be forced to a map here.
    ///
    /// <para><b>AIS-SEC-2: the guarantee is that a body which fails validation produces zero inventory writes.</b>
    /// This used to end in <c>catch { return new OSDMap(); }</c> and return that same empty map for an absent
    /// body, which handed <see cref="AisSlam.ParseBody"/> something it read as an intentional empty slam — so a
    /// truncated <c>PUT /category/{COF}/links</c>, and a dropped connection is enough, emptied the wearer's
    /// Current Outfit. A body that cannot be understood is now a distinct answer from a body that asks for
    /// nothing, and only the caller decides what to do with each.</para>
    ///
    /// <para><b>Do not reduce the failure test to a try/catch and a null check.</b> Verified against the shipped
    /// parser on 2026-09-12: <c>OSDParser.DeserializeLLSDXml</c> neither throws nor returns null for truncated
    /// XML or for well-formed XML that is not LLSD — it returns a bare <see cref="OSD"/> whose
    /// <see cref="OSD.Type"/> is <c>OSDType.Unknown</c>. That value is the actual signal, and without it a
    /// malformed <c>PATCH</c> body still becomes an empty map and still answers 200.</para>
    ///
    /// <para>The ceiling is enforced <b>while</b> the stream is copied, never after: a body too large to trust is
    /// also a body too large to hold, so reading stops the moment the limit is passed.</para>
    /// </summary>
    private static AisBody ReadBody(IOSHttpRequest request)
    {
        var stream = request?.InputStream;
        if (stream is null) return AisBody.None();
        try
        {
            var buffer = new byte[8192];
            using var ms = new System.IO.MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ms.Length + read > MaxBodyBytes) return AisBody.Oversize(ms.Length + read);
                ms.Write(buffer, 0, read);
            }
            if (ms.Length == 0) return AisBody.None();

            var bytes = ms.ToArray();
            OSD parsed;
            try { parsed = OSDParser.DeserializeLLSDXml(bytes); }
            catch { return AisBody.Failure(bytes.Length); }
            if (parsed is null || parsed.Type == OSDType.Unknown) return AisBody.Failure(bytes.Length);
            return AisBody.Ok(parsed, bytes.Length);
        }
        catch { return AisBody.Failure(-1); }
        finally { try { request.InputStream?.Dispose(); } catch { } }
    }

    /// <summary>
    /// PATCH /item/{id}. The updated item goes back as **top-level content** — on a mutation the viewer ignores
    /// anything embedded that is not in <c>_created_items</c> (§1c) — and its parent folder is listed in
    /// <c>_updated_category_versions</c>, without which the viewer discards even the zero-delta entry
    /// <c>parseItem</c> creates (§1d-bis, <c>llaisapi.cpp:1625-1629</c>). Fields this tree cannot store are
    /// ignored, not refused: the viewer sends the whole item map, so most keys carry unchanged values.
    ///
    /// <para>
    /// A16: the body's asset travels either as <c>asset_id</c> or, on the path a wearable save actually takes, as
    /// <c>hash_id</c> — the id of the xfer transaction that uploaded it. The map fields are applied and stored
    /// first and the transaction is handed over second, which is the order the legacy UDP route uses
    /// (<c>Scene.Inventory.cs:576</c> then <c>:579-582</c>); the transaction module stores the item again once the
    /// xfer completes, so the item is re-read before the envelope is built. Both writes bump the parent folder's
    /// version in the data layer (<c>MySQLXInventoryData.cs:238-246</c>), which is what makes the viewer re-read
    /// the item at all — before A16 no write happened, so no version moved and the edit was never fetched back.
    /// </para>
    /// </summary>
    private void UpdateItem(AisRoute route, OSDMap body, IOSHttpResponse response)
    {
        var item = m_backend.GetItem(m_agentId, route.Id);
        if (item is null) { WriteError(response, HttpStatusCode.NotFound, $"no item {route.Id}", route); return; }
        // AIS-SEC-3: key on item.Folder - the item's parent folder, whose version the store bumps.
        WithFolderLock(item.Folder, route, response, () =>
        {

            // S9: captured before ApplyToItem, which mutates the item in place.
            var assetBefore = item.AssetID;

            var applied = AisMutation.ApplyToItem(body, item);
            if (applied.Any && !m_backend.UpdateItem(item))
            {
                WriteError(response, HttpStatusCode.InternalServerError, $"the inventory service refused the update of item {route.Id}", route);
                return;
            }

            if (applied.Transaction.IsNotZero())
            {
                // Unknown transaction ids are not an error here: the module opens a pending uploader for one and the
                // asset lands when the xfer does, exactly as it does for the legacy route (AgentAssetsTransactions.cs:68-90).
                // A19: a refused transaction is a FAILED save and must be answered as one. Before this, the verdict
                // was discarded and the cap answered 200 with the item's old asset id in the envelope, so the viewer
                // recorded a save that had not happened - observed 2026-09-06 09:52:55, a wearable referencing a
                // library texture refused by the uploader and reported as "UpdateItem -> 200".
                //
                // 403 rather than 500: the refusal is always a permission verdict on the referenced assets
                // (AssetXferUploader.ValidateAssets), and the viewer treats a non-2xx as an error without special
                // handling for this command (llaisapi.cpp:880-948). The error body carries no
                // _updated_category_versions, so the folder version the viewer holds does NOT advance and its next
                // fetch of that folder still sees the true state.
                if (m_backend.ApplyAssetTransaction(m_agentId, applied.Transaction, item) == AisAssetTransaction.Refused)
                {
                    WriteError(response, HttpStatusCode.Forbidden,
                        $"the asset uploaded by transaction {applied.Transaction} was refused for item {route.Id}; the item still points at its previous asset", route);
                    return;
                }
                item = m_backend.GetItem(m_agentId, route.Id) ?? item;
            }

            // S9: an edit to a WORN wearable is the one appearance change nothing else tells the region about. The
            // worn set does not move (the viewer keeps the item id), so no AgentIsNowWearing follows, and the
            // UpdateAvatarAppearance POST is deferred behind pending uploads and can arrive stale. This PATCH is the
            // moment the new asset exists and is known, so it is where the save is queued.
            if (item.AssetID != assetBefore && item.AssetID.IsNotZero())
                m_backend.OnItemAssetChanged(m_agentId, route.Id, item.AssetID);

            var envelope = AisEnvelope.Item(item, m_agentId);
            AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, item.Folder));
            Write(response, envelope, route);
        });
    }

    /// <summary>
    /// PATCH /category/{id}. <c>parseCategory</c> creates zero-delta entries for the category **and** its parent
    /// (§1d-bis, <c>llaisapi.cpp:1419-1428</c>), so both are listed. <c>thumbnail</c> and <c>favorite</c> have no
    /// storage in this tree and are dropped.
    /// </summary>
    private void UpdateCategory(AisRoute route, OSDMap body, IOSHttpResponse response)
    {
        // AIS-SEC-3: key on route.Id - the folder itself.
        WithFolderLock(route.Id, route, response, () =>
        {
            var folder = m_backend.GetFolder(m_agentId, route.Id);
            if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

            var applied = AisMutation.ApplyToFolder(body, folder);
            if (applied.Any && !m_backend.UpdateFolder(folder))
            {
                WriteError(response, HttpStatusCode.InternalServerError, $"the inventory service refused the update of category {route.Id}", route);
                return;
            }

            var fresh = m_backend.GetFolder(m_agentId, route.Id) ?? folder;
            var envelope = AisEnvelope.Category(fresh, m_agentId);
            AisMutation.ReportVersion(envelope, fresh);
            AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, fresh.ParentID));
            Write(response, envelope, route);
        });
    }

    /// <summary>
    /// DELETE /item/{id}. No content: the removal travels as <c>_removed_items</c> and the parent's new version
    /// as <c>_updated_category_versions</c> (§1d-bis). The parent is read **after** the delete, so the version is
    /// the post-operation one the data layer bumped (S0a V6, tree state T3/T4).
    /// </summary>
    private void RemoveItem(AisRoute route, IOSHttpResponse response)
    {
        var item = m_backend.GetItem(m_agentId, route.Id);
        if (item is null) { WriteError(response, HttpStatusCode.NotFound, $"no item {route.Id}", route); return; }
        var parentId = item.Folder;
        // AIS-SEC-3: key on parentId - the item's parent: its child list changes.
        WithFolderLock(parentId, route, response, () =>
        {

            if (!m_backend.DeleteItems(m_agentId, new[] { route.Id }))
            {
                WriteError(response, HttpStatusCode.InternalServerError, $"the inventory service refused the delete of item {route.Id}", route);
                return;
            }

            var envelope = new OSDMap();
            AisMutation.ReportRemoved(envelope, AisMutation.RemovedItems, route.Id);
            AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, parentId));
            Write(response, envelope, route);
        });
    }

    /// <summary>
    /// COPY /category/{sourceId}?tid= — CopyLibraryCategory (<see cref="AisCopy"/> for the permission rule this
    /// reuses and the no-rollback reasoning).
    ///
    /// <para>The destination folder id travels in the HTTP <c>Destination</c> header
    /// (<c>llcorehttputil.cpp:1135</c>, A1). The tid carries a quirk: when the viewer does **not** want
    /// sub-folders it appends the literal <c>,depth=0</c> to the tid value rather than adding a query parameter
    /// (<c>llaisapi.cpp:275-278</c>), which <see cref="AisRouter"/> already splits out into the <c>depth</c>
    /// query value — so <c>depth == 0</c> here means "this folder only".</para>
    ///
    /// <para>The destination is **not** subjected to the protected-folder rule. Copying a library folder into
    /// Clothing or into the inventory root is the ordinary case, and that rule governs moving, deleting and
    /// retyping a folder, not adding children to it — the same reconciliation as slam (A3) and purge. What is
    /// checked is the thing that matters: the destination must exist and be the agent's own folder.</para>
    /// </summary>
    private void CopyCategory(AisRoute route, IOSHttpRequest request, IOSHttpResponse response)
    {
        if (m_mode != AisMode.Library || m_destination is null)
        {
            WriteError(response, HttpStatusCode.NotImplemented,
                "COPY is a LibraryAPIv3 operation; this cap cannot serve it", route);
            return;
        }

        var destinationHeader = request.Headers["Destination"];
        if (!UUID.TryParse(destinationHeader, out var destinationId) || destinationId.IsZero())
        {
            WriteError(response, HttpStatusCode.BadRequest,
                "COPY needs a Destination header carrying the destination folder id", route);
            return;
        }

        var destinationFolder = m_destination.GetFolder(m_destinationAgentId, destinationId);
        if (destinationFolder is null)
        {
            WriteError(response, HttpStatusCode.NotFound, $"no destination category {destinationId}", route);
            return;
        }

        // ",depth=0" on the tid means this folder only (llaisapi.cpp:275-278)
        // AIS-SEC-3: COPY writes into the DESTINATION folder as the DESTINATION agent, not as this cap's
        // owner (which is the library). So the key is (m_destinationAgentId, destinationId) - keying it on
        // the library owner would order library copies against each other and not against the resident's
        // own slams into the same folder, which is the pairing that actually races.
        WithFolderLock(m_destinationAgentId, destinationId, route, response, () =>
        {
            var copySubfolders = route.Depth != 0;

            var outcome = AisCopy.Run(m_backend, m_destination, m_agentId, m_destinationAgentId,
                route.Id, destinationId, copySubfolders);

            var envelope = new OSDMap();
            var categoryIds = new OSDArray();
            var itemIds = new OSDArray();
            var embeddedCategories = new OSDMap();
            var embeddedItems = new OSDMap();
            foreach (var folder in outcome.Categories)
            {
                categoryIds.Add(OSD.FromUUID(folder.ID));
                embeddedCategories[folder.ID.ToString()] = AisEnvelope.Category(folder, m_destinationAgentId,
                    AisEnvelope.EmbeddedMap(new OSDMap(), new OSDMap(), new OSDMap()));
            }
            foreach (var item in outcome.Items)
            {
                itemIds.Add(OSD.FromUUID(item.ID));
                embeddedItems[item.ID.ToString()] = AisEnvelope.Item(item, m_destinationAgentId);
            }

            if (!outcome.Ok)
            {
                // additive, so a partial copy leaves what it made and risks nothing that existed before
                WriteError(response, HttpStatusCode.InternalServerError,
                    $"{outcome.Failure}; {categoryIds.Count} categories and {itemIds.Count} items were created before the failure", route);
                return;
            }

            if (categoryIds.Count > 0) envelope[AisMutation.CreatedCategories] = categoryIds;
            if (itemIds.Count > 0) envelope[AisMutation.CreatedItems] = itemIds;
            if (embeddedCategories.Count > 0 || embeddedItems.Count > 0)
            {
                var embedded = new OSDMap();
                if (embeddedCategories.Count > 0) embedded[AisEnvelope.Categories] = embeddedCategories;
                if (embeddedItems.Count > 0) embedded[AisEnvelope.Items] = embeddedItems;
                envelope[AisEnvelope.Embedded] = embedded;
            }
            AisMutation.ReportVersion(envelope, m_destination.GetFolder(m_destinationAgentId, destinationId));
            Write(response, envelope, route);
        });
    }
    /// <summary>
    /// DELETE /category/{id}/children — empty the folder, keeping the folder (<see cref="AisPurge"/>, which
    /// documents who calls it, why the deltas must be enumerated and what the composition costs).
    ///
    /// <para>The protected-folder rule is deliberately not applied: Trash and Lost and Found are protected types
    /// and are precisely the folders this operation exists to empty (<c>llinventorymodel.cpp:4125-4131</c>).</para>
    /// </summary>
    private void PurgeDescendents(AisRoute route, IOSHttpResponse response)
    {
        var folderId = route.Id;
        if (route.IsAlias)
        {
            var cof = AisInventory.GetCurrentOutfit(m_backend, m_agentId);
            if (cof is null) { WriteError(response, HttpStatusCode.NotFound, "the agent has no Current Outfit folder", route); return; }
            folderId = cof.ID;
        }

        var folder = m_backend.GetFolder(m_agentId, folderId);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {folderId}", route); return; }

        // AIS-SEC-3: key on folderId - snapshot -> delete, the other side of the same race.
        WithFolderLock(folderId, route, response, () =>
        {
            var outcome = AisPurge.Run(m_backend, m_agentId, folder);

            var envelope = new OSDMap();
            foreach (var id in outcome.RemovedCategories) AisMutation.ReportRemoved(envelope, AisMutation.CategoriesRemoved, id);
            foreach (var id in outcome.RemovedItems) AisMutation.ReportRemoved(envelope, AisMutation.RemovedItems, id);
            AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, folderId));

            if (!outcome.Ok)
            {
                // Partly purged. A purge cannot be rolled back, so the honest answer is to say which children
                // survived; re-issuing the purge finishes the job (see AisPurge.Run).
                WriteError(response, HttpStatusCode.InternalServerError,
                    $"category {folderId} was only partly purged; these children remain: {string.Join(", ", outcome.Survivors)}", route);
                return;
            }
            Write(response, envelope, route);
        });
    }
    /// <summary>
    /// POST /category/{parentId}?tid= — create categories, items and links in that folder.
    ///
    /// <para><b>The route is the parent category itself</b>, not <c>/children</c>: <c>AISAPI::CreateInventory</c>
    /// builds <c>{inv}/category/{parentId}</c> (<c>llaisapi.cpp:115</c>).</para>
    ///
    /// <para><b>The body</b> is a map of arrays, and A4 pinned two of the three against their builders:</para>
    /// <list type="bullet">
    ///   <item><b><c>links</c> — verified.</b> <c>link_inventory_array</c> builds each entry with exactly
    ///   <c>linked_id</c>, <c>type</c> (<c>AT_LINK</c> or <c>AT_LINK_FOLDER</c>), <c>inv_type</c> (the
    ///   <i>target's</i> inventory type), <c>name</c> and <c>desc</c>, and sends them as
    ///   <c>new_inventory["links"]</c> (<c>llviewerinventory.cpp:1352-1370</c>). No <c>parent_id</c>: the folder
    ///   is the one in the URL.</item>
    ///   <item><b><c>items</c> — verified, and deliberately refused.</b> The one builder wraps the item's whole
    ///   <c>asLLSD()</c> with a null <c>item_id</c> and a null <c>asset_id</c> — <i>"don't know yet, whenever
    ///   server creates it"</i> — because the server is expected to mint the asset
    ///   (<c>llviewerinventory.cpp:1124-1157</c>). It sits inside <c>#ifdef USE_AIS_FOR_NC</c>, which is never
    ///   defined in that file, above the viewer's own comment <i>"not yet implemented within AIS3"</i>
    ///   (<c>:1120-1121</c>) — so a stock viewer never sends it. This handler answers **501** for a non-empty
    ///   <c>items</c> array rather than creating an item with no asset behind it, which is what A3's guess did.
    ///   </item>
    ///   <item><b><c>categories</c> — verified (A5).</b> <c>LLInventoryCategory::asAISCreateCatLLSD</c>
    ///   (<c>indra/llinventory/llinventory.cpp:1256-1276</c>) emits exactly <c>category_id</c> (null on a create,
    ///   since the viewer builds the category with <c>LLUUID::null</c>, <c>llinventorymodel.cpp:1038</c>),
    ///   <c>parent_id</c>, <c>type_default</c> as an **integer** preferred type, <c>name</c>, and — only when set —
    ///   <c>thumbnail</c>{<c>asset_id</c>} and <c>favorite</c>{<c>toggled</c>}. It is a base-class method, which is
    ///   why A4 could not find it in <c>llviewerinventory.cpp</c>. Everything it sends is accepted;
    ///   <c>thumbnail</c> and <c>favorite</c> have no column in this tree and are dropped, as they are for a
    ///   PATCH.</item>
    /// </list>
    ///
    /// <para><b>AIS-SEC-4: a failure partway reports what it already created, and does not roll back.</b>
    /// Categories are added one at a time and then links one at a time, so a refusal on the third write leaves the
    /// first two in the database. Before this, the response carried only the error keys, so those objects were
    /// invisible to the client: it could neither adopt nor remove them, and its retry made duplicates. The failure
    /// path now returns the <b>same</b> delta envelope the success path builds — <c>_created_categories</c>,
    /// <c>_created_items</c>, <c>_embedded</c> and a re-read <c>_updated_category_versions</c> — so a client parses
    /// one shape either way, and the viewer genuinely adopts it: it applies every response body as an update, error
    /// or not (<c>onUpdateReceived</c>, <c>llaisapi.cpp:946</c>, spec §1f), firing its completion callback per
    /// created id (§1c).</para>
    ///
    /// <para><b>No rollback, deliberately</b>, and <see cref="AisCopy"/> is the precedent: a create is purely
    /// additive, so a partial one leaves what it made and risks nothing that existed before, whereas a rollback
    /// that itself failed would leave a worse and less describable state than the one it tried to repair — and it
    /// would have to delete objects a concurrent operation may already have touched. Reporting beats repairing
    /// here. A slam is the opposite case and does roll back (<see cref="AisSlam.Run"/>), because there the
    /// dangerous outcome is a folder left with fewer links than it started with.</para>
    ///
    /// <para>The status stays <b>500</b>. 207 would be a 2xx, and <c>llaisapi</c> would then treat the response as
    /// success and never log the failure at all (§1f's table: "any other failure (4xx/5xx, timeout) → warn with
    /// status and pretty-printed body"). 500 with a populated body keeps the failure visible and still hands the
    /// client what it needs.</para>
    /// </summary>
    private void CreateInventory(AisRoute route, OSDMap body, IOSHttpResponse response)
    {
        // AIS-SEC-3: key on route.Id - the addressed folder, and it is the only one written: AIS-SEC-2 refuses a body parent_id that disagrees (400).
        WithFolderLock(route.Id, route, response, () =>
        {
            var parent = m_backend.GetFolder(m_agentId, route.Id);
            if (parent is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

            // Refused before anything is written, so a mixed body does not half-succeed. See the remarks above:
            // the viewer's own items builder is compiled out and expects the server to create the asset.
            if (body["items"] is OSDArray requested && requested.Count > 0)
            {
                WriteError(response, HttpStatusCode.NotImplemented,
                    "creating inventory items through AIS is not implemented: the body carries a null asset_id for the server to fill, and this region does not create assets. The viewer's own path is disabled (USE_AIS_FOR_NC).", route);
                return;
            }

            // AIS-SEC-1. asAISCreateCatLLSD repeats the parent in the body (llinventory.cpp:1256-1276) and
            // link_inventory_array sends none at all (llviewerinventory.cpp:1352-1370), so a body parent that
            // disagrees with the URL is not something a viewer sends. The URL is the authority
            // (AISAPI::CreateInventory builds {inv}/category/{parentId}, llaisapi.cpp:115) and the disagreement is
            // refused rather than resolved: honouring the body would let a create addressed to a folder the caller
            // owns plant objects in a folder they do not. The backend's own parent check would refuse the write in
            // any case; this makes the refusal say why, and says it before anything is written, so a mixed body
            // cannot half-succeed.
            foreach (var key in new[] { "categories", "links" })
            {
                if (body[key] is not OSDArray entries) continue;
                foreach (var entry in entries)
                {
                    if (entry is not OSDMap m || !m.ContainsKey("parent_id")) continue;
                    var named = m["parent_id"].AsUUID();
                    if (named.IsZero() || named.Equals(route.Id)) continue;
                    WriteError(response, HttpStatusCode.BadRequest,
                        $"the body names parent_id {named} but the request addressed category {route.Id}; the URL is the authority", route);
                    return;
                }
            }

            var createdCategories = new OSDMap();
            var createdItems = new OSDMap();
            var createdLinks = new OSDMap();
            var categoryIds = new OSDArray();
            var itemIds = new OSDArray();

            // AIS-SEC-4. Built the same way whether this request succeeds or fails partway, so a client parses
            // one shape either way. See WriteErrorWithDeltas and the remarks on this method.
            OSDMap BuildEnvelope()
            {
                var env = new OSDMap();
                if (categoryIds.Count > 0) env[AisMutation.CreatedCategories] = categoryIds;
                if (itemIds.Count > 0) env[AisMutation.CreatedItems] = itemIds;
                if (createdCategories.Count > 0 || createdItems.Count > 0 || createdLinks.Count > 0)
                {
                    var emb = new OSDMap();
                    if (createdCategories.Count > 0) emb[AisEnvelope.Categories] = createdCategories;
                    if (createdItems.Count > 0) emb[AisEnvelope.Items] = createdItems;
                    if (createdLinks.Count > 0) emb[AisEnvelope.Links] = createdLinks;
                    env[AisEnvelope.Embedded] = emb;
                }
                // Re-read: the parent's version moved with every write that landed, and without it the viewer
                // skips the folder entirely (§1d-bis, llaisapi.cpp:1625-1629) - which on a partial failure would
                // leave it never re-reading the folder it half-filled.
                AisMutation.ReportVersion(env, m_backend.GetFolder(m_agentId, route.Id));
                return env;
            }

            // Reports the failure with everything created before it. No rollback; see the method remarks.
            void Fail(string what)
            {
                m_log.LogWarning(
                    "[AIS]: CreateInventory into folder {Folder} for agent {Agent} failed after creating "
                    + "{Categories} categories and {Items} items: {What}. The created objects are reported in the "
                    + "response so the client can reconcile; nothing is rolled back.",
                    route.Id, m_agentId, categoryIds.Count, itemIds.Count, what);
                WriteErrorWithDeltas(response, HttpStatusCode.InternalServerError, what, route, BuildEnvelope());
            }

            if (body["categories"] is OSDArray categories)
            {
                foreach (var entry in categories)
                {
                    if (entry is not OSDMap m) continue;
                    // asAISCreateCatLLSD sends parent_id alongside the URL parent; honour it when it names a real
                    // folder, and fall back to the folder the POST addressed, as an item create does.
                    var bodyParent = m["parent_id"].AsUUID();
                    var folder = new InventoryFolderBase(UUID.Random(), m["name"].AsString() ?? "", m_agentId,
                        (short)(m.ContainsKey("type_default") ? m["type_default"].AsInteger()
                            : m.ContainsKey("type") ? m["type"].AsInteger() : -1),
                        bodyParent.IsZero() ? route.Id : bodyParent, 1);
                    if (!m_backend.AddFolder(folder))
                    {
                        Fail($"could not create the category {folder.Name}");
                        return;
                    }
                    categoryIds.Add(OSD.FromUUID(folder.ID));
                    createdCategories[folder.ID.ToString()] = AisEnvelope.Category(folder, m_agentId,
                        AisEnvelope.EmbeddedMap(new OSDMap(), new OSDMap(), new OSDMap()));
                }
            }

            foreach (var (key, isLink) in new[] { ("links", true) })
            {
                if (body[key] is not OSDArray array) continue;
                foreach (var entry in array)
                {
                    if (entry is not OSDMap m) continue;
                    var row = NewItem(m, isLink, route.Id);
                    if (!m_backend.AddItem(row))
                    {
                        Fail($"could not create {(isLink ? "the link" : "the item")} {row.Name}");
                        return;
                    }
                    itemIds.Add(OSD.FromUUID(row.ID));
                    if (AisEnvelope.IsLink(row)) createdLinks[row.ID.ToString()] = AisEnvelope.Link(row, m_agentId);
                    else createdItems[row.ID.ToString()] = AisEnvelope.Item(row, m_agentId);
                }
            }

            Write(response, BuildEnvelope(), route);
        });
    }

    /// <summary>An item or link row from a create body. Unknown keys are ignored, as they are for a PATCH.</summary>
    private InventoryItemBase NewItem(OSDMap m, bool isLink, UUID parentId)
    {
        var assetType = m.ContainsKey("type") ? m["type"].AsInteger()
            : isLink ? (int)AssetType.Link : (int)AssetType.Unknown;
        var linked = m["linked_id"].AsUUID();
        return new InventoryItemBase(UUID.Random(), m_agentId)
        {
            // the body may name a parent; absent, the object goes in the folder the POST addressed
            Folder = m.ContainsKey("parent_id") && !m["parent_id"].AsUUID().IsZero()
                ? m["parent_id"].AsUUID() : parentId,
            Name = m["name"].AsString() ?? "",
            Description = m["desc"].AsString() ?? "",
            AssetID = linked.IsZero() ? m["asset_id"].AsUUID() : linked,
            AssetType = assetType,
            InvType = m.ContainsKey("inv_type") ? m["inv_type"].AsInteger() : 0,
            Flags = (uint)m["flags"].AsInteger(),
            CreatorId = m_agentId.ToString(),
            CreationDate = (int)Util.UnixTimeSinceEpoch(),
            BasePermissions = (uint)OpenSim.Framework.PermissionMask.All,
            CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
            NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        };
    }
    /// <summary>
    /// PUT /category/{id}/links — replace the folder's links (<see cref="AisSlam"/>, which documents the
    /// ordering and the exact guarantee). <c>current</c> resolves to the Current Outfit folder as it does for a
    /// fetch.
    ///
    /// <para>A slam is deliberately **not** subject to the protected-folder rule. That rule is the viewer's
    /// <c>lookupIsProtectedType</c>, which governs moving, deleting and retyping a folder
    /// (<c>llfoldertype.cpp:151-153</c>) — the Current Outfit folder is protected by it, and slamming the Current
    /// Outfit is the single most common thing the viewer does (<c>llappearancemgr.cpp:2251</c>).</para>
    ///
    /// <para>Only **links** are touched. Non-link items in the folder are left alone: the viewer builds the body
    /// from link rows only (<c>:1795-1833</c> switches on <c>AT_LINK</c> / <c>AT_LINK_FOLDER</c> and ignores
    /// everything else), so a slam has nothing to say about them.</para>
    ///
    /// <para>The envelope: the created links are named in <c>_created_items</c> and carried in
    /// <c>_embedded.links</c> — on a mutation the viewer accepts an embedded object only when its id is in
    /// <c>_created_items</c> (§1c) — the removed ones in <c>_removed_items</c>, and the folder's fresh version in
    /// <c>_updated_category_versions</c>. Each parsed link adds +1 to the folder's descendent count
    /// (<c>llaisapi.cpp:1310-1312</c>) and each removal −1 (<c>:1130</c>), so the arithmetic closes.</para>
    /// </summary>
    private void SlamFolder(AisRoute route, OSD rawBody, IOSHttpResponse response)
    {
        var folderId = route.Id;
        if (route.IsAlias)
        {
            var cof = AisInventory.GetCurrentOutfit(m_backend, m_agentId);
            if (cof is null) { WriteError(response, HttpStatusCode.NotFound, "the agent has no Current Outfit folder", route); return; }
            folderId = cof.ID;
        }

        // AIS-SEC-3: key on folderId - the whole snapshot -> create -> delete window, which is the AIS-SEC-3 defect itself.
        WithFolderLock(folderId, route, response, () =>
        {
            var contents = AisInventory.GetContents(m_backend, m_agentId, folderId);
            if (contents is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {folderId}", route); return; }

            var wanted = AisSlam.ParseBody(rawBody);
            if (wanted is null)
            {
                WriteError(response, HttpStatusCode.BadRequest,
                    "a slam body must be an LLSD array of link maps (name, desc, linked_id, type)", route);
                return;
            }

            var outcome = AisSlam.Run(m_backend, m_agentId, folderId, contents.Links, wanted);
            if (!outcome.Ok)
            {
                var detail = outcome.CompensationFailed
                    ? $"{outcome.Failure}; the rollback also failed and these links remain: {string.Join(", ", outcome.Leftover)}"
                    : outcome.Failure;
                WriteError(response, HttpStatusCode.InternalServerError, detail, route);
                return;
            }

            var envelope = new OSDMap();
            var createdIds = new OSDArray();
            var links = new OSDMap();
            foreach (var link in outcome.Created)
            {
                createdIds.Add(OSD.FromUUID(link.ID));
                links[link.ID.ToString()] = AisEnvelope.Link(link, m_agentId);
            }
            if (createdIds.Count > 0)
            {
                envelope[AisMutation.CreatedItems] = createdIds;
                envelope[AisEnvelope.Embedded] = new OSDMap { [AisEnvelope.Links] = links };
            }
            foreach (var removed in outcome.Removed) AisMutation.ReportRemoved(envelope, AisMutation.RemovedItems, removed);
            AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, folderId));
            Write(response, envelope, route);
        });
    }
    /// <summary>
    /// DELETE /category/{id}. Only the folder id goes in <c>_categories_removed</c>: the viewer purges the
    /// descendents itself (<c>LLInventoryModel::onObjectDeletedFromServer</c> calls
    /// <c>onDescendentsPurgedFromServer</c> for a category, <c>llinventorymodel.cpp:2019-2023</c>), so they are
    /// implied rather than enumerated.
    ///
    /// <para><b>Deletion is not restricted to Trash</b> (A2b, Ledger A-Q9 resolved). The call passes
    /// <c>onlyIfTrash: false</c> through the <c>IInventoryService</c> overload added in A2b, because the viewer
    /// deletes any non-protected folder wherever it sits (<c>llviewerinventory.cpp:1545-1568</c>, read in A2). The
    /// result is still verified by re-reading the folder rather than trusting the return value, since the service
    /// returns true even when it deleted nothing.</para>
    ///
    /// <para><b>Protected folders are refused</b> with 403. The viewer refuses to send RemoveCategory for a folder
    /// whose type <c>LLFolderType::lookupIsProtectedType</c> accepts (<c>llviewerinventory.cpp:1557-1561</c>), so
    /// this is defence in depth rather than a path the viewer exercises. That predicate's exact membership lives in
    /// <c>llfoldertype.cpp</c>, which is not a permitted read, so the server rule is stated in our own terms and
    /// marked UNVERIFIED against the viewer's list: a folder is protected when it is the agent's root or carries a
    /// system type, **except** <see cref="FolderType.Outfit"/>, which is an ordinary saved outfit that users delete
    /// routinely.</para>
    /// </summary>
    private void RemoveCategory(AisRoute route, IOSHttpResponse response)
    {
        var folder = m_backend.GetFolder(m_agentId, route.Id);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }
        var parentId = folder.ParentID;
        // AIS-SEC-3: key on parentId - the PARENT's child list is what changes, so the parent is the key - two deletes of siblings must order.
        WithFolderLock(parentId, route, response, () =>
        {

            if (IsProtected(folder))
            {
                WriteError(response, HttpStatusCode.Forbidden,
                    $"category {route.Id} is a protected system folder and cannot be deleted", route);
                return;
            }

            m_backend.DeleteFolders(m_agentId, new[] { route.Id }, onlyIfTrash: false);
            if (m_backend.GetFolder(m_agentId, route.Id) is not null)
            {
                WriteError(response, HttpStatusCode.InternalServerError,
                    $"the inventory service did not delete category {route.Id}", route);
                return;
            }

            var envelope = new OSDMap();
            AisMutation.ReportRemoved(envelope, AisMutation.CategoriesRemoved, route.Id);
            AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, parentId));
            Write(response, envelope, route);
        });
    }

    /// <summary>
    /// The folder types the viewer does **not** protect, taken from `LLFolderDictionary` itself
    /// (`indra/llinventory/llfoldertype.cpp:85-127`): every `addEntry` whose PROTECTED column is `false`. They are
    /// `FT_NONE`, the unused ensemble range `FT_ENSEMBLE_START`..`FT_ENSEMBLE_END`, `FT_OUTFIT`, and the three
    /// marketplace types. Everything else in the table is protected, and — importantly —
    /// `lookupIsProtectedType` **returns true for any type the table does not contain** (`:154-162`), which is why
    /// this is expressed as an allow-list with a protected default.
    /// </summary>
    private static readonly HashSet<short> UnprotectedFolderTypes = new()
    {
        (short)FolderType.None,
        (short)FolderType.Outfit,
        (short)FolderType.MarketplaceListings,
        (short)FolderType.MarkplaceStock,
        // FT_MARKETPLACE_VERSION (55) is unprotected in the viewer's table but this tree's FolderType has no
        // member for it, so it falls through to the protected default. It is a marketplace type no OpenSim grid
        // creates; see the session decisions.
    };

    /// <summary>The unused ensemble range, entered as unprotected in the viewer's table (`llfoldertype.cpp:106-109`).</summary>
    private const short EnsembleStart = 26;
    private const short EnsembleEnd = 45;

    /// <summary>
    /// A folder the server refuses to delete. This is the viewer's own rule: `LLFolderType::lookupIsProtectedType`
    /// looks the type up in `LLFolderDictionary` and returns that entry's PROTECTED flag, **defaulting to true for
    /// an unknown type** (`indra/llinventory/llfoldertype.cpp:154-162`). The viewer refuses to send RemoveCategory
    /// for such a folder (`llviewerinventory.cpp:1557-1561`), so this is defence in depth — but it also means a
    /// type this tree has and the viewer does not, such as `FolderType.Suitcase`, is protected automatically,
    /// which is the safe answer.
    ///
    /// <para>The agent's inventory root is refused as well. The viewer covers it by type
    /// (`FT_ROOT_INVENTORY` is protected), and this adds the structural case of a folder with no parent, which is
    /// either the root or an orphan and is not something a delete should walk into.</para>
    /// </summary>
    public static bool IsProtected(InventoryFolderBase folder)
    {
        if (folder is null) return true;
        if (folder.ParentID.IsZero()) return true;                      // the inventory root, or an orphan
        if (folder.Type >= EnsembleStart && folder.Type <= EnsembleEnd) return false;
        return !UnprotectedFolderTypes.Contains(folder.Type);           // unknown types are protected, as the viewer's are
    }
    // ------------------------------------------------------------------ wire

    /// <summary>
    /// 200 with an LLSD XML body. The viewer sends and reads LLSD XML and sets both <c>Content-Type</c> and
    /// <c>Accept</c> to <c>application/llsd+xml</c> on every AIS request (A-Q2, resolved A1:
    /// <c>llcorehttputil.cpp:1219-1222</c> <c>checkDefaultHeaders</c>; bodies serialised with
    /// <c>LLSDSerialize::toXML</c> at <c>:144</c>, <c>:169</c>, <c>:193</c> and parsed with
    /// <c>LLSDSerialize::fromXML</c> at <c>:123</c>).
    ///
    /// <para><c>tid</c> is echoed when the request carried one, so a client can correlate a response with the
    /// transaction it asked for. Nothing in the permitted viewer files reads it back, so it is inert there.</para>
    /// </summary>
    private static void Write(IOSHttpResponse response, OSDMap body, AisRoute route)
    {
        if (!route.Tid.IsZero()) body["tid"] = route.Tid;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/llsd+xml";
        response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(body);
        LogMutationResponse(route, (int)HttpStatusCode.OK, body);
    }

    /// <summary>
    /// A11: what a mutation actually answered — the status, and the delta keys with their contents. A10 could not
    /// tell a response the viewer rejected from a response that was never wrong, because nothing recorded what we
    /// sent back; this is that record. Fetches are skipped deliberately: their bodies are whole inventory
    /// listings and logging them would bury the mutations that matter.
    ///
    /// <para><b>Cost when DEBUG is off:</b> one enum switch and one <c>IsEnabled</c> call, both of which run
    /// before anything is built. <see cref="AisMutation.SummariseDeltas"/> is the only allocating work and it sits
    /// in the argument list of a call that is never reached unless both predicates pass — so a production log
    /// level pays two predicates and nothing else. No interpolated string is ever constructed: the message is a
    /// constant template and the values are passed as arguments.</para>
    /// </summary>
    private static void LogMutationResponse(AisRoute route, int status, OSDMap body)
    {
        if (!AisOperations.IsMutation(route.Operation)) return;
        if (!m_log.IsEnabled(LogLevel.Debug)) return;
        m_log.LogDebug("[AIS]: {Operation} -> {Status} {Deltas}",
            route.Operation, status, AisMutation.SummariseDeltas(body));
    }

    /// <summary>The error body: an LLSD map with conventional keys the viewer ignores (spec §1f).</summary>
    public static OSDMap ErrorBody(HttpStatusCode status, string message, AisRoute route)
    {
        return new OSDMap
        {
            ["error_code"] = (int)status,
            ["error_description"] = status.ToString(),
            ["message"] = message,
            ["operation"] = route.Operation.ToString(),
            ["verb"] = route.Verb,
            ["path"] = route.Path,
        };
    }

    /// <summary>
    /// AIS-SEC-4. An error response that also carries a delta envelope — the objects a mutation had already
    /// created when it failed.
    ///
    /// <para>This is not a courtesy. The viewer parses <b>every</b> response body as an update, success or failure
    /// (<c>onUpdateReceived</c>, <c>llaisapi.cpp:946</c>, spec §1f), and its completion callback fires per entry in
    /// <c>_created_categories</c> / <c>_created_items</c> (§1c) — so ids placed here are genuinely adopted, and a
    /// client that would otherwise retry and duplicate them does not have to.</para>
    ///
    /// <para>The error keys are layered <b>under</b> the envelope, so a delta key can never be shadowed by one of
    /// them, and the envelope's own keys are the same ones the success path emits. Nothing adds a top-level
    /// <c>item_id</c> or <c>category_id</c>, which §1f forbids in a body that does not mean them.</para>
    /// </summary>
    private static void WriteErrorWithDeltas(IOSHttpResponse response, HttpStatusCode status, string message,
        AisRoute route, OSDMap envelope)
    {
        var body = ErrorBody(status, message, route);
        foreach (KeyValuePair<string, OSD> kv in envelope) body[kv.Key] = kv.Value;

        response.StatusCode = (int)status;
        response.ContentType = "application/llsd+xml";
        response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(body);

        if (AisOperations.IsMutation(route.Operation) && m_log.IsEnabled(LogLevel.Debug))
            m_log.LogDebug("[AIS]: {Operation} -> {Status} {Message} {Deltas}",
                route.Operation, (int)status, message, AisMutation.SummariseDeltas(body));
    }

    private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string message, AisRoute route)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/llsd+xml";
        var body = ErrorBody(status, message, route);
        response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(body);

        // A failed mutation is the case A10 most needed and least had: the status and the reason, on the same
        // line shape as a success, so a log grep shows both.
        if (AisOperations.IsMutation(route.Operation) && m_log.IsEnabled(LogLevel.Debug))
            m_log.LogDebug("[AIS]: {Operation} -> {Status} {Message}", route.Operation, (int)status, message);
    }
}
