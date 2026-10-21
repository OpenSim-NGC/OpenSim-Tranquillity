/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// The three mandatory
/// disclosure layers beyond the name marker — door notices (attach/detach region alert + entry
/// notice on becoming root) and the proximity notice from a voiced NPC. UNCONDITIONAL by design:
/// no config key disables any of them (no undisclosed mode exists). Delivery is behind
/// delegate seams so the logic and dedupe are unit-testable without a Scene
/// (VoiceConnectorDisclosureTests); VoiceConnectorModule wires the real scene surfaces.
/// Dedupe keys on the LOGIN SESSION id (ControllingClient.SessionId): a relog re-arms every
/// notice — disclosure repeats rather than lapses. Thread-safe: heartbeat, MakeRoot and console
/// paths all take the one lock.
/// </summary>
public sealed class VoiceConnectorDisclosure
{
    /// <summary>Region-wide notice to everyone (the estate "message region" surface).</summary>
    public delegate void RegionAlertDelegate(string pMessage);
    /// <summary>One notice line to one agent (the entry notice).</summary>
    public delegate void AgentNoticeDelegate(UUID pAgentId, string pMessage);
    /// <summary>One local chat line to one agent, spoken AS the record's NPC (the proximity notice).</summary>
    public delegate void NpcChatDelegate(VoiceConnectorRecord pRecord, UUID pAgentId, string pMessage);

    private readonly RegionAlertDelegate m_regionAlert;
    private readonly AgentNoticeDelegate m_agentNotice;
    private readonly NpcChatDelegate m_npcChat;
    private readonly float m_voiceRangeSq;

    private readonly object m_lock = new object();
    // Entry-notice dedupe: login sessions already told (SessionId is unique per login per agent,
    // and this object is per region instance — so the key IS agent+region+login).
    private readonly HashSet<UUID> m_entryNoticed = new HashSet<UUID>();
    // Proximity dedupe: (login session, NPC id) pairs already told. An NPC re-incarnation gets a
    // fresh UUID, so a stop/start naturally re-arms — disclosure repeats rather than lapses.
    private readonly HashSet<(UUID Session, UUID NpcId)> m_proximityNoticed = new HashSet<(UUID, UUID)>();

    public VoiceConnectorDisclosure(RegionAlertDelegate pRegionAlert, AgentNoticeDelegate pAgentNotice,
        NpcChatDelegate pNpcChat, float pVoiceRangeMetres)
    {
        m_regionAlert = pRegionAlert;
        m_agentNotice = pAgentNotice;
        m_npcChat = pNpcChat;
        m_voiceRangeSq = pVoiceRangeMetres * pVoiceRangeMetres;
    }

    // ---- Layer (ii), door notice 1: attach/detach region alert. Called by the registrar.

    public void OnAttach(VoiceConnectorRecord pRecord)
    {
        m_regionAlert(
            $"Voice connector {pRecord.NpcFullName} attached — an NPC (recording / automated voice) is present in this region's voice.");
    }

    public void OnDetach(VoiceConnectorRecord pRecord)
    {
        m_regionAlert(
            $"Voice connector {pRecord.NpcFullName} detached — its NPC has left this region's voice.");
    }

    // ---- Layer (ii), door notice 2: entry notice on becoming root while attached.

    /// <summary>One line to an agent becoming root, naming every attached connector and whether it
    /// is voiced. Once per login session; nothing when no connector is attached (and the session
    /// is NOT marked in that case, so a connector attaching later still gets announced via the
    /// attach alert — the two door notices cover each other's window).</summary>
    public void OnMakeRoot(UUID pAgentId, UUID pLoginSessionId, IReadOnlyList<VoiceConnectorRecord> pAttached)
    {
        if (pAttached is null || pAttached.Count == 0)
            return;
        lock (m_lock)
        {
            if (!m_entryNoticed.Add(pLoginSessionId))
                return;
        }
        List<string> parts = new List<string>(pAttached.Count);
        foreach (VoiceConnectorRecord r in pAttached)
            parts.Add($"{r.NpcFullName} ({(r.MayInject ? "voiced" : "recording")})");
        m_agentNotice(pAgentId,
            $"This region's voice has NPC connector(s) attached: {string.Join(", ", parts)}.");
    }

    // ---- Layer (iii): proximity notice from a voiced NPC.

    /// <summary>
    /// Called from the region heartbeat. For every attached MayInject connector, the first time a
    /// root agent's position comes within voice range of the NPC, one chat line spoken as the NPC.
    /// Cost: O(rootAgents × attached voiced connectors) squared-distance compares per call —
    /// no allocation on the quiet path beyond the caller's snapshots.
    /// </summary>
    public void ProximityTick(IReadOnlyList<(UUID AgentId, UUID LoginSessionId, Vector3 Position)> pRootAgents,
        IReadOnlyList<VoiceConnectorRecord> pAttachedVoiced)
    {
        if (pRootAgents is null || pAttachedVoiced is null || pRootAgents.Count == 0 || pAttachedVoiced.Count == 0)
            return;
        foreach (VoiceConnectorRecord record in pAttachedVoiced)
        {
            if (!record.MayInject || record.NpcId == UUID.Zero)
                continue;   // recording-only or not attached: no voice, no proximity notice
            foreach ((UUID agentId, UUID sessionId, Vector3 pos) in pRootAgents)
            {
                if (agentId == record.NpcId)
                    continue;   // the NPC's own presence never notices itself
                if (Vector3.DistanceSquared(pos, record.Position) > m_voiceRangeSq)
                    continue;
                lock (m_lock)
                {
                    if (!m_proximityNoticed.Add((sessionId, record.NpcId)))
                        continue;
                }
                m_npcChat(record, agentId,
                    $"{record.NpcFullName} is an NPC — its voice is automated or remotely operated.");
            }
        }
    }
}
