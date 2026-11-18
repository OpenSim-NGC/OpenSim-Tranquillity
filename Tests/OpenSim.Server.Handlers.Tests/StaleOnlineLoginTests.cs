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
using OpenSim.Services.Interfaces;
using OpenSim.Services.LLLoginService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A login for a user the grid user record marks online. With a live session somewhere the login still refuses and
/// kicks the old session, as before. With no session at all the record is stale (its simulator stopped without
/// logging the avatar out) and the login goes ahead instead of refusing and making the user try again.
/// </summary>
public class StaleOnlineLoginTests
{
    private sealed class Presence : IPresenceService
    {
        public readonly List<PresenceInfo> Sessions = new();
        public bool LoginAgent(string userID, UUID sessionID, UUID secureSessionID) => true;
        public bool LogoutAgent(UUID sessionID) => true;
        public bool LogoutRegionAgents(UUID regionID) => true;
        public bool ReportAgent(UUID sessionID, UUID regionID) => true;
        public PresenceInfo GetAgent(UUID sessionID) => null;
        public PresenceInfo[] GetAgents(string[] userIDs) => Sessions.Where(s => userIDs.Contains(s.UserID)).ToArray();
    }

    [Fact]
    public void AUserWithNoSessionAnywhereIsAStaleOnlineRecord()
    {
        Presence presence = new();
        presence.Sessions.Add(new PresenceInfo { UserID = UUID.Random().ToString(), RegionID = UUID.Random() });

        Assert.True(LLLoginService.OnlineFlagIsStale(presence, UUID.Random().ToString()));
    }

    [Fact]
    public void AUserWithALiveSessionIsNot()
    {
        string user = UUID.Random().ToString();
        Presence presence = new();
        presence.Sessions.Add(new PresenceInfo { UserID = user, RegionID = UUID.Random() });

        Assert.False(LLLoginService.OnlineFlagIsStale(presence, user));
    }

    [Fact]
    public void WithoutAPresenceServiceNothingIsAssumedStale()
    {
        Assert.False(LLLoginService.OnlineFlagIsStale(null, UUID.Random().ToString()));
    }
}
