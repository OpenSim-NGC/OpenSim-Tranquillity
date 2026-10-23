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

using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice;

// Room visibility authority and the join capability.
public partial class JanusAudioBridge
{
    /// <summary>Whether spatial "local" rooms are declared as having a sim authority.</summary>
    public bool DeclareVisAuthority { get; }

    /// <summary>([WebRtcVoice] JoinCapabilityEnabled, default false): mint a join capability for every join
    /// this bridge performs. False sends the join body without one.</summary>
    public bool JoinCapabilityEnabled { get; set; }

    /// <summary>([JanusWebRtcVoice] JoinCapabilitySecret): the HMAC key shared with the mixer's
    /// JS_JOIN_CAP_SECRET, and never the same value as the API token. Never logged; empty mints nothing.</summary>
    public string JoinCapabilitySecret { get; set; } = string.Empty;

    /// <summary>Only spatial "local" rooms are declared. Agent-to-agent "multiagent" rooms get no visibility batches,
    /// so declaring them would silence every call once fail-closed is enabled.</summary>
    public static bool ShouldDeclareVisAuthority(bool pDeclare, string pChannelType)
        => pDeclare && string.Equals(pChannelType, "local", StringComparison.Ordinal);
}
