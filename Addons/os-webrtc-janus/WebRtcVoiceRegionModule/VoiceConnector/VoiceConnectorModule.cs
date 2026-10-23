/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice;

/// <summary>
/// The voice-connector region module.
/// It loads the [VoiceConnector.&lt;name&gt;] policy records, logs the
/// outcome, and exposes the registry per scene via IVoiceConnectorRegistry. At the first
/// heartbeat after region load it creates each record's NPC, registers its voice session (identity +
/// membership only; the external peer joins the mixer itself), records the estate room, and pushes
/// the moderation mute for a recording-only record.
/// Non-shared: one instance per region; records carry an optional Region filter so an enabled
/// record does not spawn in every region of a multi-region instance.
/// </summary>
public class VoiceConnectorModule : INonSharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[CONNECTOR]";

    public const string DefaultNpcNameToken = "NPC";
    // Proximity notice range. The sim-side matrix has no audibility distance (the spatial
    // cull is mixer-side, SLV_CULL in the plugin), so the range is a [WebRtcVoice] key of its own:
    // VoiceRangeMetres, default 20 (the SL-conventional voice earshot).
    public const float DefaultVoiceRangeMetres = 20f;

    private VoiceConnectorRegistry m_registry;
    private string m_npcNameToken = DefaultNpcNameToken;
    private bool m_connectorsEnabled = false;
    private float m_voiceRangeMetres = DefaultVoiceRangeMetres;
    // The grid part of the derived connector identity - the normalised GatekeeperURI (empty if none).
    private string m_gridId = string.Empty;

    private Scene m_scene;
    private INPCModule m_npcModule;
    private IWebRtcVoiceService m_voiceService;
    private VoiceConnectorDisclosure m_disclosure;   // unconditional (no undisclosed mode exists)
    private int m_started = 0;   // one-shot latch for the first-heartbeat start
    // The connector join-capability endpoint. One handler on the region HTTP server for the whole
    // process (DefaultServer, as WorldMapModule registers its per-region handlers), shared by every region's instance.
    // Built only when some record carries a CapabilitySecret: with none, no handler exists.
    private static VoiceConnectorJoinCapEndpoint s_joinCapEndpoint;
    private VoiceConnectorJoinCapEndpoint.Source m_joinCapSource;
    private bool m_mintEnabled;
    private string m_mintSecret = string.Empty;

    // Console command support: one registration process-wide, handlers span the per-region
    // instances (the non-shared-module equivalent of VoiceModerationCommands' shared owner).
    private static readonly List<VoiceConnectorModule> s_instances = new List<VoiceConnectorModule>();
    private static bool s_commandsRegistered = false;
    private static readonly object s_commandLock = new object();

    public string Name => "VoiceConnectorModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource pConfig)
    {
        IConfig moduleConfig = pConfig?.Configs["WebRtcVoice"];
        if (moduleConfig is null)
            return;   // no voice config at all -> stay inert (no registry registered)

        // Connectors need the slvoice mixer: on the stock plugin the switch is reported and switched off here.
        MixerOnlySwitches.DisableOnStockPlugin(pConfig, m_log);

        // [WebRtcVoice] ConnectorsEnabled: NPC voice connectors (recorder and injector records). Off by default;
        // with it off the module reads no [VoiceConnector.*] record and registers nothing.
        m_connectorsEnabled = moduleConfig.GetBoolean("ConnectorsEnabled", false);
        if (!m_connectorsEnabled)
            return;

        m_npcNameToken = moduleConfig.GetString("NpcNameToken", DefaultNpcNameToken);
        m_voiceRangeMetres = moduleConfig.GetFloat("VoiceRangeMetres", DefaultVoiceRangeMetres);
        // The same grid identity the multiagent room numbers use (JanusAudioBridge.ReadGridId).
        m_gridId = JanusAudioBridge.ReadGridId(pConfig);

        VoiceConnectorLoadResult result = VoiceConnectorRegistry.LoadFrom(pConfig, m_npcNameToken);
        m_registry = result.Registry;

        foreach ((string sectionName, string reason) in result.Refusals)
            m_log.LogWarning("{LogHeader} record [{Section}] REFUSED: {Reason}", LogHeader, sectionName, reason);
        foreach ((string sectionName, string reason) in result.Warnings)
            m_log.LogWarning("{LogHeader} record [{Section}]: {Reason}", LogHeader, sectionName, reason);
        // The same two keys the Janus service mints avatar capabilities with (WebRtcJanusService.cs).
        m_mintEnabled = moduleConfig.GetBoolean("JoinCapabilityEnabled", false);
        m_mintSecret = pConfig.Configs["JanusWebRtcVoice"]?.GetString("JoinCapabilitySecret", string.Empty) ?? string.Empty;
        foreach (string sectionName in result.SkippedDisabled)
            m_log.LogDebug("{LogHeader} record [{Section}] disabled; skipped", LogHeader, sectionName);
        foreach (VoiceConnectorRecord r in m_registry.Snapshot())
            m_log.LogInformation("{LogHeader} loaded {Name} inject={MayInject} scope=estate",
                LogHeader, r.Name, r.MayInject);
    }

    public void AddRegion(Scene scene)
    {
        if (!m_connectorsEnabled)
            return;
        m_scene = scene;
        // The registry is registered even when empty: the AllowNpcVoice guard resolves it per
        // scene and an empty registry answers IsConnectorIdentity=false, exactly like a null one.
        if (m_registry is not null)
            scene.RegisterModuleInterface<IVoiceConnectorRegistry>(m_registry);
        lock (s_commandLock)
            s_instances.Add(this);
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_connectorsEnabled)
            return;
        scene.EventManager.OnRegionHeartbeatEnd -= OnHeartbeat;
        scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
        StopAll("region close");
        if (m_joinCapSource is not null)
        {
            lock (s_commandLock)
                s_joinCapEndpoint?.Detach(m_joinCapSource);
            m_joinCapSource = null;
        }
        if (m_registry is not null)
            scene.UnregisterModuleInterface<IVoiceConnectorRegistry>(m_registry);
        lock (s_commandLock)
            s_instances.Remove(this);
        m_scene = null;
    }

    public void RegionLoaded(Scene scene)
    {
        if (m_registry is null || m_registry.Count == 0)
            return;

        // INPCModule and IWebRtcVoiceService register their scene interfaces in AddRegion
        // (NPCModule.cs:81, WebRtcVoiceServiceModule.cs AddRegion), so both are resolvable by
        // RegionLoaded. Resolve here; DEFER the actual NPC creation to the first heartbeat
        // (one-shot on OnRegionHeartbeatEnd): at RegionLoaded the scene's heartbeat and physics
        // are not provably running yet, and CreateNPC drives a full circuit + CompleteMovement.
        // The heartbeat firing IS the proof the scene is live (the build plan's fallback shape).
        m_npcModule = scene.RequestModuleInterface<INPCModule>();
        m_voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
        if (m_npcModule is null)
        {
            m_log.LogWarning("{LogHeader} region {RegionName}: INPCModule not available; connectors inactive",
                LogHeader, scene.Name);
            return;
        }
        if (m_voiceService is null)
        {
            m_log.LogWarning("{LogHeader} region {RegionName}: IWebRtcVoiceService not available; connectors inactive",
                LogHeader, scene.Name);
            return;
        }
        // The three disclosure layers, wired to the real scene surfaces. All
        // UNCONDITIONAL (no undisclosed mode exists, so no config key gates these).
        m_disclosure = new VoiceConnectorDisclosure(
            // Attach/detach: the estate "message region" surface —
            // IDialogModule.SendNotificationToUsersInRegion (IDialogModule.cs:181; the same call
            // EstateManagementModule.cs:1613 makes for "message region").
            pRegionAlert: msg => scene.RequestModuleInterface<IDialogModule>()
                ?.SendNotificationToUsersInRegion(UUID.Zero, "Voice Connector", msg),
            // Entry notice: one per-agent line via IDialogModule.SendAlertToUser (IDialogModule.cs:60).
            pAgentNotice: (agentId, msg) => scene.RequestModuleInterface<IDialogModule>()
                ?.SendAlertToUser(agentId, msg),
            // Proximity notice: local chat spoken AS the NPC, delivered to the one agent
            // (IClientAPI.SendChatMessage, IClientAPI.cs:1095).
            pNpcChat: (record, agentId, msg) =>
            {
                if (scene.TryGetScenePresence(agentId, out ScenePresence sp))
                    sp.ControllingClient.SendChatMessage(msg, (byte)ChatTypeEnum.Say, record.Position,
                        record.NpcFullName, record.NpcId, record.NpcId,
                        (byte)ChatSourceType.Agent, (byte)ChatAudibleLevel.Fully);
            },
            pVoiceRangeMetres: m_voiceRangeMetres);

        // Entry notice trigger: the scene's root-transition event (EventManager.cs:698
        // OnMakeRootAgent, raised by TriggerOnMakeRootAgent at :2077 from
        // ScenePresence.MakeRootAgent) — fires for logins and for teleports/crossings into root.
        scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;

        // Heartbeat: PERSISTENT, not a one-shot — the first beat runs StartAll
        // (the scene is provably live), every beat runs the proximity check.
        scene.EventManager.OnRegionHeartbeatEnd += OnHeartbeat;
        RegisterConsoleCommands();
        AttachJoinCapEndpoint();
    }

    /// <summary>The room this record belongs in, as the provisioning path would resolve it for an
    /// avatar at the record's Position, ensured to exist. Called at registration and at every capability fetch. When
    /// the parcel's channel has changed since the record was made, the record MOVES, the way an avatar's re-provision
    /// moves it, with one INFO line. Returns null when there is no scene or the room could not be ensured.</summary>
    private int? ResolveAndEnsureRoom(VoiceConnectorRecord pRecord)
    {
        Scene scene = m_scene;
        if (scene is null)
            return null;
        LandData land = scene.LandChannel?.GetLandObject(pRecord.Position.X, pRecord.Position.Y)?.LandData;
        int room = ConnectorRoomResolver.RoomFor(scene.RegionInfo.RegionID, land);
        // The ensure always asks the mixer, so a non-null answer means the
        // room EXISTS - only then is the unknown_room backoff released for everyone recorded there. A failed ensure
        // proves nothing and resets nothing.
        int? ensured = ConnectorRoomResolver.EnsureAndProve(
            () => m_voiceService?.EnsureSpatialRoom(scene.RegionInfo.RegionID, FeederWorldFromScene.ParcelLocalIdFor(land)),
            r => scene.RequestModuleInterface<VoiceVisibilityService>()?.RoomExists(r));
        if (ensured is null)
            m_log.LogWarning("{LogHeader} {Name}: could not ensure mixer room {Room} exists; the peer may have nothing to join",
                LogHeader, pRecord.Name, room);
        if (pRecord.Room.HasValue && pRecord.Room.Value != room)
        {
            m_log.LogInformation("{LogHeader} {Name}: room moved {Old} -> {New} (the parcel at {Position} changed its voice " +
                "channel); re-recording it as a re-provision would", LogHeader, pRecord.Name, pRecord.Room.Value, room,
                pRecord.Position);
            pRecord.Room = room;
            if (pRecord.NpcId != UUID.Zero)
                scene.RequestModuleInterface<VoiceVisibilityService>()?.OnListenerProvisioned(pRecord.NpcId, room);
        }
        return room;
    }

    // Attach this region's records to the process-wide endpoint, only if one of them has a secret.
    private void AttachJoinCapEndpoint()
    {
        if (!m_registry.Snapshot().Any(r => r.CapabilitySecret is not null))
            return;   // the default: no CapabilitySecret anywhere in this region, no endpoint, nothing registered
        VoiceConnectorRegistry registry = m_registry;
        m_joinCapSource = new VoiceConnectorJoinCapEndpoint.Source(() => registry.Snapshot(),
            m_mintEnabled && !string.IsNullOrEmpty(m_mintSecret), m_mintSecret, ResolveAndEnsureRoom,
            // The region's global origin + the record's Position, at every fetch.
            rec => m_scene is null ? ((int X, int Y, int Z)?)null
                : FeederWorldFromScene.GlobalCentimetres(m_scene.RegionInfo.WorldLocX, m_scene.RegionInfo.WorldLocY, rec.Position));
        lock (s_commandLock)
        {
            s_joinCapEndpoint ??= new VoiceConnectorJoinCapEndpoint(MainServer.Instance.DefaultServer, m_log);
            s_joinCapEndpoint.Attach(m_joinCapSource);
        }
    }

    public void Close()
    {
        StopAll("module close");
    }

    private void OnHeartbeat(Scene scene)
    {
        if (!ReferenceEquals(scene, m_scene))
            return;
        if (Interlocked.Exchange(ref m_started, 1) == 0)
            StartAll();   // one-shot: the first beat proves the scene is live

        // Proximity notices (disclosure layer iii). Snapshot the voiced attached records first — the
        // common case (none, e.g. a recording-only connector) exits before touching presences.
        // Cost when armed: one presence walk + O(rootAgents × voiced connectors) squared-distance
        // compares per heartbeat; at this scale (a handful of agents, 1-2 connectors) that is a
        // few float compares per ~90 ms beat.
        List<VoiceConnectorRecord> voiced = null;
        foreach (VoiceConnectorRecord r in m_registry.Snapshot())
        {
            if (r.MayInject && r.NpcId != UUID.Zero)
                (voiced ??= new List<VoiceConnectorRecord>()).Add(r);
        }
        if (voiced is null)
            return;
        List<(UUID, UUID, Vector3)> roots = new List<(UUID, UUID, Vector3)>();
        scene.ForEachScenePresence(sp =>
        {
            if (!sp.IsChildAgent && sp.ControllingClient is not null)
                roots.Add((sp.UUID, sp.ControllingClient.SessionId, sp.AbsolutePosition));
        });
        m_disclosure?.ProximityTick(roots, voiced);
    }

    // Entry notice (disclosure layer ii): an agent becoming root while a connector is attached gets one line
    // naming the attached connector(s), once per login session.
    private void OnMakeRootAgent(ScenePresence sp)
    {
        if (sp is null || sp.ControllingClient is null || m_disclosure is null)
            return;
        List<VoiceConnectorRecord> attached = new List<VoiceConnectorRecord>();
        foreach (VoiceConnectorRecord r in m_registry.Snapshot())
        {
            if (r.NpcId != UUID.Zero && r.NpcId != sp.UUID)
                attached.Add(r);
        }
        m_disclosure.OnMakeRoot(sp.UUID, sp.ControllingClient.SessionId, attached);
    }

    // =====================================================================
    // Start/stop

    private void StartAll()
    {
        foreach (VoiceConnectorRecord record in m_registry.Snapshot())
            StartRecord(record);
    }

    private void StopAll(string pReason)
    {
        if (m_registry is null)
            return;
        foreach (VoiceConnectorRecord record in m_registry.Snapshot())
        {
            if (record.NpcId != UUID.Zero)
                m_log.LogDebug("{LogHeader} {Name}: teardown ({Reason})", LogHeader, record.Name, pReason);
            StopRecord(record);
        }
    }

    private void StartRecord(VoiceConnectorRecord record)
    {
        Scene scene = m_scene;
        if (scene is null || record.NpcId != UUID.Zero)
            return;
        if (record.Region is not null && !string.Equals(record.Region, scene.Name, StringComparison.OrdinalIgnoreCase))
            return;   // record pinned to another region of this instance

        // The per-region visibility service carries both the room record (OnListenerProvisioned)
        // and the moderation store. Registered per scene by WebRtcVoiceRegionModule.RegionLoaded.
        VoiceVisibilityService svc = scene.RequestModuleInterface<VoiceVisibilityService>();
        if (svc is null && !record.MayInject)
        {
            // Same refusal shape as the moderation CAP's "cannot enforce, not recorded": a
            // recording-only record NEEDS the mute channel; without the feeder nothing enforces
            // silence, so the connector is not brought up at all.
            m_log.LogWarning("{LogHeader} {Name}: visibility feeder disabled in region {RegionName}; MayInject=false cannot be enforced - connector NOT started",
                LogHeader, record.Name, scene.Name);
            return;
        }

        // The room an avatar at this Position would be provisioned into - the parcel's own
        // channel unless the parcel runs the estate channel - and then MAKE SURE IT EXISTS. A room is otherwise
        // created on one path only (a viewer provisioning voice), so without this a connector on an own-channel
        // parcel would be armed at the wrong room, and a connector in a region with nobody voiced would be armed at
        // a room that does not exist at all.
        int estateRoom = ResolveAndEnsureRoom(record) ?? JanusAudioBridge.CalcRoomNumber(
            string.Empty, scene.RegionInfo.RegionID.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);

        // The NPC's agent id is DERIVED (UUIDv5 of grid/region/record, ConnectorIdentity), not random, so
        // the peer's DISPLAY stays valid across restarts. Created through the fixed-id CreateNPC overload, which
        // refuses (UUID.Zero) if a presence with that id is still in the scene - the registrar then logs
        // "CreateNPC failed" and leaves the record inactive.
        UUID derivedId = ConnectorIdentity.DeriveAgentId(m_gridId, scene.RegionInfo.RegionName, record.Name);
        bool ok = VoiceConnectorRegistrar.Register(record, estateRoom,
            pCreateNpc: r => m_npcModule.CreateNPC(r.NpcFirstName, r.NpcLastName, r.Position, derivedId,
                scene.RegionInfo.EstateSettings.EstateOwner, string.Empty, UUID.Zero, false /* sense as NPC, not agent */,
                scene, new AvatarAppearance()),
            pCreateSession: npcId =>
            {
                IVoiceViewerSession vs = m_voiceService.CreateViewerSession(
                    new OSDMap { { "channel_type", OSD.FromString("local") } }, npcId, scene.RegionInfo.RegionID);
                return vs;
            },
            pRecordRoom: (npcId, room) => svc?.OnListenerProvisioned(npcId, room),
            pMute: npcId =>
            {
                // The same call the SpatialVoiceModerationRequest "mute" case makes
                // (WebRtcVoiceRegionModule.cs, svc.Moderation.MuteAgent(land.GlobalID, target)),
                // keyed on the parcel at the NPC's configured position (the rule reads the
                // SOURCE's own parcel, FeederWorldFromScene.voiceModerated).
                ILandObject parcel = scene.LandChannel?.GetLandObject(record.Position.X, record.Position.Y);
                if (parcel?.LandData is null)
                    m_log.LogWarning("{LogHeader} {Name}: no parcel at {Position}; moderation mute NOT pushed",
                        LogHeader, record.Name, record.Position);
                else
                    svc.Moderation.MuteAgent(parcel.LandData.GlobalID, npcId);
            },
            pLog: m_log,
            pDisclosure: m_disclosure);

        if (ok)
            // The operator copies this line into the peer's config (DISPLAY = npc, ROOM = room). npc= is
            // the derived id, the same on every restart; identity=derived says so (appended, so the fields before it
            // keep the format the connector READMEs document).
            m_log.LogInformation("{LogHeader} registered {Name} npc={NpcId} room={Room} inject={MayInject} session={ViewerSessionId} identity=derived",
                LogHeader, record.Name, record.NpcId, estateRoom, record.MayInject, record.ViewerSessionId);
    }

    private void StopRecord(VoiceConnectorRecord record)
    {
        Scene scene = m_scene;
        VoiceConnectorRegistrar.Unregister(record,
            npcId => { if (scene is not null) m_npcModule?.DeleteNPC(npcId, scene); },
            m_log, m_disclosure);
    }

    // =====================================================================
    // Console: "voice connector start|stop <name>" — testing without a region restart.

    private static void RegisterConsoleCommands()
    {
        lock (s_commandLock)
        {
            if (s_commandsRegistered || MainConsole.Instance is null)
                return;   // unit tests / embedded hosts have no console
            s_commandsRegistered = true;
            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice connector stop",
                "voice connector stop <name>",
                "Tear down a running voice connector (session, then NPC) without a restart",
                HandleConnectorCommand);
            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice connector start",
                "voice connector start <name>",
                "Start (or restart) a loaded voice connector record",
                HandleConnectorCommand);
        }
    }

    private static void HandleConnectorCommand(string module, string[] args)
    {
        // args: ["voice", "connector", "start"|"stop", "<name>"]
        if (args.Length < 4)
        {
            MainConsole.Instance.Output("Usage: voice connector start|stop <name>");
            return;
        }
        string op = args[2];
        string name = args[3];
        List<VoiceConnectorModule> instances;
        lock (s_commandLock)
            instances = new List<VoiceConnectorModule>(s_instances);
        bool found = false;
        foreach (VoiceConnectorModule inst in instances)
        {
            foreach (VoiceConnectorRecord record in inst.m_registry?.Snapshot() ?? new List<VoiceConnectorRecord>())
            {
                if (!string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                found = true;
                if (op == "stop")
                {
                    inst.StopRecord(record);
                    MainConsole.Instance.Output("connector {0} stopped in {1}", record.Name, inst.m_scene?.Name);
                }
                else
                {
                    inst.StartRecord(record);
                    MainConsole.Instance.Output(record.NpcId != UUID.Zero
                        ? string.Format("connector {0} running in {1} (npc {2})", record.Name, inst.m_scene?.Name, record.NpcId)
                        : string.Format("connector {0} NOT started in {1} (see log)", record.Name, inst.m_scene?.Name));
                }
            }
        }
        if (!found)
            MainConsole.Instance.Output("no loaded connector record named \"{0}\"", name);
    }
}
