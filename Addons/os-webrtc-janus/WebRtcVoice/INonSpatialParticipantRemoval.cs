/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// Takes an agent's participants out of a call's room, for a voice service that can: the participants this process
/// joined are closed with the viewer session's own teardown, and on the stock AudioBridge the ones another region
/// server joined are kicked by the id the service set when it joined them.
/// </summary>
public interface INonSpatialParticipantRemoval
{
    /// <summary>
    /// Drop every participant of the agent in the room with this number. Returns how many were dropped here or
    /// kicked. Zero is not an error: the agent may have no participant in the room (or, on the slvoice mixer, only
    /// one that another region server joined, which that server drops when it learns of the removal).
    /// </summary>
    int DropCallParticipants(UUID pAgentID, int pRoomNumber);
}
