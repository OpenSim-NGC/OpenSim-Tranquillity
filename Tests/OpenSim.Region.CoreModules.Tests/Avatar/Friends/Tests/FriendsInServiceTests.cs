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
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace OpenSim.Region.CoreModules.Avatar.Friends.Tests;

/// <summary>
/// IFriendsModule.IsFriendInService: asks the friends service, so the user need not be on this simulator.
/// </summary>
public class FriendsInServiceTests : OpenSimTestCase
{
    private sealed class StubFriendsService : IFriendsService
    {
        public Dictionary<UUID, FriendInfo[]> Lists = new();
        public bool Throw;

        public FriendInfo[] GetFriends(UUID principalID)
        {
            if (Throw)
                throw new InvalidOperationException("service down");
            return Lists.TryGetValue(principalID, out FriendInfo[] l) ? l : Array.Empty<FriendInfo>();
        }

        public FriendInfo[] GetFriends(string principalID) => GetFriends(UUID.Parse(principalID));
        public bool StoreFriend(string principalID, string friend, int flags) => false;
        public bool Delete(UUID principalID, string friend) => false;
        public bool Delete(string principalID, string friend) => false;
    }

    private sealed class ModuleWithService : FriendsModule
    {
        public ModuleWithService(IFriendsService service) { m_FriendsService = service; }
    }

    private static readonly UUID User = UUID.Random();
    private static readonly UUID Friend = UUID.Random();

    private static FriendInfo Entry(UUID friend, int theirFlags) =>
        new FriendInfo { PrincipalID = User, Friend = friend.ToString(), MyFlags = 1, TheirFlags = theirFlags };

    [Fact]
    public void AnAcceptedFriendIsAFriend()
    {
        StubFriendsService svc = new();
        svc.Lists[User] = new[] { Entry(Friend, 1) };
        IFriendsModule fm = new ModuleWithService(svc);

        Assert.True(fm.IsFriendInService(User, Friend));
    }

    [Fact]
    public void AnOfferNotYetAcceptedIsNotAFriend()
    {
        StubFriendsService svc = new();
        svc.Lists[User] = new[] { Entry(Friend, -1) };
        IFriendsModule fm = new ModuleWithService(svc);

        Assert.False(fm.IsFriendInService(User, Friend));
    }

    [Fact]
    public void SomeoneNotOnTheListIsNotAFriend()
    {
        StubFriendsService svc = new();
        svc.Lists[User] = new[] { Entry(UUID.Random(), 1) };
        IFriendsModule fm = new ModuleWithService(svc);

        Assert.False(fm.IsFriendInService(User, Friend));
    }

    [Fact]
    public void AServiceThatFailsAnswersFalse()
    {
        StubFriendsService svc = new() { Throw = true };
        IFriendsModule fm = new ModuleWithService(svc);

        Assert.False(fm.IsFriendInService(User, Friend));
    }
}
