using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Nini.Config;
using OpenMetaverse.StructuredData;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSimNGC.Appearance.Baking;
using Microsoft.Extensions.Logging;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// Server-side baking (Design Brief §4.2 C2, ADR-001/002/004/005). S1 was the orchestrator plus a console
/// command; S2 added the ADR-004 index and the input-hash skip; S3 is the wire — <c>RegionProtocols</c> bit 0,
/// the <c>AppearanceData</c> block, the <c>UpdateAvatarAppearance</c> cap with the §4.3 handshake, and a
/// login-time bake. Config:
/// <code>
/// [Appearance]
///     ServerSideBaking = false   ; simulator-wide default for the wire flag
///     BakeSize = 1024            ; 512, 1024 or 2048
///     BakeQuality = 0.85
///
/// [&lt;Region Name&gt;]
///     ServerSideBaking = true    ; per-region override, same idiom as [AIS] AIS_Enabled
/// </code>
///
/// <para>
/// Everything the flag gates is add-only (ADR-001). On a region where it is off, the handshake carries the value
/// it always carried, no cap is advertised, no login bake runs, and <c>SendAppearance</c> emits the count-0
/// <c>AppearanceData</c> form — Firestorm keeps client-baking there exactly as before. The module still always
/// loads, so <c>appearance serverbake &lt;first&gt; &lt;last&gt;</c> exists on every region console.
/// </para>
/// </summary>
public class ServerSideBakingModule : ISharedRegionModule, IServerSideBaker
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(ServerSideBakingModule));

    public const string ConfigSection = "Appearance";

    private readonly List<Scene> m_scenes = new();
    private readonly Dictionary<Scene, ServerSideBakingRegion> m_regions = new();
    private IBakeBackend m_backend;
    private TexLayerCompositor m_compositor;

    /// <summary>The cap the LL viewer POSTs to after every COF change (viewer contract V3).</summary>
    public const string CapName = "UpdateAvatarAppearance";

    /// <summary>The simulator-wide default for the wire flag; a <c>[&lt;Region Name&gt;]</c> section overrides it.</summary>
    public bool ServerSideBakingEnabled { get; private set; }
    public int BakeSize { get; private set; } = 1024;
    public double BakeQuality { get; private set; } = 0.85;

    public string Name => "ServerSideBakingModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs[ConfigSection];
        if (config is not null)
        {
            ServerSideBakingEnabled = config.GetBoolean("ServerSideBaking", false);
            BakeSize = config.GetInt("BakeSize", 1024);
            BakeQuality = config.GetDouble("BakeQuality", 0.85);
        }
        if (BakeSize is not (512 or 1024 or 2048))
        {
            m_log.LogWarning("[SSB]: [Appearance] BakeSize {Size} is not 512, 1024 or 2048; using 1024", BakeSize);
            BakeSize = 1024;
        }
        BakeQuality = Math.Clamp(BakeQuality, 0.1, 1.0);
        m_log.LogInformation("[SSB]: ServerSideBaking={Flag} (wire flag; not acted on before S3), BakeSize={Size}, BakeQuality={Quality}",
            ServerSideBakingEnabled, BakeSize, BakeQuality);
    }

    public void PostInitialise() { }
    public void Close() { }

    public void AddRegion(Scene scene)
    {
        scene.RegisterModuleInterface<IServerSideBaker>(this);
    }

    public void RemoveRegion(Scene scene)
    {
        scene.UnregisterModuleInterface<IServerSideBaker>(this);
        ServerSideBakingRegion region;
        lock (m_scenes)
        {
            m_scenes.Remove(scene);
            m_regions.Remove(scene, out region);
        }
        if (region is null) return;
        scene.UnregisterModuleInterface<IServerSideBakingRegion>(region);
        scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
        scene.EventManager.OnRemovePresence -= region.Forget;
        scene.EventManager.OnAvatarAppearanceChange -= OnAvatarAppearanceChanged;
    }

    /// <summary>This region's state, or null on a region the module has not finished loading.</summary>
    public ServerSideBakingRegion RegionOf(Scene scene)
    {
        lock (m_scenes) return m_regions.TryGetValue(scene, out var r) ? r : null;
    }

    public void RegionLoaded(Scene scene)
    {
        // The per-region flag is resolved once, here, and everything wire-facing reads it off this object.
        var regionName = scene.RegionInfo?.RegionName;
        var enabled = ServerSideBakingRegion.ResolveEnabled(ServerSideBakingEnabled, scene.Config, regionName);
        var source = ServerSideBakingRegion.EnabledSource(scene.Config, regionName);

        // S12: one line per region, naming which config decided. This is what the flip verify reads - after the
        // two global lines go in and a region section comes out, every region must say "on (global)".
        m_log.LogInformation("[SSB]: region {Region}: server-side baking {State} ({Source})",
            scene.Name, enabled ? "ON" : "off", source);

        var region = new ServerSideBakingRegion(enabled, new CofHandshake());
        lock (m_scenes)
        {
            m_scenes.Add(scene);
            m_regions[scene] = region;
        }
        scene.RegisterModuleInterface<IServerSideBakingRegion>(region);

        if (enabled)
        {
            // V3: the viewer POSTs here after every COF change. V1: bit 0 of RegionProtocols is what makes it do
            // so, and LLClientView reads that off the same object.
            scene.EventManager.OnRegisterCaps += (agentID, caps) => RegisterCaps(scene, agentID, caps);
            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
            scene.EventManager.OnRemovePresence += region.Forget;
            scene.EventManager.OnAvatarAppearanceChange += OnAvatarAppearanceChanged;
            m_log.LogInformation(
                "[SSB]: region {Region} has server-side baking ON: RegionProtocols bit 0 set, {Cap} advertised, "
                + "login bake armed, appearances carry AppearanceData. Firestorm there will stop client-baking.",
                scene.Name, CapName);
        }
        else
        {
            m_log.LogInformation("[SSB]: region {Region} has server-side baking off; the wire is unchanged there", scene.Name);
        }

        scene.AddCommand(
            "Users", this, "appearance serverbake",
            "appearance serverbake <first-name> <last-name>",
            "Bake the avatar's current wearables on the server, store the bakes as assets, write them into the avatar's "
            + "texture entry and send the appearance. (Not 'appearance rebake', which asks the viewer to re-upload its own bakes.)",
            HandleServerBakeCommand);
    }

    /// <summary>The backend is created on first use so a region without any bake never loads the compositor's resources.</summary>
    private IBakeBackend Backend
    {
        get
        {
            if (m_backend is null)
            {
                lock (m_scenes)
                {
                    if (m_backend is null)
                    {
                        m_compositor = new TexLayerCompositor();
                        m_backend = new SkiaBakeBackend(m_compositor) { Quality = BakeQuality };
                    }
                }
            }
            return m_backend;
        }
    }

    // ------------------------------------------------------------------ IServerSideBaker

    public async Task<BakeOutcome> BakeAsync(ScenePresence sp, BakeReason reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sp);
        if (sp.IsChildAgent) throw new InvalidOperationException($"{sp.Name} is a child agent here");
        var scene = sp.Scene;
        var backend = Backend;

        var cofVersion = CofVersionOf(scene, sp.UUID);
        var region = RegionOf(scene);

        // S8, step 0: refuse a bake whose wearable set has lost a body part since the last one. Composing from a
        // set with no skin produces a valid-looking bake of nothing, and storing it supersedes - deletes - the
        // good bakes it replaces, so the damage is not recoverable by baking again. Observed 2026-09-05: four
        // unresolvable item ids emptied slots 1-4 and the CofChanged bake that followed stored 4 and superseded 4.
        var refusal = region?.RefusalForBodyPartLoss(sp.UUID, sp.Appearance.Wearables);
        if (refusal is not null)
        {
            m_log.LogWarning("[SSB]: bake for {Name} ({Agent}) reason={Reason} REFUSED: {Reason2}",
                sp.Name, sp.UUID, reason, refusal);
            return new BakeOutcome(sp.UUID, reason, Array.Empty<ChannelOutcome>(), 0);
        }

        // steps 2, 4-6, scene-free; the ADR-004 index in the avatar service is read for the reuse decision and
        // written back at the end of the run
        var outcome = await Task.Run(() => BakeOrchestrator.Run(sp.UUID, reason, sp.Appearance.Wearables, sp.Appearance.VisualParams, sp.Appearance,
            scene.AssetService, scene.AvatarService, backend, m_compositor, BakeSize, cofVersion, ct), ct).ConfigureAwait(false);

        // Record the bake before sending: SendAppearanceToAgentNF asks IServerSideBakingRegion for the version,
        // and an appearance sent before the record would go out without its AppearanceData block. RecordBake
        // ignores the call on a flag-off region, which is what keeps a console bake there off the wire.
        if (outcome.Count(ChannelStatus.Baked) + outcome.Count(ChannelStatus.Reused) > 0)
        {
            region?.RecordBake(sp.UUID, cofVersion);
            // The baseline for the next body-part check is what a bake actually succeeded from, never what one
            // was refused for: recording a refused set would let the second attempt through unchallenged.
            region?.RecordGoodBodyParts(sp.UUID, sp.Appearance.Wearables);
        }

        // step 7: send to everyone in view and to self. A reused channel is sent exactly like a fresh one — the
        // reason for the bake may be that nobody has seen it yet.
        //
        // No QueueAppearanceSave here, deliberately. A bake changes only the baked faces of the TextureEntry, and
        // the avatar service does not persist those at all: AvatarData(AvatarAppearance) carries the serial,
        // height, wearables, visual params and attachments and nothing else (IAvatarService.cs:142-189). What a
        // save would do is destroy this bake's index, because AvatarService.SetAvatar deletes every row for the
        // agent before rewriting those keys (AvatarService.cs:93). The bake index written above IS the
        // persistence of the baked faces.
        if (outcome.Count(ChannelStatus.Baked) + outcome.Count(ChannelStatus.Reused) > 0)
        {
            sp.SendAppearanceToAllOtherAgents();
            sp.SendAppearanceToAgent(sp);
        }

        // step 8: one INFO line per bake, carrying the phase split (Ledger Q-10); the fidelity evidence at DEBUG
        var t = outcome.Timings;
        m_log.LogInformation("[SSB]: bake for {Name} ({Agent}) reason={Reason}: {Summary} in {Ms} ms [{Split}, other={Other} ms]",
            sp.Name, sp.UUID, reason, Summarise(outcome), outcome.ElapsedMs, t.Summary,
            Math.Max(0, outcome.ElapsedMs - (long)t.Accounted.TotalMilliseconds));
        if (m_log.IsEnabled(LogLevel.Debug))
            foreach (var c in outcome.Channels)
            {
                if (c.Fidelity.Refusals.Count > 0) m_log.LogDebug("[SSB]: {Channel} refusals: {R}", c.Channel, string.Join("; ", c.Fidelity.Refusals));
                if (c.Fidelity.MissingTextures.Count > 0) m_log.LogDebug("[SSB]: {Channel} missing textures: {T}", c.Channel, string.Join(", ", c.Fidelity.MissingTextures));
                if (c.Fidelity.UnsupportedLayers.Count > 0) m_log.LogDebug("[SSB]: {Channel} unsupported layers: {L}", c.Channel, string.Join(" | ", c.Fidelity.UnsupportedLayers));
                foreach (var note in c.Fidelity.Notes) m_log.LogDebug("[SSB]: {Channel} layer {Note}", c.Channel, note);
            }
        return outcome;
    }

    private static string Summarise(BakeOutcome o)
        => string.Join(", ", o.Channels.Where(c => c.Status != ChannelStatus.Skipped).Select(c => $"{c.Channel}={c.Status}{(c.Status == ChannelStatus.Failed ? $"({c.Reason})" : "")}"))
           + $" [{o.Count(ChannelStatus.Skipped)} skipped]"
           + $" reused {o.Count(ChannelStatus.Reused)}/{o.Count(ChannelStatus.Baked) + o.Count(ChannelStatus.Reused)}"
           + (o.Superseded.Count > 0 ? $", superseded {o.Superseded.Count}" : "")
           + (o.IndexWritten ? "" : ", index NOT written");

    /// <summary>
    /// The Current Outfit folder's version, stored with the bake as <c>BakeCOFVersion</c> (ADR-006: the sim reads
    /// the COF folder's own <c>Version</c> and needs no AIS). Zero when there is no inventory service or no COF.
    /// </summary>
    private static int CofVersionOf(Scene scene, UUID agentId) => CofVersionOf(scene?.InventoryService, agentId);

    /// <summary>
    /// The same read, over the service alone, so the identity with AIS's number is testable. AIS reports the COF
    /// version from the very same field — <c>AisMutation.ReportVersion</c> writes <c>(int)folder.Version</c> of
    /// the <see cref="InventoryFolderBase"/> it gets back from the same <see cref="IInventoryService"/>, and
    /// <c>AisEnvelope.Category</c> does the same for <c>version</c>. So the <c>cof_version</c> the viewer sends
    /// back and the number read here are one quantity with one writer, the data layer's folder-version bump.
    /// </summary>
    public static int CofVersionOf(IInventoryService inventory, UUID agentId)
    {
        try
        {
            var cof = inventory?.GetFolderForType(agentId, FolderType.CurrentOutfit);
            return cof?.Version ?? 0;
        }
        catch (Exception ex)
        {
            m_log.LogDebug(ex, "[SSB]: could not read the COF version for {Agent}", agentId);
            return 0;
        }
    }

    // ------------------------------------------------------------------ S3: the cap and the login trigger

    /// <summary>
    /// Register <c>UpdateAvatarAppearance</c> for one agent. Only ever called on a flag-on region: advertising it
    /// where the flag is off would tell the viewer to expect server bakes that are not coming.
    /// </summary>
    private void RegisterCaps(Scene scene, UUID agentID, Caps caps)
    {
        string capPath = "/" + UUID.Random();
        caps.RegisterSimpleHandler(CapName,
            new SimpleStreamHandler(capPath, (httpRequest, httpResponse) => HandleUpdateAvatarAppearance(httpRequest, httpResponse, scene, agentID)));
        m_log.LogDebug("[SSB]: registered {Cap} at {Path} for agent {Agent} in {Region}", CapName, capPath, agentID, scene.Name);
    }

    /// <summary>
    /// The §4.3 handshake over HTTP. The viewer POSTs <c>{cof_version:N}</c> after every COF change (V3) and
    /// expects <c>{success, expected, error}</c>. The decision itself is <see cref="CofHandshake"/>, which knows
    /// nothing about HTTP; this method reads the body, reads the folder version fresh (ADR-006), and turns the
    /// verdict into a bake and a response.
    /// </summary>
    private void HandleUpdateAvatarAppearance(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, Scene scene, UUID agentID)
    {
        if (httpRequest.HttpMethod != "POST")
        {
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var region = RegionOf(scene);
        if (region is null || !region.ServerSideBakingEnabled)
        {
            WriteCapResult(httpResponse, false, -1, "server-side baking is not enabled on this region");
            return;
        }

        int clientVersion;
        try
        {
            var body = (OSDMap)OSDParser.DeserializeLLSDXml(httpRequest.InputStream);
            clientVersion = body is not null && body.TryGetValue("cof_version", out var v) ? v.AsInteger() : -1;
        }
        catch (Exception)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        ScenePresence sp = scene.GetScenePresence(agentID);
        if (sp is null || sp.IsChildAgent)
        {
            WriteCapResult(httpResponse, false, -1, "no root presence for this agent here");
            return;
        }

        // ADR-006: read the folder's Version fresh, never a cached copy. AIS mutates the same field, so the
        // number the viewer sends and the number read here are the same quantity (see AisMutation.ReportVersion).
        int serverVersion = CofVersionOf(scene, agentID);
        CofDecision decision = region.Handshake.Decide(agentID, clientVersion, serverVersion, () => CofVersionOf(scene, agentID), DateTime.UtcNow);

        if (decision.Verdict == CofVerdict.LivelockBake)
            m_log.LogWarning("[SSB]: anti-livelock for {Name} ({Agent}) in {Region}: {Reason}", sp.Name, agentID, scene.Name, decision.Reason);

        if (!decision.Success)
        {
            m_log.LogDebug("[SSB]: {Cap} for {Name}: stale — {Reason}", CapName, sp.Name, decision.Reason);
            WriteCapResult(httpResponse, false, decision.Version, null);
            return;
        }

        // Q-16: do NOT bake here. The POST is the viewer telling us its COF moved, and it arrives before the
        // region has resolved the new items to asset ids. The POST arrives ahead of the appearance save that
        // resolves them, and that save is up to DelayBeforeAppearanceSave (5 s) away — the only interval here
        // that is measured. Baking now would composite an outfit whose wearables still carry UUID.Zero asset ids
        // and store the result as if it were the new look.
        //
        // S10: but DO read the folder. On a bit-0 region this POST is the only notice the sim gets that the worn
        // SET changed - the LL viewer's AgentIsNowWearing is a four-item dummy with no callers
        // (llagentwearables.cpp:819-851), so nothing else turns a COF link into a wearable. Without this the save
        // below persists the wearables the sim already had and the bake reuses every channel, which is exactly
        // what happened on Ebony on 2026-09-06: two shirts linked in the COF, one shirt in the Avatars record.
        ApplyCofToWearables(scene, sp);

        // Instead the cap joins the same path the legacy route already takes: queue an appearance save, and let
        // the bake happen when that save completes (OnAvatarAppearanceChanged). Both signals therefore converge
        // on one trigger and one ordering. The queue is keyed by agent, so a POST arriving alongside an
        // AgentIsNowWearing costs nothing extra.
        scene.AvatarFactory?.QueueAppearanceSave(agentID);

        WriteCapResult(httpResponse, true, decision.Version, null);
    }

    /// <summary>
    /// S10. Read the agent's Current Outfit Folder and put every wearable link it holds into the presence's
    /// <see cref="AvatarAppearance.Wearables"/>, in the viewer's order. The rules are
    /// <see cref="CofWearables.Derive"/>'s; this is only the read.
    ///
    /// <para>
    /// A link is kept when its target resolves in this region and is a wearable. A link whose target cannot be
    /// read is dropped rather than guessed at, which is what makes the S8 rule hold: its type then looks
    /// untouched by this read and keeps what the agent already wears, instead of being emptied by an inventory
    /// lookup that failed.
    /// </para>
    /// </summary>
    private static void ApplyCofToWearables(Scene scene, ScenePresence sp)
    {
        var inventory = scene?.InventoryService;
        if (inventory is null || sp is null) return;

        List<CofWearableLink> links;
        try
        {
            var cof = inventory.GetFolderForType(sp.UUID, FolderType.CurrentOutfit);
            if (cof is null) return;
            var content = inventory.GetFolderContent(sp.UUID, cof.ID);
            if (content?.Items is null) return;

            links = new List<CofWearableLink>(content.Items.Count);
            var unresolved = 0;
            foreach (var link in content.Items)
            {
                if (link is null || link.AssetType != (int)AssetType.Link || link.AssetID.IsZero()) continue;
                var target = inventory.GetItem(sp.UUID, link.AssetID);
                if (target is null) { unresolved++; continue; }
                if (target.InvType != (int)InventoryType.Wearable) continue;   // an attachment or a gesture link
                var type = (int)(target.Flags & 0xff);
                if (type < 0 || type >= AvatarWearable.MAX_WEARABLES) { unresolved++; continue; }
                links.Add(new CofWearableLink(link.ID, link.Description, target.ID, type, target.AssetID));
            }
            if (unresolved > 0)
                m_log.LogWarning("[SSB]: {Count} COF link(s) for {Name} name an item this region cannot resolve as a wearable; their types keep what the agent already wears (S8)", unresolved, sp.Name);
        }
        catch (Exception ex)
        {
            m_log.LogWarning(ex, "[SSB]: could not read the COF for {Name}; the wearables are left as they are", sp.Name);
            return;
        }

        var derived = CofWearables.Derive(sp.Appearance?.Wearables, links, out var changed);
        if (!changed) return;

        sp.Appearance.Wearables = derived;
        m_log.LogDebug("[SSB]: {Name}'s worn set from the COF: {Summary}", sp.Name, DescribeWearables(derived));
    }

    /// <summary>"Shirt x2, Pants" — the worn types and how many of each, for the log.</summary>
    private static string DescribeWearables(AvatarWearable[] wearables)
    {
        var parts = new List<string>();
        for (var type = 0; type < wearables.Length; type++)
        {
            var count = wearables[type]?.Count ?? 0;
            if (count == 0) continue;
            parts.Add(count == 1 ? $"{(WearableType)type}" : $"{(WearableType)type} x{count}");
        }
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>The V3 response body: <c>success</c> always, <c>expected</c> when there is a version to quote, <c>error</c> when there is something to say.</summary>
    private static void WriteCapResult(IOSHttpResponse response, bool success, int expected, string error)
    {
        var map = new OSDMap { ["success"] = success };
        if (expected >= 0) map["expected"] = expected;
        if (!string.IsNullOrEmpty(error)) map["error"] = error;
        response.RawBuffer = Util.UTF8NBGetbytes(OSDParser.SerializeLLSDXmlString(map));
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    /// <summary>
    /// The change trigger (Design Brief §4.6, Ledger Q-16). Fires when the region has finished applying an
    /// appearance change <b>and</b> persisted it — <c>AvatarFactoryModule.SaveAppearance</c> raises it right after
    /// <c>SetAppearanceAssets</c> has resolved every worn item to its asset id and the avatar service has stored
    /// the result. Baking any earlier composites an outfit whose wearables are still <c>UUID.Zero</c>.
    ///
    /// <para>
    /// <b>Both signal paths reach here.</b> The legacy route arrives as <c>AgentIsNowWearing</c> and queues a save
    /// in <c>Client_OnAvatarNowWearing</c> (<c>AvatarFactoryModule.cs:1292</c>); the cap route now queues one too
    /// (see <see cref="HandleUpdateAvatarAppearance"/>). Attachment changes and the login/teleport cache check
    /// also queue saves, so this fires for those as well — deliberately. A spurious trigger costs one hash check
    /// per channel and re-sends the appearance; a missed one leaves the avatar wrong until relog, so the bias is
    /// towards triggering (S5 brief).
    /// </para>
    ///
    /// <para>Runs on the appearance-save thread pool thread. The bake goes to its own work item so a slow bake
    /// cannot hold up the rest of the save queue.</para>
    /// </summary>
    private void OnAvatarAppearanceChanged(ScenePresence sp)
    {
        if (sp is null || sp.IsChildAgent || sp.IsNPC) return;
        var scene = sp.Scene;
        var region = RegionOf(scene);
        if (region is not { ServerSideBakingEnabled: true }) return;

        if (!region.TryClaimChangeBake(sp.UUID, DateTime.UtcNow))
        {
            m_log.LogDebug("[SSB]: change bake for {Name} coalesced into the one just done", sp.Name);
            return;
        }

        Util.FireAndForget(_ =>
        {
            try
            {
                // BakeAsync sends the appearance itself when anything is live, reused included, so a change whose
                // hashes all match still reaches the viewer — which it must, because the viewer is waiting for an
                // AvatarAppearance it can accept and will not re-request one.
                BakeAsync(sp, BakeReason.CofChanged, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                m_log.LogError(ex, "[SSB]: change bake for {Name} threw", sp.Name);
            }
        }, null, "SSB change bake");
    }

    /// <summary>
    /// Login-time bake (Design Brief §4.2 step 1). Only armed on flag-on regions. It runs off the login thread:
    /// a bake is ~2.8 s cold and must never sit in the path that makes the agent root.
    ///
    /// <para>A warm agent — one whose stored bakes still match its wearables — costs about 70 ms and stores
    /// nothing, so this is cheap for everyone but a first login or an outfit change (Ledger Q-10).</para>
    /// </summary>
    private void OnMakeRootAgent(ScenePresence sp)
    {
        if (sp is null || sp.IsChildAgent || sp.IsNPC) return;
        var scene = sp.Scene;
        if (RegionOf(scene) is not { ServerSideBakingEnabled: true }) return;

        Util.FireAndForget(_ =>
        {
            try
            {
                BakeAsync(sp, BakeReason.Login, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                m_log.LogError(ex, "[SSB]: login bake for {Name} threw", sp.Name);
            }
        }, null, "SSB login bake");
    }

    // ------------------------------------------------------------------ console

    private void HandleServerBakeCommand(string module, string[] cmd)
    {
        if (cmd.Length != 4)
        {
            MainConsole.Instance.Output("Usage: appearance serverbake <first-name> <last-name>");
            return;
        }
        string firstname = cmd[2], lastname = cmd[3];
        List<Scene> scenes;
        lock (m_scenes) scenes = new List<Scene>(m_scenes);
        var found = false;
        foreach (var scene in scenes)
        {
            ScenePresence sp = scene.GetScenePresence(firstname, lastname);
            if (sp is null || sp.IsChildAgent) continue;
            found = true;
            BakeOutcome outcome;
            try { outcome = BakeAsync(sp, BakeReason.Console, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                MainConsole.Instance.Output("Server bake for {0} in {1} threw: {2}", sp.Name, scene.RegionInfo.RegionName, ex.Message);
                m_log.LogError(ex, "[SSB]: console bake for {Name} threw", sp.Name);
                continue;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Server bake for {sp.Name} in {scene.RegionInfo.RegionName}: {outcome.ElapsedMs} ms, size {BakeSize}");
            sb.AppendLine($"time split: {outcome.Timings.Summary}, other {Math.Max(0, outcome.ElapsedMs - (long)outcome.Timings.Accounted.TotalMilliseconds)}");
            sb.AppendLine($"{"channel",-8} {"face",4} {"status",-8} {"asset",-36} detail");
            foreach (var c in outcome.Channels)
            {
                var detail = c.Status switch
                {
                    ChannelStatus.Reused => $"hash {c.InputHash[..Math.Min(12, c.InputHash.Length)]}; inputs unchanged, not recomputed",
                    ChannelStatus.Baked => $"hash {c.InputHash[..Math.Min(12, c.InputHash.Length)]}"
                        + (c.Fidelity.MissingTextures.Count > 0 ? $"; missing textures {c.Fidelity.MissingTextures.Count}" : "")
                        + (c.Fidelity.UnsupportedLayers.Count > 0 ? $"; unsupported layers {c.Fidelity.UnsupportedLayers.Count}" : ""),
                    _ => c.Reason,
                };
                sb.AppendLine($"{c.Channel,-8} {BakeOrchestrator.FaceOf(c.Channel),4} {c.Status,-8} {(c.AssetId.IsZero() ? "-" : c.AssetId.ToString()),-36} {detail}");
            }
            sb.AppendLine($"reused {outcome.Count(ChannelStatus.Reused)} of {outcome.Count(ChannelStatus.Baked) + outcome.Count(ChannelStatus.Reused)} live channels; "
                + $"superseded {outcome.Superseded.Count} old asset(s); index {(outcome.IndexWritten ? "written" : "NOT written")}, COF version {CofVersionOf(scene, sp.UUID)}");
            foreach (var note in outcome.Notes) sb.AppendLine($"  note: {note}");
            MainConsole.Instance.Output(sb.ToString().TrimEnd());
        }
        if (!found) MainConsole.Instance.Output("No root agent named {0} {1} in any region here", firstname, lastname);
    }
}
