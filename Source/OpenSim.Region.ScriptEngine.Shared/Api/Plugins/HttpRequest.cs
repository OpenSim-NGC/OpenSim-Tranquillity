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

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins;

public class HttpRequest
{
    public AsyncCommandManager m_CmdManager;

    public HttpRequest(AsyncCommandManager CmdManager)
    {
        m_CmdManager = CmdManager;
    }

    public void CheckHttpRequests()
    {
        Scene scene = m_CmdManager.m_ScriptEngine.World;
        if (scene == null)
            return;

        IHttpRequestModule iHttpReq = scene.RequestModuleInterface<IHttpRequestModule>();
        if(iHttpReq == null)
            return;

        // Use the IHttpServiceRequest interface only: HttpRequestClass is defined in
        // OpenSim.Region.CoreModules, which may be loaded into a different plugin
        // load context than this assembly, making a direct cast to the concrete
        // type fail with an InvalidCastException even though the type name matches.
        IHttpServiceRequest httpInfo = iHttpReq.GetNextCompletedRequest();
        while (httpInfo != null)
        {
            // Deliver data to prim's remote_data handler
            //
            // TODO: Returning null for metadata, since the lsl function
            // only returns the byte for HTTP_BODY_TRUNCATED, which is not
            // implemented here yet anyway.  Should be fixed if/when maxsize
            // is supported

            // The region's completed queue is drained by every script engine's pump, and this
            // pump took the response: the scripts in the prim may run in any engine of the region.
            // As in SL, every script in the prim gets it: each engine is offered it once and posts
            // it to its own scripts in that prim. The prim is in this region (the module is per
            // region), so no other region's engine is offered it: local ids are per region.
            IScriptEngine[] listed = m_CmdManager.ScriptEngines;
            foreach (IScriptEngine e in RegionScriptEngines(scene))
            {
                // Built for each engine: an engine may convert the arguments in place. The engines
                // this pump serves get the LSL_Types they always got; any other engine gets plain
                // values, as core modules post to any engine (UrlModule): string, int, object[].
                object[] resobj = Array.IndexOf(listed, e) >= 0
                    ? new object[]
                    {
                        new LSL_Types.LSLString(httpInfo.ReqID.ToString()),
                        new LSL_Types.LSLInteger(httpInfo.Status),
                        new LSL_Types.list(),
                        new LSL_Types.LSLString(httpInfo.ResponseBody)
                    }
                    : new object[]
                    {
                        httpInfo.ReqID.ToString(),
                        httpInfo.Status,
                        new object[0],
                        httpInfo.ResponseBody
                    };

                e.PostObjectEvent(httpInfo.LocalID,
                        new EventParams("http_response",
                        resobj, new DetectParams[0]));
            }
            httpInfo = iHttpReq.GetNextCompletedRequest();
        }
    }

    /// <summary>
    /// This pump's engine and the region's other script engines, each once.
    /// </summary>
    private List<IScriptEngine> RegionScriptEngines(Scene scene)
    {
        List<IScriptEngine> engines = new List<IScriptEngine> { m_CmdManager.m_ScriptEngine };
        foreach (IScriptModule m in scene.RequestModuleInterfaces<IScriptModule>())
        {
            if (m is IScriptEngine e && !engines.Contains(e))
                engines.Add(e);
        }
        return engines;
    }
}
