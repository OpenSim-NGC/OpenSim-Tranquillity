/*
 * Copyright (c) Contributors, http://opensimulator.org/
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
// Adapted 2026 by Legion Builds

using System.Net;

using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;

using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// Janus request hardening: the provision refusal throttle.
public partial class WebRtcVoiceRegionModule
{
    // Replays a provision refusal (403/404/501) to the viewer's immediate retries for RefusalCacheSeconds
    // without re-running the estate/parcel checks. Replaced from config in Initialise.
    private readonly RefusalEpisodeLog m_refusalLog = new RefusalEpisodeLog(m_log, logHeader + "[ProvisionVoice]:");
    private ProvisionRefusalCache m_refusalCache => m_refusalLog.Cache;

    // What one provision call decided, read back by the wrapper once the handler has answered.
    private sealed class ProvisionCallContext
    {
        public ProvisionKind? Kind;          // set once admission has decided (null: refused earlier)
        public bool AnsweredFromCache;       // the refusal cache answered this call
        public bool Provisioned;             // the voice service returned a success map (viewer_session)
    }

    // A cached region-level refusal (the exact status and body the viewer got).
    private sealed record CachedRefusal(int StatusCode, byte[] Body);

    private static bool IsCacheableRefusalStatus(int pStatusCode) =>
        pStatusCode == (int)HttpStatusCode.Forbidden || pStatusCode == (int)HttpStatusCode.NotFound
        || pStatusCode == (int)HttpStatusCode.NotImplemented;

    // Refusal throttle: the stock viewer re-provisions immediately after ANY refusal (~2 Hz live). The
    // handler below is unchanged except that, for an admitted "local" provision, a refusal recorded within
    // RefusalCacheSeconds for this (agent, region) is answered again from the cache before the estate/parcel
    // checks run. This wrapper records a fresh 403/404/501 refusal of a "local" provision, and clears the
    // entry on a successful provision or a logout. A run of refusals for one (agent, region) is one episode: one INFO
    // line for the first refusal and one with the count when the episode ends (RefusalEpisodeLog).
    public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        var ctx = new ProvisionCallContext();
        ProvisionVoiceAccountRequestCore(request, response, agentID, scene, ctx);

        UUID regionId = scene.RegionInfo.RegionID;
        if (ctx.Kind == ProvisionKind.Logout || ctx.Provisioned)
        {
            m_refusalLog.Ended(agentID, regionId, ctx.Kind == ProvisionKind.Logout ? "it logged out" : "it was admitted");
        }
        else if (ctx.Kind == ProvisionKind.Local && !ctx.AnsweredFromCache && IsCacheableRefusalStatus(response.StatusCode))
        {
            int suppressed = m_refusalCache.RecordRefusal(agentID, regionId, new CachedRefusal(response.StatusCode, response.RawBuffer));
            m_refusalLog.Refused(agentID, regionId, $" in \"{scene.Name}\"", string.Empty, suppressed);
        }
    }

    // Answers a repeat of a refusal this (agent, region) got within RefusalCacheSeconds straight from the cache.
    // Returns true when it has answered the request.
    private bool TryReplayCachedRefusal(ProvisionAdmission admission, UUID agentID, Scene scene, IOSHttpResponse response,
        ProvisionCallContext ctx)
    {
        if (admission.Kind == ProvisionKind.Local
            && m_refusalCache.TryGetRefusal(agentID, scene.RegionInfo.RegionID, out object cachedRefusal)
            && cachedRefusal is CachedRefusal cached)
        {
            ctx.AnsweredFromCache = true;
            response.RawBuffer = cached.Body;
            response.StatusCode = cached.StatusCode;
            return true;
        }
        return false;
    }

    // Reads how long a refused provision is replayed to the viewer's immediate retries.
    private void InitialiseRefusalCache()
    {
        // [WebRtcVoice] RefusalCacheSeconds (default 5; 0 disables).
        m_refusalLog.SetWindow(TimeSpan.FromSeconds(
            Math.Max(0, m_Config.GetInt("RefusalCacheSeconds", ProvisionRefusalCache.DefaultSeconds))));
        m_refusalLog.Start();
    }
}
