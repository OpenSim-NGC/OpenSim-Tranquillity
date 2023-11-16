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
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
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
        public void llStopPointAt()
        {
        }

        public void llPointAt(LSL_Vector pos)
        {
        }


        public LSL_Float llGetEnergy()
        {
            // TODO: figure out real energy value
            return 1.0f;
        }


        public LSL_Integer llGetMemoryLimit()
        {
            // The value returned for Mono scripts in SL
            return 65536;
        }

        public LSL_Integer llSetMemoryLimit(LSL_Integer limit)
        {
            // Treat as an LSO script
            return ScriptBaseClass.FALSE;
        }

        public LSL_Integer llGetSPMaxMemory()
        {
            // The value returned for Mono scripts in SL
            return 65536;
        }

        public virtual LSL_Integer llGetUsedMemory()
        {
            // The value returned for Mono scripts in SL
            return 65536;
        }

        public void llScriptProfiler(LSL_Integer flags)
        {
            // This does nothing for LSO scripts in SL
        }

        //
        // Listing the unimplemented lsl functions here, please move
        // them from this region as they are completed
        //
        public void llCollisionSprite(LSL_String impact_sprite)
        {
            // Viewer 2.0 broke this and it's likely LL has no intention
            // of fixing it. Therefore, letting this be a NOP seems appropriate.
        }

        public void llGodLikeRezObject(string inventory, LSL_Vector pos)
        {
            if (!World.Permissions.IsGod(m_host.OwnerID))
                NotImplemented("llGodLikeRezObject");

            var rezAsset = World.AssetService.Get(inventory);
            if (rezAsset == null)
            {
                llSay(0, "Asset not found");
                return;
            }

            SceneObjectGroup group = null;

            try
            {
                var xmlData = Utils.BytesToString(rezAsset.Data);
                group = SceneObjectSerializer.FromOriginalXmlFormat(xmlData);
            }
            catch
            {
                llSay(0, "Asset not found");
                return;
            }

            if (group == null)
            {
                llSay(0, "Asset not found");
                return;
            }

            group.RootPart.AttachedPos = group.AbsolutePosition;

            group.ResetIDs();

            var llpos = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            World.AddNewSceneObject(group, true, llpos, Quaternion.Identity, Vector3.Zero);
            group.CreateScriptInstances(0, true, World.DefaultScriptEngine, 3);
            group.ScheduleGroupForFullUpdate();

            // objects rezzed with this method are die_at_edge by default.
            group.RootPart.SetDieAtEdge(true);

            group.ResumeScripts();

            m_ScriptEngine.PostObjectEvent(m_host.LocalId, new EventParams(
                "object_rez", new object[]
                {
                    new LSL_String(
                        group.RootPart.UUID.ToString())
                },
                new DetectParams[0]));
        }

        public LSL_Key llTransferLindenDollars(LSL_Key destination, LSL_Integer amount)
        {
            var money = World.RequestModuleInterface<IMoneyModule>();
            var txn = UUID.Random();
            var toID = UUID.Zero;

            var replydata = "UnKnownError";
            var bad = true;
            while (true)
            {
                if (amount <= 0)
                {
                    replydata = "INVALID_AMOUNT";
                    break;
                }

                if (money == null)
                {
                    replydata = "TRANSFERS_DISABLED";
                    break;
                }

                if (m_host.OwnerID.Equals(m_host.GroupID))
                {
                    replydata = "GROUP_OWNED";
                    break;
                }

                if (m_item == null)
                {
                    replydata = "SERVICE_ERROR";
                    break;
                }

                if (m_item.PermsGranter.IsZero())
                {
                    replydata = "MISSING_PERMISSION_DEBIT";
                    break;
                }

                if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_DEBIT) == 0)
                {
                    replydata = "MISSING_PERMISSION_DEBIT";
                    break;
                }

                if (!UUID.TryParse(destination, out toID))
                {
                    replydata = "INVALID_AGENT";
                    break;
                }

                bad = false;
                break;
            }

            if (bad)
            {
                m_ScriptEngine.PostScriptEvent(m_item.ItemID, new EventParams(
                    "transaction_result", new object[]
                    {
                        new LSL_String(txn.ToString()),
                        new LSL_Integer(0),
                        new LSL_String(replydata)
                    },
                    new DetectParams[0]));
                return txn.ToString();
            }

            //fire and forget...
            Action<string> act = eventID =>
            {
                var replycode = 0;
                try
                {
                    var account = m_userAccountService.GetUserAccount(RegionScopeID, toID);
                    if (account == null)
                    {
                        replydata = "LINDENDOLLAR_ENTITYDOESNOTEXIST";
                        return;
                    }

                    var result = money.ObjectGiveMoney(m_host.ParentGroup.RootPart.UUID,
                        m_host.ParentGroup.RootPart.OwnerID,
                        toID, amount, txn, out var reason);
                    if (result)
                    {
                        replycode = 1;
                        replydata = destination + "," + amount.ToString();
                        return;
                    }

                    replydata = reason;
                }
                finally
                {
                    m_ScriptEngine.PostScriptEvent(m_item.ItemID, new EventParams(
                        "transaction_result", new object[]
                        {
                            new LSL_String(txn.ToString()),
                            new LSL_Integer(replycode),
                            new LSL_String(replydata)
                        },
                        new DetectParams[0]));
                }
            };

            m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            return txn.ToString();
        }
    }
}