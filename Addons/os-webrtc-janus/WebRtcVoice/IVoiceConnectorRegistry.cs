/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// The voice-connector policy registry, as the
/// AllowNpcVoice guard in WebRtcVoiceServiceModule sees it. Lives in the shared WebRtcVoice
/// project because the service module does not (and must not) reference the region-module
/// project; the region module registers the concrete registry per scene via
/// RegisterModuleInterface. A null interface on a scene means "no connector identities".
/// </summary>
public interface IVoiceConnectorRegistry
{
    /// <summary>True iff this agent id is a registered voice-connector NPC identity. False for every
    /// agent until a connector has been registered.</summary>
    bool IsConnectorIdentity(UUID pAgentId);
}
