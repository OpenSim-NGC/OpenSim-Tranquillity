/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the slvoice mixer is told for a call's voice mutes.
 *
 * The mixer keeps a mute set per listener (the mute channel of peer_ctl_batch): a source in a listener's set is
 * silenced for that listener and the listener's viewer is told the source is muted. So a muted agent has to be in
 * the column of every listener in the room. The sim sends the whole picture for the room at once (op "replace",
 * one column per listener, an empty column for a listener who hears everyone), which makes a send repeatable.
 *
 * A muted agent is also put in their own column. The viewer learns that it is muted from the same flag it reads for
 * the others (llspeakers.cpp, LLSpeaker::mModeratorMutedVoice), and the mixer never mixes a listener's own voice
 * back to it, so the entry costs nothing in the mix.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    public static class CallMuteColumns
    {
        /// <summary>
        /// One column per member who holds a seat. A source is in the columns when moderation silences it and it is not
        /// a moderator (<paramref name="isModerator"/>), which is never muted.
        /// </summary>
        public static Dictionary<UUID, IReadOnlyCollection<UUID>> Build(NonSpatialVoiceSession session, Func<UUID, bool> isModerator)
        {
            List<UUID> listeners = new List<UUID>();
            List<UUID> silenced = new List<UUID>();
            foreach (NonSpatialMember m in session.Members)
            {
                if (!m.HoldsSeat) continue;
                listeners.Add(m.AgentId);
                if (session.IsVoiceMuted(m.AgentId) && !isModerator(m.AgentId))
                    silenced.Add(m.AgentId);
            }
            Dictionary<UUID, IReadOnlyCollection<UUID>> columns = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (UUID l in listeners)
                columns[l] = new List<UUID>(silenced);
            return columns;
        }

        /// <summary>
        /// Reads the mixer's answer to a mute send of <paramref name="columns"/> columns. A mixer that answers nothing
        /// about the mute channel does not have one, and is reported as unable, never as having applied it.
        /// </summary>
        public static ParticipantMuteResult Classify(PeerCtlSendResult sent, JanusPeerCtlBatchSink.SlvoiceReply reply, int columns)
        {
            if (columns == 0)
                return sent == PeerCtlSendResult.Ok ? ParticipantMuteResult.Applied : ParticipantMuteResult.Failed;
            if (sent != PeerCtlSendResult.Ok)
                return ParticipantMuteResult.Failed;
            if (!reply.Present)
                return ParticipantMuteResult.Unsupported;
            if (string.Equals(reply.Reason, "unknown_room", StringComparison.Ordinal))
                return ParticipantMuteResult.NotInRoom;
            if (!string.Equals(reply.Status, "applied", StringComparison.Ordinal))
                return ParticipantMuteResult.Failed;
            return reply.MuteEntries > 0 ? ParticipantMuteResult.Applied : ParticipantMuteResult.Unsupported;
        }
    }
}
