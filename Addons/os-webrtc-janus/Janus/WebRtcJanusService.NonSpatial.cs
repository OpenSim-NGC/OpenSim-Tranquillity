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

// The grid identity folded into non-spatial room numbers.
public partial class WebRtcJanusService
{
    // The grid's identity folded into every non-spatial room number so
    // two grids on a shared mixer cannot collide on the same channel. Read once from the region's
    // own config (GatekeeperURI, the same chain GridInfo uses); empty when the grid has none.
    private string _GridId = string.Empty;

    // Reads the grid id folded into non-spatial room numbers.
    private void ReadGridIdConfig()
    {
        _GridId = JanusAudioBridge.ReadGridId(_Config);
        if (!string.IsNullOrEmpty(_GridId))
            _log.LogInformation($"{LogHeader} grid id for multiagent rooms = {_GridId}");
        else if (NonSpatialVoiceConfigured())
            _log.LogWarning($"{LogHeader} no GatekeeperURI in [Hypergrid]/[Startup]/[Const] (nor [GatekeeperService] ExternalName / [GridService] Gatekeeper): multiagent rooms are derived without a grid id");
    }

    // True when a call feature is switched on in [WebRtcVoice]. Only then does a missing grid id matter, so only
    // then is it warned about.
    private bool NonSpatialVoiceConfigured()
    {
        IConfig voice = _Config?.Configs["WebRtcVoice"];
        return voice is not null
            && (voice.GetBoolean("A2AVoiceEnabled", false)
                || voice.GetBoolean("GroupVoiceEnabled", false)
                || voice.GetBoolean("AdhocVoiceEnabled", false));
    }
}
