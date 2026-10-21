/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// The registration/teardown orchestration for one
/// connector record, extracted from VoiceConnectorModule so it is unit-testable with fakes at the
/// seams (the IsProvisionableChannelType extraction pattern). The delegates are the module's real
/// integrations: INPCModule.CreateNPC/DeleteNPC, IWebRtcVoiceService.CreateViewerSession, the
/// VoiceVisibilityService room record and moderation store. The registrar owns the ORDER and the
/// record's mutable slots; it holds no locks of its own (the module serialises calls per record).
/// </summary>
public static class VoiceConnectorRegistrar
{
    /// <summary>Create the NPC; UUID.Zero on failure (the INPCModule.CreateNPC contract).</summary>
    public delegate UUID CreateNpcDelegate(VoiceConnectorRecord pRecord);
    /// <summary>Delete the NPC (INPCModule.DeleteNPC).</summary>
    public delegate void DeleteNpcDelegate(UUID pNpcId);
    /// <summary>Create (NOT provision) a viewer session for the agent id — no JSEP exists for an
    /// NPC; the viewer session only carries identity and membership.</summary>
    public delegate IVoiceViewerSession CreateSessionDelegate(UUID pNpcId);
    /// <summary>Record the room the connector listens in (the OnListenerProvisioned equivalent).</summary>
    public delegate void RecordRoomDelegate(UUID pNpcId, int pRoom);
    /// <summary>Push the moderation mute for the NPC identity.</summary>
    public delegate void MuteDelegate(UUID pNpcId);

    /// <summary>
    /// Register one enabled record: NPC -> session (with a non-zero ClientSessionId, see the
    /// generation token note in the body) -> AddViewerSession (membership; IsAgentInRegion flips true) -> room record ->
    /// moderation mute iff MayInject=false. Returns false and leaves the record INACTIVE (slots
    /// empty, no session, no membership) when the NPC could not be created. Idempotent: an
    /// already-registered record (NpcId set) is a no-op true.
    /// </summary>
    public static bool Register(VoiceConnectorRecord pRecord, int pEstateRoom,
        CreateNpcDelegate pCreateNpc, CreateSessionDelegate pCreateSession,
        RecordRoomDelegate pRecordRoom, MuteDelegate pMute, ILogger pLog,
        VoiceConnectorDisclosure pDisclosure = null)
    {
        if (pRecord.NpcId != UUID.Zero)
            return true;   // already registered

        UUID npcId = pCreateNpc(pRecord);
        if (npcId == UUID.Zero)
        {
            pLog?.LogWarning("[CONNECTOR] {Name}: CreateNPC failed; record left inactive (no session, no membership)",
                pRecord.Name);
            return false;
        }
        pLog?.LogDebug("[CONNECTOR] {Name}: NPC created npc={NpcId}", pRecord.Name, npcId);

        IVoiceViewerSession session = pCreateSession(npcId);
        // The generation token: NPCAvatar.SessionId is UUID.Zero, so the
        // normal CaptureGenerationToken path would register this viewer session sweepable by ANY close
        // for the agent. A fresh UUID per NPC incarnation gives it the same close-selection
        // semantics a real login has.
        session.ClientSessionId = UUID.Random();
        VoiceViewerSession.AddViewerSession(session);
        pRecord.NpcId = npcId;
        pRecord.ViewerSessionId = session.ViewerSessionID;
        pLog?.LogDebug("[CONNECTOR] {Name}: voice session registered session={ViewerSessionId} (membership on)",
            pRecord.Name, session.ViewerSessionID);

        pRecordRoom(npcId, pEstateRoom);
        pRecord.Room = pEstateRoom;   // the room the join-capability endpoint binds to

        if (!pRecord.MayInject)
        {
            // Recording-only connectors are silenced at the source for
            // every listener via the existing moderation mute channel — the same call the
            // SpatialVoiceModerationRequest "mute" case makes (WebRtcVoiceRegionModule.cs,
            // svc.Moderation.MuteAgent). The rule reads the SOURCE's own parcel, and the NPC
            // never moves off its configured position, so the mute holds for its lifetime.
            pMute(npcId);
            pLog?.LogDebug("[CONNECTOR] {Name}: moderation mute pushed (MayInject=false)", pRecord.Name);
        }

        // Door notice: the attach alert fires LAST — only a fully
        // registered connector (session, room, mute all in place) is announced as attached.
        pDisclosure?.OnAttach(pRecord);
        return true;
    }

    /// <summary>
    /// Tear one record down: session first (membership off — the matrix stops emitting for the
    /// identity), then the NPC. Plain RemoveViewerSession is CORRECT here, unlike the logout
    /// case: this viewer session was created but never provisioned, so no Janus session, handle, or room
    /// membership exists to shut down — there is nothing to orphan. No moderation UNMUTE is
    /// pushed either: the mute entry keys on the (parcel, NPC id) pair. The NPC id is derived
    /// (ConnectorIdentity), so a later incarnation of the same record reuses it; the entry left behind is
    /// exactly the one its MayInject=false registration pushes again (MuteAgent is idempotent), and a
    /// region restart clears the in-memory store anyway.
    /// Idempotent: an inactive record is a no-op.
    /// </summary>
    public static void Unregister(VoiceConnectorRecord pRecord, DeleteNpcDelegate pDeleteNpc, ILogger pLog,
        VoiceConnectorDisclosure pDisclosure = null)
    {
        bool wasActive = pRecord.NpcId != UUID.Zero || pRecord.ViewerSessionId != null;
        if (pRecord.ViewerSessionId != null)
        {
            VoiceViewerSession.RemoveViewerSession(pRecord.ViewerSessionId);
            pLog?.LogDebug("[CONNECTOR] {Name}: voice session removed session={ViewerSessionId} (membership off)",
                pRecord.Name, pRecord.ViewerSessionId);
            pRecord.ViewerSessionId = null;
        }
        if (pRecord.NpcId != UUID.Zero)
        {
            pDeleteNpc(pRecord.NpcId);
            pLog?.LogDebug("[CONNECTOR] {Name}: NPC removed npc={NpcId}", pRecord.Name, pRecord.NpcId);
            pRecord.NpcId = UUID.Zero;
        }
        pRecord.Room = null;
        // Door notice: announced only if something was actually torn down
        // — an idempotent re-teardown of an inactive record stays silent.
        if (wasActive)
            pDisclosure?.OnDetach(pRecord);
    }
}
