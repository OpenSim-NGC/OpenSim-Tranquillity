/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

// Per-participant mute in a call's room: forwarded to the non-spatial voice service when it can do it.
public partial class WebRtcVoiceServiceModule : INonSpatialParticipantMute
{
    // INonSpatialParticipantMute
    public ParticipantMuteResult SetCallMute(UUID pAgentID, int pRoomNumber, bool pMuted)
        => m_nonSpatialVoiceService is INonSpatialParticipantMute mute
            ? mute.SetCallMute(pAgentID, pRoomNumber, pMuted)
            : ParticipantMuteResult.Unsupported;

    // INonSpatialParticipantMute
    public bool HasCallParticipant(UUID pAgentID, int pRoomNumber)
        => m_nonSpatialVoiceService is INonSpatialParticipantMute mute && mute.HasCallParticipant(pAgentID, pRoomNumber);
}
