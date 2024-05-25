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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSim.Framework;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using OpenSim.Server.Base;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.Avatar.Friends
{
    public class HGStatusNotifier
    {
        private static ILogger? m_logger;

        private HGFriendsModule m_FriendsModule;

        public HGStatusNotifier(HGFriendsModule friendsModule)
        {
            m_logger ??= OpenSimServer.Instance.ServiceProvider.GetRequiredService<ILogger<HGStatusNotifier>>();
            m_FriendsModule = friendsModule;
        }

        public void Notify(UUID userID, Dictionary<string, List<FriendInfo>> friendsPerDomain, bool online)
        {
            if(m_FriendsModule is null)
                return;

            foreach (KeyValuePair<string, List<FriendInfo>> kvp in friendsPerDomain)
            {
                // For the others, call the user agent service
                List<string> ids = new(kvp.Value.Count);
                foreach (FriendInfo f in kvp.Value)
                    ids.Add(f.Friend);

                if (ids.Count == 0)
                    continue; // no one to notify. caller don't do this

                //m_log.DebugFormat("[HG STATUS NOTIFIER]: Notifying {0} friends in {1}", ids.Count, kvp.Key);
                // ASSUMPTION: we assume that all users for one home domain
                // have exactly the same set of service URLs.
                // If this is ever not true, we need to change this.
                if (Util.ParseUniversalUserIdentifier(ids[0], out UUID friendID))
                {
                    string friendsServerURI = m_FriendsModule.UserManagementModule.GetUserServerURL(friendID, "FriendsServerURI");
                    if (!string.IsNullOrEmpty(friendsServerURI))
                    {
                        HGFriendsServicesConnector fConn = new(friendsServerURI);

                        List<UUID> friendsOnline = fConn.StatusNotification(ids, userID, online);

                        if (friendsOnline.Count > 0)
                        {
                            IClientAPI client = m_FriendsModule.LocateClientObject(userID);
                            if(client is not null)
                            {
                                m_FriendsModule.CacheFriendsOnline(userID, friendsOnline, online);
                                if(online)
                                    client?.SendAgentOnline(friendsOnline.ToArray());
                                else
                                    client?.SendAgentOffline(friendsOnline.ToArray());
                            }
                        }
                    }
                }
            }
        }
    }
}
