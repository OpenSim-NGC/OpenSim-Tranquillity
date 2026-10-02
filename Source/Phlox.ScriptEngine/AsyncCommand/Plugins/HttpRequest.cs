/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
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

// Ported from Halcyon/InWorldz to this engine
// Adaptations:
//   - HttpRequestObject replaced with IHttpServiceRequest interface (no concrete cast)
//   - Uses PostObjectEvent by LocalID; another engine's response goes through the region's other engines

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins
{
    public class HttpRequest
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public AsyncCommandManager m_CmdManager;

        public HttpRequest(AsyncCommandManager CmdManager)
        {
            m_CmdManager = CmdManager;
        }

        public void CheckHttpRequests()
        {
            if (m_CmdManager.m_ScriptEngine.World == null)
                return;

            IHttpRequestModule iHttpReq =
                m_CmdManager.m_ScriptEngine.World.RequestModuleInterface<IHttpRequestModule>();

            if (iHttpReq == null)
                return;

            // Use the IHttpServiceRequest interface only: HttpRequestClass is defined in
            // OpenSim.Region.CoreModules, which develop's McMaster-based plugin loader may
            // load into a different AssemblyLoadContext than this assembly, making a direct
            // cast to the concrete type fail with an InvalidCastException even though the
            // type name matches. The interface exposes Status/ResponseBody safely.
            IHttpServiceRequest req = iHttpReq.GetNextCompletedRequest();
            while (req != null)
            {
                iHttpReq.RemoveCompletedRequest(req.ReqID);

                switch (Complete(req))
                {
                    case Owner.Dropped:
                        // The script that asked is gone or was reset since - dropped here, never queued.
                        System.Threading.Interlocked.Increment(ref m_Dropped);
                        if (m_log.IsEnabled(LogLevel.Debug))
                            m_log.LogDebug("[Phlox HTTP]: late http_response {0} for {1} dropped (script reset or gone)", req.ReqID, req.ItemID);
                        break;

                    default:
                    {
                        // SL - "triggered in all scripts in the prim, not just in the requesting script". Phlox's
                        // own request or another engine's (the core's one completed queue is drained by every
                        // engine's pump), every script in the prim gets it: Phlox's here, each other engine's through it.
                        object[] resobj = new object[]
                        {
                            req.ReqID.ToString(),
                            req.Status,
                            new object[0],   // metadata — HTTP_BODY_TRUNCATED not implemented
                            req.ResponseBody
                        };

                        bool posted = m_CmdManager.m_ScriptEngine.PostObjectEvent(req.LocalID,
                            new EventParams("http_response", resobj, Array.Empty<DetectParams>()));

                        if (m_log.IsEnabled(LogLevel.Debug))
                            m_log.LogDebug("[Phlox HTTP]: http_response {0} status {1} -> prim {2} (accepted={3})",
                                req.ReqID, resobj[1], req.LocalID, posted);
                        PostToOtherEngines(req);
                        break;
                    }
                }

                req = iHttpReq.GetNextCompletedRequest();
            }
        }

        /// <summary>
        /// The region's other script engines get the response for their scripts in the prim, each engine once, as
        /// the core's pump (OpenSim.Region.ScriptEngine.Shared/Api/Plugins/HttpRequest.cs) offers what it takes: the same
        /// arguments (LSLString id, LSLInteger status, empty list, LSLString body), built for each engine, and no stopping
        /// at the first that takes it - each engine posts only to its own scripts. Phlox is left out: it had it above.
        /// </summary>
        private void PostToOtherEngines(IHttpServiceRequest req)
        {
            int offered = 0;
            IScriptModule[] engines = m_CmdManager.m_ScriptEngine.World?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>();
            var seen = new List<IScriptEngine>();
            foreach (IScriptModule m in engines)
            {
                if (ReferenceEquals(m, m_CmdManager.m_ScriptEngine) || m is not IScriptEngine e || seen.Contains(e))
                    continue;
                seen.Add(e);
                object[] resobj = new object[]
                {
                    new LSL_Types.LSLString(req.ReqID.ToString()),
                    new LSL_Types.LSLInteger(req.Status),
                    new LSL_Types.list(),
                    new LSL_Types.LSLString(req.ResponseBody)
                };
                e.PostObjectEvent(req.LocalID, new EventParams("http_response", resobj, new DetectParams[0]));
                offered++;
            }

            if (m_log.IsEnabled(LogLevel.Debug))
                m_log.LogDebug("[Phlox HTTP]: http_response {0} for script {1} offered to {2} other engine(s), prim {3}",
                    req.ReqID, req.ItemID, offered, req.LocalID);
        }

        // ── Requests belong to the script that made them ──
        //
        // The core stops a script's PENDING requests (HttpRequestModule.StopHttpRequest), but one that has already
        // completed stays in its completed queue and would still be posted. So Phlox keeps the ids of its scripts'
        // outstanding requests; a reset or removal forgets them, and a response whose id is no longer here is dropped
        // when its script is a Phlox script (it was reset) or is no longer in the prim (it was deleted). Anything else
        // is another engine's request that this pump happened to take; it is posted through that engine.
        //
        // Another engine's pump can take a Phlox request's response and offer it to Phlox (PostObjectEvent). It
        // carries no script id, so the ids a reset or removal forgets are kept a while (m_Forgotten) and such an offer is
        // dropped too (Offered). A forgotten id is kept 10 minutes: the core times a request out long before that
        // (ScriptsHttpRequestModule, 30 s by default) and a completed one waits one pump pass.

        private static readonly TimeSpan ForgottenKept = TimeSpan.FromMinutes(10);
        private readonly object m_TrackLock = new object();
        private readonly Dictionary<UUID, (UUID Item, UUID Object)> m_Outstanding = new();   // request id -> script item, object
        private readonly Dictionary<UUID, DateTime> m_Forgotten = new();   // request id -> when its script was reset or removed
        private long m_Dropped;

        // Halcyon's in-flight caps, ScriptsHttpRequests.cs:88-99 - MAX_SINGLE_OBJECT_QUEUE_SIZE
        // (per object, keyed by the object group, :373-376) and MAX_REQUEST_QUEUE_SIZE (per region, :271-272). A request is
        // in flight from its start until its response is taken or its script is reset or removed. The region count is
        // this engine's (Phlox scripts'); the core keeps no count Phlox can read. The core's rate limit is separate.
        internal const int MaxInFlightPerObject = 10;
        internal const int MaxInFlightPerRegion = 200;

        /// <summary>
        /// llHTTPRequest: start the request and record it in one step. The core can complete a request before
        /// StartHttpRequest returns (a filtered URL), and the pump must not see it untracked. Refused, without
        /// starting, when the object already has <see cref="MaxInFlightPerObject"/> requests in flight or the region
        /// <see cref="MaxInFlightPerRegion"/> (<paramref name="capped"/> true, NULL_KEY). Only while the engine's
        /// [InWorldz.Phlox] HttpInFlightThrottle is on (default true); with it off nothing is capped.
        /// </summary>
        internal UUID Start(UUID itemID, UUID objectID, Func<UUID> start, out bool capped)
        {
            bool enforceCaps = (m_CmdManager.m_ScriptEngine as global::Phlox.ScriptEngine.PhloxEngine)?.HttpInFlightThrottle ?? true;
            lock (m_TrackLock)
            {
                capped = enforceCaps
                    && (m_Outstanding.Count >= MaxInFlightPerRegion || InFlightFor(objectID) >= MaxInFlightPerObject);
                if (capped) return UUID.Zero;
                UUID reqID = start();
                if (!reqID.IsZero()) m_Outstanding[reqID] = (itemID, objectID);
                return reqID;
            }
        }

        private int InFlightFor(UUID objectID)
        {
            int n = 0;
            foreach (var v in m_Outstanding.Values)
                if (v.Object == objectID) n++;
            return n;
        }

        private enum Owner { Phlox, OtherEngine, Dropped }

        /// <summary>
        /// Whose response this is: a live request of a Phlox script, a request of a script another engine runs,
        /// or one to drop (a Phlox script reset since it asked, or a script or prim that is gone).
        /// </summary>
        private Owner Complete(IHttpServiceRequest req)
        {
            lock (m_TrackLock)
            {
                if (m_Outstanding.Remove(req.ReqID)) return Owner.Phlox;
                if (m_Forgotten.Remove(req.ReqID)) return Owner.Dropped;   // its Phlox script was reset or removed since
            }

            if (m_CmdManager.m_ScriptEngine is global::Phlox.ScriptEngine.PhloxEngine phlox && phlox.HasOrIsLoading(req.ItemID))
                return Owner.Dropped;   // a Phlox script that has been reset since it asked
            SceneObjectPart part = m_CmdManager.m_ScriptEngine.World?.GetSceneObjectPart(req.LocalID);
            return part?.Inventory?.GetInventoryItem(req.ItemID) != null
                ? Owner.OtherEngine
                : Owner.Dropped;   // gone with its script or prim
        }

        /// <summary>
        /// The script is reset or removed. Its requests are forgotten and the core stops the ones still in flight
        /// (Halcyon AsyncCommandManager.RemoveScript: iHttpReq.StopHttpRequest(localID, itemID)).
        /// </summary>
        public void RemoveEvents(uint localID, OpenMetaverse.UUID itemID)
        {
            lock (m_TrackLock)
            {
                List<UUID> mine = null;
                foreach (var kvp in m_Outstanding)
                    if (kvp.Value.Item == itemID) (mine ??= new List<UUID>()).Add(kvp.Key);
                DateTime now = DateTime.UtcNow;
                if (mine != null)
                    foreach (UUID id in mine)
                    {
                        m_Outstanding.Remove(id);
                        m_Forgotten[id] = now;
                    }
                if (m_Forgotten.Count > 0)
                {
                    List<UUID> old = null;
                    foreach (var kvp in m_Forgotten)
                        if (now - kvp.Value > ForgottenKept) (old ??= new List<UUID>()).Add(kvp.Key);
                    if (old != null)
                        foreach (UUID id in old) m_Forgotten.Remove(id);
                }
            }
            m_CmdManager.m_ScriptEngine.World?.RequestModuleInterface<IHttpRequestModule>()?.StopHttpRequest(localID, itemID);
        }

        /// <summary>
        /// An http_response offered to Phlox's PostObjectEvent - by this pump after Complete, or by another engine's
        /// pump that took it. False when it is a Phlox request whose script was reset or removed since (dropped).
        /// A live Phlox request is no longer outstanding: another pump delivered it.
        /// </summary>
        internal bool Offered(object requestID)
        {
            if (requestID == null || !UUID.TryParse(requestID.ToString(), out UUID id)) return true;
            lock (m_TrackLock)
            {
                if (m_Forgotten.Remove(id))
                {
                    System.Threading.Interlocked.Increment(ref m_Dropped);
                    return false;
                }
                m_Outstanding.Remove(id);
            }
            return true;
        }

        internal int OutstandingCount { get { lock (m_TrackLock) return m_Outstanding.Count; } }
        internal long DroppedResponses => System.Threading.Interlocked.Read(ref m_Dropped);
    }
}
