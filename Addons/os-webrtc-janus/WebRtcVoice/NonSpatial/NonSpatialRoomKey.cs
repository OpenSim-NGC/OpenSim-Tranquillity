/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The MEDIA ROOM identity, derived INDEPENDENTLY of the session identity.
 *
 * WHY NOT REUSE THE SESSION ID. The A2A path sets ChannelUri => SessionId.ToString()
 * (A2ASession.ChannelUri). That works, but it is OUR choice and not the viewer's contract:
 * The viewer treats channel_uri as OPAQUE -- startAdHocSession takes it
 * straight out of channelInfo (llvoicewebrtc.cpp) and posts it back verbatim as `channel`
 * (SL llvoicewebrtc.cpp:3342, Firestorm llvoicewebrtc.cpp:3696). Binding room identity to session identity costs us the ability to re-room a live session
 * (a mixer move, a shard, a room rebuild) without changing the voice session the viewer is keyed to, so
 * this engine keeps them separate and makes the mapping explicit and one-way.
 *
 * DERIVATION
 *     RoomKey = "nsv1:" + tag + ":" + Uuid(SHA256(NS || 0x1F || gridId || 0x1F || tag || 0x1F || sessionId))
 *   where tag is p2p|adhoc|group and NS is the fixed namespace below. First 16 bytes of the digest
 *   become the UUID. Deterministic, so any instance on the grid derives the same key for the same
 *   session without coordination, and one-way, so the room key leaks no session identity.
 *
 * COLLISION ARGUMENT, in three layers, because only two of them are structural and the report must
 * not pretend otherwise:
 *
 *  (1) AGAINST A2A ROOMS -- STRUCTURAL, provable. A2A admission resolves a room by
 *      TryGetByChannel, which is `UUID.TryParse(channel)` and returns null when it fails
 *      (A2ACalls.cs). A room key minted here NEVER parses as a bare UUID: it
 *      always carries the "nsv1:<tag>:" prefix. So no conference room key can ever be mistaken for
 *      an A2A channel, and no A2A channel can ever equal a room key. This holds regardless of hash
 *      behaviour. (The reverse structural claim one might reach for -- that XOR-derived A2A ids have
 *      a fixed version nibble -- is false here: LLUUID::generate runs the whole
 *      16 bytes through HBXXH128 (lluuid.cpp:850), so viewer ids carry no RFC-4122 version or
 *      variant bits at all. The prefix is what does the work.)
 *
 *  (2) AGAINST SPATIAL ROOMS -- STRUCTURAL. A spatial room is not keyed by any channel string: its
 *      number is hashed from (regionId, "local", parcelLocalID) and the "local" arm never reads
 *      pChannelID at all (JanusAudioBridge.cs). A room key is only ever presented under a
 *      non-"local" channel type, so it cannot enter that arm and cannot alias a parcel's room by
 *      construction of the hash inputs.
 *
 *  (3) AGAINST ANY ROOM AT THE MIXER'S NUMBER LAYER -- NOT eliminated, and I will not claim it is.
 *      The mixer's room identity is a positive int: FoldHashToRoom(djb2(...).GetHashCode())
 *      (JanusAudioBridge.cs), a ~2^31 space shared by spatial, A2A and anything added
 *      later. Two distinct inputs can fold to the same number; at ~46k concurrent rooms that is a
 *      coin flip. This risk PRE-DATES this engine and is shared equally by spatial and A2A today.
 *      What (1) and (2) buy is the removal of SYSTEMATIC aliasing -- distinct input tuples, no
 *      shared key space -- not the removal of birthday collisions. The residual is detectable
 *      rather than silent: the bridge stamps a roomDesc of regionId/channelType/parcel/channelID on
 *      create and treats "already exists" as success, so an aliased room is a room
 *      whose description does not match what this voice session expects. ExpectedRoomDescription below
 *      gives the engine the string to compare when room allocation is wired; closing the residual
 *      properly means widening the mixer's room number, which is a separate change.
 */
using System;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public static class NonSpatialRoomKey
    {
        /// <summary>
        /// Namespace for the room-key digest. A fixed, arbitrary constant whose only job is to keep
        /// this derivation from ever coinciding with some other SHA-256 of the same tuple.
        /// </summary>
        public const string Namespace = "urn:opensim:webrtc-voice:nonspatial-room:v1";

        /// <summary>The prefix that makes a room key structurally un-parseable as a bare UUID (layer 1).</summary>
        public const string Prefix = "nsv1";

        private const char Sep = '\u001F';

        public static string Tag(NonSpatialSessionType type)
        {
            switch (type)
            {
                case NonSpatialSessionType.P2P: return "p2p";
                case NonSpatialSessionType.Adhoc: return "adhoc";
                case NonSpatialSessionType.Group: return "group";
                default: throw new ArgumentOutOfRangeException(nameof(type), type, "unknown non-spatial session type");
            }
        }

        /// <summary>
        /// The media room key for this voice session. Deterministic across instances and processes for a
        /// given (gridId, type, sessionId), so two regions resolve the same room with no coordination.
        /// </summary>
        public static string Derive(string gridId, NonSpatialSessionType type, UUID sessionId)
        {
            string tag = Tag(type);
            string material = Namespace + Sep + (gridId ?? string.Empty) + Sep + tag + Sep + sessionId.ToString();
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            byte[] sixteen = new byte[16];
            Buffer.BlockCopy(digest, 0, sixteen, 0, 16);
            return Prefix + ":" + tag + ":" + new UUID(sixteen, 0).ToString();
        }

        /// <summary>
        /// True iff <paramref name="candidate"/> was minted here. Cheap guard for a future admission
        /// path so a conference room key is never fed to the A2A arm and vice versa.
        /// </summary>
        public static bool IsRoomKey(string candidate)
            => !string.IsNullOrEmpty(candidate) && candidate.StartsWith(Prefix + ":", StringComparison.Ordinal);

        /// <summary>
        /// What the mixer's room description should read for the room of this voice session, given the channel
        /// type it is presented under. Matches JanusAudioBridge.SelectRoom's roomDesc shape
        /// so an aliased room number is detectable rather than silent (layer 3 above).
        /// </summary>
        public static string ExpectedRoomDescription(UUID regionId, string channelType, string roomKey)
            => regionId.ToString() + "/" + channelType + "/0/" + roomKey;
    }
}
