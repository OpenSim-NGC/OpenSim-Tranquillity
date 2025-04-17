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
using OpenSim.Framework.ServiceAuth;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Services.Connectors
{
    public class PresenceServicesConnector : IPresenceService
    {
        private const string _section = "PresenceService";
        private const string _uriName = "PresenceServerURI";

        private readonly IConfiguration _configuration;
        private readonly ILogger<PresenceServicesConnector> _logger;
        private IServiceAuth _auth = null;

        private readonly string _serverURI = String.Empty;

        public PresenceServicesConnector(
            IConfiguration configuration,
            ILogger<PresenceServicesConnector> logger
            )
        {
            _configuration = configuration;
            _logger = logger;

            _auth = ServiceAuth.Create(_configuration, _section);
            _serverURI = ServiceURI.LookupServiceURI(_configuration, _section, _uriName);
        }

        #region IPresenceService

        public bool LoginAgent(string userID, UUID sessionID, UUID secureSessionID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            //sendData["SCOPEID"] = scopeID.ToString();
            sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
            sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
            sendData["METHOD"] = "login";

            sendData["UserID"] = userID;
            sendData["SessionID"] = sessionID.ToString();
            sendData["SecureSessionID"] = secureSessionID.ToString();

            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/presence";

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
                    _logger.LogDebug("[PRESENCE CONNECTOR]: LoginAgent received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[PRESENCE CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
            }

            return false;

        }

        public bool LogoutAgent(UUID sessionID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            //sendData["SCOPEID"] = scopeID.ToString();
            sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
            sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
            sendData["METHOD"] = "logout";

            sendData["SessionID"] = sessionID.ToString();

            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/presence";

            // _logger.LogDebug("[PRESENCE CONNECTOR]: queryString = {0}", reqString);
            
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
                    _logger.LogDebug("[PRESENCE CONNECTOR]: LogoutAgent received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[PRESENCE CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
            }

            return false;
        }

        public bool LogoutRegionAgents(UUID regionID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            //sendData["SCOPEID"] = scopeID.ToString();
            sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
            sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
            sendData["METHOD"] = "logoutregion";

            sendData["RegionID"] = regionID.ToString();

            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/presence";
            // _logger.LogDebug("[PRESENCE CONNECTOR]: queryString = {0}", reqString);
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
                    _logger.LogDebug("[PRESENCE CONNECTOR]: LogoutRegionAgents received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[PRESENCE CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
            }

            return false;
        }

        public bool ReportAgent(UUID sessionID, UUID regionID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            //sendData["SCOPEID"] = scopeID.ToString();
            sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
            sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
            sendData["METHOD"] = "report";

            sendData["SessionID"] = sessionID.ToString();
            sendData["RegionID"] = regionID.ToString();

            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/presence";

            // _logger.LogDebug("[PRESENCE CONNECTOR]: queryString = {0}", reqString);
            
            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (reply != string.Empty)
                {
                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                    if (replyData.ContainsKey("result"))
                    {
                        if (replyData["result"].ToString().ToLower() == "success")
                            return true;

                        return false;
                    }
                    else
                    {
                        _logger.LogDebug("[PRESENCE CONNECTOR]: ReportAgent reply data does not contain result field");
                    }

                }
                else
                {
                    _logger.LogDebug("[PRESENCE CONNECTOR]: ReportAgent received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[PRESENCE CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
            }

            return false;
        }

        public PresenceInfo GetAgent(UUID sessionID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            //sendData["SCOPEID"] = scopeID.ToString();
            sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
            sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
            sendData["METHOD"] = "getagent";

            sendData["SessionID"] = sessionID.ToString();

            string reply = string.Empty;
            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/presence";
            // _logger.LogDebug("[PRESENCE CONNECTOR]: queryString = {0}", reqString);

            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (string.IsNullOrEmpty(reply))
                {
                    _logger.LogDebug("[PRESENCE CONNECTOR]: GetAgent received null or empty reply");
                    return null;
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[PRESENCE CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
                return null;
            }

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
            PresenceInfo pinfo = null;

            if ((replyData != null) && replyData.ContainsKey("result") && (replyData["result"] != null))
            {
                if (replyData["result"] is Dictionary<string, object>)
                {
                    pinfo = new PresenceInfo((Dictionary<string, object>)replyData["result"]);
                }
                else
                {
                    if (replyData["result"].ToString() == "null")
                        return null;

                    _logger.LogDebug($"[PRESENCE CONNECTOR]: Invalid reply (result not dictionary) received from presence server when querying for sessionID {sessionID.ToString()}");
                }
            }
            else
            {
                _logger.LogDebug($"[PRESENCE CONNECTOR]: Invalid reply received from presence server when querying for sessionID {sessionID.ToString()}");
            }

            return pinfo;
        }

        public PresenceInfo[] GetAgents(string[] userIDs)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            //sendData["SCOPEID"] = scopeID.ToString();
            sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
            sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
            sendData["METHOD"] = "getagents";

            sendData["uuids"] = new List<string>(userIDs);

            string reply = string.Empty;
            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/presence";
            //_logger.LogDebug("[PRESENCE CONNECTOR]: queryString = {0}", reqString);

            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (string.IsNullOrEmpty(reply))
                {
                    _logger.LogDebug("[PRESENCE CONNECTOR]: GetAgents received null or empty reply");
                    return null;
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[PRESENCE CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
            }

            List<PresenceInfo> rinfos = new List<PresenceInfo>();

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

            if (replyData != null)
            {
                if (replyData.ContainsKey("result") &&
                    (replyData["result"].ToString() == "null" || replyData["result"].ToString() == "Failure"))
                {
                    return new PresenceInfo[0];
                }

                Dictionary<string, object>.ValueCollection pinfosList = replyData.Values;
                //_logger.LogDebug("[PRESENCE CONNECTOR]: GetAgents returned {0} elements", pinfosList.Count);
                foreach (object presence in pinfosList)
                {
                    if (presence is Dictionary<string, object>)
                    {
                        PresenceInfo pinfo = new PresenceInfo((Dictionary<string, object>)presence);
                        rinfos.Add(pinfo);
                    }
                    else
                    {
                        _logger.LogDebug($"[PRESENCE CONNECTOR]: GetAgents received invalid response type {presence.GetType()}");
                    }
                }
            }
            else
            {
                _logger.LogDebug("[PRESENCE CONNECTOR]: GetAgents received null response");
            }

            return rinfos.ToArray();
        }

        #endregion

    }
}
