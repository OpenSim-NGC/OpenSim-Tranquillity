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
    public class MuteListServicesConnector : IMuteListService
    {
        private const string _section = "MuteListService";
        private const string _uriName = "MuteListServerURI";

        private readonly IConfiguration _configuration;
        private readonly ILogger<MuteListServicesConnector> _logger;
        private IServiceAuth _auth = null;
        private readonly string _serverURI = String.Empty;

        public MuteListServicesConnector(IConfiguration configuration, ILogger<MuteListServicesConnector> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _auth = ServiceAuth.Create(_configuration, _section);
            var serviceURI = ServiceURI.LookupServiceURI(_configuration, _section, _uriName);
            
            _serverURI = serviceURI + "/mutelist";
        }

        #region IMuteListService
        public Byte[] MuteListRequest(UUID agentID, uint crc)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["METHOD"] = "get";
            sendData["agentid"] = agentID.ToString();
            sendData["mutecrc"] = crc.ToString();

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", _serverURI,
                                    ServerUtils.BuildQueryString(sendData), _auth);
                if (reply != string.Empty)
                {
                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                    if (replyData.ContainsKey("result"))
                    {
                        string datastr = replyData["result"].ToString();
                        if (String.IsNullOrWhiteSpace(datastr))
                            return null;

                        return Convert.FromBase64String(datastr);
                    }
                    else
                    {
                        _logger.LogDebug("[MUTELIST CONNECTOR]: get reply data does not contain result field");
                    }
                }
                else
                {
                    _logger.LogDebug("[MUTELIST CONNECTOR]: get received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[MUTELIST CONNECTOR]: Exception when contacting server at {_serverURI}: {e.Message}");
            }

            return null;
        }

        public bool UpdateMute(MuteData mute)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["METHOD"] = "update";
            sendData["agentid"] = mute.AgentID.ToString();
            sendData["muteid"] = mute.MuteID.ToString();
            if(mute.MuteType != 0)
                sendData["mutetype"] = mute.MuteType.ToString();
            if(mute.MuteFlags != 0)
                sendData["muteflags"] = mute.MuteFlags.ToString();
            sendData["mutestamp"] = mute.Stamp.ToString();
            if(!String.IsNullOrEmpty(mute.MuteName))
                sendData["mutename"] = mute.MuteName;

            return doSimplePost(ServerUtils.BuildQueryString(sendData), "update");
         }

        public bool RemoveMute(UUID agentID, UUID muteID, string muteName)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["METHOD"] = "delete";
            sendData["agentid"] = agentID.ToString();
            sendData["muteid"] = muteID.ToString();
            if(!String.IsNullOrEmpty(muteName))
                sendData["mutename"] = muteName;

            return doSimplePost(ServerUtils.BuildQueryString(sendData), "remove");
        }

        #endregion IMuteListService

        private bool doSimplePost(string reqString, string meth)
        {
            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", _serverURI, reqString, _auth);
                if (reply != string.Empty)
                {
                    int indx = reply.IndexOf("success", StringComparison.InvariantCultureIgnoreCase);
                    if (indx > 0)
                        return true;
                    return false;
                }
                else
                {
                    _logger.LogDebug($"[MUTELIST CONNECTOR]: {meth} received empty reply");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[MUTELIST CONNECTOR]: Exception when contacting server at {_serverURI}: {e.Message}");
            }

            return false;
        }
    }
}
