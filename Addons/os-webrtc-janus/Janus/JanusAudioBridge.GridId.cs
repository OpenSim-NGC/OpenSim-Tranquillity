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

// The grid identity folded into non-spatial room numbers.
public partial class JanusAudioBridge
{
    /// <summary>The grid id folded into every "multiagent" room number by <see cref="SelectRoom"/>.</summary>
    public string GridId { get; }

    /// <summary>
    /// The grid identifier for room derivation: the region's GatekeeperURI, read through the
    /// same section chain GridInfo uses for Scene.SceneGridInfo ([Const] / [Startup] / [Hypergrid],
    /// then [GatekeeperService] ExternalName, then [GridService] Gatekeeper), so it is the identity
    /// the region already presents on Hypergrid. Stable across restarts and identical for every
    /// region of the grid because it comes from the shared grid config; different per grid by
    /// construction. Normalised (trim, lower-case, no trailing slash) because the value is a hash
    /// input and two regions must not disagree over a spelling. No DNS resolution here (GridInfo
    /// resolves and throws; this must not take the voice service down). Empty when unconfigured.
    /// </summary>
    public static string ReadGridId(IConfigSource pConfig)
    {
        if (pConfig is null)
            return string.Empty;
        string[] sections = ["Const", "Startup", "Hypergrid"];
        string gatekeeper = Util.GetConfigVarFromSections<string>(pConfig, "GatekeeperURI", sections, string.Empty);
        if (string.IsNullOrEmpty(gatekeeper))
            gatekeeper = pConfig.Configs["GatekeeperService"]?.GetString("ExternalName", string.Empty);
        if (string.IsNullOrEmpty(gatekeeper))
            gatekeeper = pConfig.Configs["GridService"]?.GetString("Gatekeeper", string.Empty);
        if (string.IsNullOrEmpty(gatekeeper))
            return string.Empty;
        return gatekeeper.Trim().TrimEnd('/').ToLowerInvariant();
    }
}
