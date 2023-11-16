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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Services.Interfaces;
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
        public void llWhisper(int channelID, string text)
        {
            var binText = Utils.StringToBytesNoTerm(text, 1023);
            World.SimChat(binText,
                ChatTypeEnum.Whisper, channelID, m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);

            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm != null)
                wComm.DeliverMessage(ChatTypeEnum.Whisper, channelID, m_host.Name, m_host.UUID,
                    Util.UTF8.GetString(binText), m_host.AbsolutePosition);
        }


        public void llSay(int channelID, string text)
        {
            if (channelID == 0)
//                m_SayShoutCount++;
                CheckSayShoutTime();

            if (m_SayShoutCount >= 11)
                ScriptSleep(2000);

            if (m_scriptConsoleChannelEnabled && channelID == m_scriptConsoleChannel)
            {
                Console.WriteLine(text);
            }
            else
            {
                var binText = Utils.StringToBytesNoTerm(text, 1023);
                World.SimChat(binText,
                    ChatTypeEnum.Say, channelID, m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);

                var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
                if (wComm != null)
                    wComm.DeliverMessage(ChatTypeEnum.Say, channelID, m_host.Name, m_host.UUID,
                        Util.UTF8.GetString(binText), m_host.AbsolutePosition);
            }
        }

        public void llShout(int channelID, string text)
        {
            if (channelID == 0)
//                m_SayShoutCount++;
                CheckSayShoutTime();

            if (m_SayShoutCount >= 11)
                ScriptSleep(2000);

            var binText = Utils.StringToBytesNoTerm(text, 1023);

            World.SimChat(binText,
                ChatTypeEnum.Shout, channelID, m_host.AbsolutePosition, m_host.Name, m_host.UUID, true);

            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm != null)
                wComm.DeliverMessage(ChatTypeEnum.Shout, channelID, m_host.Name, m_host.UUID,
                    Util.UTF8.GetString(binText), m_host.AbsolutePosition);
        }

        public void llRegionSay(int channelID, string text)
        {
            if (channelID == 0)
            {
                Error("llRegionSay", "Cannot use on channel 0");
                return;
            }

            var binText = Utils.StringToBytesNoTerm(text, 1023);

            // debug channel is also sent to avatars
            if (channelID == ScriptBaseClass.DEBUG_CHANNEL)
                World.SimChat(binText,
                    ChatTypeEnum.Shout, channelID, m_host.ParentGroup.RootPart.AbsolutePosition, m_host.Name,
                    m_host.UUID, true);

            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm != null)
                wComm.DeliverMessage(ChatTypeEnum.Region, channelID, m_host.Name, m_host.UUID,
                    Util.UTF8.GetString(binText));
        }

        public void llRegionSayTo(string target, int channel, string msg)
        {
            if (channel == ScriptBaseClass.DEBUG_CHANNEL)
                return;

            if (UUID.TryParse(target, out var TargetID) && TargetID.IsNotZero())
            {
                var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
                if (wComm != null)
                {
                    if (msg.Length > 1023)
                        msg = msg.Substring(0, 1023);

                    wComm.DeliverMessageTo(TargetID, channel, m_host.AbsolutePosition, m_host.Name, m_host.UUID, msg);
                }
            }
        }

        public void llInstantMessage(string userKey, string message)
        {
            if (m_TransferModule == null || string.IsNullOrEmpty(message))
                return;

            if (!UUID.TryParse(userKey, out var userID) || userID.IsZero())
            {
                Error("llInstantMessage", "An invalid key  was passed to llInstantMessage");
                ScriptSleep(2000);
                return;
            }

            var pos = m_host.AbsolutePosition;
            var msg = new GridInstantMessage
            {
                fromAgentID = m_host.OwnerID.Guid,
                toAgentID = userID.Guid,
                imSessionID = m_host.UUID.Guid, // This is the item we're mucking with here
                timestamp = (uint)Util.UnixTimeSinceEpoch(),
                fromAgentName = m_host.Name, //client.FirstName + " " + client.LastName;// fromAgentName;
                dialog = 19, // MessageFromObject
                fromGroup = false,
                offline = 0,
                ParentEstateID = World.RegionInfo.EstateSettings.EstateID,
                Position = pos,
                RegionID = World.RegionInfo.RegionID.Guid,
                message = message.Length > 1024 ? message.Substring(0, 1024) : message,
                binaryBucket =
                    Util.StringToBytes256("{0}/{1}/{2}/{3}", m_regionName, (int)pos.X, (int)pos.Y, (int)pos.Z)
            };

            m_TransferModule?.SendInstantMessage(msg, delegate { });
            ScriptSleep(m_sleepMsOnInstantMessage);
        }

        public void llEmail(string address, string subject, string message)
        {
            if (m_emailModule == null)
            {
                Error("llEmail", "Email module not configured");
                return;
            }

            // this is a fire and forget no event is sent to script
            Action<string> act = eventID =>
            {
                //Restrict email destination to the avatars registered email address?
                //The restriction only applies if the destination address is not local.
                if (m_restrictEmail && address.Contains(m_internalObjectHost) == false)
                {
                    var account = m_userAccountService.GetUserAccount(RegionScopeID, m_host.OwnerID);
                    if (account == null)
                        return;

                    if (string.IsNullOrEmpty(account.Email))
                        return;
                    address = account.Email;
                }

                m_emailModule.SendEmail(m_host.UUID, m_host.ParentGroup.OwnerID, address, subject, message);
                // no dataserver event
            };

            m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId,
                m_item.ItemID, act);
            ScriptSleep(m_sleepMsOnEmail);
        }

        public void llGetNextEmail(string address, string subject)
        {
            if (m_emailModule == null)
            {
                Error("llGetNextEmail", "Email module not configured");
                return;
            }

            Email email;

            email = m_emailModule.GetNextEmail(m_host.UUID, address, subject);

            if (email == null)
                return;

            m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                new EventParams("email",
                    new object[]
                    {
                        new LSL_String(email.time),
                        new LSL_String(email.sender),
                        new LSL_String(email.subject),
                        new LSL_String(email.message),
                        new LSL_Integer(email.numLeft)
                    },
                    new DetectParams[0]));
        }

        public void llOwnerSay(string msg)
        {
            if (m_host.OwnerID.Equals(m_host.GroupID))
                return;
            World.SimChatBroadcast(msg, ChatTypeEnum.Owner, 0,
                m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
        }

        public void llTargetedEmail(LSL_Integer target, LSL_String subject, LSL_String message)
        {
            var parent = m_host.ParentGroup;
            if (parent == null || parent.IsDeleted)
                return;

            if (m_emailModule == null)
            {
                Error("llTargetedEmail", "Email module not configured");
                return;
            }

            if (subject.Length + message.Length > 4096)
            {
                Error("llTargetedEmail", "Message is too large");
                return;
            }

            // this is a fire and forget no event is sent to script
            Action<string> act = eventID =>
            {
                UserAccount account = null;
                if (target == ScriptBaseClass.TARGETED_EMAIL_OBJECT_OWNER)
                {
                    if (parent.OwnerID.Equals(parent.GroupID))
                        return;
                    account = m_userAccountService.GetUserAccount(RegionScopeID, parent.OwnerID);
                }
                else if (target == ScriptBaseClass.TARGETED_EMAIL_ROOT_CREATOR)
                {
                    // non standard avoid creator spam
                    if (m_item.CreatorID.Equals(parent.RootPart.CreatorID))
                        account = m_userAccountService.GetUserAccount(RegionScopeID, parent.RootPart.CreatorID);
                    else
                        return;
                }
                else
                {
                    return;
                }

                if (account == null) return;

                var address = account.Email;
                if (string.IsNullOrEmpty(address)) return;

                m_emailModule.SendEmail(m_host.UUID, m_host.ParentGroup.OwnerID, address, subject, message);
            };

            m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId,
                m_item.ItemID, act);
            ScriptSleep(m_sleepMsOnEmail);
        }
    }
}