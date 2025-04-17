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

using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using Microsoft.Extensions.Configuration;
using log4net.Core;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.ServiceAuth;

namespace OpenSim.Services.Connectors.Friends
{
    public class FriendsServicesConnector : IFriendsService
    {
        protected const string _serviceName = "FriendsService";
        protected const string _uriName = "FriendsServerURI";

        protected readonly IConfiguration _config;
        protected readonly ILogger<FriendsServicesConnector> _logger;
        protected readonly IServiceAuth _auth;
        protected readonly string _serverURI;

        public FriendsServicesConnector(
            IConfiguration configuration,
            ILogger<FriendsServicesConnector> logger
            )
        {
            _config = configuration;
            _logger = logger;
            _auth = ServiceAuth.Create(configuration, _serviceName);

            var serviceURI = ServiceURI.LookupServiceURI(configuration, _serviceName, _uriName);
            if (string.IsNullOrEmpty(serviceURI))
            {
                _logger.LogError("[FRIENDS SERVICE CONNECTOR]: No Server URI named in section FriendsService");
                throw new Exception("Friends connector init error");
            }

            _serverURI = serviceURI;
        }

        #region IFriendsService

        public FriendInfo[] GetFriends(UUID PrincipalID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();

            sendData["PRINCIPALID"] = PrincipalID.ToString();
            sendData["METHOD"] = "getfriends";

            return GetFriends(sendData, PrincipalID.ToString());
        }

        public FriendInfo[] GetFriends(string PrincipalID)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();

            sendData["PRINCIPALID"] = PrincipalID;
            sendData["METHOD"] = "getfriends_string";

            return GetFriends(sendData, PrincipalID);
        }

        protected FriendInfo[] GetFriends(Dictionary<string, object> sendData, string PrincipalID)
        {
            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = _serverURI + "/friends";

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
                if (reply != string.Empty)
                {
                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                    if (replyData != null)
                    {
                        if (replyData.ContainsKey("result") && (replyData["result"].ToString().ToLower() == "null"))
                        {
                            return new FriendInfo[0];
                        }

                        List<FriendInfo> finfos = new List<FriendInfo>();
                        Dictionary<string, object>.ValueCollection finfosList = replyData.Values;

                        foreach (object f in finfosList)
                        {
                            if (f is Dictionary<string, object>)
                            {
                                FriendInfo finfo = new FriendInfo((Dictionary<string, object>)f);
                                finfos.Add(finfo);
                            }
                            else
                            {
                                _logger.LogDebug($"[FRIENDS SERVICE CONNECTOR]: GetFriends {PrincipalID} received invalid response type {f.GetType()}");
                            }
                        }

                        // Success
                        return finfos.ToArray();
                    }
                    else
                    {
                        _logger.LogDebug($"[FRIENDS SERVICE CONNECTOR]: GetFriends {PrincipalID} received null response");
                    }

                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[FRIENDS SERVICE CONNECTOR]: Exception when contacting friends server at {uri}: {e.Message}");
            }

            return new FriendInfo[0];

        }

        public bool StoreFriend(string PrincipalID, string Friend, int flags)
        {

            Dictionary<string, object> sendData = ToKeyValuePairs(PrincipalID, Friend, flags);

            sendData["METHOD"] = "storefriend";

            string reply = string.Empty;
            string uri = _serverURI + "/friends";
            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, ServerUtils.BuildQueryString(sendData), _auth);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[FRIENDS SERVICE CONNECTOR]: Exception when contacting friends server at {uri}: {e.Message}");
                return false;
            }

            if (reply != string.Empty)
            {
                Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                if ((replyData != null) && replyData.ContainsKey("Result") && (replyData["Result"] != null))
                {
                    bool success = false;
                    Boolean.TryParse(replyData["Result"].ToString(), out success);
                    return success;
                }
                else
                {
                    _logger.LogDebug("[FRIENDS SERVICE CONNECTOR]: StoreFriend {0} {1} received null response",
                        PrincipalID, Friend);
                }
            }
            else
            {
                _logger.LogDebug("[FRIENDS SERVICE CONNECTOR]: StoreFriend received null reply");
            }

            return false;
        }

        public bool Delete(string PrincipalID, string Friend)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["PRINCIPALID"] = PrincipalID.ToString();
            sendData["FRIEND"] = Friend;
            sendData["METHOD"] = "deletefriend_string";

            return Delete(sendData, PrincipalID, Friend);
        }

        public bool Delete(UUID PrincipalID, string Friend)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["PRINCIPALID"] = PrincipalID.ToString();
            sendData["FRIEND"] = Friend;
            sendData["METHOD"] = "deletefriend";

            return Delete(sendData, PrincipalID.ToString(), Friend);
        }

        public bool Delete(Dictionary<string, object> sendData, string PrincipalID, string Friend)
        {
            string reply = string.Empty;
            string uri = _serverURI + "/friends";
            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, ServerUtils.BuildQueryString(sendData), _auth);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e,$"[FRIENDS SERVICE CONNECTOR]: Exception when contacting friends server at {uri}: {e.Message}");
                return false;
            }

            if (reply != string.Empty)
            {
                Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                if ((replyData != null) && replyData.ContainsKey("Result") && (replyData["Result"] != null))
                {
                    bool success = false;
                    Boolean.TryParse(replyData["Result"].ToString(), out success);
                    return success;
                }
                else
                {
                    _logger.LogDebug($"[FRIENDS SERVICE CONNECTOR]: DeleteFriend {PrincipalID} {Friend} received null response");
                }
            }
            else
            {
                _logger.LogDebug("[FRIENDS SERVICE CONNECTOR]: DeleteFriend received null reply");
            }

            return false;
        }

        #endregion

        public Dictionary<string, object> ToKeyValuePairs(string principalID, string friend, int flags)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["PrincipalID"] = principalID;
            result["Friend"] = friend;
            result["MyFlags"] = flags;

            return result;
        }
    }
}