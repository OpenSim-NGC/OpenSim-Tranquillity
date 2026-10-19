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

using OpenSim.Region.Framework.Interfaces;

using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

// STUN servers advertised to viewers as a simulator feature.
public partial class WebRtcVoiceRegionModule
{
    // Comma-separated STUN URIs advertised to viewers as SimulatorFeatures
    // "stun-servers"; empty => the key is not emitted.
    private string m_StunServers = string.Empty;

    // Advertise STUN servers to viewers so their WebRTC ICE config is non-empty.
    // The viewer's OpenSim path reads SimulatorFeatures["stun-servers"] as a
    // comma-separated string of full ICE URIs (llviewerregion.cpp). Absent config
    // => omit the key. Stock viewers REQUIRE a non-empty valid entry or
    // CreatePeerConnection fails "ICE server parsing failed: Empty uri".
    private void AdvertiseStunServers(ISimulatorFeaturesModule simFeatures)
    {
        if (!string.IsNullOrWhiteSpace(m_StunServers))
        {
            simFeatures?.AddFeature("stun-servers", OSD.FromString(m_StunServers));
        }
    }

    // Reads the STUN servers advertised to viewers.
    private void InitialiseStunServers()
    {
        m_StunServers = m_Config.GetString("StunServers", string.Empty);
    }
}
