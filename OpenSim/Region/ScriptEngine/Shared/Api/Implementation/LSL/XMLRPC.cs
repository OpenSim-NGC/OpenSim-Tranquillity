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
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
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

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public void llOpenRemoteDataChannel()
        {
            var xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            if (xmlrpcMod != null && xmlrpcMod.IsEnabled())
            {
                var channelID = xmlrpcMod.OpenXMLRPCChannel(m_host.LocalId, m_item.ItemID, UUID.Zero);
                var xmlRpcRouter = m_ScriptEngine.World.RequestModuleInterface<IXmlRpcRouter>();
                if (xmlRpcRouter != null)
                {
                    var ExternalHostName = m_ScriptEngine.World.RegionInfo.ExternalHostName;

                    xmlRpcRouter.RegisterNewReceiver(m_ScriptEngine.ScriptModule, channelID, m_host.UUID,
                        m_item.ItemID, string.Format("http://{0}:{1}/", ExternalHostName,
                            xmlrpcMod.Port.ToString()));
                }

                object[] resobj =
                {
                    new LSL_Integer(1),
                    new LSL_String(channelID.ToString()),
                    new LSL_String(ScriptBaseClass.NULL_KEY),
                    new LSL_String(string.Empty),
                    new LSL_Integer(0),
                    new LSL_String(string.Empty)
                };
                m_ScriptEngine.PostScriptEvent(m_item.ItemID, new EventParams("remote_data", resobj,
                    new DetectParams[0]));
            }

            ScriptSleep(m_sleepMsOnOpenRemoteDataChannel);
        }

        public void llRemoteDataReply(string channel, string message_id, string sdata, int idata)
        {
            var xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            if (xmlrpcMod != null)
                xmlrpcMod.RemoteDataReply(channel, message_id, sdata, idata);
            ScriptSleep(m_sleepMsOnRemoteDataReply);
        }

        public void llCloseRemoteDataChannel(string channel)
        {
            var xmlRpcRouter = m_ScriptEngine.World.RequestModuleInterface<IXmlRpcRouter>();
            if (xmlRpcRouter != null) xmlRpcRouter.UnRegisterReceiver(channel, m_item.ItemID);

            var xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            if (xmlrpcMod != null)
                xmlrpcMod.CloseXMLRPCChannel((UUID)channel);
            ScriptSleep(m_sleepMsOnCloseRemoteDataChannel);
        }

        public LSL_Key llSendRemoteData(string channel, string dest, int idata, string sdata)
        {
            var xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            ScriptSleep(m_sleepMsOnSendRemoteData);
            if (xmlrpcMod == null)
                return "";
            return xmlrpcMod.SendRemoteData(m_host.LocalId, m_item.ItemID, channel, dest, idata, sdata).ToString();
        }
    }
}