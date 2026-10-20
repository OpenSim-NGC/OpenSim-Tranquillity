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

// Room visibility authority, off unless configured.
public partial class WebRtcJanusService
{
    // Create spatial "local" rooms with vis_authority=true. True only when the sim arms: [WebRtcVoice]
    // VisibilityArmingEnabled (default false) with the feeder and emission on (both default false), resolved
    // exactly as WebRtcVoiceRegionModule resolves them.
    private bool _DeclareVisAuthority = false;

    // Reads whether rooms are declared with a visibility authority.
    private void ReadArmingConfig(IConfig webRtcVoiceConfig)
    {
        _DeclareVisAuthority = webRtcVoiceConfig.GetBoolean("VisibilityArmingEnabled", false)
            && webRtcVoiceConfig.GetBoolean("VisibilityFeederEnabled", false)
            && webRtcVoiceConfig.GetBoolean("VisibilityEmitEnabled", false);
        if (_DeclareVisAuthority)
            _log.LogInformation($"{LogHeader} spatial \"local\" rooms are created with vis_authority=true ([WebRtcVoice] VisibilityArmingEnabled)");
    }
}
