/*
 * Copyright (c) 2026 Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Collections.Generic;
using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Caps = OpenSim.Framework.Capabilities.Caps;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenSim.Services.Interfaces;   // IPresenceService, for the online-only ring filter
using osWebRtcVoice.NonSpatial;   // the non-spatial engine and the group arm

namespace osWebRtcVoice;

// Per-listener voice visibility: the feeder service, the peer_ctl batch sink and its settings.
public partial class WebRtcVoiceRegionModule
{
    // [WebRtcVoice] VisibilityFeederEnabled / VisibilityEmitEnabled. Off by default: the feeder pushes
    // peer_ctl batches, which only a mixer that implements them understands.
    public const bool DefaultVisibilityFeederEnabled = false;

    public const bool DefaultVisibilityEmitEnabled = false;

    public static bool ReadVisibilityFeederEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityFeederEnabled", DefaultVisibilityFeederEnabled);

    public static bool ReadVisibilityEmitEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityEmitEnabled", DefaultVisibilityEmitEnabled);

    // [WebRtcVoice] VisibilityArmingEnabled. Arming, room epochs, generations, heartbeats and
    // acting on the mixer's reply. DISABLED by default: with the key
    // absent or false the sim emits exactly the payloads it sent before arming existed (VisibilityKnobOffGoldenTests).
    // Arming deliberately does not ride VisibilityEmitEnabled: that key is already true on existing
    // grids, so riding it would have changed the wire on deploy.
    public const bool DefaultVisibilityArmingEnabled = false;

    // [WebRtcVoice] VisibilityHeartbeatMs: milliseconds between the heartbeats an armed region sends to the mixer.
    // The default is the interval the code always used. A region server that runs many regions sends one heartbeat
    // per region per interval and can lengthen it, but the mixer treats a region that has been silent for its
    // staleness window (JS_VIS_STALE_MS in the mixer, 8000 ms by default) as gone, so the ceiling keeps two beats inside
    // that window and the floor is the feeder's tick. A value outside the range is refused and the default is used.
    public const int MinVisibilityHeartbeatMs = 250;

    public const int MaxVisibilityHeartbeatMs = 4000;

    public static int ReadVisibilityHeartbeatMs(IConfig pConfig, ILogger pLog = null)
    {
        int ms = pConfig.GetInt("VisibilityHeartbeatMs", VisAuthority.DefaultHeartbeatIntervalMs);
        if (ms >= MinVisibilityHeartbeatMs && ms <= MaxVisibilityHeartbeatMs)
            return ms;
        pLog?.LogWarning("[WebRtcVoice] VisibilityHeartbeatMs = {Value} is outside {Min} to {Max}; using {Default}",
            ms, MinVisibilityHeartbeatMs, MaxVisibilityHeartbeatMs, VisAuthority.DefaultHeartbeatIntervalMs);
        return VisAuthority.DefaultHeartbeatIntervalMs;
    }

    public static bool ReadVisibilityArmingEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityArmingEnabled", DefaultVisibilityArmingEnabled);

    // [WebRtcVoice] PositionFeedEnabled. The region tells the mixer where each voice participant really is, so a
    // viewer's own claim of its position can be compared with it. Off by default: with the key absent or false
    // nothing is computed or sent. Needs VisibilityFeederEnabled and VisibilityEmitEnabled, and a mixer that
    // advertises vis_protocol 3 (a mixer that does not is sent nothing).
    public const bool DefaultPositionFeedEnabled = false;

    // The feed's settings, or null when the feed is off. PositionFeedMode is "observe" (the default) or
    // "enforce"; only observe is active, enforce is accepted and runs as observe. pWarn receives a message for a value
    // that had to be replaced.
    public static PositionSettings ReadPositionSettings(IConfig pConfig, Action<string> pWarn = null)
    {
        if (!pConfig.GetBoolean("PositionFeedEnabled", DefaultPositionFeedEnabled))
            return null;

        string modeText = pConfig.GetString("PositionFeedMode", "observe");
        PositionFeedMode mode = PositionFeedModes.Parse(modeText, out bool recognised);
        if (!recognised)
            pWarn?.Invoke($"PositionFeedMode \"{modeText}\" is neither observe nor enforce; using observe");

        return new PositionSettings
        {
            Mode = mode,
            TickMs = pConfig.GetInt("PositionFeedTickMs", PositionSettings.DefaultTickMs),
            KeepAliveMs = ReadPositionKeepAliveMs(pConfig, pWarn),
            MoveThresholdCm = pConfig.GetInt("PositionFeedMoveThresholdCm", PositionSettings.DefaultMoveThresholdCm),
            MarginCm = pConfig.GetInt("PositionFeedMarginCm", PositionSettings.DefaultMarginCm),
            SpeedFloorCmS = pConfig.GetInt("PositionFeedSpeedFloorCmS", PositionSettings.DefaultSpeedFloorCmS),
            FallbackSpeedMaxCmS = pConfig.GetInt("PositionFeedSpeedMaxCmS", PositionSettings.DefaultFallbackSpeedMaxCmS),
        }.Normalised();
    }

    // [WebRtcVoice] PositionFeedKeepAliveMs: how often a full snapshot of a room is sent. The mixer drops a point it
    // has not heard of for 6000 ms, and the gap between two snapshots is the keep-alive plus up to 750 ms, so a value
    // above PositionSettings.MaxKeepAliveMs would let one lost request make every standing participant stale. Outside
    // 1 to that maximum, a warning and the default.
    private static int ReadPositionKeepAliveMs(IConfig pConfig, Action<string> pWarn)
    {
        int ms = pConfig.GetInt("PositionFeedKeepAliveMs", PositionSettings.DefaultKeepAliveMs);
        if (ms >= 1 && ms <= PositionSettings.MaxKeepAliveMs)
            return ms;
        pWarn?.Invoke($"PositionFeedKeepAliveMs = {ms} is outside 1 to {PositionSettings.MaxKeepAliveMs}, which would let a single lost " +
            $"request make the mixer treat a standing participant as gone; using {PositionSettings.DefaultKeepAliveMs}");
        return PositionSettings.DefaultKeepAliveMs;
    }

    // The feed's settings for this module, or null when it is off or cannot run.
    private PositionSettings m_PositionSettings;

    // Per-listener visibility feeder, one service per region. Off by default.
    private bool m_VisibilityFeederEnabled = DefaultVisibilityFeederEnabled;

    private int m_VisibilityTickMs = 250;

    private int m_VisibilityHeartbeatMs = VisAuthority.DefaultHeartbeatIntervalMs;

    // Emit the matrix to the mixer (peer_ctl_batch), separate from running the matrix. Off by default;
    // false runs the feeder matrix-only for diagnostics.
    private bool m_VisibilityEmitEnabled = DefaultVisibilityEmitEnabled;

    // Arming (see ReadVisibilityArmingEnabled). Off by default.
    private bool m_VisibilityArmingEnabled = DefaultVisibilityArmingEnabled;

    // [JanusWebRtcVoice] admin endpoint/secret for the peer_ctl_batch sink this module now OWNS
    // (option c-new): the sink is constructed here and handed directly to the feeder's sender, so
    // sink and sender share one ALC and IPeerCtlBatchSink identity matches.
    private string m_JanusAdminUri = string.Empty;

    private string m_JanusAdminToken = string.Empty;

    // [JanusWebRtcVoice] JanusGatewayCACertificateFile: private CA trusted for an https admin URL. Empty = system roots only.
    private string m_JanusCaCertificateFile = string.Empty;

    private int m_AdminTimeoutMs = 5000;

    private int m_VisibilityRoomSendConcurrency = JanusPeerCtlBatchSink.DefaultRoomSendConcurrency;

    private readonly Dictionary<Scene, VoiceVisibilityService> m_visibilityServices = new();

    // A copy of the per-region service map for the console commands. A copy, not the live
    // dictionary: a console handler resolves names, enumerates parcels and writes to a terminal,
    // and none of that may happen while holding the lock that the CAP handler and RegionLoaded
    // contend for.
    private List<KeyValuePair<Scene, VoiceVisibilityService>> SnapshotVisibilityServices()
    {
        lock (m_visibilityServices)
            return new List<KeyValuePair<Scene, VoiceVisibilityService>>(m_visibilityServices);
    }

    // Construct the Janus peer_ctl_batch sink for this scene, or null to run matrix-only.
    // Null when emission is disabled (no log — intentional) or when [JanusWebRtcVoice] admin
    // endpoint/secret is absent (one loud WARN — the config saved us before, keep it loud).
    private IPeerCtlBatchSink BuildPeerCtlSinkOrNull(Scene scene)
    {
        if (!m_VisibilityEmitEnabled)
            return null;

        if (string.IsNullOrEmpty(m_JanusAdminUri) || string.IsNullOrEmpty(m_JanusAdminToken))
        {
            m_log.LogWarning($"{logHeader}[Visibility]: VisibilityEmitEnabled but [JanusWebRtcVoice] " +
                $"JanusGatewayAdminURI/AdminAPIToken missing; region \"{scene.RegionInfo.RegionName}\" runs matrix-only (no emission)");
            return null;
        }

        // The sink logs its fallback room number once at Info, and the service hands it the room
        // resolver in the service constructor immediately below this call.
        return new JanusPeerCtlBatchSink(m_JanusAdminUri, m_JanusAdminToken,
            TimeSpan.FromMilliseconds(m_AdminTimeoutMs), scene.RegionInfo.RegionID, scene.RegionInfo.RegionName,
            m_VisibilityRoomSendConcurrency, sendOne: null, caCertificateFile: m_JanusCaCertificateFile);
    }

    // Start the per-listener visibility feeder for this region (opt-in).
    private void StartVisibilityService(Scene scene)
    {
        if (m_VisibilityFeederEnabled)
        {
            // Build the sink HERE and hand it directly to the service — same ALC, no scene
            // registry (option c-new). Null when emission is off or admin config is missing;
            // the service/sender then runs matrix-only and logs once.
            IPeerCtlBatchSink sink = BuildPeerCtlSinkOrNull(scene);
            VoiceVisibilityService svc = new VoiceVisibilityService(scene, m_VisibilityTickMs, m_VisibilityEmitEnabled, sink,
                TimeSpan.FromMilliseconds(m_AdminTimeoutMs), m_VisibilityArmingEnabled, m_VisibilityHeartbeatMs, m_PositionSettings);
            svc.Start();
            lock (m_visibilityServices)
                m_visibilityServices[scene] = svc;
            // Other modules in this assembly reach the per-region service (room record and
            // moderation store) through the scene. Concrete type, deliberately: no other assembly
            // needs it.
            scene.RegisterModuleInterface<VoiceVisibilityService>(svc);
        }
    }

    // Stops and forgets the visibility service of a removed region.
    private void StopVisibilityService(Scene scene)
    {
        lock (m_visibilityServices)
        {
            if (m_visibilityServices.TryGetValue(scene, out VoiceVisibilityService svc))
            {
                svc.Stop();
                m_visibilityServices.Remove(scene);
                scene.UnregisterModuleInterface<VoiceVisibilityService>(svc);
            }
        }
    }

    // Stops every visibility service when the module closes.
    private void StopAllVisibilityServices()
    {
        lock (m_visibilityServices)
        {
            foreach (VoiceVisibilityService svc in m_visibilityServices.Values)
                svc.Stop();
            m_visibilityServices.Clear();
        }
    }

    // A successful provision means this agent will join the mixer
    // room — hand it to the visibility sender's pending-join path so its full exclusion
    // column is (re)sent once it is present (the mixer silently drops a batch for a listener
    // not yet in the room). The replace is addressed at the room the provision joined, which is
    // recorded just before the agent is queued, whatever kind of channel that room belongs to. If the
    // agent never shows up in that room, the bounded re-send gives up loudly.
    // The success map also carries the mixer room the service actually joined.
    // Record it per agent so batches can be addressed per room. A failure or logout map has
    // no "room" -> null -> the service leaves any earlier record untouched.
    // ONLY a spatial ("local") provision is recorded. An A2A room is
    // not the agent's spatial room -- recording it would point the visibility batches at the
    // A2A room and the spatial exclusions would silently miss. The pending-join re-send is
    // therefore never armed for an A2A join either. A logout map has no room and is excluded
    // by the same gate (the service keeps its earlier record; the close/leave path clears it).
    private void RecordListenerRoom(OSDMap resp, UUID agentID, Scene scene)
    {
        int? provisionedRoom = resp.TryGetInt("room", out int provRoom) ? provRoom : (int?)null;
        VoiceVisibilityService svc;
        lock (m_visibilityServices)
            m_visibilityServices.TryGetValue(scene, out svc);
        svc?.OnListenerProvisioned(agentID, provisionedRoom);
    }

    // Reads the position feed settings and drops them, with a warning, when the feed cannot run: it is sent
    // through the peer_ctl sink, which exists only with the visibility feeder and emission both on.
    private void InitialisePositionFeed()
    {
        m_PositionSettings = ReadPositionSettings(m_Config, message => m_log.LogWarning("{LogHeader}[Positions]: {Message}", logHeader, message));
        if (m_PositionSettings is null)
            return;

        if (!m_VisibilityFeederEnabled || !m_VisibilityEmitEnabled)
        {
            m_log.LogWarning("{LogHeader}[Positions]: PositionFeedEnabled is true but VisibilityFeederEnabled and VisibilityEmitEnabled are not " +
                "both true; positions are sent through the same admin connection, so the position feed is inactive", logHeader);
            m_PositionSettings = null;
            return;
        }

        m_log.LogInformation("{LogHeader}[Positions]: position feed enabled, mode {Mode}", logHeader,
            m_PositionSettings.Mode.ToString().ToLowerInvariant());
        if (m_PositionSettings.Mode == PositionFeedMode.Enforce)
            m_log.LogInformation("{LogHeader}[Positions]: PositionFeedMode enforce is accepted but not active yet; " +
                "positions are sent in observe mode", logHeader);
    }

    // Reads the visibility feeder settings and registers its console commands.
    private void InitialiseVisibility(IConfigSource config)
    {
        m_VisibilityFeederEnabled = ReadVisibilityFeederEnabled(m_Config);
        m_VisibilityTickMs = m_Config.GetInt("VisibilityTickMs", 250);
        m_VisibilityHeartbeatMs = ReadVisibilityHeartbeatMs(m_Config, m_log);
        m_VisibilityEmitEnabled = ReadVisibilityEmitEnabled(m_Config);
        m_VisibilityArmingEnabled = ReadVisibilityArmingEnabled(m_Config);
        InitialisePositionFeed();

        // Rooms addressed concurrently within one send. A latency budget, not a
        // throughput knob � see JanusPeerCtlBatchSink.DefaultRoomSendConcurrency.
        m_VisibilityRoomSendConcurrency = m_Config.GetInt("VisibilityRoomSendConcurrency",
            JanusPeerCtlBatchSink.DefaultRoomSendConcurrency);

        // Sink endpoint from [JanusWebRtcVoice] (the same section the Janus service reads).
        IConfig janusCfg = config.Configs["JanusWebRtcVoice"];
        if (janusCfg is not null)
        {
            m_JanusAdminUri = janusCfg.GetString("JanusGatewayAdminURI", string.Empty);
            m_JanusAdminToken = janusCfg.GetString("AdminAPIToken", string.Empty);
            m_AdminTimeoutMs = janusCfg.GetInt("AdminTimeoutMs", 5000);
            m_JanusCaCertificateFile = janusCfg.GetString(JanusTls.CaCertificateFileKey, string.Empty);
        }

        // Console surfaces. The moderation commands are registered only with moderation enabled, and the
        // peer_ctl counters only with the visibility feeder enabled; with the feeder off the commands
        // would have nothing to report.
        if (m_VisibilityFeederEnabled)
            new VoiceVisibilityCommands(SnapshotVisibilityServices).Register();
    }
}
