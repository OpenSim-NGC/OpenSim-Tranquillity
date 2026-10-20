/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;

using Microsoft.Extensions.Logging;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice;

// Removing a member from a call, and keeping them out.
//
// A removal has four effects, in this order. Order matters: the record comes first, so a provision that races the
// removal loses.
//   1. Record it in the call's session record (engine Moderate with ModerationOp.Remove), which frees the seat.
//   2. Drop the member's media: the participants this process joined are closed, and on the stock AudioBridge a
//      participant another region server joined is kicked.
//   3. Tell the member's viewer to close the call's session (ChatterBoxForceClose). A stock viewer that gets a refused
//      provision starts another at once and without end (llvoicewebrtc.cpp, LLVoiceWebRTCAdHocConnection after
//      VOICE_STATE_CLOSED) until its user closes the conversation, and closing it is what the forced close asks.
//   4. Refuse the way back: provision, "call", "accept invitation" and invite all read the record. The refusal of a
//      provision is cached per member and call, so the viewer's retries cost a dictionary read each.
// A removal lasts until the call ends, or for [WebRtcVoice] CallRemovalMinutes when that is set. The text of a group
// call is the group's, so a group member removed from the call keeps the group's text; a conference member is
// removed from the conference text with the seat.
public partial class WebRtcVoiceRegionModule
{
    /// <summary>The reason key sent to the viewer: it shows "You no longer have the ability to be in the chat session".</summary>
    public const string ForceCloseReasonKey = "close_on_no_ability";

    // Replays a removal refusal to the viewer's immediate retries, per (member, call).
    private readonly RefusalEpisodeLog m_removalLog = new RefusalEpisodeLog(m_log, logHeader + " [CALL REMOVAL]");
    private ProvisionRefusalCache m_removalRefusals => m_removalLog.Cache;

    /// <summary>What a removal or a readmission did.</summary>
    public readonly record struct CallRemovalResult(bool Ok, string Decision, bool Changed, int Dropped, string Note);

    private void InitialiseCallRemovalCache()
    {
        m_removalLog.SetWindow(TimeSpan.FromSeconds(
            Math.Max(0, m_Config.GetInt("RefusalCacheSeconds", ProvisionRefusalCache.DefaultSeconds))));
        m_removalLog.Start();
    }

    // The refusal cache is keyed by a UUID; a call's channel string is folded to one.
    internal static UUID RemovalCacheKey(string channel)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(channel ?? string.Empty));
        return new UUID(hash, 0);
    }

    private INonSpatialParticipantRemoval StockCallRemoval()
    {
        foreach (Scene s in CallScenes())
            if (s.RequestModuleInterface<IWebRtcVoiceService>() is INonSpatialParticipantRemoval removal)
                return removal;
        return null;
    }

    // Takes the member's participants out of the call's room and tells their viewer to close the call. Returns how many
    // participants were dropped here.
    private int DropMemberFromCallMedia(NonSpatialVoiceSession session, UUID target, string reasonKey)
    {
        int dropped = 0;
        try
        {
            INonSpatialParticipantRemoval removal = StockCallRemoval();
            if (removal is not null)
                dropped = removal.DropCallParticipants(target, CallRoomNumber(session));
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} dropping {Agent} from call {Session} failed", logHeader, target, session.SessionId);
        }

        List<Scene> scenes = CallScenes();
        Scene scene = A2AInviteDelivery.ResolveCalleeScene(scenes, target, out _);
        IEventQueue queue = scene?.RequestModuleInterface<IEventQueue>();
        try { queue?.ChatterBoxForceClose(target, session.SessionId, reasonKey); }
        catch (Exception e) { m_log.LogWarning(e, "{LogHeader} the forced close for {Agent} could not be queued", logHeader, target); }
        return dropped;
    }

    // The members still in the call are told the removed member left.
    private void TellCallMemberLeft(NonSpatialVoiceSession after, UUID target)
    {
        if (after is null) return;
        if (after.Type == NonSpatialSessionType.Adhoc)
        {
            SendConferenceRoster(after, target, joined: false);
            return;
        }
        List<Scene> scenes = CallScenes();
        List<GroupChatListAgentUpdateData> leave = A2AAgentListDelivery.LeaveUpdates(target);
        foreach (NonSpatialMember m in after.Members)
            if (m.HoldsSeat && m.AgentId != target)
                A2AAgentListDelivery.Deliver(scenes, m.AgentId, after.SessionId, leave, null);
    }

    /// <summary>
    /// Remove <paramref name="target"/> from the call <paramref name="sessionId"/> (a group's id, or a conference's).
    /// <paramref name="by"/> is who asked: a moderator through the viewer, or the operator at the console.
    /// </summary>
    public CallRemovalResult RemoveFromCall(UUID sessionId, UUID by, UUID target, string reason)
    {
        if (!m_callModeration.IsUsable || m_nonSpatial is null)
            return new CallRemovalResult(false, "call-moderation-off", false, 0, null);
        NonSpatialVoiceSession before = m_nonSpatial.Get(sessionId, UUID.Zero, out bool unavailable);
        if (before is null)
            return new CallRemovalResult(false, unavailable ? SessionOutcome.StoreUnavailable : SessionOutcome.NoSuchSession, false, 0, null);
        if (before.Type == NonSpatialSessionType.P2P)
            return new CallRemovalResult(false, "removal-refused-p2p", false, 0, null);

        int minutes = m_callModeration.Settings.RemovalMinutes;
        SessionOutcome recorded = m_nonSpatial.Moderate(sessionId, by, ModerationOp.Remove, target, true, reason,
                                                        minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);
        if (!recorded.Ok)
            return new CallRemovalResult(false, recorded.Decision, false, 0, null);
        bool changed = recorded.Decision == SessionOutcome.ModerationChanged;

        int dropped = 0;
        string note = null;
        if (changed)
        {
            dropped = DropMemberFromCallMedia(before, target, ForceCloseReasonKey);
            TellCallMemberLeft(recorded.Session ?? before, target);
            lock (m_callFlagsSent)
                m_callFlagsSent.Remove((before.SessionId, target));
            if (dropped == 0 && !m_stockPlugin && m_gridSessions is null)
                note = "remove-local-only: the member has no participant in this process; one that joined through another "
                       + "region server stays in the room until that server tears it down, which needs the grid session store";
            m_log.LogInformation("{LogHeader} [CALL REMOVAL] session={Session} by={By} target={Target} reason=\"{Reason}\" dropped={Dropped} lasts={Lasts}",
                logHeader, before.SessionId, by, target, reason ?? string.Empty, dropped,
                minutes > 0 ? minutes + " min" : "until the call ends");
        }
        return new CallRemovalResult(true, recorded.Decision, changed, dropped, note);
    }

    /// <summary>Lets the member back into the call. They are not seated again: they call or accept as anyone does.</summary>
    public CallRemovalResult ReadmitToCall(UUID sessionId, UUID by, UUID target)
    {
        if (!m_callModeration.IsUsable || m_nonSpatial is null)
            return new CallRemovalResult(false, "call-moderation-off", false, 0, null);
        NonSpatialVoiceSession before = m_nonSpatial.Get(sessionId, UUID.Zero, out bool unavailable);
        if (before is null)
            return new CallRemovalResult(false, unavailable ? SessionOutcome.StoreUnavailable : SessionOutcome.NoSuchSession, false, 0, null);
        SessionOutcome recorded = m_nonSpatial.Moderate(sessionId, by, ModerationOp.Remove, target, false);
        if (!recorded.Ok)
            return new CallRemovalResult(false, recorded.Decision, false, 0, null);
        m_removalLog.Ended(target, RemovalCacheKey(before.RoomKey), "the member was let back in");
        if (recorded.Decision == SessionOutcome.ModerationChanged)
            m_log.LogInformation("{LogHeader} [CALL REMOVAL] session={Session} by={By} target={Target} readmitted", logHeader, before.SessionId, by, target);
        return new CallRemovalResult(true, recorded.Decision, recorded.Decision == SessionOutcome.ModerationChanged, 0, null);
    }

    // A provision into a call was refused because the member was removed: the refusal is kept for the viewer's retries.
    private void NoteRemovalRefusal(UUID agentID, string channel, string decision, Scene scene)
    {
        UUID key = RemovalCacheKey(channel);
        int suppressed = m_removalRefusals.RecordRefusal(agentID, key, decision);
        m_removalLog.Refused(agentID, key, string.Empty, $" ({decision})", suppressed);
    }

    // The service reports that a seat this region holds belongs to a member who was removed from the call elsewhere.
    private void OnGridSeatRemoved(SeatKey seat)
    {
        NonSpatialVoiceSession session = m_nonSpatial?.Get(seat.Session);
        if (session is null)
            return;
        int dropped = DropMemberFromCallMedia(session, seat.Agent, ForceCloseReasonKey);
        m_log.LogInformation("{LogHeader} [CALL REMOVAL] session={Session} target={Target} removed elsewhere; dropped={Dropped} here",
            logHeader, seat.Session, seat.Agent, dropped);
    }

    // A group member who no longer holds the powers to be in the call (or is no longer in the group) is dropped from it.
    // This is not a removal: it leaves no record, and the member can come back when the powers do.
    // A pass over many members passes one memo's lookups so the groups service is asked once for each member.
    private bool DropIfCallPowerLost(NonSpatialVoiceSession session, UUID agent, GroupStandingMemo standing = null)
    {
        if (!m_groupVoice.IsUsable || session.Type != NonSpatialSessionType.Group)
            return false;
        Func<UUID, UUID, bool> isMember = standing is null ? m_groupVoice.IsMember : standing.IsMember;
        Func<UUID, UUID, ulong> powers = standing is null ? m_groupVoice.Powers : standing.Powers;
        if (isMember(agent, session.Owner) && (powers(agent, session.Owner) & m_groupVoice.RequiredMask) == m_groupVoice.RequiredMask)
            return false;
        m_nonSpatial.Depart(session.SessionId, agent, DepartureReason.PowerLost);
        int dropped = DropMemberFromCallMedia(session, agent, ForceCloseReasonKey);
        TellCallMemberLeft(m_nonSpatial.Get(session.SessionId), agent);
        m_log.LogInformation("{LogHeader} [CALL REMOVAL] session={Session} target={Target} no longer holds the powers for the call; dropped={Dropped}",
            logHeader, session.SessionId, agent, dropped);
        return true;
    }
}
