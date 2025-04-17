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

namespace OpenSim.Services.Connectors;

public class GridUserServicesConnector : IGridUserService
{
    protected const string _serviceName = "GridUserService";
    protected const string _serviceURI = "GridUserServerURI";

    protected readonly IConfiguration _config;
    protected readonly ILogger<GridUserServicesConnector> _logger;

    protected readonly IServiceAuth _auth;
    protected readonly string _serverURI = String.Empty;

    public GridUserServicesConnector(
        IConfiguration configuration,
        ILogger<GridUserServicesConnector> logger
        )
    {
        _config = configuration;
        _logger = logger;

        _auth = ServiceAuth.Create(configuration, _serviceName);
        var serviceURI = ServiceURI.LookupServiceURI(_config, _serviceName, _serviceURI);

        if (string.IsNullOrWhiteSpace(serviceURI))
        {
            _logger.LogError($"[GRIDUSER CONNECTOR]: GridUserServerURI not found section GridUserService");
            throw new Exception("GridUser connector init error");
        }

        OSHHTPHost tmp = new OSHHTPHost(serviceURI, true);
        if (!tmp.IsResolvedHost)
        {
            string reason = tmp.IsValidHost ? "Could not resolve GridUserServerURI" : "GridUserServerURI is a invalid host";
            _logger.LogError($"[GRIDUSER CONNECTOR]: {reason}");
            throw new Exception("User account connector init error");
        }

        _serverURI = tmp.URI;
    }

    #region IGridUserService

    public GridUserInfo LoggedIn(string userID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "loggedin";

        sendData["UserID"] = userID;

        return Get(sendData);

    }

    public bool LoggedOut(string userID, UUID sessionID, UUID region, Vector3 position, Vector3 lookat)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "loggedout";

        return Set(sendData, userID, region, position, lookat);
    }

    public bool SetHome(string userID, UUID regionID, Vector3 position, Vector3 lookAt)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "sethome";

        return Set(sendData, userID, regionID, position, lookAt);
    }

    public bool SetLastPosition(string userID, UUID sessionID, UUID regionID, Vector3 position, Vector3 lookAt)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "setposition";

        return Set(sendData, userID, regionID, position, lookAt);
    }

    public GridUserInfo GetGridUserInfo(string userID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "getgriduserinfo";

        sendData["UserID"] = userID;

        return Get(sendData);
    }

    #endregion

    protected bool Set(Dictionary<string, object> sendData, string userID, UUID regionID, Vector3 position, Vector3 lookAt)
    {
        sendData["UserID"] = userID;
        sendData["RegionID"] = regionID.ToString();
        sendData["Position"] = position.ToString();
        sendData["LookAt"] = lookAt.ToString();

        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/griduser";

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
                    else
                        return false;
                }
                else
                {
                    _logger.LogDebug($"[GRID USER CONNECTOR]: SetPosition reply data does not contain result field");
                }

            }
            else
            {
                _logger.LogDebug($"[GRID USER CONNECTOR]: SetPosition received empty reply");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, $"[GRID USER CONNECTOR]: Exception when contacting grid user server at {uri}: {e.Message}");
        }

        return false;
    }

    protected GridUserInfo Get(Dictionary<string, object> sendData)
    {
        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/griduser";

        try
        {
            string reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
            if (reply != string.Empty)
            {
                Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
                GridUserInfo guinfo = null;

                if ((replyData != null) && replyData.ContainsKey("result") && (replyData["result"] != null))
                {
                    if (replyData["result"] is Dictionary<string, object>)
                        guinfo = Create((Dictionary<string, object>)replyData["result"]);
                }

                return guinfo;

            }
            else
                _logger.LogDebug($"[GRID USER CONNECTOR]: Get received empty reply");
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, $"[GRID USER CONNECTOR]: Exception when contacting grid user server at {uri}: {e.Message}");
        }

        return null;
    }

    public GridUserInfo[] GetGridUserInfo(string[] userIDs)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "getgriduserinfos";

        sendData["AgentIDs"] = new List<string>(userIDs);

        string reply = string.Empty;
        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/griduser";

        try
        {
            reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
            if (string.IsNullOrEmpty(reply))
            {
                _logger.LogDebug($"[GRID USER CONNECTOR]: GetGridUserInfo received null or empty reply");
                return null;
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug($"[GRID USER CONNECTOR]: Exception when contacting grid user server at {uri}: {e.Message}");
        }

        List<GridUserInfo> rinfos = new List<GridUserInfo>();
        Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

        if (replyData != null)
        {
            if (replyData.ContainsKey("result") &&
                (replyData["result"].ToString() == "null" || replyData["result"].ToString() == "Failure"))
            {
                return new GridUserInfo[0];
            }

            Dictionary<string, object>.ValueCollection pinfosList = replyData.Values;

            foreach (object griduser in pinfosList)
            {
                if (griduser is Dictionary<string, object>)
                {
                    GridUserInfo pinfo = Create((Dictionary<string, object>)griduser);
                    rinfos.Add(pinfo);
                }
                else
                {
                    _logger.LogDebug($"[GRID USER CONNECTOR]: GetGridUserInfo received invalid response type {griduser.GetType()}");
                }
            }
        }
        else
        {
            _logger.LogDebug("[GRID USER CONNECTOR]: GetGridUserInfo received null response");
        }

        return rinfos.ToArray();
    }

    protected virtual GridUserInfo Create(Dictionary<string, object> griduser)
    {
        return new GridUserInfo(griduser);
    }
}
