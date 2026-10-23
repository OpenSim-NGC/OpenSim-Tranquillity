/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Carry an avatar-to-avatar call between region-server processes.
 *
 * A call is rung into the process that holds the callee's ROOT presence, the same way a group or
 * conference ring is (GroupVoiceRingTransport): a grid instant message that no other
 * OnIncomingInstantMessage subscriber claims (SessionSend with fromGroup false) and a magic header in
 * binaryBucket. The four steps of a call each travel as their own carrier kind:
 *
 *   ring     caller's process -> callee's process   a call is being placed; carries the call's credential
                                                        (not with SessionStoreMode grid: the credential is in the store)
 *   join     callee's process -> caller's process   the callee accepted and joined the voice room
 *   decline  callee's process -> caller's process   the callee declined
 *   leave    either process   -> the other          a party hung up or left
 *
 * What the stock viewer needs from each, read in the viewer source:
 *   ring     the ChatterBoxInvitation that opens the incoming-call dialog (llimview.cpp:5196-5214). The
 *            receiving process rebuilds that body itself from the signed fields, so nothing unsigned
 *            reaches the viewer.
 *   join     ChatterBoxSessionAgentListUpdates with an ENTER for the callee, so the caller's speaker list
 *            gains the callee (LLIMSpeakerMgr::updateSpeakers, llspeakers.cpp:762). The audio side needs
 *            no carrier: the callee joins the same mixer room, and the caller's voice client adds the
 *            participant from the mixer's join message (llvoicewebrtc.cpp:3310-3318).
 *   decline  an agent-list update that carries can_voice_chat false for the callee, which makes the
 *            caller's viewer show "call declined" and end the call (llimview.cpp:4366-4382).
 *   leave    an agent-list LEAVE for the party who left (llspeakers.cpp:758). The caller's call itself
 *            ends from the mixer: the voice client hangs up when the peer's participant leaves the room
 *            (llvoicewebrtc.cpp:1468).
 *
 * Every carrier is authenticated with CarrierAuth, with the step as part of the kind, so a ring cannot be
 * replayed as a decline and a carrier for one target cannot be redirected to another. The payload holds
 * only the fields the MAC covers.
 *
 * With SessionStoreMode grid the carriers need no secret. The receiver asks the grid session service for the
 * facts the carrier claims (the call exists and the target is its callee; the callee has declined, joined or
 * left) and acts only on what the service says, so a forged carrier can do no more than repeat a fact the
 * service already holds. A region that does have a CarrierSecret signs and checks the MAC as well.
 */
using System;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;   // GridInstantMessage

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>One parsed avatar-to-avatar carrier, before it has been authenticated.</summary>
    public readonly struct A2ACarrier
    {
        public string Step { get; init; }
        /// <summary>The agent the carrier is addressed to (taken from the instant message's recipient).</summary>
        public UUID Target { get; init; }
        public UUID SessionId { get; init; }
        /// <summary>The other party: the caller for a ring, the one who joined, declined or left otherwise.</summary>
        public UUID About { get; init; }
        /// <summary>The display name of <see cref="About"/>; carried by a ring, for the incoming-call dialog.</summary>
        public string AboutName { get; init; }
        public string Room { get; init; }
        public string Token { get; init; }
        public OSDMap Auth { get; init; }
        public UUID OriginRegion { get; init; }
    }

    public static class A2ACarrierTransport
    {
        /// <summary>InstantMessageDialog.SessionSend, as in the group ring carrier.</summary>
        public const byte DialogSessionSend = 17;

        /// <summary>Magic header on binaryBucket. Anything without it is not ours, whatever the dialog says.</summary>
        public const string BucketMagic = "LGVA2A1";

        public const string InstrumentTag = "[A2A XHOST]";

        public const string StepRing = "ring";
        public const string StepJoin = "join";
        public const string StepDecline = "decline";
        public const string StepLeave = "leave";

        public const string DecisionSent = "sent-cross-instance";
        public const string DecisionNoTransfer = "no-message-transfer-module";
        public const string DecisionOffline = "target-offline";
        public const string DecisionCarrierOff = "carrier-off-no-secret";
        public const string DecisionFeatureOff = "feature-off";
        public const string DecisionCarrierRefused = "carrier-refused";
        public const string DecisionBadPair = "refused-session-is-not-the-pair";
        public const string DecisionBadRoom = "refused-room-key-mismatch";
        public const string DecisionNoRecord = "refused-no-record";
        public const string DecisionBadToken = "refused-bad-token";
        public const string DecisionNotRootHere = "refused-target-not-root-here";
        public const string DecisionGlare = "refused-call-in-progress";
        public const string DecisionAlreadyActive = "refused-already-active";
        public const string DecisionNotTrue = "refused-store-does-not-say-so";

        public static bool IsStep(string step)
            => step == StepRing || step == StepJoin || step == StepDecline || step == StepLeave;

        public static string KindOf(string step) => CarrierAuth.KindA2APrefix + step;

        /// <summary>The fields a carrier's MAC covers.</summary>
        public static CarrierFields Fields(in A2ACarrier c)
            => new CarrierFields
            {
                Kind = KindOf(c.Step),
                Target = c.Target,
                SessionId = c.SessionId,
                SessionType = NonSpatialSessionType.P2P.ToString(),
                RoomKey = c.Room,
                Caller = c.About,
                Credential = c.Token,
                Text = c.AboutName,
            };

        /// <summary>
        /// Build the carrier for one step. <paramref name="auth"/> signs it; with no usable secret the carrier
        /// is built unsigned and every receiver refuses it, so callers check <see cref="CarrierAuth.Enabled"/> first.
        /// </summary>
        public static GridInstantMessage Build(string step, UUID target, UUID about, string aboutName, UUID sessionId,
                                               string roomKey, string token, UUID originRegion, CarrierAuth auth)
        {
            if (!IsStep(step)) throw new ArgumentException("not an avatar-to-avatar step", nameof(step));
            A2ACarrier c = new A2ACarrier
            {
                Step = step, Target = target, SessionId = sessionId, About = about,
                AboutName = aboutName ?? string.Empty, Room = roomKey ?? string.Empty, Token = token ?? string.Empty,
            };
            OSDMap payload = new OSDMap
            {
                ["step"] = OSD.FromString(step),
                ["about"] = OSD.FromUUID(about),
                ["name"] = OSD.FromString(c.AboutName),
                ["room"] = OSD.FromString(c.Room),
                ["token"] = OSD.FromString(c.Token),
            };
            if (auth is { Enabled: true })
                payload[CarrierAuth.AuthKey] = auth.Seal(Fields(c));
            return new GridInstantMessage
            {
                toAgentID = target.Guid,
                fromAgentID = about.Guid,
                fromAgentName = c.AboutName,
                dialog = DialogSessionSend,
                fromGroup = false,
                imSessionID = sessionId.Guid,
                message = string.Empty,
                offline = 0,                       // a ring is worthless once stored and replayed later
                RegionID = originRegion.Guid,
                timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                binaryBucket = Encoding.UTF8.GetBytes(BucketMagic + OSDParser.SerializeJsonString(payload)),
            };
        }

        /// <summary>
        /// Is this incoming grid IM one of ours? False for everything else, having touched nothing, so every
        /// other subscriber still sees the message as before.
        /// </summary>
        public static bool TryParse(GridInstantMessage msg, out A2ACarrier carrier)
        {
            carrier = default;
            if (msg is null || msg.dialog != DialogSessionSend || msg.fromGroup)
                return false;
            if (msg.binaryBucket is null || msg.binaryBucket.Length <= BucketMagic.Length)
                return false;

            string raw;
            try { raw = Encoding.UTF8.GetString(msg.binaryBucket); }
            catch { return false; }
            if (!raw.StartsWith(BucketMagic, StringComparison.Ordinal))
                return false;

            OSDMap map;
            try { map = OSDParser.DeserializeJson(raw.Substring(BucketMagic.Length)) as OSDMap; }
            catch { return false; }
            if (map is null)
                return false;
            if (!map.TryGetString("step", out string step) || !IsStep(step) || !map.TryGetUUID("about", out UUID about))
                return false;

            carrier = new A2ACarrier
            {
                Step = step,
                Target = new UUID(msg.toAgentID),
                SessionId = new UUID(msg.imSessionID),
                About = about,
                AboutName = map.TryGetString("name", out string name) ? name : string.Empty,
                Room = map.TryGetString("room", out string room) ? room : string.Empty,
                Token = map.TryGetString("token", out string token) ? token : string.Empty,
                Auth = map.TryGetOSDMap(CarrierAuth.AuthKey, out OSDMap auth) ? auth : null,
                OriginRegion = new UUID(msg.RegionID),
            };
            return true;
        }

        /// <summary>
        /// A call's two parties are the only agents its session id can name: the session id is the XOR of
        /// the two agent ids. A carrier whose session is not the pair's is refused whatever its MAC says.
        /// </summary>
        public static bool SessionIsThePair(in A2ACarrier c)
            => c.SessionId == A2ACalls.ComputeSessionId(c.About, c.Target)
               && c.About != UUID.Zero && c.Target != UUID.Zero && c.About != c.Target;

        public static string Line(string step, UUID target, UUID about, UUID sessionId, string decision, string detail = null)
            => $"{InstrumentTag} step={step} target={target} about={about} session-id={sessionId} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
