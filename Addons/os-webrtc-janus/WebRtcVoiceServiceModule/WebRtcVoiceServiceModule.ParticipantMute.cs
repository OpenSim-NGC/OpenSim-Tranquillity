/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

// Per-participant mute: forwarded to the spatial voice service when it can do it.
public partial class WebRtcVoiceServiceModule : ISpatialParticipantMute
{
    // ISpatialParticipantMute
    public ParticipantMuteResult SetSpatialMute(UUID pSceneID, UUID pAgentID, bool pMuted)
        => m_spatialVoiceService is ISpatialParticipantMute mute
            ? mute.SetSpatialMute(pSceneID, pAgentID, pMuted)
            : ParticipantMuteResult.Unsupported;
}
