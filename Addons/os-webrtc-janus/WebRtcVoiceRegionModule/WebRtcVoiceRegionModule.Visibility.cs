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

    public static bool ReadVisibilityArmingEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityArmingEnabled", DefaultVisibilityArmingEnabled);

    // Per-listener visibility feeder, one service per region. Off by default.
    private bool m_VisibilityFeederEnabled = DefaultVisibilityFeederEnabled;

    private int m_VisibilityTickMs = 250;

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
                TimeSpan.FromMilliseconds(m_AdminTimeoutMs), m_VisibilityArmingEnabled);
            svc.Start();
            lock (m_visibilityServices)
                m_visibilityServices[scene] = svc;
            // The connector module (same assembly, separate module instance) reaches
            // the per-region service — room record + moderation store — through the scene, the
            // same way it reaches INPCModule. Concrete type, deliberately: no other assembly
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
    // not yet in the room). Estate-channel scoped; harmless for a per-parcel-channel agent
    // (its replace targets the estate room and the bounded re-send simply gives up loudly).
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

    // Reads the visibility feeder settings and registers its console commands.
    private void InitialiseVisibility(IConfigSource config)
    {
        m_VisibilityFeederEnabled = ReadVisibilityFeederEnabled(m_Config);
        m_VisibilityTickMs = m_Config.GetInt("VisibilityTickMs", 250);
        m_VisibilityEmitEnabled = ReadVisibilityEmitEnabled(m_Config);
        m_VisibilityArmingEnabled = ReadVisibilityArmingEnabled(m_Config);

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
