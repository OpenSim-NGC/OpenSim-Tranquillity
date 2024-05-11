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

using System.Collections;
using System.Net;
using System.Security;
using Nwc.XmlRpc;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Handlers.Grid
{
    public class GridInfoHandlers
    {
        private IConfiguration m_Config;
        private ILogger m_logger;

        private Dictionary<string, string> _info = new Dictionary<string, string>();
        private byte[]? cachedJsonAnswer = null;
        private byte[]? cachedRestAnswer = null;
        
        /// <summary>
        /// Instantiate a GridInfoService object.
        /// </summary>
        /// <param name="configPath">path to config path containing
        /// grid information</param>
        /// <remarks>
        /// GridInfoService uses the [GridInfo] section of the
        /// standard OpenSim.ini file --- which is not optimal, but
        /// anything else requires a general redesign of the config
        /// system.
        /// </remarks>
        public GridInfoHandlers(IConfiguration configSource, ILogger logger)
        {
            m_Config = configSource;
            m_logger = logger;

            loadGridInfo(configSource);
        }

        private void loadGridInfo(IConfiguration configSource)
        {
            _info["platform"] = "OpenSim";

            try
            {
                var gridCfg = configSource.GetSection("GridInfoService");

                if (gridCfg.Exists())
                {
                    foreach (var k in gridCfg.AsEnumerable())
                    {
                        string[] keyparts = k.Key.Split(":");
                        if ((keyparts.Length > 1) && (keyparts[0] == "GridInfoService"))
                        {
                            string? v = gridCfg.GetValue<string>(keyparts[1]);
                            if (!string.IsNullOrEmpty(v))
                                _info[keyparts[1]] = v;
                        }
                    }
                }
                else 
                {
                    var netCfg = configSource.GetSection("Network");
                    if (netCfg.Exists())
                    {
                        _info["login"] = string.Format("http://127.0.0.1:{0}/",
                            netCfg.GetValue<string>("http_listener_port", ConfigSettings.DefaultRegionHttpPort.ToString()));
                    }
                    else
                    {
                        _info["login"] = "http://127.0.0.1:9000/";
                    }

                    IssueWarning();
                }

                _info.TryGetValue("home", out string tmp);

                tmp = Util.GetConfigVarFromSections<string>(m_Config, "HomeURI",
                    new string[] { "Startup", "Hypergrid" }, tmp);

                if (string.IsNullOrEmpty(tmp))
                {
                    var logincfg = m_Config.GetSection("LoginService");
                    if (logincfg.Exists())
                        tmp = logincfg.GetValue<string>("SRV_HomeURI", tmp);
                }

                if (!string.IsNullOrEmpty(tmp))
                    _info["home"] = OSD.FromString(tmp);

                tmp = Util.GetConfigVarFromSections<string>(m_Config, "HomeURIAlias",
                    new string[] { "Startup", "Hypergrid" }, string.Empty);
                
                if (!string.IsNullOrEmpty(tmp))
                    _info["homealias"] = OSD.FromString(tmp);

                _info.TryGetValue("gatekeeper", out tmp);
                tmp = Util.GetConfigVarFromSections<string>(m_Config, "GatekeeperURI",
                    new string[] { "Startup", "Hypergrid" }, tmp);
                
                if (!string.IsNullOrEmpty(tmp))
                    _info["gatekeeper"] = OSD.FromString(tmp);

                tmp = Util.GetConfigVarFromSections<string>(m_Config, "GatekeeperURIAlias",
                    new string[] { "Startup", "Hypergrid" }, string.Empty);
                
                if (!string.IsNullOrEmpty(tmp))
                    _info["gatekeeperalias"] = OSD.FromString(tmp);

            }
            catch (Exception)
            {
                m_logger.LogWarning("[GRID INFO SERVICE]: Cannot get grid info from config source, using minimal defaults");
            }

            m_logger.LogDebug($"[GRID INFO SERVICE]: Grid info service initialized with {_info.Count} keys");
        }

        private void IssueWarning()
        {
            m_logger.LogWarning("[GRID INFO SERVICE]: found no [GridInfoService] section in your configuration files");
            m_logger.LogWarning("[GRID INFO SERVICE]: trying to guess sensible defaults, you might want to provide better ones:");

            foreach (string k in _info.Keys)
            {
                m_logger.LogWarning($"[GRID INFO SERVICE]: {k}: {_info[k]}");
            }
        }

        public XmlRpcResponse XmlRpcGridInfoMethod(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            m_logger.LogDebug("[GRID INFO SERVICE]: Request for grid info");

            foreach (KeyValuePair<string, string>  k in _info)
            {
                responseData[k.Key] = k.Value;
            }
            
            response.Value = responseData;

            return response;
        }

        public void RestGetGridInfoMethod(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            httpResponse.KeepAlive = false;
            if (httpRequest.HttpMethod != "GET")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if(cachedRestAnswer == null)
            {
                osUTF8 osb = OSUTF8Cached.Acquire();
                osb.AppendASCII("<gridinfo>");
                foreach (KeyValuePair<string, string> k in _info)
                {
                    osb.AppendASCII('<');
                    osb.AppendASCII(k.Key);
                    osb.AppendASCII('>');
                    osb.AppendASCII(SecurityElement.Escape(k.Value.ToString()));
                    osb.AppendASCII("</");
                    osb.AppendASCII(k.Key);
                    osb.AppendASCII('>');
                }
                osb.AppendASCII("</gridinfo>");
                cachedRestAnswer = OSUTF8Cached.GetArrayAndRelease(osb);
            }
            httpResponse.ContentType = "application/xml";
            httpResponse.RawBuffer = cachedRestAnswer;
        }

        /// <summary>
        /// Get GridInfo in json format: Used by the OSSL osGetGrid*
        /// Adding the SRV_HomeURI to the kvp returned for use in scripts
        /// </summary>
        /// <returns>
        /// json string
        /// </returns>
        /// </param>
        /// <param name='httpRequest'>
        /// Http request.
        /// </param>
        /// <param name='httpResponse'>
        /// Http response.
        /// </param>
        public void JsonGetGridInfoMethod(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            httpResponse.KeepAlive = false;

            if (httpRequest.HttpMethod != "GET")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (cachedJsonAnswer == null)
            {
                OSDMap map = new OSDMap();
                foreach (KeyValuePair<string, string> k in _info)
                {
                    map[k.Key] = OSD.FromString(k.Value.ToString());
                }
                cachedJsonAnswer = OSDParser.SerializeJsonToBytes(map);
            }

            httpResponse.ContentType = "application/json";
            httpResponse.RawBuffer = cachedJsonAnswer;
        }
    }
}
