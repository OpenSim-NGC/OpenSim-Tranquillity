/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The CROSS-INSTANCE carrier for ad-hoc conference TEXT, and the resolution of the
 * carrier collision with the ring.
 *
 * THE COLLISION. GroupVoiceRingTransport carries a ring as SessionSend + fromGroup=false, chosen
 * because no OnIncomingInstantMessage subscriber in the tree claims that combination (its header
 * enumerates all of them). Conference text needs to cross instances the same way and is ALSO
 * SessionSend + fromGroup=false, because it is the same free combination and for the same reason.
 * So dialog and fromGroup cannot tell a ring from a message, and a receiver that guessed would
 * either ring a viewer with somebody's chat line or swallow a chat line as a ring.
 *
 * THE DISCRIMINATOR, AND WHY THIS ONE. A SEPARATE VERSIONED BUCKET HEADER, "LGVTEXT1", alongside
 * the ring's "LGVRING1" -- not a type field inside one shared carrier format. Three reasons, in
 * order of weight:
 *
 *   1. IT IS ENFORCED BY CODE THAT IS ALREADY LIVE AND ALREADY PROVEN. GroupVoiceRingTransport
 *      .TryParse requires the bucket to START WITH "LGVRING1" and returns false otherwise
 *      (GroupVoiceRingTransport.cs). A text message carrying "LGVTEXT1" is therefore
 *      rejected by the deployed ring parser WITHOUT ONE LINE OF CHANGE TO IT. The ring half of the
 *      discrimination needs no new code, so it cannot be broken by new code.
 *   2. A SHARED FORMAT WOULD MEAN EDITING THE RING PARSER. The ring is already deployed and
 *      proven in use; text is not. Putting a type field inside one format makes
 *      every text change a change to the ring's receive path. That trade is the wrong way round.
 *   3. THE RING'S OWN HEADER SAYS TO DO THIS. "Versioned so a future change of payload shape is a
 *      different header, not a silent misparse" (GroupVoiceRingTransport.cs). Text is a
 *      different payload shape. A new header is the documented answer, not an improvisation.
 *
 * The cost is two parsers instead of one. Accepted: each is ~20 lines, they share no state and no
 * enum, so neither can drift into the other, and each is ignorant of the other's magic -- which is
 * exactly what makes BOTH directions closed by construction rather than by a branch someone has to
 * keep correct. Tested both ways (a ring is not text, text is not a ring).
 *
 * ORDER OF DISPATCH DOES NOT MATTER and the module does not rely on one. The two magics are
 * distinct prefixes over the same byte range, so at most one parser can claim any given message.
 *
 * THE MAGIC NEVER REACHES A VIEWER. This is a module-to-module carrier only. The receiving instance
 * rebuilds a clean SessionSend for the viewer (AdhocTextSession.BuildForViewer), exactly as the
 * ring rebuilds its ChatterBoxInvitation locally, so a viewer cannot tell the local and the
 * cross-instance path apart.
 */
using System;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;   // GridInstantMessage

namespace osWebRtcVoice.NonSpatial
{
    public static class AdhocTextTransport
    {
        /// <summary>InstantMessageDialog.SessionSend. Mirrored, not referenced -- same reason as the ring.</summary>
        public const byte DialogSessionSend = 17;

        /// <summary>
        /// Our magic. Anything without it is not ours, whatever the dialog says -- and critically,
        /// anything carrying the RING's magic is not ours either, because this is a prefix test.
        /// </summary>
        public const string BucketMagic = "LGVTEXT1";

        public const string InstrumentTag = "[ADHOC TEXT XHOST]";

        public const string DecisionSent = "sent-cross-instance";
        public const string DecisionNoTransfer = "no-message-transfer-module";
        public const string DecisionNotOurs = "not-ours";

        /// <summary>Payload keys. Kept flat: there is no nesting to get wrong.</summary>
        public const string KeyMessage = "message";
        public const string KeyFromName = "from_name";
        public const string KeySessionName = "session_name";
        public const string KeyRoomKey = "room_key";

        /// <summary>
        /// Build the carrier for one remote recipient. The session id rides in imSessionID and the
        /// recipient in toAgentID -- the same field discipline the ring uses -- and the text rides
        /// in the bucket behind the magic, so the receiving side reconstructs the viewer's message
        /// rather than forwarding a half-formed one.
        /// </summary>
        public static GridInstantMessage Build(UUID target, UUID fromAgent, string fromName, UUID sessionId,
                                               UUID originRegion, string message, string sessionName = null,
                                               string roomKey = null, CarrierAuth auth = null, string sessionKey = null)
        {
            OSDMap payload = new OSDMap
            {
                [KeyMessage] = OSD.FromString(message ?? string.Empty),
                [KeyFromName] = OSD.FromString(fromName ?? string.Empty),
                [KeySessionName] = OSD.FromString(sessionName ?? string.Empty),
                [KeyRoomKey] = OSD.FromString(roomKey ?? string.Empty),
            };
            if (!string.IsNullOrEmpty(sessionKey) || auth is { Enabled: true })
                payload[CarrierAuth.AuthKey] = (auth ?? CarrierAuth.Off()).Seal(Fields(target, sessionId, fromAgent, roomKey, message), sessionKey);
            byte[] bucket = Encoding.UTF8.GetBytes(BucketMagic + OSDParser.SerializeJsonString(payload));
            return new GridInstantMessage
            {
                toAgentID = target.Guid,
                fromAgentID = fromAgent.Guid,
                fromAgentName = fromName ?? string.Empty,
                // Same free combination as the ring; the bucket magic is what separates them.
                dialog = DialogSessionSend,
                fromGroup = false,
                imSessionID = sessionId.Guid,
                message = message ?? string.Empty,
                // A conference line is worthless stored and replayed hours later, and
                // both offline allowlists reject SessionSend anyway. Zero here says so at the source.
                offline = 0,
                RegionID = originRegion.Guid,
                // DateTimeOffset, not Util.UnixTimeSinceEpoch: same purity rule as the ring builder.
                timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                binaryBucket = bucket,
            };
        }

        /// <summary>
        /// Is this incoming grid IM one of our conference messages? Returns false for everything
        /// else -- including a ring -- having touched nothing.
        /// </summary>
        public static bool TryParse(GridInstantMessage msg, out UUID target, out UUID sessionId,
                                    out string message, out string fromName, out string sessionName)
            => TryParse(msg, out target, out sessionId, out message, out fromName, out sessionName,
                        out _, out _);

        /// <summary>As above, and also returns the room key the sender signed and the carrier's auth block.</summary>
        public static bool TryParse(GridInstantMessage msg, out UUID target, out UUID sessionId,
                                    out string message, out string fromName, out string sessionName,
                                    out string roomKey, out OSDMap auth)
        {
            roomKey = null;
            auth = null;
            target = UUID.Zero;
            sessionId = UUID.Zero;
            message = null;
            fromName = null;
            sessionName = null;
            if (msg is null || msg.dialog != DialogSessionSend || msg.fromGroup)
                return false;
            if (msg.binaryBucket is null || msg.binaryBucket.Length <= BucketMagic.Length)
                return false;

            string raw;
            try { raw = Encoding.UTF8.GetString(msg.binaryBucket); }
            catch { return false; }
            // THE RING TEST. "LGVRING1" does not start with "LGVTEXT1", so a ring falls out here.
            if (!raw.StartsWith(BucketMagic, StringComparison.Ordinal))
                return false;

            try
            {
                if (OSDParser.DeserializeJson(raw.Substring(BucketMagic.Length)) is not OSDMap map)
                    return false;
                message = map.TryGetString(KeyMessage, out string m) ? m : string.Empty;
                fromName = map.TryGetString(KeyFromName, out string f) ? f : string.Empty;
                sessionName = map.TryGetString(KeySessionName, out string s) ? s : string.Empty;
                roomKey = map.TryGetString(KeyRoomKey, out string rk) ? rk : string.Empty;
                auth = map.TryGetOSDMap(CarrierAuth.AuthKey, out OSDMap carried) ? carried : null;
            }
            catch { return false; }

            target = new UUID(msg.toAgentID);
            sessionId = new UUID(msg.imSessionID);
            return true;
        }

        /// <summary>The fields a conference text line's MAC covers. The sender fills the caller slot.</summary>
        public static CarrierFields Fields(UUID target, UUID sessionId, UUID sender, string roomKey, string message)
            => new CarrierFields
            {
                Kind = CarrierAuth.KindText,
                Target = target,
                SessionId = sessionId,
                SessionType = NonSpatialSessionType.Adhoc.ToString(),
                RoomKey = roomKey,
                Caller = sender,
                Text = message,
            };

        public static string Line(UUID target, UUID session, string decision, string detail = null)
            => $"{InstrumentTag} target={target} session={session} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
