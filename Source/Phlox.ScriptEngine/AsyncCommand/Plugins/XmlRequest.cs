/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
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

// Ported from Halcyon/InWorldz to this engine

using System;
using System.Collections.Generic;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Region.ScriptEngine.Shared;
using LSL_Types = OpenSim.Region.ScriptEngine.Shared.LSL_Types;

namespace Phlox.ScriptEngine.AsyncCommand.Plugins
{
    public class XmlRequest
    {
        public AsyncCommandManager m_CmdManager;

        public XmlRequest(AsyncCommandManager CmdManager)
        {
            m_CmdManager = CmdManager;
        }

        public void CheckXMLRPCRequests()
        {
            if (m_CmdManager.m_ScriptEngine.World == null)
                return;

            IXMLRPC xmlrpc =
                m_CmdManager.m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();

            if (xmlrpc == null)
                return;

            // Use the IXmlRpcRequestInfo interface only: RPCRequestInfo is defined in
            // OpenSim.Region.CoreModules, which develop's McMaster-based plugin loader may
            // load into a different AssemblyLoadContext than this assembly, making a direct
            // cast to the concrete type fail with an InvalidCastException even though the
            // type name matches.
            IXmlRpcRequestInfo rInfo = xmlrpc.GetNextCompletedRequest();
            while (rInfo != null)
            {
                xmlrpc.RemoveCompletedRequest(rInfo.GetMessageID());

                object[] resobj = new object[]
                {
                    new LSL_Types.LSLInteger(2),
                    new LSL_Types.LSLString(rInfo.GetChannelKey().ToString()),
                    new LSL_Types.LSLString(rInfo.GetMessageID().ToString()),
                    new LSL_Types.LSLString(string.Empty),
                    new LSL_Types.LSLInteger(rInfo.GetIntValue()),
                    new LSL_Types.LSLString(rInfo.GetStrVal())
                };

                PostRemoteData(rInfo.GetItemID(), resobj);

                rInfo = xmlrpc.GetNextCompletedRequest();
            }

            SendRemoteDataRequest srdInfo =
                (SendRemoteDataRequest)xmlrpc.GetNextCompletedSRDRequest();

            while (srdInfo != null)
            {
                xmlrpc.RemoveCompletedSRDRequest(srdInfo.GetReqID());

                object[] resobj = new object[]
                {
                    new LSL_Types.LSLInteger(3),
                    new LSL_Types.LSLString(srdInfo.Channel.ToString()),
                    new LSL_Types.LSLString(srdInfo.GetReqID().ToString()),
                    new LSL_Types.LSLString(string.Empty),
                    new LSL_Types.LSLInteger(srdInfo.Idata),
                    new LSL_Types.LSLString(srdInfo.Sdata)
                };

                PostRemoteData(srdInfo.ItemID, resobj);

                srdInfo = (SendRemoteDataRequest)xmlrpc.GetNextCompletedSRDRequest();
            }
        }

        /// <summary>
        /// remote_data goes to the one script it is for. The core's XML-RPC module is shared by every region and
        /// drained by every engine's pump, so this pump can take a request for a script another engine, or another
        /// region's Phlox, runs. Phlox's PostScriptEvent says yes to any item, so it is asked whether it runs the script;
        /// if not, each other script engine of the regions is offered it once (the core's pump,
        /// OpenSim.Region.ScriptEngine.Shared/Api/Plugins/XmlRequest.cs, does the same), and only the one that runs it posts.
        /// </summary>
        private void PostRemoteData(OpenMetaverse.UUID itemID, object[] resobj)
        {
            IScriptEngine own = m_CmdManager.m_ScriptEngine;
            if (own is not global::Phlox.ScriptEngine.PhloxEngine phlox || phlox.HasOrIsLoading(itemID))
            {
                own.PostScriptEvent(itemID, new EventParams("remote_data", resobj, Array.Empty<DetectParams>()));
                return;
            }

            var scenes = new HashSet<Scene>(SceneManager.Instance.GetScenes());
            foreach (IScriptEngine e in m_CmdManager.ScriptEngines)
                if (e.World != null) scenes.Add(e.World);

            var others = new List<IScriptEngine>();
            foreach (Scene scene in scenes)
                foreach (IScriptModule m in scene.RequestModuleInterfaces<IScriptModule>())
                    if (m is IScriptEngine e && !ReferenceEquals(e, own) && !others.Contains(e))
                        others.Add(e);

            foreach (IScriptEngine e in others)
                e.PostScriptEvent(itemID, new EventParams("remote_data", (object[])resobj.Clone(), Array.Empty<DetectParams>()));
        }

        /// <summary>
        /// The script is removed. Its channels close and its llSendRemoteData requests are cancelled (Halcyon
        /// AsyncCommandManager.RemoveScript: xmlrpc.DeleteChannels(itemID); xmlrpc.CancelSRDRequests(itemID)). Not called
        /// on a reset or a state change: Halcyon's XmlRequestPlugin.RemoveEvents did nothing there, SL says nothing, and a
        /// reset script that opens its channel again gets the same one.
        /// </summary>
        public void RemoveEvents(uint localID, OpenMetaverse.UUID itemID)
        {
            IXMLRPC xmlrpc = m_CmdManager.m_ScriptEngine.World?.RequestModuleInterface<IXMLRPC>();
            if (xmlrpc == null) return;
            xmlrpc.DeleteChannels(itemID);
            xmlrpc.CancelSRDRequests(itemID);
        }
    }
}
