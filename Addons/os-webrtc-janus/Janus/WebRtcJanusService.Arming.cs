/*
 * Copyright (c) 2026 Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;

using OpenSim.Framework;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// Room visibility authority and the join capability, both off unless configured.
public partial class WebRtcJanusService
{
    // Create spatial "local" rooms with vis_authority=true. True only when the sim arms: [WebRtcVoice]
    // VisibilityArmingEnabled (default false) with the feeder and emission on (both default false), resolved
    // exactly as WebRtcVoiceRegionModule resolves them.
    private bool _DeclareVisAuthority = false;

    // Mint a join capability for every join. Off by default; the key lives beside the other mixer credentials in
    // [JanusWebRtcVoice] and is never logged.
    private bool _JoinCapabilityEnabled = false;

    private string _JoinCapabilitySecret = string.Empty;

    // Reads whether rooms are declared with a visibility authority and whether joins carry a capability.
    private void ReadArmingConfig(IConfig webRtcVoiceConfig, IConfig janusConfig)
    {
        _DeclareVisAuthority = webRtcVoiceConfig.GetBoolean("VisibilityArmingEnabled", false)
            && webRtcVoiceConfig.GetBoolean("VisibilityFeederEnabled", false)
            && webRtcVoiceConfig.GetBoolean("VisibilityEmitEnabled", false);
        if (_DeclareVisAuthority)
            _log.LogInformation($"{LogHeader} spatial \"local\" rooms are created with vis_authority=true ([WebRtcVoice] VisibilityArmingEnabled)");
        // Enabling the capability without a secret mints nothing, so say so and stay off rather than sending half a
        // capability on every join.
        _JoinCapabilityEnabled = webRtcVoiceConfig.GetBoolean("JoinCapabilityEnabled", false);
        _JoinCapabilitySecret = janusConfig.GetString("JoinCapabilitySecret", string.Empty);
        if (_JoinCapabilityEnabled && string.IsNullOrEmpty(_JoinCapabilitySecret))
        {
            _log.LogError($"{LogHeader} [WebRtcVoice] JoinCapabilityEnabled is true but [JanusWebRtcVoice] JoinCapabilitySecret is empty: no capability can be minted, so joins carry none");
            _JoinCapabilityEnabled = false;
        }
        else if (_JoinCapabilityEnabled && _JoinCapabilitySecret.Length < MinSecretLength)
        {
            // A capability is an HMAC over a known structure: anyone who sees one can test candidate keys offline,
            // so a short key must not be used. Named by key only; the value and its length are not logged.
            _log.LogError($"{LogHeader} [JanusWebRtcVoice] JoinCapabilitySecret is shorter than {MinSecretLength} characters: refusing to mint capabilities with it, so joins carry none");
            _JoinCapabilityEnabled = false;
        }
        else if (_JoinCapabilityEnabled)
            _log.LogInformation($"{LogHeader} join capability enabled: every join carries one, {JoinCapability.LifetimeSeconds} s lifetime ([WebRtcVoice] JoinCapabilityEnabled)");
    }
}
