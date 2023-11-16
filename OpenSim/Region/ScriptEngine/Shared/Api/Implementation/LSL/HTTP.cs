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

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using RegionFlags = OpenSim.Framework.RegionFlags;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public void llReleaseURL(string url)
        {
            if (m_UrlModule != null)
                m_UrlModule.ReleaseURL(url);
        }

        public void llLoadURL(string avatar_id, string message, string url)
        {
            if (m_host.OwnerID.Equals(m_host.GroupID))
                return;
            try
            {
                var m_checkuri = new Uri(url);
                if (m_checkuri.Scheme != Uri.UriSchemeHttp && m_checkuri.Scheme != Uri.UriSchemeHttps)
                {
                    Error("llLoadURL", "Invalid url schema");
                    ScriptSleep(200);
                    return;
                }
            }
            catch
            {
                Error("llLoadURL", "Invalid url");
                ScriptSleep(200);
                return;
            }

            var dm = World.RequestModuleInterface<IDialogModule>();
            if (null != dm)
                dm.SendUrlToUser(
                    new UUID(avatar_id), m_host.Name, m_host.UUID, m_host.OwnerID, false, message, url);

            ScriptSleep(m_sleepMsOnLoadURL);
        }

        public void llSetContentType(LSL_Key reqid, LSL_Integer type)
        {
            if (m_UrlModule == null)
                return;

            if (!UUID.TryParse(reqid, out var id) || id.IsZero())
                return;

            // Make sure the content type is text/plain to start with
            m_UrlModule.HttpContentType(id, "text/plain");

            // Is the object owner online and in the region
            var agent = World.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (agent == null || agent.IsChildAgent || agent.IsDeleted)
                return; // Fail if the owner is not in the same region

            // Is it the embeded browser?
            var userAgent = m_UrlModule.GetHttpHeader(id, "user-agent");
            if (string.IsNullOrEmpty(userAgent))
                return;

            if (userAgent.IndexOf("SecondLife") < 0)
                return; // Not the embedded browser

            // Use the IP address of the client and check against the request
            // seperate logins from the same IP will allow all of them to get non-text/plain as long
            // as the owner is in the region. Same as SL!
            var logonFromIPAddress = agent.ControllingClient.RemoteEndPoint.Address.ToString();
            if (string.IsNullOrEmpty(logonFromIPAddress))
                return;

            var requestFromIPAddress = m_UrlModule.GetHttpHeader(id, "x-remote-ip");
            //m_log.Debug("IP from header='" + requestFromIPAddress + "' IP from endpoint='" + logonFromIPAddress + "'");
            if (requestFromIPAddress == null)
                return;

            requestFromIPAddress = requestFromIPAddress.Trim();

            // If the request isnt from the same IP address then the request cannot be from the owner
            if (!requestFromIPAddress.Equals(logonFromIPAddress))
                return;

            switch (type)
            {
                case ScriptBaseClass.CONTENT_TYPE_HTML:
                    m_UrlModule.HttpContentType(id, "text/html");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_XML:
                    m_UrlModule.HttpContentType(id, "application/xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_XHTML:
                    m_UrlModule.HttpContentType(id, "application/xhtml+xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_ATOM:
                    m_UrlModule.HttpContentType(id, "application/atom+xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_JSON:
                    m_UrlModule.HttpContentType(id, "application/json");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_LLSD:
                    m_UrlModule.HttpContentType(id, "application/llsd+xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_FORM:
                    m_UrlModule.HttpContentType(id, "application/x-www-form-urlencoded");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_RSS:
                    m_UrlModule.HttpContentType(id, "application/rss+xml");
                    break;
                default:
                    m_UrlModule.HttpContentType(id, "text/plain");
                    break;
            }
        }


        public LSL_Integer llGetFreeURLs()
        {
            if (m_UrlModule != null)
                return new LSL_Integer(m_UrlModule.GetFreeUrls());
            return new LSL_Integer(0);
        }


        public LSL_String llGetHTTPHeader(LSL_Key request_id, string header)
        {
            if (m_UrlModule != null)
                return m_UrlModule.GetHttpHeader(new UUID(request_id), header);
            return string.Empty;
        }


        public LSL_String llGetSimulatorHostname()
        {
            var UrlModule = World.RequestModuleInterface<IUrlModule>();
            return UrlModule.ExternalHostNameForLSL;
        }

        public LSL_Key llRequestSecureURL()
        {
            if (m_UrlModule != null)
                return m_UrlModule.RequestSecureURL(m_ScriptEngine.ScriptModule, m_host, m_item.ItemID, null)
                    .ToString();
            return ScriptBaseClass.NULL_KEY;
        }

        public LSL_Key llRequestSimulatorData(string simulator, int data)
        {
            try
            {
                if (m_regionName.Equals(simulator))
                {
                    var lreply = string.Empty;
                    var rinfo = World.RegionInfo;
                    switch (data)
                    {
                        case ScriptBaseClass.DATA_SIM_POS:
                            lreply = new LSL_Vector(
                                rinfo.RegionLocX,
                                rinfo.RegionLocY,
                                0).ToString();
                            break;
                        case ScriptBaseClass.DATA_SIM_STATUS:
                            lreply = "up"; // Duh!
                            break;
                        case ScriptBaseClass.DATA_SIM_RATING:
                            switch (rinfo.RegionSettings.Maturity)
                            {
                                case 0:
                                    lreply = "PG";
                                    break;
                                case 1:
                                    lreply = "MATURE";
                                    break;
                                case 2:
                                    lreply = "ADULT";
                                    break;
                                default:
                                    lreply = "UNKNOWN";
                                    break;
                            }

                            break;
                        case ScriptBaseClass.DATA_SIM_RELEASE:
                            lreply = "OpenSim";
                            break;
                        default:
                            ScriptSleep(m_sleepMsOnRequestSimulatorData);
                            return ScriptBaseClass.NULL_KEY; // Raise no event
                    }

                    var ltid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId,
                        m_item.ItemID, lreply);
                    ScriptSleep(m_sleepMsOnRequestSimulatorData);
                    return ltid;
                }

                Action<string> act = eventID =>
                {
                    var info = World.GridService.GetRegionByName(RegionScopeID, simulator);
                    var reply = "unknown";
                    if (info != null)
                        switch (data)
                        {
                            case ScriptBaseClass.DATA_SIM_POS:
                                // Hypergrid is currently placing real destination region co-ords into RegionSecret.
                                // But other code can also use this field for a genuine RegionSecret!  Therefore, if
                                // anything is present we need to disambiguate.
                                //
                                // FIXME: Hypergrid should be storing this data in a different field.
                                var regionFlags = (RegionFlags)m_ScriptEngine.World.GridService.GetRegionFlags(
                                    info.ScopeID, info.RegionID);

                                if ((regionFlags & RegionFlags.Hyperlink) != 0)
                                {
                                    Utils.LongToUInts(Convert.ToUInt64(info.RegionSecret), out var rx, out var ry);
                                    reply = new LSL_Vector(rx, ry, 0).ToString();
                                }
                                else
                                {
                                    // Local grid co-oridnates
                                    reply = new LSL_Vector(info.RegionLocX, info.RegionLocY, 0).ToString();
                                }

                                break;
                            case ScriptBaseClass.DATA_SIM_STATUS:
                                reply = "up"; // Duh!
                                break;
                            case ScriptBaseClass.DATA_SIM_RATING:
                                switch (info.Maturity)
                                {
                                    case 0:
                                        reply = "PG";
                                        break;
                                    case 1:
                                        reply = "MATURE";
                                        break;
                                    case 2:
                                        reply = "ADULT";
                                        break;
                                    default:
                                        reply = "UNKNOWN";
                                        break;
                                }

                                break;
                            case ScriptBaseClass.DATA_SIM_RELEASE:
                                reply = "OpenSim";
                                break;
                        }

                    m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, reply);
                };

                var tid = m_AsyncCommands.DataserverPlugin.RegisterRequest(
                    m_host.LocalId, m_item.ItemID, act);

                ScriptSleep(m_sleepMsOnRequestSimulatorData);
                return tid.ToString();
            }
            catch (Exception)
            {
                //m_log.Error("[LSL_API]: llRequestSimulatorData" + e.ToString());
                return ScriptBaseClass.NULL_KEY;
            }
        }

        public LSL_Key llRequestURL()
        {
            if (m_UrlModule != null)
                return m_UrlModule.RequestURL(m_ScriptEngine.ScriptModule, m_host, m_item.ItemID, null).ToString();
            return ScriptBaseClass.NULL_KEY;
        }

        public void llParcelMediaCommandList(LSL_List commandList)
        {
            // TODO: Not implemented yet (missing in libomv?):
            //  PARCEL_MEDIA_COMMAND_LOOP_SET    float loop      Use this to get or set the parcel's media loop duration. (1.19.1 RC0 or later)


            // according to the docs, this command only works if script owner and land owner are the same
            // lets add estate owners and gods, too, and use the generic permission check.
            var landObject = World.LandChannel.GetLandObject(m_host.AbsolutePosition);
            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID, landObject, GroupPowers.ChangeMedia, false))
                return;

            var update = false; // send a ParcelMediaUpdate (and possibly change the land's media URL)?
            byte loop = 0;

            var landData = landObject.LandData;
            var url = landData.MediaURL;
            var texture = landData.MediaID.ToString();
            var autoAlign = landData.MediaAutoScale != 0;
            var mediaType = ""; // TODO these have to be added as soon as LandData supports it
            var description = "";
            var width = 0;
            var height = 0;

            ParcelMediaCommandEnum? commandToSend = null;
            var time = 0.0f; // default is from start

            uint cmndFlags = 0;
            ScenePresence presence = null;
            int cmd;
            for (var i = 0; i < commandList.Data.Length; i++)
            {
                if (commandList.Data[i] is LSL_Integer)
                    cmd = (LSL_Integer)commandList.Data[i];
                else
                    cmd = (int)commandList.Data[i];

                var command = (ParcelMediaCommandEnum)cmd;

                switch (command)
                {
                    case ParcelMediaCommandEnum.Agent:
                        // we send only to one agent
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_String)
                            {
                                if (UUID.TryParse((LSL_String)commandList.Data[i + 1], out var agentID) &&
                                    agentID.IsNotZero())
                                {
                                    presence = World.GetScenePresence(agentID);
                                    if (presence == null || presence.IsNPC)
                                        return;
                                }
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_AGENT must be a key");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.Loop:
                        loop = 1;
                        cmndFlags |= 1 << ScriptBaseClass.PARCEL_MEDIA_COMMAND_LOOP;
                        commandToSend = command;
                        update = true; //need to send the media update packet to set looping
                        break;

                    case ParcelMediaCommandEnum.Play:
                        loop = 0;
                        cmndFlags |= 1 << ScriptBaseClass.PARCEL_MEDIA_COMMAND_PLAY;
                        commandToSend = command;
                        update = true; //need to send the media update packet to make sure it doesn't loop
                        break;

                    case ParcelMediaCommandEnum.Pause:
                        cmndFlags |= 1 << ScriptBaseClass.PARCEL_MEDIA_COMMAND_PAUSE;
                        commandToSend = command;
                        break;
                    case ParcelMediaCommandEnum.Stop:
                        cmndFlags |= 1 << ScriptBaseClass.PARCEL_MEDIA_COMMAND_STOP;
                        commandToSend = command;
                        break;
                    case ParcelMediaCommandEnum.Unload:
                        cmndFlags |= 1 << ScriptBaseClass.PARCEL_MEDIA_COMMAND_UNLOAD;
                        commandToSend = command;
                        break;

                    case ParcelMediaCommandEnum.Url:
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_String)
                            {
                                url = (LSL_String)commandList.Data[i + 1];
                                if (string.IsNullOrWhiteSpace(url))
                                    url = string.Empty;
                                else
                                    try
                                    {
                                        var dummy = new Uri(url, UriKind.Absolute);
                                    }
                                    catch
                                    {
                                        Error("llParcelMediaCommandList", "invalid PARCEL_MEDIA_COMMAND_URL");
                                        return;
                                    }

                                update = true;
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_URL must be a string");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.Texture:
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_String)
                            {
                                texture = (LSL_String)commandList.Data[i + 1];
                                update = true;
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_TEXTURE must be a string or a key");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.Time:
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_Float)
                            {
                                time = (float)(LSL_Float)commandList.Data[i + 1];
                                cmndFlags |= 1 << ScriptBaseClass.PARCEL_MEDIA_COMMAND_TIME;
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_TIME must be a float");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.AutoAlign:
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_Integer)
                            {
                                autoAlign = (LSL_Integer)commandList.Data[i + 1];
                                update = true;
                            }

                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_AUTO_ALIGN must be an integer");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.Type:
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_String)
                            {
                                mediaType = (LSL_String)commandList.Data[i + 1];
                                update = true;
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_TYPE must be a string");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.Desc:
                        if (i + 1 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_String)
                            {
                                description = (LSL_String)commandList.Data[i + 1];
                                update = true;
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The argument of PARCEL_MEDIA_COMMAND_DESC must be a string");
                            }

                            ++i;
                        }

                        break;

                    case ParcelMediaCommandEnum.Size:
                        if (i + 2 < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is LSL_Integer)
                            {
                                if (commandList.Data[i + 2] is LSL_Integer)
                                {
                                    width = (LSL_Integer)commandList.Data[i + 1];
                                    height = (LSL_Integer)commandList.Data[i + 2];
                                    update = true;
                                }
                                else
                                {
                                    Error("llParcelMediaCommandList",
                                        "The second argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer");
                                }
                            }
                            else
                            {
                                Error("llParcelMediaCommandList",
                                    "The first argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer");
                            }

                            i += 2;
                        }

                        break;

                    default:
                        NotImplemented("llParcelMediaCommandList",
                            "Parameter not supported yet: " + Enum.Parse(typeof(ParcelMediaCommandEnum),
                                commandList.Data[i].ToString()));
                        break;
                } //end switch
            } //end for

            // if we didn't get a presence, we send to all and change the url
            // if we did get a presence, we only send to the agent specified, and *don't change the land settings*!

            // did something important change or do we only start/stop/pause?
            if (update)
            {
                if (presence == null)
                {
                    // we send to all
                    landData.MediaID = new UUID(texture);
                    landData.MediaAutoScale = autoAlign ? (byte)1 : (byte)0;
                    landData.MediaWidth = width;
                    landData.MediaHeight = height;
                    landData.MediaType = mediaType;

                    // do that one last, it will cause a ParcelPropertiesUpdate
                    landObject.SetMediaUrl(url);

                    // now send to all (non-child) agents in the parcel
                    World.ForEachRootScenePresence(delegate(ScenePresence sp)
                    {
                        if (sp.currentParcelUUID.Equals(landData.GlobalID))
                            sp.ControllingClient.SendParcelMediaUpdate(landData.MediaURL,
                                landData.MediaID,
                                landData.MediaAutoScale,
                                mediaType,
                                description,
                                width, height,
                                loop);
                    });
                }
                else if (!presence.IsChildAgent)
                {
                    // we only send to one (root) agent
                    presence.ControllingClient.SendParcelMediaUpdate(url,
                        new UUID(texture),
                        autoAlign ? (byte)1 : (byte)0,
                        mediaType,
                        description,
                        width, height,
                        loop);
                }
            }

            if (commandToSend != null)
            {
                // the commandList contained a start/stop/... command, too
                if (presence == null)
                    // send to all (non-child) agents in the parcel
                    World.ForEachRootScenePresence(delegate(ScenePresence sp)
                    {
                        if (sp.currentParcelUUID.Equals(landData.GlobalID))
                            sp.ControllingClient.SendParcelMediaCommand(cmndFlags,
                                commandToSend.Value, time);
                    });
                else if (!presence.IsChildAgent)
                    presence.ControllingClient.SendParcelMediaCommand(cmndFlags,
                        commandToSend.Value, time);
            }

            ScriptSleep(m_sleepMsOnParcelMediaCommandList);
        }

        public LSL_List llParcelMediaQuery(LSL_List aList)
        {
            var list = new LSL_List();
            var pos = m_host.AbsolutePosition;

            var landObject = World.LandChannel.GetLandObject(pos);
            if (landObject == null)
                return list;

            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID, landObject, GroupPowers.ChangeMedia, false))
                return list;

            var land = landObject.LandData;
            if (land == null)
                return list;

            //TO DO: make the implementation for the missing commands
            //PARCEL_MEDIA_COMMAND_LOOP_SET    float loop      Use this to get or set the parcel's media loop duration. (1.19.1 RC0 or later)
            for (var i = 0; i < aList.Data.Length; i++)
                if (aList.Data[i] != null)
                    switch ((ParcelMediaCommandEnum)Convert.ToInt32(aList.Data[i].ToString()))
                    {
                        case ParcelMediaCommandEnum.Url:
                            list.Add(new LSL_String(land.MediaURL));
                            break;
                        case ParcelMediaCommandEnum.Desc:
                            list.Add(new LSL_String(land.MediaDescription));
                            break;
                        case ParcelMediaCommandEnum.Texture:
                            list.Add(new LSL_String(land.MediaID.ToString()));
                            break;
                        case ParcelMediaCommandEnum.Type:
                            list.Add(new LSL_String(land.MediaType));
                            break;
                        case ParcelMediaCommandEnum.Size:
                            list.Add(new LSL_String(land.MediaWidth));
                            list.Add(new LSL_String(land.MediaHeight));
                            break;
                        default:
                            var mediaCommandEnum = ParcelMediaCommandEnum.Url;
                            NotImplemented("llParcelMediaQuery",
                                "Parameter not supported yet: " + Enum.Parse(mediaCommandEnum.GetType(),
                                    aList.Data[i].ToString()));
                            break;
                    }

            ScriptSleep(m_sleepMsOnParcelMediaQuery);
            return list;
        }

        public LSL_Key llHTTPRequest(string url, LSL_List parameters, string body)
        {
            var httpScriptMod = m_ScriptEngine.World.RequestModuleInterface<IHttpRequestModule>();
            if (httpScriptMod == null)
                return string.Empty;

            if (!httpScriptMod.CheckThrottle(m_host.LocalId, m_host.OwnerID))
                return ScriptBaseClass.NULL_KEY;

            try
            {
                var m_checkuri = new Uri(url);
                if (m_checkuri.Scheme != Uri.UriSchemeHttp && m_checkuri.Scheme != Uri.UriSchemeHttps)
                {
                    Error("llHTTPRequest", "Invalid url schema");
                    return string.Empty;
                }
            }
            catch
            {
                Error("llHTTPRequest", "Invalid url");
                return string.Empty;
            }

            var param = new List<string>();
            bool ok;
            var nCustomHeaders = 0;

            for (var i = 0; i < parameters.Data.Length; i += 2)
            {
                ok = int.TryParse(parameters.Data[i].ToString(), out var flag);
                if (!ok || flag < 0 ||
                    flag > (int)HttpRequestConstants.HTTP_PRAGMA_NO_CACHE)
                {
                    Error("llHTTPRequest", "Parameter " + i + " is an invalid flag");
                    ScriptSleep(200);
                    return string.Empty;
                }

                param.Add(parameters.Data[i].ToString()); //Add parameter flag

                if (flag != (int)HttpRequestConstants.HTTP_CUSTOM_HEADER)
                {
                    param.Add(parameters.Data[i + 1].ToString()); //Add parameter value
                }
                else
                {
                    //Parameters are in pairs and custom header takes
                    //arguments in pairs so adjust for header marker.
                    ++i;

                    //Maximum of 8 headers are allowed based on the
                    //Second Life documentation for llHTTPRequest.
                    for (var count = 1; count <= 8; ++count)
                    {
                        if (nCustomHeaders >= 8)
                        {
                            Error("llHTTPRequest", "Max number of custom headers is 8, excess ignored");
                            break;
                        }

                        //Enough parameters remaining for (another) header?
                        if (parameters.Data.Length - i < 2)
                        {
                            //There must be at least one name/value pair for custom header
                            if (count == 1)
                                Error("llHTTPRequest", "Missing name/value for custom header at parameter " + i);
                            return string.Empty;
                        }

                        var paramName = parameters.Data[i].ToString();

                        var paramNamelwr = paramName.ToLower();
                        if (paramNamelwr.StartsWith("proxy-"))
                        {
                            Error("llHTTPRequest", "Name is invalid as a custom header at parameter " + i);
                            return string.Empty;
                        }

                        if (paramNamelwr.StartsWith("sec-"))
                        {
                            Error("llHTTPRequest", "Name is invalid as a custom header at parameter " + i);
                            return string.Empty;
                        }

                        var noskip = true;
                        if (HttpForbiddenHeaders.TryGetValue(paramName, out var fatal))
                        {
                            if (fatal)
                            {
                                Error("llHTTPRequest", "Name is invalid as a custom header at parameter " + i);
                                return string.Empty;
                            }

                            noskip = false;
                        }

                        var paramValue = parameters.Data[i + 1].ToString();
                        if (paramName.Length + paramValue.Length > 253)
                        {
                            Error("llHTTPRequest",
                                "name and value length exceds 253 characters for custom header at parameter " + i);
                            return string.Empty;
                        }

                        if (noskip)
                        {
                            param.Add(paramName);
                            param.Add(paramValue);
                            nCustomHeaders++;
                        }

                        //Have we reached the end of the list of headers?
                        //End is marked by a string with a single digit.
                        if (i + 2 >= parameters.Data.Length ||
                            char.IsDigit(parameters.Data[i + 2].ToString()[0]))
                            break;

                        i += 2;
                    }
                }
            }

            var position = m_host.AbsolutePosition;
            var velocity = m_host.Velocity;
            var rotation = m_host.GetWorldRotation();

            var ownerName = string.Empty;
            var scenePresence = World.GetScenePresence(m_host.OwnerID);
            if (scenePresence == null)
                ownerName = resolveName(m_host.OwnerID);
            else
                ownerName = scenePresence.Name;

            var regionInfo = World.RegionInfo;

            var httpHeaders = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(m_lsl_shard))
                httpHeaders["X-SecondLife-Shard"] = m_lsl_shard;
            httpHeaders["X-SecondLife-Object-Name"] = m_host.Name;
            httpHeaders["X-SecondLife-Object-Key"] = m_host.UUID.ToString();
            httpHeaders["X-SecondLife-Region"] = string.Format("{0} ({1}, {2})", regionInfo.RegionName,
                regionInfo.WorldLocX, regionInfo.WorldLocY);
            httpHeaders["X-SecondLife-Local-Position"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000})",
                position.X, position.Y, position.Z);
            httpHeaders["X-SecondLife-Local-Velocity"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000})",
                velocity.X, velocity.Y, velocity.Z);
            httpHeaders["X-SecondLife-Local-Rotation"] = string.Format(
                "({0:0.000000}, {1:0.000000}, {2:0.000000}, {3:0.000000})", rotation.X, rotation.Y, rotation.Z,
                rotation.W);
            httpHeaders["X-SecondLife-Owner-Name"] = ownerName;
            httpHeaders["X-SecondLife-Owner-Key"] = m_host.OwnerID.ToString();
            if (!string.IsNullOrWhiteSpace(m_lsl_user_agent))
                httpHeaders["User-Agent"] = m_lsl_user_agent;

            // See if the URL contains any header hacks
            var urlParts = url.Split('\n');
            if (urlParts.Length > 1)
            {
                // Iterate the passed headers and parse them
                for (var i = 1; i < urlParts.Length; i++)
                {
                    // The rest of those would be added to the body in SL.
                    // Let's not do that.
                    if (urlParts[i].Length == 0)
                        break;

                    // See if this could be a valid header
                    var headerParts = urlParts[i].Split(new[] { ':' }, 2);
                    if (headerParts.Length != 2)
                        continue;

                    var headerName = headerParts[0].Trim();
                    if (!HttpForbiddenInHeaders.Contains(headerName))
                    {
                        var headerValue = headerParts[1].Trim();
                        httpHeaders[headerName] = headerValue;
                    }
                }

                // Finally, strip any protocol specifier from the URL
                url = urlParts[0].Trim();
                var idx = url.IndexOf(" HTTP/");
                if (idx != -1)
                    url = url.Substring(0, idx);
            }

            var authregex = @"^(https?:\/\/)(\w+):(\w+)@(.*)$";
            var r = new Regex(authregex);
            var gnums = r.GetGroupNumbers();
            var m = r.Match(url);
            if (m.Success)
                //for (int i = 1; i < gnums.Length; i++)
                //{
                //System.Text.RegularExpressions.Group g = m.Groups[gnums[i]];
                //CaptureCollection cc = g.Captures;
                //}
                if (m.Groups.Count == 5)
                {
                    httpHeaders["Authorization"] = string.Format("Basic {0}",
                        Convert.ToBase64String(Encoding.ASCII.GetBytes(m.Groups[2] + ":" + m.Groups[3])));
                    url = m.Groups[1] + m.Groups[4].ToString();
                }

            var reqID = httpScriptMod.StartHttpRequest(m_host.LocalId, m_item.ItemID, url, param, httpHeaders, body,
                out var status);

            if (status == HttpInitialRequestStatus.DISALLOWED_BY_FILTER)
                Error("llHttpRequest", string.Format("Request to {0} disallowed by filter", url));

            return reqID.IsZero() ? "" : reqID.ToString();
        }


        public void llHTTPResponse(LSL_Key id, int status, string body)
        {
            // Partial implementation: support for parameter flags needed
            //   see http://wiki.secondlife.com/wiki/llHTTPResponse


            if (m_UrlModule != null)
                m_UrlModule.HttpResponse(new UUID(id), status, body);
        }
    }
}