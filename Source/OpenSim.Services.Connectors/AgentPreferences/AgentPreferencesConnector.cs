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

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.ServiceAuth;

namespace OpenSim.Services.Connectors
{
    public class AgentPreferencesServicesConnector : IAgentPreferencesService
    {
        private const string _section = "AgentPreferencesService";
        private ILogger<AgentPreferencesServicesConnector> _logger;
        private string _serverURI = String.Empty;
        private IServiceAuth _auth = null;

        public AgentPreferencesServicesConnector(
            IConfiguration source, 
            ILogger<AgentPreferencesServicesConnector> logger)
        {
            _logger = logger;
            
            _auth = ServiceAuth.Create(source, _section);
            _serverURI = ServiceURI.LookupServiceURI(source, _section, "AgentPreferencesServerURI");
        }

        #region IAgentPreferencesService

        public AgentPrefs GetAgentPreferences(UUID principalID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();

            string reply = string.Empty;
            string uri = String.Concat(_serverURI, "/agentprefs");

            sendData["METHOD"] = "getagentprefs";
            sendData["UserID"] = principalID;
            string reqString = ServerUtils.BuildQueryString(sendData);

            _logger.LogDebug($"[AGENT PREFS CONNECTOR]: queryString = {reqString}");

            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (string.IsNullOrEmpty(reply))
                {
                    _logger.LogDebug("[AGENT PREFERENCES CONNECTOR]: GetAgentPreferences received null or empty reply");
                    return null;
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[AGENT PREFERENCES CONNECTOR]: Exception when contacting agent preferences server at {uri}: {e.Message}");
            }

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
            if (replyData != null)
            {
                if (replyData.ContainsKey("result") && 
                    (replyData["result"].ToString() == "null" || replyData["result"].ToString() == "Failure"))
                {
                    _logger.LogDebug("[AGENT PREFERENCES CONNECTOR]: GetAgentPreferences received Failure response");
                    return null;
                }
            }
            else
            {
                _logger.LogDebug("[AGENT PREFERENCES CONNECTOR]: GetAgentPreferences received null response");
                return null;
            }

            AgentPrefs prefs = new AgentPrefs(replyData);
            return prefs;
        }

        public bool StoreAgentPreferences(AgentPrefs data)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();

            sendData["METHOD"] = "setagentprefs";

            sendData["PrincipalID"] = data.PrincipalID.ToString();
            sendData["AccessPrefs"] = data.AccessPrefs;
            sendData["HoverHeight"] = data.HoverHeight.ToString();
            sendData["Language"] = data.Language;
            sendData["LanguageIsPublic"] = data.LanguageIsPublic.ToString();
            sendData["PermEveryone"] = data.PermEveryone.ToString();
            sendData["PermGroup"] = data.PermGroup.ToString();
            sendData["PermNextOwner"] = data.PermNextOwner.ToString();

            string uri = string.Concat(_serverURI, "/agentprefs");
            string reqString = ServerUtils.BuildQueryString(sendData);

            _logger.LogDebug($"[AGENT PREFS CONNECTOR]: queryString = {reqString}");

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (reply != string.Empty)
                {
                    int indx = reply.IndexOf("success", StringComparison.InvariantCultureIgnoreCase);
                    if (indx > 0)
                        return true;

                    return false;
                }
                else
                {
                    _logger.LogDebug("[AGENT PREFERENCES CONNECTOR]: StoreAgentPreferences received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug($"[AGENT PREFERENCES CONNECTOR]: Exception when contacting agent preferences server at {uri}: {e.Message}");
            }

            return false;
        }

        public string GetLang(UUID principalID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            string reply = string.Empty;

            sendData["METHOD"] = "getagentlang";
            sendData["UserID"] = principalID.ToString();

            string uri = string.Concat(_serverURI, "/agentprefs");
            string reqString = ServerUtils.BuildQueryString(sendData);

            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (string.IsNullOrEmpty(reply))
                {
                    _logger.LogDebug("[AGENT PREFERENCES CONNECTOR]: GetLang received null or empty reply");
                    return "en-us"; // I guess? Gotta return somethin'!
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[AGENT PREFERENCES CONNECTOR]: Exception when contacting agent preferences server at {uri}: {e.Message}");
            }

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
            if (replyData != null)
            {
                if (replyData.ContainsKey("result") &&
                    (replyData["result"].ToString() == "null" || replyData["result"].ToString() == "Failure"))
                {
                    _logger.LogDebug($"[AGENT PREFERENCES CONNECTOR]: GetLang received Failure response");
                    return "en-us";
                }

                if (replyData.ContainsKey("Language"))
                    return replyData["Language"].ToString();
            }
            else
            {
                _logger.LogDebug("[AGENT PREFERENCES CONNECTOR]: GetLang received null response");

            }
            return "en-us";
        }

        #endregion IAgentPreferencesService
    }
}
