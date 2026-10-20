/*
 * Copyright (c) Contributors, http://opensimulator.org/
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
// Adapted 2026 by Legion Builds

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

// Group voice: opt-in voice sessions for a group, started with a call from a member.
public partial class WebRtcVoiceRegionModule
{
    // [WebRtcVoice] GroupVoiceEnabled. OFF by default, so an install that never sets
    // it behaves exactly as it does today -- a group "call" falls through to the A2A arm and 404s,
    // and a group provision cannot exist because no room key was ever handed out. Opt-in only.
    public const bool DefaultGroupVoiceEnabled = false;

    public static bool ReadGroupVoiceEnabled(IConfig pConfig)
        => pConfig.GetBoolean("GroupVoiceEnabled", DefaultGroupVoiceEnabled);

    // [WebRtcVoice] GroupVoiceRequireVoicePower. The viewer separates GP_SESSION_JOIN (may be in the
    // session, roles_constants.h:144) from GP_SESSION_VOICE (may hear/talk, :145); we require both by
    // default so a role explicitly denied voice is not admitted. False requires JOIN only.
    public const bool DefaultGroupVoiceRequireVoicePower = true;

    public static bool ReadGroupVoiceRequireVoicePower(IConfig pConfig)
        => pConfig.GetBoolean("GroupVoiceRequireVoicePower", DefaultGroupVoiceRequireVoicePower);

    // [WebRtcVoice] GroupVoiceCap. Clamped by NonSpatialCaps.Effective to the mixer's SLV_MAX_MIX.
    public static int ReadGroupVoiceCap(IConfig pConfig)
        => pConfig.GetInt("GroupVoiceCap", NonSpatialCaps.DefaultConferenceCap);

    private GroupVoicePolicy m_groupVoice = GroupVoicePolicy.Disabled;

    /// <summary>
    /// Ring the members this process CANNOT reach -- those on another region server.
    ///
    /// The roster comes from the groups service through the INITIATOR's own client, which is present
    /// here by construction (it just started the call). IGroupsModule.GroupMembersRequest
    /// dereferences its IClientAPI, so a null is not an option and the
    /// initiator's client is the correct requester anyway.
    ///
    /// Anyone already rung locally is excluded, so a member never gets two popups: the local walk and
    /// this list are disjoint by construction, and both are marked invited in the same cycle.
    /// </summary>
    private void RingRemoteGroupMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName,
                                        OSDMap body, List<UUID> alreadyRungLocally, List<GroupMembersData> roster = null)
    {
        if (session is null || !m_groupVoice.IsUsable || body is null)
            return;
        if (!m_carrierAuth.Enabled && !RingsAreVerifiedByTheStore)
        {
            LogCarrierOff(session.Owner);
            return;
        }

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        IMessageTransferModule transfer = null;
        Scene originScene = null;
        foreach (Scene sc in scenes)
        {
            transfer ??= sc.RequestModuleInterface<IMessageTransferModule>();
            if (sc.GetScenePresence(initiator) is not null) originScene = sc;
        }
        if (transfer is null)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(UUID.Zero, session.Owner, GroupVoiceRingTransport.DecisionNoTransfer));
            return;
        }

        // The roster the local walk already fetched, or one fetched now.
        roster ??= FetchGroupRoster(scenes, initiator, session.Owner);
        if (roster is null) return;

        HashSet<UUID> local = new HashSet<UUID>(alreadyRungLocally ?? new List<UUID>());
        // Root presences only: a member who is just a child presence here is rung through the carrier,
        // so the process that holds their root presence records the call before their accept arrives.
        HashSet<UUID> presentHere = new HashSet<UUID>(GroupVoiceInvite.RootAgents(scenes));
        List<UUID> remote = new List<UUID>();
        foreach (GroupMembersData m in roster)
        {
            if (m.AgentID == UUID.Zero || m.AgentID == initiator) continue;
            if (local.Contains(m.AgentID) || presentHere.Contains(m.AgentID)) continue;   // the local walk has them
            if (session.Find(m.AgentID)?.HoldsSeat == true) continue;
            if (session.WasInvited(m.AgentID)) continue;
            if ((m.AgentPowers & m_groupVoice.RequiredMask) != m_groupVoice.RequiredMask) continue;
            remote.Add(m.AgentID);
        }
        if (remote.Count == 0)
            return;

        // (a) Only members the PRESENCE SERVICE reports online somewhere. An offline member cannot
        // answer a ring, and a ring delivered at their next login announces a call that ended hours
        // ago. The offline modules would not store this dialog anyway (see below), but not sending
        // is the correct behaviour rather than relying on the receiver to discard it.
        IPresenceService presence = null;
        foreach (Scene sc in scenes)
        {
            presence = sc.PresenceService;
            if (presence is not null) break;
        }
        int before = remote.Count;
        remote = OnlineMembers(presence, remote);
        if (remote.Count != before)
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(UUID.Zero, session.Owner, GroupVoiceInvite.DecisionOffline,
                    $"{before - remote.Count} of {before} candidate(s) are offline"));
        if (remote.Count == 0)
            return;

        UUID originRegion = originScene?.RegionInfo?.RegionID ?? UUID.Zero;
        // Marked before the rings go out, because a region that keeps its sessions in the grid service asks the
        // service whether a ring it received was one the sender sent, and the answer is yes only for a target
        // already marked. Marked whatever delivery says, as before.
        m_nonSpatial?.MarkInvited(session.SessionId, remote);
        foreach (UUID target in remote)
        {
            try
            {
                GridInstantMessage im = GroupVoiceRingTransport.Build(target, initiator, initiatorName,
                                                                      session.Owner, originRegion, body, m_carrierAuth);
                // Root only: if the member is a child presence in this process the transfer module would hand the
                // carrier to that viewer as an instant message; it is meant for the process where they are root.
                transfer.SendInstantMessage(im, _ => { }, true);
                m_log.LogDebug("{LogHeader} {Line}", logHeader,
                    GroupVoiceRingTransport.Line(target, session.Owner, GroupVoiceRingTransport.DecisionSent));
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} cross-instance ring to {Target} failed", logHeader, target);
            }
        }
    }

    /// <summary>
    /// A ring arrived from another region server. Adopt the voice session locally so the accept
    /// that follows can be admitted here, then deliver the invitation exactly as a local ring would
    /// -- same body, same A2AInviteDelivery, so the viewer cannot tell the two paths apart.
    ///
    /// This instance never fans out from an adopted session: AdoptRemoteRing latches RingSent.
    /// </summary>
    private void OnIncomingGroupRing(GridInstantMessage msg)
    {
        if (m_nonSpatial is null || (!m_groupVoice.IsUsable && !m_adhocVoiceEnabled))
            return;
        RingIntake ring = CarrierIntake.AcceptRing(msg, m_carrierAuth, m_nonSpatial, m_groupVoice.IsUsable,
                                                   m_groupVoice.Cap, m_adhocVoiceEnabled);
        if (!ring.Claimed)
            return;   // not ours: every other subscriber still sees it untouched
        if (ring.Authenticated)
            LogCarrierAccepted(CarrierAuth.KindRing, ring.GroupId, ring.Type, ring.OriginRegion);
        if (ring.Reason is not null)
        {
            LogCarrierRefusal(CarrierAuth.KindRing, ring.Reason, ring.RefusalCount);
            return;
        }
        if (!ring.Ok)
        {
            if (ring.Decision != CarrierIntake.DecisionFeatureOff)   // that half is switched off on this instance
                WarnRefusal("ring-" + ring.Decision, UUID.Zero, () => logHeader + " " +
                    GroupVoiceRingTransport.Line(ring.Target, ring.GroupId, ring.Decision,
                        "carried room key is missing or does not match this instance's derivation - check GatekeeperURI on both"));
            return;
        }

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        string decision = A2AInviteDelivery.Deliver(scenes, ring.Target, ring.Body, null, out string region);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            GroupVoiceRingTransport.Line(ring.Target, ring.GroupId, decision,
                "region=\"" + region + "\" adopted=" + ring.Decision));
    }

    /// <summary>
    /// The group's name for the callee's incoming-call UI. Best effort: the groups service knows it,
    /// and a blank name is survivable (GroupVoiceInvite.BuildBody substitutes a default) whereas an
    /// exception here would lose the ring.
    /// </summary>
    private string GroupNameOf(UUID groupID)
    {
        try
        {
            return GroupsModule()?.GetGroupRecord(groupID)?.GroupName ?? string.Empty;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group name lookup failed for {GroupId}", logHeader, groupID);
            return string.Empty;
        }
    }

    /// <summary>
    /// Ring every eligible member in THIS process when a group call begins.
    ///
    /// Fired only on the engine's 0 -> 1 seat transition, which the engine claims inside its own
    /// mutation, so two simultaneous first-joiners cannot both ring. Targets are agents present in
    /// any scene this module serves, holding both powers, excluding the initiator and anyone already
    /// seated or already rung this cycle. Delivery reuses A2AInviteDelivery, which already walks
    /// every scene in the process and prefers a root presence -- that is what makes the ring reach
    /// all three regions and not just the initiator's.
    ///
    /// Cross-HOST delivery is separate: a member on another region server has no presence here and is
    /// simply not rung, exactly as the A2A path behaves today.
    /// </summary>
    private void RingGroupMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName, string groupName)
    {
        if (session is null || m_nonSpatial is null || !m_groupVoice.IsUsable)
            return;

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        // One roster serves both halves of the ring: who here is a member with the powers, and who is a member elsewhere.
        // Without one (the initiator has no client here, or the groups service did not answer) the groups service is asked
        // about each avatar here instead.
        List<GroupMembersData> roster = FetchGroupRoster(scenes, initiator, session.Owner);
        Dictionary<UUID, ulong> held = null;
        if (roster is not null)
        {
            held = new Dictionary<UUID, ulong>();
            foreach (GroupMembersData m in roster)
                held[m.AgentID] = m.AgentPowers;
        }
        List<UUID> targets = GroupVoiceInvite.Targets(GroupVoiceInvite.RootAgents(scenes), session,
                                                      initiator, m_groupVoice, held);
        bool noLocalTargets = targets.Count == 0;
        if (noLocalTargets)
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceInvite.Line(UUID.Zero, session.Owner, "-", "no-local-targets"));

        string token = m_nonSpatial.IssueToken(session.SessionId, initiator);
        if (string.IsNullOrEmpty(token))
        {
            m_log.LogWarning("{LogHeader} group ring for {GroupId} has no token; not ringing", logHeader, session.Owner);
            return;
        }

        OSDMap body = GroupVoiceInvite.BuildBody(session, token, initiator, initiatorName, groupName);
        foreach (UUID target in targets)
        {
            string decision = A2AInviteDelivery.Deliver(scenes, target, body, null, out string region);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceInvite.Line(target, session.Owner, region, decision));
        }
        // Marked whatever the delivery said: a member we could not reach must not be re-rung on
        // every later join. One ring per member per cycle; the cycle resets when the room empties.
        m_nonSpatial.MarkInvited(session.SessionId, targets);

        // Everyone the local walk could not reach -- i.e. on another region server.
        RingRemoteGroupMembers(session, initiator, initiatorName, body, targets, roster);
    }

    // The group's roster, through the initiator's client; null when there is no client here or the groups service did not
    // answer.
    private List<GroupMembersData> FetchGroupRoster(List<Scene> scenes, UUID initiator, UUID groupId)
    {
        try
        {
            IClientAPI client = null;
            foreach (Scene sc in scenes)
                if (sc.GetScenePresence(initiator) is ScenePresence sp)
                    client = sp.ControllingClient;
            return client is null ? null : GroupsModule()?.GroupMembersRequest(client, groupId);
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group roster lookup failed for {GroupId}; no cross-instance ring",
                logHeader, groupId);
            return null;
        }
    }

    // Of the candidates, those the presence service reports online, from one request for the whole list. No presence
    // service means nobody is reported online, so no ring goes out.
    private static List<UUID> OnlineMembers(IPresenceService presence, IReadOnlyList<UUID> candidates)
        => GroupVoiceInvite.OnlineOnlyBatch(candidates, ids =>
        {
            Dictionary<UUID, UUID> regions = new Dictionary<UUID, UUID>();
            if (presence is null)
                return regions;
            OpenSim.Services.Interfaces.PresenceInfo[] found = presence.GetAgents(ids.Select(i => i.ToString()).ToArray());
            if (found is null)
                return regions;
            foreach (OpenSim.Services.Interfaces.PresenceInfo info in found)
            {
                // A visitor's presence can carry "uuid;home;name"; the agent id is the first 36 characters.
                string user = info?.UserID;
                if (!string.IsNullOrEmpty(user) && UUID.TryParse(user.Length > 36 ? user[..36] : user, out UUID agent))
                    regions[agent] = info.RegionID;
            }
            return regions;
        });

    // ---- group membership and powers, resolved as GRID state ------------------------------
    //
    // IGroupsModule answers through the groups service (local or remote connector), so these are the
    // same on every region of the grid and on every host -- which is what lets the voice session be
    // grid-wide while admission stays correct wherever the agent happens to be standing. Any scene in
    // this process can answer; the first one with the module wins. No scene means no groups, which is
    // a refusal, never an admission.

    private IGroupsModule GroupsModule()
    {
        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        foreach (Scene s in scenes)
        {
            IGroupsModule g = s.RequestModuleInterface<IGroupsModule>();
            if (g is not null)
                return g;
        }
        return null;
    }

    private bool GroupIsMember(UUID agentID, UUID groupID)
    {
        try
        {
            return GroupsModule()?.GetMembershipData(groupID, agentID) is not null;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group membership lookup failed for agent {AgentId} group {GroupId}; refusing",
                logHeader, agentID, groupID);
            return false;
        }
    }

    private ulong GroupPowersOf(UUID agentID, UUID groupID)
    {
        try
        {
            return GroupsModule()?.GetFullGroupPowers(agentID, groupID) ?? 0UL;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group power lookup failed for agent {AgentId} group {GroupId}; refusing",
                logHeader, agentID, groupID);
            return 0UL;
        }
    }

    // Incoming group voice rings from another region server arrive as grid instant messages.
    private void SubscribeGroupVoiceEvents(Scene scene)
    {
        scene.EventManager.OnIncomingInstantMessage += OnIncomingGroupRing;
    }

    // The presence backstop for seats: a root close releases the agent's group and conference seats. `left` is what the
    // departure of the agent from every session returned; the avatar-to-avatar calls in it are the caller's to handle.
    private void ReleaseSeatsOnClientClosed(Scene scene, UUID clientID, Scene s, IReadOnlyList<NonSpatialVoiceSession> left)
    {
        // A ROOT presence closing releases the agent's group seats too. Same guard as
        // above (ShouldMarkGone): a child teardown is a border crossing, not a departure, and
        // must not free a seat the agent still holds from its root region. This is the
        // presence backstop -- departure never waits on a POST that may never come.
        foreach (NonSpatialVoiceSession gone in left)
        {
            if (gone.Type == NonSpatialSessionType.P2P)
                continue;
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceChatSession.Line(clientID, gone.Owner, "group-seat-released-client-closed",
                    $"room={gone.RoomKey} region={s?.Name ?? scene.Name} seats={gone.SeatsHeld}/{gone.Cap}"));
            // The presence backstop is a departure like any other, so the
            // conference roster has to lose the name here too -- otherwise a crashed member
            // sits in everyone's panel until the idle TTL.
            SendConferenceRoster(gone, clientID, joined: false);
        }
    }

    // Admits or refuses a group or conference provision: a "multiagent" body whose channel is one of our room keys.
    // Writes the refusal and returns false when it is not admitted.
    private bool TryAdmitGroupProvision(OSDMap map, UUID agentID, Scene scene, IOSHttpResponse response,
        out ProvisionAdmission admission)
    {
        admission = null;
        map.TryGetString("channel", out string gch);
        // A member who was removed from the call gets the same refusal for every retry the viewer makes, so it is read
        // back from a small cache before the record, the groups service or the store is asked for anything.
        if (m_callModeration.IsUsable && !string.IsNullOrEmpty(gch)
            && m_removalRefusals.TryGetRefusal(agentID, RemovalCacheKey(gch), out _))
        {
            response.RawBuffer = llsdUndefAnswerBytes;
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return false;
        }
        NonSpatialProvisionResult g = NonSpatialProvisionAdmission.Decide(
            map, agentID, m_groupVoice, m_nonSpatial, scene.RegionInfo.RegionID, m_adhocVoiceEnabled);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            NonSpatialProvisionAdmission.Line(agentID, scene.Name, gch,
                map.TryGetString("credentials", out string gcr) && !string.IsNullOrEmpty(gcr), g.Decision));
        if (!g.Admitted && (g.Decision == NonSpatialProvisionAdmission.DecisionRemoved
                            || g.Decision == NonSpatialProvisionAdmission.DecisionAdhocRemoved))
        {
            // Logged in full once for each window the viewer keeps trying; the retries in between are cached and counted.
            NoteRemovalRefusal(agentID, gch, g.Decision, scene);
            WarnRefusal("group-provision", agentID, () =>
                $"{logHeader}[ProvisionVoice]: refusing group provision ({g.Decision}) from agent {agentID} in region \"{scene.Name}\"");
            response.RawBuffer = llsdUndefAnswerBytes;
            response.StatusCode = g.Status;
            return false;
        }
        if (!g.Admitted)
        {
            WarnRefusal("group-provision", agentID, () =>
                $"{logHeader}[ProvisionVoice]: refusing group provision ({g.Decision}) from agent {agentID} in region \"{scene.Name}\"");
            response.RawBuffer = llsdUndefAnswerBytes;
            response.StatusCode = g.Status;
            return false;
        }
        // Admitted. Hand the rest of the request to the SAME flow an admitted A2A multiagent
        // provision takes: it skips the parcel/estate checks (their authorization is the
        // registry / the group service) and calls the voice service with the body unchanged,
        // which selects the room by (gridId, channel, "multiagent") using the existing bridge.
        // Nothing below this line knows or needs to know that it was a group.
        admission = new ProvisionAdmission
        {
            // The kind follows the type of the voice session, so a conference seat is never logged or
            // torn down as a group one.
            Kind = g.Session?.Type == NonSpatialSessionType.Adhoc ? ProvisionKind.Adhoc : ProvisionKind.Group,
            Decision = g.Decision,
            ChannelType = A2AProvisionAdmission.ChannelTypeMultiagent,
            Channel = gch ?? "-",
        };
        return true;
    }

    // Tags the seat with the voice service's viewer session and promotes it to present: the agent is now in the room.
    private void RecordGroupSeatPresent(ProvisionAdmission admission, string groupVs, UUID agentID, Scene scene)
    {
        // The viewer_session exists only in the SERVICE's success map, so this is the
        // first moment the seat can be tagged with it -- and tagging it is what makes the
        // logout teardown able to find the seat later (DepartByViewerSession). Also promotes
        // Accepted -> Present: the agent is now actually in the mixer room.
        //
        // Ad-hoc takes this SAME arm, and it has to. Leaving conferences out would
        // repeat the exact defect found for groups -- a hang-up holding its seat until
        // the 8 h idle TTL, with the cap load-bearing.
        NonSpatialVoiceSession gsess = m_nonSpatial?.GetByRoomKey(admission.Channel);
        if (gsess is not null)
        {
            SessionOutcome pres = m_nonSpatial.MarkPresent(gsess.SessionId, agentID, scene.RegionInfo.RegionID, groupVs);
            bool conf = gsess.Type == NonSpatialSessionType.Adhoc;
            string detail = $"room={admission.Channel} seats={m_nonSpatial.Get(gsess.SessionId)?.SeatsHeld}/{gsess.Cap}";
            if (pres.Ok)
                SyncCallVoiceOnJoin(agentID, gsess);
            else if (pres.Decision == SessionOutcome.Removed)
            {
                // The member was removed between the admission and the answer: the join that just happened is undone.
                int undone = DropMemberFromCallMedia(gsess, agentID, ForceCloseReasonKey);
                m_log.LogInformation("{LogHeader} [CALL REMOVAL] session={Session} target={Target} joined as they were removed; dropped={Dropped}",
                    logHeader, gsess.SessionId, agentID, undone);
            }
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                conf ? AdhocVoiceChatSession.Line(agentID, gsess.SessionId, "adhoc-present-" + pres.Decision, detail)
                     : GroupVoiceChatSession.Line(agentID, gsess.Owner, "group-present-" + pres.Decision, detail));
        }
    }

    // A voice teardown releases the group and conference seats held under that viewer session.
    private void ReleaseGroupSeatsOnLogout(UUID agentID, string a2aVs, IReadOnlyList<NonSpatialVoiceSession> left)
    {
        // The SAME teardown releases a group seat. Before this, the logout arm knew
        // only about A2A records, so a group member who hung up held their seat until the
        // 8 h idle TTL -- with the 50 cap load-bearing, enough hang-ups would lock a group
        // out of its own room. Keyed by viewer session, so it frees exactly the seat this
        // logout is for and never another region's.
        foreach (NonSpatialVoiceSession gone in left)
        {
            if (gone.Type == NonSpatialSessionType.P2P)
                continue;
            // The same teardown already covered conferences -- DepartByViewerSession is
            // type-blind and always was -- but it logged every release as a group one. The
            // line now names what actually ended.
            string releaseDetail = $"room={gone.RoomKey} viewer_session={a2aVs} seats={gone.SeatsHeld}/{gone.Cap}";
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                gone.Type == NonSpatialSessionType.Adhoc
                    ? AdhocVoiceChatSession.Line(agentID, gone.SessionId, "adhoc-seat-released", releaseDetail)
                    : GroupVoiceChatSession.Line(agentID, gone.Owner, "group-seat-released", releaseDetail));
            // The voice teardown deliberately sends no roster LEAVE: that would
            // conflate lifecycles (3) and (1), because hanging up would take the member out of everyone's
            // participant panel although they were still in the conversation and could still
            // type. The seat is released above; the roster is deliberately NOT touched. The
            // member leaves the roster only on a chat leave (dialog 18) or a presence close.
        }
    }

    // The group "call" and "accept invitation" arms. Returns true when one of them answered.
    private bool TryHandleGroupChatSession(OSDMap reqmap, UUID agentID, ScenePresence sp, Scene scene, IOSHttpResponse response)
    {
        // A GROUP voice "call" is answered here, BEFORE the A2A arm, and only when group voice
        // is enabled and the voice session-id names a group this agent belongs to. TryHandleCall returns
        // false having done nothing for everything else, so the A2A path below is reached with
        // exactly the arguments and the state it would have had. A group "call" from a NON-member
        // falls through on purpose: it reaches the A2A arm and 404s there, which is how an unknown
        // session was always answered and what the viewer already survives.
        if (GroupVoiceChatSession.TryHandleCall(reqmap, agentID, m_groupVoice, m_nonSpatial,
                                                scene.RegionInfo.RegionID, out ChatSessionOutcome groupOutcome))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, groupOutcome.Instrument);
            ApplyChatSessionOutcome(groupOutcome, response);
            if (groupOutcome.Status == HttpStatusCode.OK)
                SendGroupJoinModeration(agentID, GroupSessionIdOf(reqmap));
            if (groupOutcome.StartedRinging)
                RingGroupMembers(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, GroupNameOf(GroupSessionIdOf(reqmap)));
            return true;
        }

        // The popup's Accept posts "accept invitation", which the A2A arm answers
        // with 400 (it has no case for it), and a failed accept makes the viewer clear the invitation
        // and never call startCall. Handled here for a group session only; everything else still
        // falls through untouched.
        if (GroupVoiceChatSession.TryHandleAcceptInvitation(reqmap, agentID, m_groupVoice, m_nonSpatial,
                                                            out ChatSessionOutcome acceptOutcome))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, acceptOutcome.Instrument);
            ApplyChatSessionOutcome(acceptOutcome, response);
            if (acceptOutcome.Status == HttpStatusCode.OK)
                SendGroupJoinModeration(agentID, GroupSessionIdOf(reqmap));
            if (acceptOutcome.StartedRinging)
                RingGroupMembers(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, GroupNameOf(GroupSessionIdOf(reqmap)));
            return true;
        }
        return false;
    }
}
