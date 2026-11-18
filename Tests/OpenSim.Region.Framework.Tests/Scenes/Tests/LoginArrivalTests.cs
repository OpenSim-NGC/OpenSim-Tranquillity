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

using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Presence;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A login marks the user online once the region has taken its circuit; until the avatar is root there, nothing
/// logged it out again. A viewer that never connected (a cancelled login, a crash, UDP blocked) left the user online
/// until their next login, which was then refused once as a duplicate, and the circuit in the region for good.
/// </summary>
public class LoginArrivalTests : OpenSimTestCase
{
    private static readonly UUID s_lastRegion = TestHelpers.ParseTail(0x900);
    private static readonly TimeSpan s_wait = TimeSpan.FromMilliseconds(100);

    /// <summary>The grid user records, by user: online, and the last region.</summary>
    private sealed class GridUsers : IGridUserService
    {
        public readonly Dictionary<string, GridUserInfo> Users = new();

        public void Online(UUID user)
        {
            lock (Users)
                Users[user.ToString()] = new GridUserInfo
                {
                    UserID = user.ToString(), Online = true, LastRegionID = s_lastRegion,
                    LastPosition = new Vector3(1, 2, 3), LastLookAt = Vector3.UnitY,
                };
        }

        public bool IsOnline(UUID user)
        {
            lock (Users)
                return Users.TryGetValue(user.ToString(), out GridUserInfo g) && g.Online;
        }

        public GridUserInfo LoggedIn(string userID) => throw new NotSupportedException();

        public bool LoggedOut(string userID, UUID sessionID, UUID regionID, Vector3 lastPosition, Vector3 lastLookAt)
        {
            lock (Users)
            {
                if (!Users.TryGetValue(userID, out GridUserInfo g))
                    return false;
                g.Online = false;
                g.LastRegionID = regionID;
                g.LastPosition = lastPosition;
                g.LastLookAt = lastLookAt;
                return true;
            }
        }

        public bool LoggedOutRegion(UUID regionID) => true;
        public bool SetHome(string userID, UUID homeID, Vector3 homePosition, Vector3 homeLookAt) => true;
        public bool SetLastPosition(string userID, UUID sessionID, UUID regionID, Vector3 lastPosition, Vector3 lastLookAt) => true;

        public GridUserInfo GetGridUserInfo(string userID)
        {
            lock (Users)
                return Users.TryGetValue(userID, out GridUserInfo g) ? g : null;
        }

        public GridUserInfo[] GetGridUserInfo(string[] userID) => userID.Select(GetGridUserInfo).ToArray();
    }

    private sealed class Region
    {
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly GridUsers GridUsers = new();

        public Region()
        {
            Scene.RegisterModuleInterface<IGridUserService>(GridUsers);
            Scene.LoginConnectWait = s_wait;
        }

        /// <summary>What the login service does: a presence session, the user online, the region told.</summary>
        public AgentCircuitData Login(UUID user, UUID? session = null)
        {
            AgentCircuitData acd = SceneHelpers.GenerateAgentData(user);
            if (session is UUID s)
                acd.SessionID = s;
            Presence.LoginAgent(user.ToString(), acd.SessionID, acd.SecureSessionID);
            Assert.True(Scene.NewUserConnection(acd, (uint)TeleportFlags.ViaLogin, null, out string reason), reason);
            GridUsers.Online(user);
            return acd;
        }

        public IPresenceService Presence => ((LocalPresenceServicesConnector)Scene.PresenceService).m_PresenceService;

        public bool HasCircuit(UUID user) => Scene.AuthenticateHandler.GetAgentCircuitData(user) is not null;
    }

    private static bool SoonTrue(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                return false;
            Thread.Sleep(20);
        }
        return true;
    }

    [Fact]
    public void ALoginWhoseViewerNeverConnectsIsClosedAndLoggedOut()
    {
        TestHelpers.InMethod();
        Region r = new();
        UUID user = TestHelpers.ParseTail(0x1);
        AgentCircuitData acd = r.Login(user);

        Assert.Contains(user, r.Scene.LoginsAwaitingViewer);

        // the viewer still has time
        r.Scene.ExpireLoginArrivals();
        Assert.True(r.HasCircuit(user));
        Assert.True(r.GridUsers.IsOnline(user));

        Thread.Sleep(s_wait * 2);
        r.Scene.ExpireLoginArrivals();

        Assert.False(r.HasCircuit(user), "the login's circuit is still in the region");
        Assert.Null(r.Presence.GetAgent(acd.SessionID));
        Assert.False(r.GridUsers.IsOnline(user), "the user is still marked online");
        // the user was never in this region: where they last were stays
        Assert.Equal(s_lastRegion, r.GridUsers.GetGridUserInfo(user.ToString()).LastRegionID);
        Assert.Empty(r.Scene.LoginsAwaitingViewer);
    }

    [Fact]
    public void ALoginWhoseAvatarArrivesIsLeftAlone()
    {
        TestHelpers.InMethod();
        Region r = new();
        UUID user = TestHelpers.ParseTail(0x2);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(user);
        r.GridUsers.Online(user);

        ScenePresence sp = SceneHelpers.AddScenePresence(r.Scene, acd);
        Assert.False(sp.IsChildAgent);

        Thread.Sleep(s_wait * 2);
        r.Scene.ExpireLoginArrivals();

        Assert.Empty(r.Scene.LoginsAwaitingViewer);
        Assert.Same(sp, r.Scene.GetScenePresence(user));
        Assert.True(r.HasCircuit(user));
        Assert.NotNull(r.Presence.GetAgent(acd.SessionID));
        Assert.True(r.GridUsers.IsOnline(user));
    }

    [Fact]
    public void AViewerThatLeavesBeforeItFinishesArrivingIsLoggedOut()
    {
        TestHelpers.InMethod();
        Region r = new();
        r.Scene.LoginConnectWait = TimeSpan.FromMinutes(2);
        UUID user = TestHelpers.ParseTail(0x3);
        AgentCircuitData acd = r.Login(user);

        // the viewer connects, then goes before CompleteAgentMovement: the avatar is removed as a child agent, which
        // the presence and activity detectors do not log out
        r.Scene.AddNewAgent(new TestClient(acd, r.Scene), PresenceType.User);
        Assert.True(r.Scene.GetScenePresence(user).IsChildAgent);
        r.Scene.CloseAgent(user, false);

        Assert.True(SoonTrue(() => !r.GridUsers.IsOnline(user)), "the user is still marked online");
        Assert.Null(r.Presence.GetAgent(acd.SessionID));
        Assert.Empty(r.Scene.LoginsAwaitingViewer);
    }

    [Fact]
    public void ALaterLoginOfTheSameUserIsLeftOnline()
    {
        TestHelpers.InMethod();
        Region r = new();
        UUID user = TestHelpers.ParseTail(0x4);
        r.Login(user);

        // the user logs in again, somewhere else: the presence service replaces the first session
        UUID second = UUID.Random();
        r.Presence.LoginAgent(user.ToString(), second, UUID.Random());

        Thread.Sleep(s_wait * 2);
        r.Scene.ExpireLoginArrivals();

        Assert.False(r.HasCircuit(user), "the first login's circuit is still in the region");
        Assert.NotNull(r.Presence.GetAgent(second));
        Assert.True(r.GridUsers.IsOnline(user), "the later login was marked offline");
    }

    [Fact]
    public void NoWaitKeepsTheCircuitAsBefore()
    {
        TestHelpers.InMethod();
        Region r = new();
        r.Scene.LoginConnectWait = TimeSpan.Zero;
        UUID user = TestHelpers.ParseTail(0x5);
        r.Login(user);

        Thread.Sleep(s_wait * 2);
        r.Scene.ExpireLoginArrivals();

        Assert.Empty(r.Scene.LoginsAwaitingViewer);
        Assert.True(r.HasCircuit(user));
        Assert.True(r.GridUsers.IsOnline(user));
    }
}
