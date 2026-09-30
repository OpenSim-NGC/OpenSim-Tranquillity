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

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins;

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

        IXMLRPC xmlrpc = m_CmdManager.m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();

        if (xmlrpc != null)
        {
            // Use the IXmlRpcRequestInfo interface only: RPCRequestInfo is defined in
            // OpenSim.Region.CoreModules, which may be loaded into a different plugin
            // load context than this assembly, making a direct cast to the concrete
            // type fail with an InvalidCastException even though the type name matches.
            IXmlRpcRequestInfo rInfo = xmlrpc.GetNextCompletedRequest();

            while (rInfo != null)
            {
                xmlrpc.RemoveCompletedRequest(rInfo.GetMessageID());

                //Deliver data to prim's remote_data handler
                object[] resobj = new object[]
                {
                    new LSL_Types.LSLInteger(2),
                    new LSL_Types.LSLString(
                            rInfo.GetChannelKey().ToString()),
                    new LSL_Types.LSLString(
                            rInfo.GetMessageID().ToString()),
                    new LSL_Types.LSLString(String.Empty),
                    new LSL_Types.LSLInteger(rInfo.GetIntValue()),
                    new LSL_Types.LSLString(rInfo.GetStrVal())
                };

                PostRemoteData(rInfo.GetItemID(), resobj);

                rInfo = xmlrpc.GetNextCompletedRequest();
            }

            SendRemoteDataRequest srdInfo = (SendRemoteDataRequest)xmlrpc.GetNextCompletedSRDRequest();

            while (srdInfo != null)
            {
                xmlrpc.RemoveCompletedSRDRequest(srdInfo.GetReqID());

                //Deliver data to prim's remote_data handler
                object[] resobj = new object[]
                {
                    new LSL_Types.LSLInteger(3),
                    new LSL_Types.LSLString(srdInfo.Channel.ToString()),
                    new LSL_Types.LSLString(srdInfo.GetReqID().ToString()),
                    new LSL_Types.LSLString(String.Empty),
                    new LSL_Types.LSLInteger(srdInfo.Idata),
                    new LSL_Types.LSLString(srdInfo.Sdata)
                };

                PostRemoteData(srdInfo.ItemID, resobj);

                srdInfo = (SendRemoteDataRequest)xmlrpc.GetNextCompletedSRDRequest();
            }
        }
    }

    /// <summary>
    /// remote_data goes to the one script it is for. The XML-RPC module is shared by every
    /// region and drained by every script engine's pump, so the script may run in an engine
    /// this pump does not serve. When none of this pump's engines runs it, each other script
    /// engine of the regions is offered it once; only the engine that runs the script posts it.
    /// </summary>
    private void PostRemoteData(UUID itemID, object[] resobj)
    {
        foreach (IScriptEngine e in m_CmdManager.ScriptEngines)
        {
            if (e.PostScriptEvent(
                    itemID, new EventParams(
                        "remote_data", resobj,
                        new DetectParams[0])))
                return;
        }

        // Not stopping at the first that says yes: an engine may accept an item it does not run.
        // Arguments copied for each: an engine may convert them in place.
        foreach (IScriptEngine e in OtherScriptEngines())
            e.PostScriptEvent(itemID, new EventParams("remote_data", (object[])resobj.Clone(), new DetectParams[0]));
    }

    /// <summary>
    /// The script engines of the simulator's regions that this pump does not serve, each once.
    /// </summary>
    private List<IScriptEngine> OtherScriptEngines()
    {
        IScriptEngine[] own = m_CmdManager.ScriptEngines;

        HashSet<Scene> scenes = new HashSet<Scene>(SceneManager.Instance.GetScenes());
        foreach (IScriptEngine e in own)
        {
            Scene world = e.World;
            if (world != null)
                scenes.Add(world);
        }

        List<IScriptEngine> others = new List<IScriptEngine>();
        foreach (Scene scene in scenes)
        {
            foreach (IScriptModule m in scene.RequestModuleInterfaces<IScriptModule>())
            {
                if (m is IScriptEngine e && Array.IndexOf(own, e) < 0 && !others.Contains(e))
                    others.Add(e);
            }
        }
        return others;
    }
}
