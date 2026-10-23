/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Pure decision logic for the ChatSessionRequest capability.
 *
 * The HTTP handler in WebRtcVoiceRegionModule.ChatSessionRequest is a thin adapter: it parses the body,
 * calls Decide(), then applies the outcome (status code, LLSD response body, ChatterBoxSessionStartReply
 * event) and logs the instrument line. Everything that can be unit-tested without a Scene lives here.
 *
 * Wire contract (the Second Life and Firestorm viewers' llimview.cpp and llvoicechannel.cpp):
 *  - "start p2p voice" (llimview.cpp:627-664): session-id = viewer XOR of the two agent ids, params = the
 *    other participant's UUID. The viewer needs the ChatterBoxSessionStartReply echo (temp_session_id ==
 *    its session-id). A missing/unparseable params is a malformed request -> 400, replacing the
 *    old UUID.Random fallback which minted a voice session no one else could ever join.
 *  - "call" (llvoicechannel.cpp:623-688): expects voice_credentials { channel_uri, channel_credentials } in
 *    the HTTP RESPONSE BODY; 403 -> VoiceNotAllowed, other failures -> VoiceCallGenericError.
 *  - "decline p2p voice" removes the invitation; "decline invitation" and "start conference": 200, no body;
 *    "fetch history": 200 with an empty array.
 */
using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    /// <summary>What the HTTP adapter must do for one ChatSessionRequest.</summary>
    public sealed class ChatSessionOutcome
    {
        public HttpStatusCode Status { get; init; }
        /// <summary>LLSD map to serialise as the response body, or null for an empty body.</summary>
        public OSDMap Body { get; init; }

        /// <summary>
        /// LLSD ARRAY to serialise as the response body. Separate from <see cref="Body"/>
        /// because "fetch history" is the one method whose reply is an array, not a map, and the
        /// viewer type-checks it: LLSD history = result[HTTP_RESULTS_CONTENT] followed by
        /// history.isArray() (llimview.cpp:816-822). A map here would take the else-branch and log
        /// "Bad array data fetching chat history". Ignored when <see cref="Body"/> is set.
        /// </summary>
        public OSDArray BodyArray { get; init; }
        /// <summary>When non-null, enqueue a ChatterBoxSessionStartReply with these values to the requester.</summary>
        public StartReply Reply { get; init; }
        /// <summary>Single-line, greppable instrument text. Always set.</summary>
        public string Instrument { get; init; }

        /// <summary>
        /// this request took a GROUP room from 0 seats to 1, so the module should ring the
        /// group. Set only by the group arms; every A2A path leaves it false and no A2A code reads
        /// it, so the A2A decisions are unchanged.
        /// </summary>
        public bool StartedRinging { get; init; }

        /// <summary>
        /// When non-null, deliver this ChatterBoxInvitation body to <see cref="Invitation.Callee"/>
        /// via IEventQueue.BuildEvent + Enqueue. Set ONLY by "call" (never by "start p2p voice", which the
        /// viewer fires on every P2P IM window open and must not ring anyone).
        /// </summary>
        public Invitation Invite { get; init; }

        /// <summary>
        /// Set by "decline p2p voice" when it removed a call: the removed record, so the module can tell the caller
        /// when the callee was the one who declined. Null for every other outcome.
        /// </summary>
        public A2ASession Declined { get; init; }

        public sealed class StartReply
        {
            public UUID SessionId { get; init; }
            public UUID TempSessionId { get; init; }
        }

        public sealed class Invitation
        {
            public UUID Callee { get; init; }
            public UUID Caller { get; init; }
            public UUID SessionId { get; init; }
            public OSDMap Body { get; init; }
        }
    }

    /// <summary>Builds the voice ChatterBoxInvitation body the viewer's voice branch requires.</summary>
    public static class A2AInvitation
    {
        /// <summary>EMultiAgentChatSessionType::P2P_CHAT_SESSION in the viewer (llimview.cpp:119-125).</summary>
        public const int InvitationTypeP2P = 2;

        /// <summary>
        /// WEBRTC_VOICE_SERVER_TYPE (llvoicewebrtc.cpp:83). Every channel-info map the sim
        /// hands the viewer must carry it: LLVoiceClient::setNonSpatialChannel routes the channel to a
        /// voice module by this key (llvoiceclient.cpp:514-528) and getVoiceModule defaults an ABSENT
        /// value to Vivox (:126-132) -- without it the webrtc module never sees the channel and no
        /// multiagent provision is attempted. It is also the first thing
        /// LLWebRTCVoiceClient::compareChannels tests (llvoicewebrtc.cpp:1682-1687), so its absence made
        /// isThisVoiceChannel false for our own channel and tore down live channels on any re-offer.
        /// Deliberately NO sip_uri: compareChannels' second test is sip_uri equality, and the module's
        /// own maps (getAudioSessionChannelInfo, :1626-1636) do not carry it -- absent==absent compares
        /// equal, while adding it would make our maps unequal to every module-built map.
        /// </summary>
        public const string VoiceServerType = "webrtc";

        /// <summary>
        /// Body shape (LLViewerChatterBoxInvitation, llimview.cpp): top-level
        /// session_id / session_name / from_id / from_name, plus a `voice` map that becomes the callee's
        /// voice_channel_info verbatim -- so it must carry channel_uri and channel_credentials, which the
        /// callee's startAdHocSession reads (llvoicewebrtc.cpp:1497-1498). No `instantmessage` key: its
        /// presence would route the viewer to the IM branch (llimview.cpp:5047). session_name labels the
        /// callee's incoming-call UI, so it is the CALLER's name.
        /// </summary>
        public static OSDMap BuildBody(A2ASession session, UUID caller, string callerName)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrEmpty(session.Token)) throw new InvalidOperationException("invitation requires a minted token");

            OSDMap voice = new OSDMap
            {
                ["invitation_type"] = OSD.FromInteger(InvitationTypeP2P),
                ["voice_server_type"] = OSD.FromString(VoiceServerType),   // see the const
                ["channel_uri"] = OSD.FromString(session.ChannelUri),
                ["channel_credentials"] = OSD.FromString(session.Token),
            };
            return new OSDMap
            {
                ["session_id"] = OSD.FromUUID(session.SessionId),
                ["session_name"] = OSD.FromString(callerName ?? string.Empty),
                ["from_id"] = OSD.FromUUID(caller),
                ["from_name"] = OSD.FromString(callerName ?? string.Empty),
                ["voice"] = voice,
            };
        }
    }

    public static class ChatSessionRequestLogic
    {
        public const string MethodStartP2PVoice = "start p2p voice";
        public const string MethodCall = "call";
        public const string MethodDeclineP2PVoice = "decline p2p voice";
        public const string MethodDeclineInvitation = "decline invitation";
        public const string MethodStartConference = "start conference";
        public const string MethodFetchHistory = "fetch history";

        /// <summary>The instrument prefix; grep for it.</summary>
        public const string InstrumentTag = "[A2A CHATSESSION]";

        /// <summary>
        /// Decide the outcome for a parsed ChatSessionRequest body. Pure apart from the call store.
        /// </summary>
        /// <param name="reqmap">The parsed LLSD body (already known to be a map).</param>
        /// <param name="agentID">The cap-bound requesting agent.</param>
        /// <param name="calls">The calls this region serves, kept in the call session store.</param>
        public static ChatSessionOutcome Decide(OSDMap reqmap, UUID agentID, A2ACalls calls)
            => Decide(reqmap, agentID, string.Empty, calls);

        /// <param name="callerName">The requesting agent's display name; labels the callee's incoming-call UI.</param>
        /// <param name="originRegion">The region the request arrived in; the seat a started call takes is recorded against it.</param>
        public static ChatSessionOutcome Decide(OSDMap reqmap, UUID agentID, string callerName, A2ACalls calls,
                                                UUID originRegion = default)
        {
            if (reqmap == null)
                return Fail(HttpStatusCode.NoContent, agentID, "-", UUID.Zero, "no body");

            if (!reqmap.TryGetString("method", out string method) || string.IsNullOrEmpty(method))
                return Fail(HttpStatusCode.NotFound, agentID, "-", UUID.Zero, "missing method");

            method = method.ToLowerInvariant();

            if (!reqmap.TryGetUUID("session-id", out UUID sessionID))
                return Fail(HttpStatusCode.NotFound, agentID, method, UUID.Zero, "missing session-id");

            switch (method)
            {
                case MethodStartP2PVoice:
                    return StartP2PVoice(reqmap, agentID, sessionID, calls, originRegion);

                case MethodCall:
                    return Call(reqmap, agentID, callerName, sessionID, calls);

                // The callee's (or caller's) decline removes the invitation. The viewer sends this
                // string for a P2P voice invite (llimview.cpp:3419-3425); a non-party's decline is ignored.
                case MethodDeclineP2PVoice:
                {
                    A2ASession before = calls.TryGet(sessionID, UUID.Zero);
                    bool removed = calls.Decline(sessionID, agentID, out bool wasParty);
                    // Brief words: removed | refused-not-party | stub-ok (no such session: the old stub behaviour).
                    string decision = removed ? "removed" : wasParty ? "stub-ok" : calls.TryGet(sessionID, UUID.Zero) == null ? "stub-ok" : "refused-not-party";
                    return new ChatSessionOutcome
                    {
                        Status = HttpStatusCode.OK,
                        Instrument = Line(agentID, MethodDeclineP2PVoice, sessionID, decision, null),
                        Declined = removed ? before : null,
                    };
                }

                // "fetch history", answered HONESTLY instead of stubbed.
                //
                // WHAT HONEST MEANS HERE, decided from what the viewer actually does with each
                // shape (chatterBoxHistoryCoro, llimview.cpp:815-865):
                //   an ARRAY with entries  -> parsed into the panel as past messages
                //   an EMPTY ARRAY         -> "Empty history from chat server, nothing to add",
                //                             a clean, silent no-op (:852-855)
                //   ANYTHING NOT AN ARRAY  -> "Bad array data fetching chat history", a WARNING
                //                             in the viewer's log (:856-859)
                // The 200-with-no-body stub was the third case: every conference and every group
                // chat that opened made the viewer log a complaint. We keep no history -- there is
                // no store for one and does not add one -- so the truthful answer is the EMPTY
                // ARRAY. It says "none", which is the fact, and it is the only shape that says it
                // without the viewer treating the reply as malformed.
                //
                // A non-empty array is what a chat history reply would carry; nothing else about
                // this arm would change.
                case MethodFetchHistory:
                    return new ChatSessionOutcome
                    {
                        Status = HttpStatusCode.OK,
                        BodyArray = new OSDArray(),
                        Instrument = Line(agentID, method, sessionID, "history-empty",
                                          "no history is kept; empty array is the honest answer"),
                    };

                // Stubs carried over unchanged: 200, no body.
                case MethodDeclineInvitation:
                case MethodStartConference:
                    return new ChatSessionOutcome
                    {
                        Status = HttpStatusCode.OK,
                        Instrument = Line(agentID, method, sessionID, "stub-ok", null),
                    };

                default:
                    return Fail(HttpStatusCode.BadRequest, agentID, method, sessionID, "unknown method");
            }
        }

        private static ChatSessionOutcome StartP2PVoice(OSDMap reqmap, UUID agentID, UUID sessionID, A2ACalls calls, UUID originRegion)
        {
            // Params = the other participant. Absent or not a UUID -> malformed -> 400. The
            // viewer maps 400 on this POST to session_does_not_exist_error (llimview.cpp:658-662).
            if (!reqmap.TryGetUUID("params", out UUID otherID) || otherID == UUID.Zero)
                return Fail(HttpStatusCode.BadRequest, agentID, MethodStartP2PVoice, sessionID, "params missing or not a UUID");
            if (otherID == agentID)
                return Fail(HttpStatusCode.BadRequest, agentID, MethodStartP2PVoice, sessionID, "params is the caller");

            UUID derived = A2ACalls.ComputeSessionId(agentID, otherID);
            A2ASession s = calls.Record(agentID, otherID, originRegion, out bool created);
            if (s == null)
                return Fail(HttpStatusCode.ServiceUnavailable, agentID, MethodStartP2PVoice, sessionID, "the call session store did not take the call");

            string altVst = reqmap.TryGetOSDMap("alt_params", out OSDMap alt) && alt.TryGetString("voice_server_type", out string v) ? v : "-";
            string detail = $"callee={otherID} derived={derived} viewer-session-id={sessionID} match={(derived == sessionID).ToString().ToLowerInvariant()} " +
                            $"record={(created ? "created" : "refreshed")} token={(s.Token == null ? "none" : "present")} alt.voice_server_type={altVst}";

            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                // The viewer keys the reply by temp_session_id == the voice session-id it sent (llimview.cpp:4881);
                // session_id is the server's id for the voice session -- the same XOR, so the re-key is a no-op.
                Reply = new ChatSessionOutcome.StartReply { SessionId = derived, TempSessionId = sessionID },
                Instrument = Line(agentID, MethodStartP2PVoice, sessionID, "recorded", detail),
            };
        }

        private static ChatSessionOutcome Call(OSDMap reqmap, UUID agentID, string callerName, UUID sessionID, A2ACalls calls)
        {
            string vst = reqmap.TryGetOSDMap("alt_params", out OSDMap alt) && alt.TryGetString("preferred_voice_server_type", out string v) ? v : "-";
            A2ASession s = calls.IssueToken(sessionID, agentID, out bool unavailable);
            if (s == null)
            {
                // A store that could not be reached has not said the call is gone, so answer as "start p2p voice"
                // does when the store does not take a call: 503, and the viewer can try again.
                if (unavailable)
                    return Fail(HttpStatusCode.ServiceUnavailable, agentID, MethodCall, sessionID, $"the call session store did not answer alt.preferred_voice_server_type={vst}");
                // Policy is real now. A live session whose parties do not include this agent is
                // 403 -> the viewer shows VoiceNotAllowed (llvoicechannel.cpp:660-666). An unknown or
                // expired session stays 404 -> VoiceCallGenericError (:668-673).
                A2ASession live = calls.TryGet(sessionID, UUID.Zero, out unavailable);
                if (unavailable)
                    return Fail(HttpStatusCode.ServiceUnavailable, agentID, MethodCall, sessionID, $"the call session store did not answer alt.preferred_voice_server_type={vst}");
                if (live != null && !live.IsParty(agentID))
                    return Fail(HttpStatusCode.Forbidden, agentID, MethodCall, sessionID, $"not a party of this session alt.preferred_voice_server_type={vst}");
                return Fail(HttpStatusCode.NotFound, agentID, MethodCall, sessionID, $"no live invitation for this agent alt.preferred_voice_server_type={vst}");
            }

            // Voice_server_type rides here too -- the caller's viewer stores this map as
            // mChannelInfo (voiceCallCapCoro, llvoicechannel.cpp:687 -> setChannelInfo :504) and later
            // routes by ITS voice_server_type (activate -> setNonSpatialChannel, :465-469) and compares
            // channel identity against it (isThisVoiceChannel). Same keys as the invitation's voice map;
            // deliberately no sip_uri (see A2AInvitation.VoiceServerType).
            OSDMap creds = new OSDMap
            {
                ["voice_server_type"] = OSD.FromString(A2AInvitation.VoiceServerType),
                ["channel_uri"] = OSD.FromString(s.ChannelUri),
                ["channel_credentials"] = OSD.FromString(s.Token),
            };
            OSDMap body = new OSDMap { ["voice_credentials"] = creds };

            // The invitation to the OTHER party is triggered here, on "call", never on
            // "start p2p voice" (which the viewer sends on every P2P IM window open). A repeat "call"
            // (the viewer retries up to 3x, llvoicechannel.cpp:571-579) re-sends it; the callee viewer
            // ignores a duplicate while the first is pending (mPendingInvitations, llimview.cpp:4257-4281).
            UUID other = s.OtherParty(agentID);

            // The invitation fires ONLY when the
            // requester is the record's CALLER, and at most once per Invited record. Without that,
            // a received ChatterBoxInvitation makes each viewer issue its own bare "call" (no accept body),
            // so the callee's "call" would invite the caller back, whose viewer would call again, without
            // bound and with a chat announcement per invitation. A callee's "call" now gets
            // credentials and rings nobody; a repeat caller "call" after a DELIVERED invitation rings
            // nobody either (InviteSent, set by the module on confirmed delivery -- a failed delivery
            // leaves it clear so a retry can still reach the callee).
            // once the callee has accepted (record Active) a repeat "call" must NOT re-ring:
            // the callee viewer has no guard after accept (its mPendingInvitations entry is cleared on
            // accept, llimview.cpp:3336). Credentials are still returned in every case.
            ChatSessionOutcome.Invitation invite = null;
            string inviteWord;
            if (s.State == A2ASessionState.Active)
                inviteWord = "suppressed-active";
            else if (agentID != s.Caller)
                inviteWord = "suppressed-callee";
            else if (s.InviteSent)
                inviteWord = "suppressed-already-sent";
            else
            {
                invite = new ChatSessionOutcome.Invitation
                {
                    Callee = other,
                    Caller = agentID,
                    SessionId = s.SessionId,
                    Body = A2AInvitation.BuildBody(s, agentID, callerName),
                };
                inviteWord = "pending";
            }

            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                Body = body,
                Invite = invite,
                Instrument = Line(agentID, MethodCall, sessionID, "credentials-issued",
                    $"channel_uri={s.ChannelUri} other={other} state={s.State} invite={inviteWord} alt.preferred_voice_server_type={vst}"),
            };
        }

        private static ChatSessionOutcome Fail(HttpStatusCode status, UUID agentID, string method, UUID sessionID, string reason)
            => new ChatSessionOutcome
            {
                Status = status,
                Instrument = Line(agentID, method, sessionID, $"refused-{(int)status}", reason),
            };

        /// <summary>One greppable line: tag agent method session-id decision [detail]. Never contains the token.</summary>
        public static string Line(UUID agentID, string method, UUID sessionID, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} method=\"{method}\" session-id={sessionID} decision={decision}" +
               (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
