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
using System.Diagnostics;
using OpenMetaverse;
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
        /// <summary>
        ///     Reset the named script. The script must be present
        ///     in the same prim.
        /// </summary>
        [DebuggerNonUserCode]
        public void llResetScript()
        {
            // We need to tell the URL module, if we hav one, to release
            // the allocated URLs
            if (m_UrlModule != null)
                m_UrlModule.ScriptRemoved(m_item.ItemID);

            m_ScriptEngine.ApiResetScript(m_item.ItemID);
        }

        public void llResetOtherScript(string name)
        {
            var item = GetScriptByName(name);

            if (item.IsZero())
            {
                Error("llResetOtherScript", "Can't find script '" + name + "'");
                return;
            }

            if (item.Equals(m_item.ItemID))
                llResetScript();
            else
                m_ScriptEngine.ResetScript(item);
        }

        public void llSetScriptState(string name, int run)
        {
            var item = GetScriptByName(name);


            // These functions are supposed to be robust,
            // so get the state one step at a time.

            if (!item.IsZero())
                m_ScriptEngine.SetScriptState(item, run == 0 ? false : true, item.Equals(m_item.ItemID));
            else
                Error("llSetScriptState", "Can't find script '" + name + "'");
        }


        public void llSetTimerEvent(double sec)
        {
            if (sec != 0.0 && sec < m_MinTimerInterval)
                sec = m_MinTimerInterval;
            // Setting timer repeat
            m_AsyncCommands.TimerPlugin.SetTimerEvent(m_host.LocalId, m_item.ItemID, sec);
        }

        public virtual void llSleep(double sec)
        {
//            m_log.Info("llSleep snoozing " + sec + "s.");

            Sleep((int)(sec * 1000));
        }


        public void llMessageLinked(int linknumber, int num, string msg, string id)
        {
            var parts = GetLinkParts(linknumber);

            UUID partItemID;
            foreach (var part in parts)
            foreach (var item in part.Inventory.GetInventoryItems())
                if (item.Type == ScriptBaseClass.INVENTORY_SCRIPT)
                {
                    partItemID = item.ItemID;
                    var linkNumber = m_host.LinkNum;
                    if (m_host.ParentGroup.PrimCount == 1)
                        linkNumber = 0;

                    object[] resobj =
                    {
                        new LSL_Integer(linkNumber), new LSL_Integer(num),
                        new LSL_Key(msg), new LSL_Key(id)
                    };

                    m_ScriptEngine.PostScriptEvent(partItemID,
                        new EventParams("link_message",
                            resobj, new DetectParams[0]));
                }
        }


        public LSL_Integer llGetScriptState(string name)
        {
            var item = GetScriptByName(name);

            if (!item.IsZero()) return m_ScriptEngine.GetScriptState(item) ? 1 : 0;

            Error("llGetScriptState", "Can't find script '" + name + "'");

            // If we didn't find it, then it's safe to
            // assume it is not running.

            return 0;
        }
    }
}