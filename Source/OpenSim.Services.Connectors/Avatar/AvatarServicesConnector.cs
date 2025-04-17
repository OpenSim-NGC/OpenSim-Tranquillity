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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Services.Connectors;

public class AvatarServicesConnector : IAvatarService
{
    private const string _serviceName = "AvatarService";
    private const string _uriName = "AvatarServerURI";

    private readonly ILogger<AvatarServicesConnector> _logger;
    private readonly string _serverURI = String.Empty;
    private readonly IServiceAuth _auth = null;

    public AvatarServicesConnector(
        IConfiguration configuration,
        ILogger<AvatarServicesConnector> logger)
    {
        _logger = logger;

        _auth = ServiceAuth.Create(configuration, _serviceName);
        _serverURI = ServiceURI.LookupServiceURI(configuration, _serviceName, _uriName);
    }

    #region IAvatarService

    public AvatarAppearance GetAppearance(UUID userID)
    {
        AvatarData avatar = GetAvatar(userID);
        return avatar.ToAvatarAppearance();
    }

    public bool SetAppearance(UUID userID, AvatarAppearance appearance)
    {
        AvatarData avatar = new AvatarData(appearance);
        return SetAvatar(userID,avatar);
    }

    public AvatarData GetAvatar(UUID userID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "getavatar";

        sendData["UserID"] = userID;

        string reply = string.Empty;

        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/avatar";

        _logger.LogDebug($"[AVATAR CONNECTOR]: queryString = {reqString}");

        try
        {
            reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);

            if (string.IsNullOrEmpty(reply))
            {
                _logger.LogDebug("[AVATAR CONNECTOR]: GetAgent received null or empty reply");
                return null;
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, $"[AVATAR CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
        }

        Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
        AvatarData avatar = null;

        if ((replyData != null) && replyData.ContainsKey("result") && (replyData["result"] != null))
        {
            if (replyData["result"] is Dictionary<string, object>)
            {
                avatar = new AvatarData((Dictionary<string, object>)replyData["result"]);
            }
        }

        return avatar;
    }

    public bool SetAvatar(UUID userID, AvatarData avatar)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "setavatar";

        sendData["UserID"] = userID.ToString();

        Dictionary<string, object> structData = avatar.ToKeyValuePairs();

        foreach (KeyValuePair<string, object> kvp in structData)
            sendData[kvp.Key] = kvp.Value.ToString();


        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/avatar";

        _logger.LogDebug($"[AVATAR CONNECTOR]: queryString = {reqString}");

        try
        {
            var reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
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
                    _logger.LogDebug("[AVATAR CONNECTOR]: SetAvatar reply data does not contain result field");
                }
            }
            else
            {
                _logger.LogDebug("[AVATAR CONNECTOR]: SetAvatar received empty reply");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, $"[AVATAR CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
        }

        return false;
    }

    public bool ResetAvatar(UUID userID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "resetavatar";

        sendData["UserID"] = userID.ToString();

        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/avatar";

        _logger.LogDebug($"[AVATAR CONNECTOR]: queryString = {reqString}");

        try
        {
            var reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);
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
                    _logger.LogDebug("[AVATAR CONNECTOR]: SetItems reply data does not contain result field");
                }

            }
            else
            {
                _logger.LogDebug("[AVATAR CONNECTOR]: SetItems received empty reply");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug($"[AVATAR CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
        }

        return false;
    }

    public bool SetItems(UUID userID, string[] names, string[] values)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "setitems";

        sendData["UserID"] = userID.ToString();
        sendData["Names"] = new List<string>(names);
        sendData["Values"] = new List<string>(values);

        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/avatar";

        _logger.LogDebug($"[AVATAR CONNECTOR]: queryString = {reqString}");

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
                    _logger.LogDebug("[AVATAR CONNECTOR]: SetItems reply data does not contain result field");
                }
            }
            else
            {
                _logger.LogDebug("[AVATAR CONNECTOR]: SetItems received empty reply");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug($"[AVATAR CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
        }

        return false;
    }

    public bool RemoveItems(UUID userID, string[] names)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["SCOPEID"] = scopeID.ToString();
        sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "removeitems";

        sendData["UserID"] = userID.ToString();
        sendData["Names"] = new List<string>(names);

        string reqString = ServerUtils.BuildQueryString(sendData);
        string uri = _serverURI + "/avatar";

        _logger.LogDebug($"[AVATAR CONNECTOR]: queryString = {reqString}");

        try
        {
            var reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, _auth);

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
                    _logger.LogDebug("[AVATAR CONNECTOR]: RemoveItems reply data does not contain result field");
                }
            }
            else
            {
                _logger.LogDebug("[AVATAR CONNECTOR]: RemoveItems received empty reply");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug($"[AVATAR CONNECTOR]: Exception when contacting presence server at {uri}: {e.Message}");
        }

        return false;
    }

    #endregion

}
