/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>What a request to mute or unmute one participant came to.</summary>
public enum ParticipantMuteResult
{
    /// <summary>The voice server muted (or unmuted) the participant.</summary>
    Applied,
    /// <summary>The agent has no spatial voice session in the region, or is not in its room. Nothing to change now.</summary>
    NotInRoom,
    /// <summary>The voice server does not do per-participant mute (an AudioBridge too old to have it).</summary>
    Unsupported,
    /// <summary>The voice server could not be asked, or refused for another reason.</summary>
    Failed,
}

/// <summary>
/// Per-participant mute for the voice service, for a plugin that has no per-listener visibility (Janus's stock
/// AudioBridge). A voice service that cannot do it does not implement this interface.
/// </summary>
public interface ISpatialParticipantMute
{
    /// <summary>Mute or unmute the agent's participant in its spatial voice room in the region.</summary>
    ParticipantMuteResult SetSpatialMute(UUID pSceneID, UUID pAgentID, bool pMuted);
}
