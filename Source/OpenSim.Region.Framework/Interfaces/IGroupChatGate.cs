/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// An optional check on a line of group chat, asked before the groups module delivers it. A module that moderates
/// group chat registers this as a scene module interface; when none is registered the groups module delivers every
/// line exactly as it does without the check.
/// </summary>
public interface IGroupChatGate
{
    /// <summary>
    /// May this agent's line of text go to the group's chat session? False drops the line before delivery.
    /// Asked for each line the agent's own client sends.
    /// </summary>
    bool MaySend(UUID groupID, UUID senderID);
}
