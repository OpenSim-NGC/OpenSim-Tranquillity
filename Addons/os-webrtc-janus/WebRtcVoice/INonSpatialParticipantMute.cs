/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// Per-participant mute for a call's room (a group call or a conference), for a voice service whose plugin can
/// mute one participant: Janus's stock AudioBridge. A voice service that cannot does not implement this interface.
/// </summary>
public interface INonSpatialParticipantMute
{
    /// <summary>
    /// Mute or unmute the agent's participant in the mixer room with this number. NotInRoom when the agent has no
    /// participant in that room.
    /// </summary>
    ParticipantMuteResult SetCallMute(UUID pAgentID, int pRoomNumber, bool pMuted);

    /// <summary>
    /// Whether the room holds at least one participant for this agent, found by the agent id the service set when it
    /// joined them. A moderator's mute is refused, and nothing recorded, for an agent this does not find.
    /// </summary>
    bool HasCallParticipant(UUID pAgentID, int pRoomNumber);
}
