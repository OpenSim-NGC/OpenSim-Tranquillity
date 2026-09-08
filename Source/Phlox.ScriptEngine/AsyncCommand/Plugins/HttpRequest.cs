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

// Ported from Halcyon/InWorldz to Legion Grid (dotnet10-modernization)
// Adaptations:
//   - HttpRequestObject replaced with IHttpServiceRequest interface (no concrete cast)
//   - Uses PostObjectEvent by LocalID instead of iterating all engines

using System;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
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

                req = iHttpReq.GetNextCompletedRequest();
            }
        }

        public void RemoveEvents(uint localID, OpenMetaverse.UUID itemID)
        {
            // Handled via IHttpRequestModule.StopHttpRequest in AsyncCommandManager.RemoveScript
        }
    }
}
