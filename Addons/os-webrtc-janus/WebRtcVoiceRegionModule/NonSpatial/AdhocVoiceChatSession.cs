/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Handles "start conference" and the event-queue ChatterBoxSessionStartReply.
 *
 * THIS FIXES A VIEWER POPUP: "Unable to start a new chat session with Multi-person
 * chat." (followed by a note that initialization timed out). Answering "start conference" with 200 and no body
 * (ChatSessionRequestLogic.cs) leaves the viewer's session uninitialised, so
 * LLSessionTimeoutTimer fires that message. A body would not have helped: startConferenceCoro reads
 * NOTHING from the response (llimview.cpp:576-624) -- it only checks the status. The authoritative
 * id arrives on the EVENT QUEUE.
 *
 * ADHOC IS THE ONE TYPE THAT GENUINELY NEEDS THE RE-KEY. For a group the viewer slams the session id
 * to the group id, and for P2P it is the XOR, so both already agree with the server. For a
 * conference the viewer mints session_id.generate() (llimview.cpp:2542) -- a random id the server
 * has never seen -- and only adopts ours when the reply arrives:
 *     LLViewerChatterBoxSessionStartReply::post reads success / temp_session_id / session_id
 *         (llimview.cpp:4877-4886)
 *     -> processSessionInitializedReply re-keys mId2SessionMap, sets mSessionID and updates the
 *        voice channel id (:1705-1737, :1165-1177)
 *     -> and fires the QUEUED CALL if mStartCallOnInitialize (:1731-1734)
 * That last line is why the reply has to be sent: without the re-key the queued call never starts.
 *
 * Modelled on GroupsMessagingModule.ChatterBoxSessionStartReplyViaCaps (:686-707), which has been
 * shipping this event for group chat for years. The module already knows how to send it -- the
 * ChatSessionOutcome.Reply path (WebRtcVoiceRegionModule.cs) -- so this arm only has to
 * produce the two ids and let the existing plumbing do the rest.
 */
using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    public static class AdhocVoiceChatSession
    {
        public const string MethodStartConference = "start conference";

        /// <summary>
        /// Add people to a conference that is ALREADY RUNNING. `params` is an LLSD array of
        /// agent ids and `session-id` is the authoritative conference id -- the viewer has been
        /// re-keyed by now, so it sends ours and not its temp one.
        /// llfloaterimsession.cpp:1256-1266, and fsfloaterim.cpp:2117-2126 sends the identical shape
        /// from Firestorm's own IM floater. Both were checked because either viewer may be in use.
        /// </summary>
        public const string MethodInvite = "invite";

        /// <summary>
        /// Decline an invitation (llimview.cpp:3436-3438). No `params` -- just the method and the session id. The
        /// viewer clears its own pending invitation immediately afterwards (:3444-3445), so the
        /// server-side unwind here is the other half of an action the viewer already considers done.
        /// </summary>
        public const string MethodDeclineInvitation = "decline invitation";

        /// <summary>
        /// The conference's voice channel. Identical shape to the group one -- and it is the
        /// call the viewer's re-key QUEUES: processSessionInitializedReply fires the pending call when
        /// mStartCallOnInitialize is set (llimview.cpp:1731-1734), which reaches
        /// LLVoiceChannelGroup::activate -> voiceCallCapCoro -> {method:"call", session-id}
        /// (llvoicechannel.cpp:492-502,:631). The `session-id` is the AUTHORITATIVE id, because the
        /// re-key has already replaced the viewer's temp one.
        /// </summary>
        public const string MethodCall = "call";

        /// <summary>
        /// The invitee's side. Accept on the popup runs chatterBoxInvitationCoro, which POSTs
        /// {method:"accept invitation", session-id} and calls startCall(voice_channel_info) only when
        /// it succeeds (llimview.cpp:3382-3385). Without this arm the popup is decorative.
        /// </summary>
        public const string MethodAcceptInvitation = "accept invitation";

        public const string DecisionStarted = "adhoc-conference-started";
        public const string DecisionIdempotent = "adhoc-conference-idempotent";
        public const string DecisionNoParams = "adhoc-refused-no-invitees";
        public const string DecisionCallAdmitted = "adhoc-call-admitted";
        public const string DecisionAcceptAdmitted = "adhoc-accept-admitted";
        public const string DecisionFull = "adhoc-refused-capacity";
        public const string DecisionInvited = "adhoc-invited";
        public const string DecisionInviteNothingNew = "adhoc-invite-nothing-new";
        public const string DecisionDeclined = "adhoc-declined";
        public const string InstrumentTag = "[ADHOC VOICE]";

        /// <summary>
        /// Answer "start conference": open (or re-find) the ADHOC session and hand back the two ids
        /// the viewer needs. Returns false -- untouched -- for anything else, so every other method
        /// and the whole A2A path behave exactly as before.
        ///
        /// Idempotent by the viewer's own temp id: a retried start finds the first session rather
        /// than opening a second room and sending a second reply. The viewer mints its temp id once,
        /// at session creation, so a retry carries the same one.
        /// </summary>
        public static bool TryHandleStartConference(OSDMap reqmap, UUID agentID, INonSpatialSessions engine,
                                                    UUID originRegion, bool enabled, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (!enabled || engine is null || reqmap is null)
                return false;
            if (!reqmap.TryGetString("method", out string method)
                || !string.Equals(method, MethodStartConference, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out UUID tempSessionId) || tempSessionId == UUID.Zero)
                return false;

            // Every start counts against the agent's request bucket, a retry included. Checked before the
            // list is read, so a refused request costs the sim almost nothing.
            if (!engine.TryTakeConferenceRequest(agentID, out bool unavailable))
            {
                string why = unavailable ? SessionOutcome.StoreUnavailable : SessionOutcome.RateLimited;
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.RefusalStatus(why),
                    Instrument = Line(agentID, tempSessionId, "adhoc-refused-" + why, null),
                };
                return true;
            }

            List<UUID> invitees = ParseInvitees(reqmap);

            SessionOutcome start = engine.StartAdhoc(agentID, tempSessionId, originRegion, invitees);
            if (!start.Ok)
            {
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.RefusalStatus(start.Decision),
                    Instrument = Line(agentID, tempSessionId, "adhoc-refused-" + start.Decision, null),
                };
                return true;
            }

            outcome = new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                // The module's existing Reply path turns this into the event-queue
                // ChatterBoxSessionStartReply carrying {success, temp_session_id, session_id}.
                Reply = new ChatSessionOutcome.StartReply
                {
                    SessionId = start.Session.SessionId,
                    TempSessionId = start.Session.TempSessionId,
                },
                // The creator takes the first seat inside Start, so the 0 -> 1 transition --
                // the moment the conference begins -- happens HERE, not at anybody's accept. This is
                // what makes the invitees from `params` get rung. Latched in the engine, so a retried
                // start does not ring the room twice.
                StartedRinging = start.StartedRinging,
                Instrument = Line(agentID, tempSessionId,
                                  start.Created ? DecisionStarted : DecisionIdempotent,
                                  $"session={start.Session.SessionId} room={start.Session.RoomKey} "
                                  + $"invitees={invitees.Count} rekey={start.Session.RequiresRekey}"),
            };
            return true;
        }

        /// <summary>
        /// The agents the viewer wants in the conference. startConferenceCoro sends them as an LLSD
        /// ARRAY in `params` (llimview.cpp:2484-2487, `agents.append(ids[i])`) -- unlike
        /// "start p2p voice", where params is a single UUID. Both shapes are accepted here because
        /// the cost is two lines and a viewer that sends the scalar form would otherwise open a
        /// conference with nobody in it.
        /// </summary>
        public static List<UUID> ParseInvitees(OSDMap reqmap)
        {
            List<UUID> invitees = new List<UUID>();
            if (reqmap is null || !reqmap.TryGetValue("params", out OSD raw))
                return invitees;
            switch (raw)
            {
                case OSDArray arr:
                    // A set for the repeat test: scanning the list per entry is quadratic, and the array
                    // is whatever the client sent.
                    HashSet<UUID> seen = new HashSet<UUID>();
                    foreach (OSD entry in arr)
                    {
                        UUID id = entry.AsUUID();
                        if (id != UUID.Zero && seen.Add(id)) invitees.Add(id);
                    }
                    break;
                default:
                    UUID single = raw.AsUUID();
                    if (single != UUID.Zero) invitees.Add(single);
                    break;
            }
            return invitees;
        }

        /// <summary>
        /// Answer "invite": add the named agents to a conference already running, and hand back the
        /// ones that are NEWLY invited so the caller rings those and nobody else. Someone already
        /// seated, or whose popup is already up, is not returned -- re-inviting must never produce a
        /// second ring for a person who is sitting in the call.
        ///
        /// Ours only when the `session-id` names an AD-HOC session this store holds. A group session,
        /// an A2A session, or an id we have never seen returns false and falls through untouched, so
        /// this arm cannot capture a method that today reaches the A2A path.
        /// </summary>
        public static bool TryHandleInvite(OSDMap reqmap, UUID agentID, INonSpatialSessions engine,
                                           bool enabled, out ChatSessionOutcome outcome, out List<UUID> newlyInvited)
        {
            outcome = null;
            newlyInvited = new List<UUID>();
            if (!Ours(reqmap, engine, enabled, MethodInvite, out UUID sessionId, out NonSpatialVoiceSession session, out bool storeDown))
            {
                // The service could not say whether this is a conference, so the viewer is told to try again.
                if (!storeDown)
                    return false;
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.StoreUnavailableHttpStatus,
                    Instrument = Line(agentID, sessionId, "adhoc-refused-" + SessionOutcome.StoreUnavailable, null),
                };
                return true;
            }

            if (!engine.TryTakeConferenceRequest(agentID, out bool unavailable))
            {
                string why = unavailable ? SessionOutcome.StoreUnavailable : SessionOutcome.RateLimited;
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.RefusalStatus(why),
                    Instrument = Line(agentID, sessionId, "adhoc-refused-" + why, null),
                };
                return true;
            }

            List<UUID> asked = ParseInvitees(reqmap);
            SessionOutcome fits = engine.CheckInviteCount(sessionId, agentID, asked);
            if (!fits.Ok)
            {
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.RefusalStatus(fits.Decision),
                    Instrument = Line(agentID, sessionId, "adhoc-refused-" + fits.Decision,
                                      $"session={sessionId} asked={asked.Count}"),
                };
                return true;
            }
            int refusals = 0;
            foreach (UUID invitee in asked)
            {
                if (invitee == agentID) continue;                      // never invite yourself
                NonSpatialMember before = session.Find(invitee);
                bool alreadyInTheCall = before != null && before.HoldsSeat;
                bool alreadyRung = session.WasInvited(invitee);

                SessionOutcome r = engine.Invite(sessionId, agentID, invitee);
                if (!r.Ok)
                {
                    refusals++;
                    continue;
                }
                if (!alreadyInTheCall && !alreadyRung)
                    newlyInvited.Add(invitee);
            }

            outcome = new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                Instrument = Line(agentID, sessionId,
                                  newlyInvited.Count > 0 ? DecisionInvited : DecisionInviteNothingNew,
                                  $"session={sessionId} asked={asked.Count} new={newlyInvited.Count}"
                                  + (refusals > 0 ? " refused=" + refusals : string.Empty)),
            };
            return true;
        }

        /// <summary>
        /// Answer "decline invitation" for an ad-hoc conference: drop the pending invitation without
        /// touching a seat, because an invitation never held one. The engine also forgets that this
        /// agent was rung, so a later invite can ring them again -- a mis-clicked decline must not
        /// make someone unreachable for the rest of the call.
        ///
        /// Scoped to AD-HOC sessions only. The viewer sends this same method for a declined GROUP
        /// invitation (llimview.cpp:3429-3438), and that path is not handled here; letting it fall
        /// through keeps today's behaviour for everything that is not a conference.
        /// </summary>
        public static bool TryHandleDeclineInvitation(OSDMap reqmap, UUID agentID, INonSpatialSessions engine,
                                                      bool enabled, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (!Ours(reqmap, engine, enabled, MethodDeclineInvitation, out UUID sessionId, out NonSpatialVoiceSession session))
                return false;
            if (session.Find(agentID) is null)
                return false;              // not a party to this conference: not ours to answer

            SessionOutcome r = engine.Decline(sessionId, agentID);
            outcome = new ChatSessionOutcome
            {
                // A decline the engine refused is still a 200: the viewer has already torn its own
                // pending invitation down and has nothing useful to do with an error.
                Status = HttpStatusCode.OK,
                Instrument = Line(agentID, sessionId, DecisionDeclined,
                                  $"session={sessionId} engine={r.Decision} "
                                  + $"seats={engine.Get(sessionId)?.SeatsHeld}"),
            };
            return true;
        }

        /// <summary>
        /// Answer "call" for an ad-hoc conference -- the initiator's own voice join, and the
        /// one the viewer's re-key queues.
        ///
        /// THE ASYMMETRY WITH GROUP IS DELIBERATE AND IS THE INTERESTING PART. A group "call" CREATES
        /// the voice session, because for a group the session id IS the group id and the first caller is
        /// simply the first. A conference cannot work that way: its id is server-minted and means
        /// nothing until "start conference" has produced it. So this arm never starts anything -- if
        /// the store has no such conference we are not ours, fall through, and the A2A arm 404s
        /// exactly as it does for any unknown session, which is how an unknown session was always
        /// answered and what the viewer already survives.
        ///
        /// Admission is the engine's: AdhocAdmission.CanJoin admits on a member record, so the
        /// creator and the invitees get in and nobody else does. The same rule the ring list uses.
        /// </summary>
        public static bool TryHandleCall(OSDMap reqmap, UUID agentID, INonSpatialSessions engine,
                                         UUID originRegion, bool enabled, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (!Ours(reqmap, engine, enabled, MethodCall, out UUID sessionId, out NonSpatialVoiceSession session, out bool unavailable))
            {
                // The service could not say whether this is a conference, so the viewer is told to try again.
                if (!unavailable)
                    return false;
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.StoreUnavailableHttpStatus,
                    Instrument = Line(agentID, sessionId, "adhoc-refused-" + SessionOutcome.StoreUnavailable, null),
                };
                return true;
            }
            // The discriminator, mirroring the group arm's membership test: someone with no record in
            // this conference is not ours and must fall through untouched.
            if (session.Find(agentID) is null)
                return false;

            outcome = Seat(agentID, session, engine, originRegion, DecisionCallAdmitted, withCredentials: true);
            return true;
        }

        /// <summary>
        /// Answer "accept invitation" for an ad-hoc conference. Accepting the popup IS the
        /// join -- the viewer goes straight to startCall with the channel info the ring carried
        /// and never posts "call" -- so this is the only place an invitee takes its seat.
        ///
        /// 200 with no body, like the group arm: the coroutine feeds the body to
        /// LLIMSpeakerMgr::setSpeakers, which tolerates an empty map, and what actually matters is the
        /// STATUS, because any failure makes it clear the invitation and never call startCall.
        /// </summary>
        public static bool TryHandleAcceptInvitation(OSDMap reqmap, UUID agentID, INonSpatialSessions engine,
                                                     UUID originRegion, bool enabled, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (!Ours(reqmap, engine, enabled, MethodAcceptInvitation, out UUID sessionId, out NonSpatialVoiceSession session, out bool unavailable))
            {
                // The service could not say whether this is a conference, so the viewer is told to try again.
                if (!unavailable)
                    return false;
                outcome = new ChatSessionOutcome
                {
                    Status = (HttpStatusCode)NonSpatialCaps.StoreUnavailableHttpStatus,
                    Instrument = Line(agentID, sessionId, "adhoc-refused-" + SessionOutcome.StoreUnavailable, null),
                };
                return true;
            }
            if (session.Find(agentID) is null)
                return false;      // uninvited: not ours, and AdhocAdmission would refuse anyway

            outcome = Seat(agentID, session, engine, originRegion, DecisionAcceptAdmitted, withCredentials: false);
            return true;
        }

        /// <summary>
        /// Take the seat and build the answer. Shared by "call" and "accept invitation" because the
        /// seat, the cap and the refusals are identical between them; only whether the reply carries
        /// voice_credentials differs, since the accept path already has them from the ring.
        /// </summary>
        private static ChatSessionOutcome Seat(UUID agentID, NonSpatialVoiceSession session,
                                               INonSpatialSessions engine, UUID originRegion,
                                               string decision, bool withCredentials)
        {
            SessionOutcome seat = engine.Accept(session.SessionId, agentID, originRegion);
            if (!seat.Ok)
            {
                // Capacity is the one refusal with a required status: 409, which the viewer maps to
                // ERROR_CHANNEL_FULL -- the same thing the mixer's own 495 produces.
                HttpStatusCode code = (HttpStatusCode)NonSpatialCaps.RefusalStatus(seat.Decision);
                string word = seat.Decision == SessionOutcome.Capacity ? DecisionFull
                    : seat.Decision == SessionOutcome.Removed ? NonSpatialProvisionAdmission.DecisionAdhocRemoved : "adhoc-refused-" + seat.Decision;
                return new ChatSessionOutcome
                {
                    Status = code,
                    Instrument = Line(agentID, session.SessionId, word, seat.Decision),
                };
            }

            OSDMap body = null;
            if (withCredentials)
            {
                // channel_uri is the AD-HOC room key, not the session id. The viewer treats it as
                // opaque and the mixer hashes it as the multiagent channel, so the
                // conference gets a room distinct from every group, A2A and parcel room with no
                // change to JanusAudioBridge.
                body = new OSDMap
                {
                    ["voice_credentials"] = new OSDMap
                    {
                        ["voice_server_type"] = OSD.FromString(A2AInvitation.VoiceServerType),
                        ["channel_uri"] = OSD.FromString(session.RoomKey),
                        ["channel_credentials"] = OSD.FromString(engine.IssueToken(session.SessionId, agentID)),
                    },
                };
            }

            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                StartedRinging = seat.StartedRinging,
                Body = body,
                Instrument = Line(agentID, session.SessionId, decision,
                                  $"room={session.RoomKey} "
                                  + $"seats={engine.Get(session.SessionId)?.SeatsHeld}/{session.Cap}"),
            };
        }

        /// <summary>
        /// The shared "is this ours?" gate for the post-start ad-hoc methods: enabled, the right
        /// method, a session id, and a voice session this store holds that is genuinely AD-HOC.
        /// </summary>
        private static bool Ours(OSDMap reqmap, INonSpatialSessions engine, bool enabled, string wanted,
                                 out UUID sessionId, out NonSpatialVoiceSession session)
            => Ours(reqmap, engine, enabled, wanted, out sessionId, out session, out _);

        /// <summary>
        /// As above, and <paramref name="unavailable"/> says the store could not be reached, so a false answer does not
        /// mean the session is not a conference.
        /// </summary>
        private static bool Ours(OSDMap reqmap, INonSpatialSessions engine, bool enabled, string wanted,
                                 out UUID sessionId, out NonSpatialVoiceSession session, out bool unavailable)
        {
            sessionId = UUID.Zero;
            session = null;
            unavailable = false;
            if (!enabled || engine is null || reqmap is null)
                return false;
            if (!reqmap.TryGetString("method", out string method)
                || !string.Equals(method, wanted, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out sessionId) || sessionId == UUID.Zero)
                return false;
            session = engine.Get(sessionId, default, out unavailable);
            return session is not null && session.Type == NonSpatialSessionType.Adhoc;
        }

        public static string Line(UUID agentID, UUID tempSessionId, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} temp-session-id={tempSessionId} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
