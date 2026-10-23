/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Call moderation: the two ChatSessionRequest methods a viewer posts to moderate a group call.
 *
 *   "mute update"    params { agent_id, mute_info { voice: <bool> } }   or   mute_info { text: <bool> }
 *   "session update" params { update_info { moderated_mode { voice: <bool> } } }
 *
 * true means muted. The shapes are those of the Second Life and Firestorm viewers' llspeakers.cpp:
 * LLIMSpeakerMgr::moderateVoiceParticipant and toggleAllowTextChat ("mute update", with the Firestorm text
 * variant writing the flag as !allow), and moderateVoiceSession ("session update", used for mute all and
 * unmute all). The viewer reads only the HTTP status of the answer: 403 shows "not a moderator", anything
 * else not 2xx shows its generic error (llspeakers.cpp, LLIMSpeakerMgr::moderationActionCoro).
 *
 * WHO MAY MODERATE is decided here, on every request, from the groups service: the requester is a member of the
 * group and holds the Moderate Group Chat power (GP_SESSION_MODERATOR, 1 << 37). The viewer's own menu gate is
 * a convenience and is never trusted. Moderators are never themselves muted, and nobody mutes themselves.
 *
 * Group calls only. A conference has no moderator menu in the stock viewers, so a request that names one is
 * refused; the operator console is the way to moderate a conference.
 *
 * THE ORDER IS: check, make sure the mute can be heard, record it, make it heard. A voice mute that cannot be
 * heard is refused before anything is recorded; one that was recorded and then could not be applied is taken
 * back out of the record, and the room is told the earlier state again so no part of it stays muted. A mute is
 * never reported as done when it is not.
 *
 * A TARGET IS AN AGENT ID. A voice mute names a member of the call by the id the region set when it joined the
 * member (the participant's display in the room is that id, never a name). A member the record does not hold, or
 * whose participant the room cannot be shown to hold, is refused and nothing is recorded.
 *
 * With the feature off (CallModerationSettings.Enabled false) TryHandle returns false having done nothing, so the
 * request goes on to the answer it always had.
 */
using System;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>The settings of call moderation, read from [WebRtcVoice].</summary>
    public sealed class CallModerationSettings
    {
        /// <summary>[WebRtcVoice] CallModerationEnabled. Off by default: moderation requests are answered as before.</summary>
        public const bool DefaultEnabled = false;

        /// <summary>[WebRtcVoice] CallModerationRevalidateSeconds. How often the moderator flags in the roster are re-checked.</summary>
        public const int DefaultRevalidateSeconds = 60;

        /// <summary>
        /// [WebRtcVoice] CallRemovalMinutes. How long a removal lasts when it should outlast the call. 0 (the default):
        /// a removal lasts until the call ends. The call-end rule applies to a conference too; a longer removal keeps the
        /// call's record for that long.
        /// </summary>
        public const int DefaultRemovalMinutes = 0;

        public bool Enabled { get; init; }
        public int RevalidateSeconds { get; init; } = DefaultRevalidateSeconds;
        public int RemovalMinutes { get; init; } = DefaultRemovalMinutes;

        public static CallModerationSettings Off => new CallModerationSettings { Enabled = false };

        public static CallModerationSettings Read(Nini.Config.IConfig config)
        {
            if (config is null) return Off;
            int seconds = config.GetInt("CallModerationRevalidateSeconds", DefaultRevalidateSeconds);
            return new CallModerationSettings
            {
                Enabled = config.GetBoolean("CallModerationEnabled", DefaultEnabled),
                RevalidateSeconds = seconds < 0 ? DefaultRevalidateSeconds : seconds,
                RemovalMinutes = Math.Clamp(config.GetInt("CallRemovalMinutes", DefaultRemovalMinutes), 0, VoiceSessionService.MaxRemovalMinutes),
            };
        }
    }

    /// <summary>Who may moderate a group call. Membership and powers come from the groups service, asked each time.</summary>
    public sealed class CallModerationPolicy
    {
        /// <summary>The viewer's GP_SESSION_MODERATOR / GroupPowers.ModerateChat.</summary>
        public const ulong PowerModerateChat = 1UL << 37;

        public CallModerationSettings Settings { get; init; } = CallModerationSettings.Off;
        public Func<UUID, UUID, bool> IsMember { get; init; }
        public Func<UUID, UUID, ulong> Powers { get; init; }

        public bool IsUsable => Settings.Enabled && IsMember != null && Powers != null;

        /// <summary>A member of the group who holds Moderate Group Chat. Asked each time; never kept.</summary>
        public bool MayModerate(UUID agent, UUID group) => MayModerate(agent, group, IsMember, Powers);

        /// <summary>As <see cref="MayModerate(UUID, UUID)"/>, asking the given lookups (a pass over many members shares one memo).</summary>
        public bool MayModerate(UUID agent, UUID group, Func<UUID, UUID, bool> isMember, Func<UUID, UUID, ulong> powers)
            => agent != UUID.Zero && isMember(agent, group) && (powers(agent, group) & PowerModerateChat) != 0;
    }

    /// <summary>What the module does for a moderation request, beside recording it.</summary>
    public interface ICallModerationEffects
    {
        /// <summary>Can a voice mute in this call be heard, with the voice server in use?</summary>
        bool CanEnforceVoice(NonSpatialVoiceSession session);

        /// <summary>
        /// Can the voice server find the participant of this one agent, by the agent id the region set at join? Asked
        /// before a mute of one agent is recorded.
        /// </summary>
        bool CanResolveVoiceTarget(NonSpatialVoiceSession session, UUID target);

        /// <summary>
        /// Make the record's voice mutes true in the call's room. <paramref name="target"/> is the one agent a mute names
        /// (nil for the whole call): a room that holds no participant for it answers NotInRoom.
        /// </summary>
        ParticipantMuteResult EnforceVoice(NonSpatialVoiceSession session, UUID target);

        /// <summary>The record changed (or was asked to): tell the people whose panels show it.</summary>
        void Changed(NonSpatialVoiceSession session, ModerationOp op, UUID target, UUID by, bool changed);
    }

    public static class CallModerationHandler
    {
        public const string MethodMuteUpdate = "mute update";
        public const string MethodSessionUpdate = "session update";

        public const string InstrumentTag = "[CALL MODERATION]";

        public const string DecisionApplied = "moderation-applied";
        public const string DecisionUnchanged = "moderation-unchanged";
        public const string DecisionBadRequest = "moderation-refused-bad-request";
        public const string DecisionNoSession = "moderation-refused-no-session";
        public const string DecisionNotModerator = "moderation-refused-not-a-moderator";
        public const string DecisionSelf = "moderation-refused-self";
        public const string DecisionTargetModerator = "moderation-refused-target-is-moderator";
        public const string DecisionAdhoc = "adhoc-moderation-refused";
        public const string DecisionP2P = "p2p-moderation-refused";
        public const string DecisionVoiceUnsupported = "refused-voice-mute-unsupported";
        public const string DecisionVoiceFailed = "moderation-voice-mute-failed";
        public const string DecisionRecordRefused = "moderation-refused-record";
        public const string DecisionTargetNotInCall = "moderation-refused-target-not-in-call";
        public const string DecisionTargetUnresolved = "moderation-refused-target-unresolved";

        /// <summary>
        /// Answer a "mute update" or "session update" for a group call. False -- having done nothing -- when moderation is
        /// off, the policy is not usable, or the method is neither.
        /// </summary>
        public static bool TryHandle(OSDMap reqmap, UUID agentID, CallModerationPolicy policy, INonSpatialSessions engine,
                                     ICallModerationEffects effects, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (policy is null || !policy.IsUsable || engine is null || effects is null || reqmap is null)
                return false;
            if (!reqmap.TryGetString("method", out string method))
                return false;
            bool muteUpdate = string.Equals(method, MethodMuteUpdate, StringComparison.OrdinalIgnoreCase);
            bool sessionUpdate = string.Equals(method, MethodSessionUpdate, StringComparison.OrdinalIgnoreCase);
            if (!muteUpdate && !sessionUpdate)
                return false;

            string name = muteUpdate ? MethodMuteUpdate : MethodSessionUpdate;
            if (!reqmap.TryGetUUID("session-id", out UUID sessionId) || sessionId == UUID.Zero)
            {
                outcome = Refuse(HttpStatusCode.BadRequest, agentID, name, UUID.Zero, DecisionBadRequest, "missing session-id");
                return true;
            }

            ModerationOp op;
            UUID target = UUID.Zero;
            bool on;
            if (!(muteUpdate ? ParseMuteUpdate(reqmap, out op, out target, out on, out string why)
                             : ParseSessionUpdate(reqmap, out op, out on, out why)))
            {
                outcome = Refuse(HttpStatusCode.BadRequest, agentID, name, sessionId, DecisionBadRequest, why);
                return true;
            }

            NonSpatialVoiceSession session = engine.Get(sessionId, agentID, out bool unavailable);
            if (session is null)
            {
                outcome = unavailable
                    ? Refuse(HttpStatusCode.ServiceUnavailable, agentID, name, sessionId, SessionOutcome.StoreUnavailable, null)
                    : Refuse(HttpStatusCode.NotFound, agentID, name, sessionId, DecisionNoSession, null);
                return true;
            }
            if (session.Type == NonSpatialSessionType.Adhoc)
            {
                outcome = Refuse(HttpStatusCode.Forbidden, agentID, name, sessionId, DecisionAdhoc, "conferences are moderated from the console");
                return true;
            }
            if (session.Type != NonSpatialSessionType.Group)
            {
                outcome = Refuse(HttpStatusCode.Forbidden, agentID, name, sessionId, DecisionP2P, null);
                return true;
            }

            UUID group = session.Owner;
            if (!policy.MayModerate(agentID, group))
            {
                outcome = Refuse(HttpStatusCode.Forbidden, agentID, name, sessionId, DecisionNotModerator, null);
                return true;
            }
            if (op != ModerationOp.VoiceModerated)
            {
                if (target == agentID)
                {
                    outcome = Refuse(HttpStatusCode.Forbidden, agentID, name, sessionId, DecisionSelf, null);
                    return true;
                }
                if (policy.MayModerate(target, group))
                {
                    outcome = Refuse(HttpStatusCode.Forbidden, agentID, name, sessionId, DecisionTargetModerator, "target=" + target);
                    return true;
                }
            }

            outcome = Apply(engine, effects, session, agentID, op, target, on, name);
            return true;
        }

        /// <summary>
        /// Carry out one moderation decision on a call, once the asker has been judged entitled to make it: check the
        /// target, make sure a voice mute can be heard, record it, make it heard, and tell the people whose panels show
        /// it. Used by the viewer's requests (after the power check) and by the operator console. A voice mute that cannot
        /// be carried out is taken back out of the record, and the room is told the earlier state again.
        /// </summary>
        public static ChatSessionOutcome Apply(INonSpatialSessions engine, ICallModerationEffects effects, NonSpatialVoiceSession session,
                                               UUID agentID, ModerationOp op, UUID target, bool on, string name)
        {
            bool voice = op != ModerationOp.TextMute;
            if (op == ModerationOp.VoiceMute)
            {
                // Only an agent the record knows. An unmute may also clear a mute left on someone who has since left.
                NonSpatialMember seat = session.Find(target);
                bool known = (seat is not null && (seat.HoldsSeat || !on))
                             || (!on && (session.VoiceMuted.Contains(target) || session.VoiceAllowed.Contains(target)));
                if (!known)
                {
                    return Refuse(HttpStatusCode.NotFound, agentID, name, session.SessionId, DecisionTargetNotInCall, "target=" + target);
                }
            }
            if (voice && !effects.CanEnforceVoice(session))
            {
                return Refuse(HttpStatusCode.NotImplemented, agentID, name, session.SessionId, DecisionVoiceUnsupported,
                                 "no voice mute is available with this voice server; nothing recorded");
            }

            if (op == ModerationOp.VoiceMute && on && !effects.CanResolveVoiceTarget(session, target))
            {
                return Refuse(HttpStatusCode.NotFound, agentID, name, session.SessionId, DecisionTargetUnresolved,
                                 "target=" + target + " has no participant in the call's room; nothing recorded");
            }

            bool wasMuted = op == ModerationOp.VoiceMute && session.IsVoiceMuted(target);
            SessionOutcome recorded = engine.Moderate(session.SessionId, agentID, op, target, on);
            if (!recorded.Ok)
            {
                HttpStatusCode code = recorded.Decision == SessionOutcome.StoreUnavailable ? HttpStatusCode.ServiceUnavailable
                                    : recorded.Decision == SessionOutcome.NoSuchSession ? HttpStatusCode.NotFound
                                    : HttpStatusCode.Forbidden;
                return Refuse(code, agentID, name, session.SessionId, DecisionRecordRefused, recorded.Decision);
            }
            NonSpatialVoiceSession now = recorded.Session ?? session;
            bool changed = recorded.Decision == SessionOutcome.ModerationChanged;

            if (voice)
            {
                ParticipantMuteResult heard = effects.EnforceVoice(now, op == ModerationOp.VoiceMute ? target : UUID.Zero);
                // A mute of one agent whose participant is not in the room is not a mute. (Mute-all with nobody in the
                // room yet is fine: it is told to the room as each member joins.)
                bool unresolved = heard == ParticipantMuteResult.NotInRoom && op == ModerationOp.VoiceMute && on;
                if (heard == ParticipantMuteResult.Unsupported || heard == ParticipantMuteResult.Failed || unresolved)
                {
                    // Not done: take it back out of the record, so it does not say something the room does not do,
                    // and say the earlier state to the room again, so nothing stays muted that the record does not hold.
                    if (changed)
                    {
                        SessionOutcome restored = engine.Moderate(session.SessionId, agentID, op, target, op == ModerationOp.VoiceMute ? wasMuted : !on);
                        effects.EnforceVoice(restored.Session ?? now, UUID.Zero);
                    }
                    bool unsupported = heard == ParticipantMuteResult.Unsupported;
                    return unresolved
                        ? Refuse(HttpStatusCode.NotFound, agentID, name, session.SessionId, DecisionTargetUnresolved, "target=" + target + "; nothing recorded")
                        : Refuse(unsupported ? HttpStatusCode.NotImplemented : HttpStatusCode.ServiceUnavailable, agentID, name,
                                 session.SessionId, unsupported ? DecisionVoiceUnsupported : DecisionVoiceFailed, "taken back out of the record");
                }
            }

            effects.Changed(now, op, target, agentID, changed);
            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                Instrument = Line(agentID, name, session.SessionId, changed ? DecisionApplied : DecisionUnchanged,
                                  $"op={op} target={(target == UUID.Zero ? "-" : target.ToString())} on={on} generation={now.ModerationGeneration}"),
            };
        }

        private static bool ParseMuteUpdate(OSDMap req, out ModerationOp op, out UUID target, out bool on, out string why)
        {
            op = ModerationOp.VoiceMute;
            target = UUID.Zero;
            on = false;
            why = null;
            if (!req.TryGetOSDMap("params", out OSDMap p))
            {
                why = "missing params";
                return false;
            }
            if (!p.TryGetUUID("agent_id", out target) || target == UUID.Zero)
            {
                why = "missing agent_id";
                return false;
            }
            if (!p.TryGetOSDMap("mute_info", out OSDMap info) || info.Count != 1)
            {
                why = "mute_info must hold exactly one of voice or text";
                return false;
            }
            bool hasVoice = info.TryGetValue("voice", out OSD v);
            bool hasText = info.TryGetValue("text", out OSD t);
            OSD flag = hasVoice ? v : t;
            if ((!hasVoice && !hasText) || flag is not OSDBoolean b)
            {
                why = "mute_info must hold exactly one of voice or text, as a boolean";
                return false;
            }
            op = hasVoice ? ModerationOp.VoiceMute : ModerationOp.TextMute;
            on = b.AsBoolean();
            return true;
        }

        private static bool ParseSessionUpdate(OSDMap req, out ModerationOp op, out bool on, out string why)
        {
            op = ModerationOp.VoiceModerated;
            on = false;
            why = null;
            if (!req.TryGetOSDMap("params", out OSDMap p) || !p.TryGetOSDMap("update_info", out OSDMap info)
                || !info.TryGetOSDMap("moderated_mode", out OSDMap mode)
                || !mode.TryGetValue("voice", out OSD voice) || voice is not OSDBoolean flag)
            {
                why = "expected update_info.moderated_mode.voice as a boolean";
                return false;
            }
            on = flag.AsBoolean();
            return true;
        }

        private static ChatSessionOutcome Refuse(HttpStatusCode status, UUID agentID, string method, UUID sessionId, string decision, string detail)
            => new ChatSessionOutcome
            {
                Status = status,
                Instrument = Line(agentID, method, sessionId, decision, detail),
            };

        public static string Line(UUID agentID, string method, UUID sessionId, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} method=\"{method}\" session-id={sessionId} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
