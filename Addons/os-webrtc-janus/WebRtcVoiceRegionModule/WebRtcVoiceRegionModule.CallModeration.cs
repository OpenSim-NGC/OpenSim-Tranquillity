/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using Nini.Config;
using Microsoft.Extensions.Logging;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice;

// Call moderation: a group call's moderators mute a member's voice or text, or the whole call's voice.
// [WebRtcVoice] CallModerationEnabled (off by default) switches it on; with it off every request is answered as it
// was before the feature existed.
public partial class WebRtcVoiceRegionModule
{
    private CallModerationPolicy m_callModeration = new CallModerationPolicy();
    private CallGroupChatGate m_callChatGate;
    private string m_callGridId = string.Empty;
    private bool m_callStockPlugin;
    private Timer m_callModerationTick;

    // The moderator flag last sent to each member of a call, so a stable call generates no traffic.
    private readonly Dictionary<(UUID Session, UUID Agent), bool> m_callFlagsSent = new();

    // Reads the call moderation settings. With the master key off, or with neither group nor conference calls on
    // (there is no call to moderate), nothing is set up and the module behaves as before.
    private void InitialiseCallModeration(IConfigSource config)
    {
        CallModerationSettings settings = CallModerationSettings.Read(m_Config);
        if (!settings.Enabled)
            return;
        if (!m_groupVoice.IsUsable && !m_adhocVoiceEnabled)
        {
            m_log.LogWarning("{LogHeader} [WebRtcVoice] CallModerationEnabled is true, but neither GroupVoiceEnabled nor "
                + "AdhocVoiceEnabled is, so there is no call to moderate. Call moderation stays off", logHeader);
            return;
        }

        m_callGridId = JanusAudioBridge.ReadGridId(config);
        IConfig janusConfig = config.Configs["JanusWebRtcVoice"];
        m_callStockPlugin = janusConfig is null
            || string.Equals(WebRtcJanusService.ReadPluginName(janusConfig), WebRtcJanusService.DefaultPluginName,
                StringComparison.OrdinalIgnoreCase);
        InitialiseCallRemovalCache();
        m_callModeration = new CallModerationPolicy { Settings = settings, IsMember = GroupIsMember, Powers = GroupPowersOf };
        m_callChatGate = new CallGroupChatGate(m_nonSpatial, cache: m_gridSessions is not null);
        RegisterCallConsole();
        m_log.LogInformation("{LogHeader} CALL MODERATION enabled ([WebRtcVoice] CallModerationEnabled): moderators of a group call "
            + "(Moderate Group Chat) can mute a member's voice or text, or the whole call's voice. {VoiceMutes}. "
            + "Roster flags are re-checked every {Seconds} s. A removal lasts {Lasts}", logHeader,
            m_callStockPlugin ? "Voice mutes use the stock AudioBridge's own per-participant mute"
                              : "The configured Janus plugin has no call voice mute here, so a voice mute is refused with 501",
            settings.RevalidateSeconds,
            settings.RemovalMinutes > 0 ? settings.RemovalMinutes + " minutes" : "until the call ends");
    }

    // A scene joins or leaves: the group chat check is offered to the groups module through the scene.
    private void RegisterCallModerationScene(Scene scene)
    {
        if (m_callChatGate is not null)
            scene.RegisterModuleInterface<IGroupChatGate>(m_callChatGate);
        StartCallModerationTick();
    }

    private void UnregisterCallModerationScene(Scene scene, bool noScenesLeft)
    {
        if (m_callChatGate is not null)
            scene.UnregisterModuleInterface<IGroupChatGate>(m_callChatGate);
        if (noScenesLeft)
            StopCallModerationTick();
    }

    private List<Scene> CallScenes()
    {
        lock (m_scenes)
            return new List<Scene>(m_scenes);
    }

    // The moderation request methods. False (nothing done) when moderation is off or the request is something else.
    private bool TryHandleCallModeration(OSDMap reqmap, UUID agentID, IOSHttpResponse response)
    {
        if (!m_callModeration.IsUsable)
            return false;
        if (!CallModerationHandler.TryHandle(reqmap, agentID, m_callModeration, m_nonSpatial, new CallEffects(this), out ChatSessionOutcome outcome))
            return false;
        m_log.LogDebug("{LogHeader} {Line}", logHeader, outcome.Instrument);
        ApplyChatSessionOutcome(outcome, response);
        return true;
    }

    // A conference has no group and so no moderators: nobody in it is exempt from a mute.
    private bool IsCallModerator(UUID group, UUID agent)
        => group != UUID.Zero && m_callModeration.IsUsable && m_callModeration.MayModerate(agent, group);

    // ---- voice -------------------------------------------------------------------------------------

    private int CallRoomNumber(NonSpatialVoiceSession session)
        => JanusAudioBridge.CalcRoomNumber(m_callGridId, string.Empty, "multiagent", 0, session.RoomKey);

    private INonSpatialParticipantMute StockCallMute()
    {
        foreach (Scene s in CallScenes())
            if (s.RequestModuleInterface<IWebRtcVoiceService>() is INonSpatialParticipantMute mute)
                return mute;
        return null;
    }

    private bool CanEnforceCallVoice()
        => m_callStockPlugin && StockCallMute() is not null;

    // Make the record's voice mutes true in the call's room: one mute request for each member who is in the room.
    // Only the stock AudioBridge's participant mute is carried out; any other plugin answers Unsupported.
    private ParticipantMuteResult EnforceCallVoice(NonSpatialVoiceSession session, UUID target = default)
    {
        if (!m_callStockPlugin)
            return ParticipantMuteResult.Unsupported;
        INonSpatialParticipantMute mute = StockCallMute();
        if (mute is null)
            return ParticipantMuteResult.Unsupported;
        Func<UUID, bool> moderator = a => IsCallModerator(session.Owner, a);
        int room = CallRoomNumber(session);
        ParticipantMuteResult worst = ParticipantMuteResult.NotInRoom;
        foreach (NonSpatialMember m in session.Members)
        {
            if (!m.HoldsSeat || string.IsNullOrEmpty(m.ViewerSession))
                continue;   // not in the room yet: it is muted when it joins
            bool muted = session.IsVoiceMuted(m.AgentId) && !moderator(m.AgentId);
            ParticipantMuteResult r = mute.SetCallMute(m.AgentId, room, muted);
            if (r == ParticipantMuteResult.Unsupported || r == ParticipantMuteResult.Failed)
                return r;
            if (r == ParticipantMuteResult.NotInRoom && m.AgentId == target)
                return r;   // the one agent a mute names has no participant: that is not a mute
            if (r == ParticipantMuteResult.Applied)
                worst = ParticipantMuteResult.Applied;
        }
        return worst == ParticipantMuteResult.NotInRoom ? ParticipantMuteResult.Applied : worst;
    }

    // Whether a voice mute of this one agent can reach a participant: the stock AudioBridge is asked for the agent's
    // participant (found by the agent id the region set at join).
    private bool CanResolveCallVoiceTarget(NonSpatialVoiceSession session, UUID target)
    {
        if (!m_callStockPlugin)
            return false;
        INonSpatialParticipantMute mute = StockCallMute();
        return mute is not null && mute.HasCallParticipant(target, CallRoomNumber(session));
    }

    // A member's provision into a call was admitted. A call that mutes anyone is told to the room again, so the new
    // member is silenced if the call says so and hears only who it should.
    private void SyncCallVoiceOnJoin(UUID agentID, NonSpatialVoiceSession session)
    {
        if (!m_callModeration.IsUsable || session is null || session.Type == NonSpatialSessionType.P2P)
            return;
        NonSpatialVoiceSession current = m_nonSpatial?.Get(session.SessionId, agentID) ?? session;
        if (!current.VoiceModerated && current.VoiceMuted.Count == 0)
            return;
        ParticipantMuteResult r = EnforceCallVoice(current);
        if (r == ParticipantMuteResult.Unsupported || r == ParticipantMuteResult.Failed)
            m_log.LogWarning("{LogHeader} call {Session} is muted, but {Agent} could not be muted on joining: {Result}",
                logHeader, current.SessionId, agentID, r);
    }

    // ---- what members are told ----------------------------------------------------------------------

    // A member joined a group call (call or accept invitation): their own entry says whether they moderate, and a
    // call that is muted says so.
    private void SendGroupJoinModeration(UUID agentID, UUID groupID)
    {
        if (!m_callModeration.IsUsable)
            return;
        NonSpatialVoiceSession session = m_nonSpatial?.Get(groupID, agentID);
        if (session is null || session.Type != NonSpatialSessionType.Group)
            return;
        bool moderator = m_callModeration.MayModerate(agentID, session.Owner);
        List<Scene> scenes = CallScenes();
        string d = CallModerationDelivery.SendEntries(scenes, agentID, session.SessionId,
            new List<GroupChatListAgentUpdateData> { CallModerationDelivery.Entry(agentID, moderator, session.IsTextMuted(agentID)) });
        lock (m_callFlagsSent)
            m_callFlagsSent[(session.SessionId, agentID)] = moderator;
        m_log.LogDebug("{LogHeader} {Line}", logHeader, CallModerationDelivery.Line(agentID, session.SessionId, "own-entry moderator=" + moderator, d));
        if (session.VoiceModerated)
            CallModerationDelivery.SendModeratedMode(scenes, agentID, session.SessionId, true);
    }

    private IEnumerable<UUID> CallAudience(NonSpatialVoiceSession session, UUID by, UUID target)
    {
        HashSet<UUID> all = new HashSet<UUID>();
        foreach (NonSpatialMember m in session.Members)
            if (m.HoldsSeat) all.Add(m.AgentId);
        all.Add(by);
        if (target != UUID.Zero) all.Add(target);
        return all;
    }

    // The record changed: the people whose panels show it are told.
    private void CallModerationChanged(NonSpatialVoiceSession session, ModerationOp op, UUID target, UUID by)
    {
        m_callChatGate?.Forget(session.SessionId);
        List<Scene> scenes = CallScenes();
        switch (op)
        {
            case ModerationOp.VoiceModerated:
                foreach (UUID a in CallAudience(session, by, UUID.Zero))
                    CallModerationDelivery.SendModeratedMode(scenes, a, session.SessionId, session.VoiceModerated);
                break;
            case ModerationOp.TextMute:
                bool targetModerator = m_callModeration.MayModerate(target, session.Owner);
                List<GroupChatListAgentUpdateData> entry = new()
                    { CallModerationDelivery.Entry(target, targetModerator, session.IsTextMuted(target)) };
                foreach (UUID a in CallAudience(session, by, target))
                    CallModerationDelivery.SendEntries(scenes, a, session.SessionId, entry);
                break;
        }
    }

    // ---- keeping the moderator flags right -------------------------------------------------------------

    private void StartCallModerationTick()
    {
        int seconds = m_callModeration.Settings.RevalidateSeconds;
        if (!m_callModeration.IsUsable || seconds <= 0 || m_callModerationTick is not null)
            return;
        TimeSpan every = TimeSpan.FromSeconds(seconds);
        m_callModerationTick = new Timer(_ => RevalidateCallFlags(), null, every, every);
    }

    private void StopCallModerationTick()
    {
        Timer t = Interlocked.Exchange(ref m_callModerationTick, null);
        t?.Dispose();
    }

    // Re-ask the groups service who moderates, for the members of group calls who are here, and send an entry only
    // where the flag differs from what that member was last sent. One pass reads each call once and asks the groups
    // service about each member once, whatever the number of members and of decisions made about each.
    private void RevalidateCallFlags()
    {
        try
        {
            if (!m_callModeration.IsUsable || m_nonSpatial is null)
                return;
            List<Scene> scenes = CallScenes();
            HashSet<UUID> here = new HashSet<UUID>(GroupVoiceInvite.PresentAgents(scenes));
            List<(UUID Session, UUID Agent)> tracked;
            lock (m_callFlagsSent)
                tracked = new List<(UUID, UUID)>(m_callFlagsSent.Keys);
            Dictionary<UUID, NonSpatialVoiceSession> calls = new();
            GroupStandingMemo standing = new GroupStandingMemo(m_callModeration.IsMember, m_callModeration.Powers);
            foreach ((UUID sessionId, UUID agent) in tracked)
            {
                if (!calls.TryGetValue(sessionId, out NonSpatialVoiceSession session))
                    calls[sessionId] = session = m_nonSpatial.Get(sessionId);
                NonSpatialMember member = session?.Find(agent);
                if (session is null || member is null || !member.HoldsSeat || !here.Contains(agent))
                {
                    lock (m_callFlagsSent)
                        m_callFlagsSent.Remove((sessionId, agent));
                    continue;
                }
                // A member who no longer holds the powers the call needs is let go of first.
                if (DropIfCallPowerLost(session, agent, standing))
                {
                    lock (m_callFlagsSent)
                        m_callFlagsSent.Remove((sessionId, agent));
                    continue;
                }
                bool now = m_callModeration.MayModerate(agent, session.Owner, standing.IsMember, standing.Powers);
                bool last;
                lock (m_callFlagsSent)
                    last = m_callFlagsSent.TryGetValue((sessionId, agent), out bool sent) && sent;
                if (now == last)
                    continue;
                CallModerationDelivery.SendEntries(scenes, agent, sessionId,
                    new List<GroupChatListAgentUpdateData> { CallModerationDelivery.Entry(agent, now, session.IsTextMuted(agent)) });
                lock (m_callFlagsSent)
                    m_callFlagsSent[(sessionId, agent)] = now;
            }
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} call moderation flag re-check failed", logHeader);
        }
    }

    // What the request handler asks of the module.
    private sealed class CallEffects : ICallModerationEffects
    {
        private readonly WebRtcVoiceRegionModule m_module;
        public CallEffects(WebRtcVoiceRegionModule module) { m_module = module; }
        public bool CanEnforceVoice(NonSpatialVoiceSession session) => m_module.CanEnforceCallVoice();
        public ParticipantMuteResult EnforceVoice(NonSpatialVoiceSession session, UUID target) => m_module.EnforceCallVoice(session, target);
        public bool CanResolveVoiceTarget(NonSpatialVoiceSession session, UUID target) => m_module.CanResolveCallVoiceTarget(session, target);
        public void Changed(NonSpatialVoiceSession session, ModerationOp op, UUID target, UUID by, bool changed)
        {
            if (changed) m_module.CallModerationChanged(session, op, target, by);
        }
    }
}
