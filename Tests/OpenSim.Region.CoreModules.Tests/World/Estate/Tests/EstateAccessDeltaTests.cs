/*
 * Copyright (c) Legion Builds
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

using System.Collections.Concurrent;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Estate.Tests;

/// <summary>
/// The viewer's "estateaccessdelta" request (EstateOwnerMessage) as EstateManagementModule handles it: who may change
/// the estate's allowed users, allowed groups, ban list and manager list, and who is told the change failed.
///
/// As in LL's viewer (LLPanelEstateAccess::updateControls, llfloaterregioninfo.cpp): the owner and the estate managers
/// change the allowed users, allowed groups and bans; only the owner changes the manager list.
///
/// The request goes through the module's real path: the client event, the request queue, and the loop that applies
/// the changes, stores the estate and sends the lists back. That loop runs only while the scene is running, which only
/// Scene.Start sets (it also starts the heartbeat); the tests set the scene's running flag directly instead, so no
/// heartbeat runs. The tests wait for the module's reply, not for a clock.
/// </summary>
public class EstateAccessDeltaTests : OpenSimTestCase
{
    // Flags of the "estateaccessdelta" request, as LL defines them (llregionflags.h ESTATE_ACCESS_*).
    private const int AllowedAgentAdd = 1 << 2;
    private const int AllowedAgentRemove = 1 << 3;
    private const int AllowedGroupAdd = 1 << 4;
    private const int AllowedGroupRemove = 1 << 5;
    private const int BannedAgentAdd = 1 << 6;
    private const int BannedAgentRemove = 1 << 7;
    private const int ManagerAdd = 1 << 8;
    private const int ManagerRemove = 1 << 9;

    private const string FailedAlert = "Method EstateAccess Failed, you don't have permissions";

    private static readonly TimeSpan s_replyTimeout = TimeSpan.FromSeconds(30);

    private static readonly UUID s_owner = new UUID("3b8e6f1a-92c4-4d07-a5e1-7c0d4b9f2e61");
    private static readonly UUID s_manager = new UUID("a46d0c2e-5f17-4b83-9e2a-1d8c7b6f0e35");
    private static readonly UUID s_stranger = new UUID("e1f97a4b-0c3d-4e58-b6a2-9f5e8d1c7a04");
    private static readonly UUID s_target = new UUID("5c2a8e7d-41b9-4f60-83d5-2e9b0a6c1f78");
    private static readonly UUID s_otherManager = new UUID("97d4b1e6-2a3f-4c8e-b057-6e1a9c3d8f20");

    private Scene m_scene = null!;
    private EstateSettings m_estate = null!;
    private RecordingEstateStore m_store = null!;

    public override void SetUp()
    {
        base.SetUp();

        IConfigSource config = new IniConfigSource();
        m_scene = new SceneHelpers().SetupScene();
        m_scene.RegisterModuleInterface<IEstateDataService>(RecordingEstateStore.Create(out m_store));
        SceneHelpers.SetupSceneModules(m_scene, config, new EstateManagementModule(), new DefaultPermissionsModule());

        m_estate = m_scene.RegionInfo.EstateSettings;
        m_estate.EstateOwner = s_owner;
        m_estate.AddEstateManager(s_manager);
        m_estate.AddEstateManager(s_otherManager);

        SetSceneRunning(true);
    }

    public override void Dispose()
    {
        // The request loop ends when it next sees the scene is not running.
        SetSceneRunning(false);
        base.Dispose();
    }

    private void SetSceneRunning(bool running)
    {
        FieldInfo? field = typeof(Scene).GetField("m_isRunning", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(m_scene, running);
    }

    /// <summary>What one client was sent by the region.</summary>
    private sealed class Received
    {
        public readonly ConcurrentQueue<string> Alerts = new();
        public readonly ConcurrentQueue<int> EstateListCodes = new();
        public readonly SemaphoreSlim Replies = new(0);

        public Received(TestClient client)
        {
            client.OnReceivedAlertMessage += m => Alerts.Enqueue(m);
            client.OnReceivedEstateList += (invoice, code, data, estateID) =>
            {
                EstateListCodes.Enqueue(code);
                Replies.Release();
            };
        }

        /// <summary>Waits for the next list the region sends back, which it sends after the whole batch is applied.</summary>
        public void WaitForReply()
        {
            Assert.True(Replies.Wait(s_replyTimeout), "the region sent no estate list back");
        }
    }

    private (TestClient client, Received received) Connect(UUID agent)
    {
        TestClient client = (TestClient)SceneHelpers.AddScenePresence(m_scene, agent).ControllingClient;
        return (client, new Received(client));
    }

    /// <summary>
    /// Requests are handled in order. An allowed-user add from the owner is answered with the lists only after every
    /// request queued before it has been applied, so when that reply arrives the earlier request is finished.
    /// </summary>
    private void WaitUntilEarlierRequestsAreHandled(TestClient ownerClient, Received ownerReceived)
    {
        ownerClient.TriggerEstateAccessDelta(UUID.Random(), AllowedAgentAdd, UUID.Random());
        ownerReceived.WaitForReply();
    }

    [Theory]
    [InlineData(AllowedAgentAdd)]
    [InlineData(AllowedGroupAdd)]
    [InlineData(BannedAgentAdd)]
    public void AnEstateManagersAddIsAppliedWithNoFailureAlert(int flag)
    {
        TestHelpers.InMethod();
        (TestClient manager, Received received) = Connect(s_manager);

        manager.TriggerEstateAccessDelta(UUID.Random(), flag, s_target);
        received.WaitForReply();

        Assert.True(IsOnList(flag, s_target), "the manager's change was not applied");
        Assert.True(m_store.Stores > 0, "the changed estate was not stored");
        Assert.Empty(received.Alerts);
    }

    [Theory]
    [InlineData(AllowedAgentRemove)]
    [InlineData(AllowedGroupRemove)]
    [InlineData(BannedAgentRemove)]
    public void AnEstateManagersRemoveIsAppliedWithNoFailureAlert(int flag)
    {
        TestHelpers.InMethod();
        PutOnList(flag, s_target);
        (TestClient manager, Received received) = Connect(s_manager);

        manager.TriggerEstateAccessDelta(UUID.Random(), flag, s_target);
        received.WaitForReply();

        Assert.False(IsOnList(flag, s_target), "the manager's change was not applied");
        Assert.Empty(received.Alerts);
    }

    [Fact]
    public void TheOwnersAccessChangeIsAppliedWithNoAlert()
    {
        TestHelpers.InMethod();
        (TestClient owner, Received received) = Connect(s_owner);

        owner.TriggerEstateAccessDelta(UUID.Random(), AllowedAgentAdd, s_target);
        received.WaitForReply();

        Assert.Contains(s_target, m_estate.EstateAccess);
        Assert.Empty(received.Alerts);
    }

    [Fact]
    public void TheOwnerAddsAndRemovesEstateManagersWithNoAlert()
    {
        TestHelpers.InMethod();
        (TestClient owner, Received received) = Connect(s_owner);

        owner.TriggerEstateAccessDelta(UUID.Random(), ManagerAdd, s_target);
        received.WaitForReply();
        Assert.Contains(s_target, m_estate.EstateManagers);

        owner.TriggerEstateAccessDelta(UUID.Random(), ManagerRemove, s_otherManager);
        received.WaitForReply();
        Assert.DoesNotContain(s_otherManager, m_estate.EstateManagers);

        Assert.All(received.EstateListCodes, code => Assert.Equal((int)Constants.EstateAccessCodex.EstateManagers, code));
        Assert.Empty(received.Alerts);
    }

    [Fact]
    public void AnEstateManagerCannotAddAnEstateManager()
    {
        TestHelpers.InMethod();
        (TestClient manager, Received received) = Connect(s_manager);
        (TestClient owner, Received ownerReceived) = Connect(s_owner);

        manager.TriggerEstateAccessDelta(UUID.Random(), ManagerAdd, s_target);
        WaitUntilEarlierRequestsAreHandled(owner, ownerReceived);

        Assert.DoesNotContain(s_target, m_estate.EstateManagers);
        Assert.Equal(new[] { FailedAlert }, received.Alerts.ToArray());
        Assert.Empty(received.EstateListCodes);
    }

    [Fact]
    public void AnEstateManagerCannotRemoveAnEstateManager()
    {
        TestHelpers.InMethod();
        (TestClient manager, Received received) = Connect(s_manager);
        (TestClient owner, Received ownerReceived) = Connect(s_owner);

        manager.TriggerEstateAccessDelta(UUID.Random(), ManagerRemove, s_otherManager);
        WaitUntilEarlierRequestsAreHandled(owner, ownerReceived);

        Assert.Contains(s_otherManager, m_estate.EstateManagers);
        Assert.Equal(new[] { FailedAlert }, received.Alerts.ToArray());
        Assert.Empty(received.EstateListCodes);
    }

    [Fact]
    public void AnEstateManagersRequestThatAlsoAddsAManagerHasOnlyTheAllowedPartApplied()
    {
        TestHelpers.InMethod();
        (TestClient manager, Received received) = Connect(s_manager);

        manager.TriggerEstateAccessDelta(UUID.Random(), AllowedAgentAdd | ManagerAdd, s_target);
        received.WaitForReply();

        Assert.Contains(s_target, m_estate.EstateAccess);
        Assert.DoesNotContain(s_target, m_estate.EstateManagers);
        Assert.Equal(new[] { FailedAlert }, received.Alerts.ToArray());
    }

    [Theory]
    [InlineData(AllowedAgentAdd)]
    [InlineData(BannedAgentAdd)]
    [InlineData(ManagerAdd)]
    public void SomeoneWithNoEstateRightsIsRefused(int flag)
    {
        TestHelpers.InMethod();
        (TestClient stranger, Received received) = Connect(s_stranger);
        (TestClient owner, Received ownerReceived) = Connect(s_owner);

        stranger.TriggerEstateAccessDelta(UUID.Random(), flag, s_target);
        WaitUntilEarlierRequestsAreHandled(owner, ownerReceived);

        Assert.False(IsOnList(flag, s_target), "a change from someone with no estate rights was applied");
        Assert.Equal(new[] { FailedAlert }, received.Alerts.ToArray());
        Assert.Empty(received.EstateListCodes);
    }

    private bool IsOnList(int flag, UUID id)
    {
        if ((flag & (AllowedAgentAdd | AllowedAgentRemove)) != 0)
            return m_estate.EstateAccess.Contains(id);
        if ((flag & (AllowedGroupAdd | AllowedGroupRemove)) != 0)
            return m_estate.EstateGroups.Contains(id);
        if ((flag & (BannedAgentAdd | BannedAgentRemove)) != 0)
            return m_estate.IsBanned(id);
        return m_estate.EstateManagers.Contains(id);
    }

    private void PutOnList(int flag, UUID id)
    {
        if ((flag & AllowedAgentRemove) != 0)
            m_estate.AddEstateUser(id);
        else if ((flag & AllowedGroupRemove) != 0)
            m_estate.AddEstateGroup(id);
        else if ((flag & BannedAgentRemove) != 0)
            m_estate.AddBan(new EstateBan { BannedUserID = id, EstateID = m_estate.EstateID, BanningUserID = s_owner });
        Assert.True(IsOnList(flag, id));
    }
}

/// <summary>An IEstateDataService that counts StoreEstateSettings and does nothing else.</summary>
public class RecordingEstateStore : DispatchProxy
{
    private int m_stores;

    public int Stores => Volatile.Read(ref m_stores);

    public static IEstateDataService Create(out RecordingEstateStore recorder)
    {
        IEstateDataService proxy = Create<IEstateDataService, RecordingEstateStore>();
        recorder = (RecordingEstateStore)(object)proxy;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IEstateDataService.StoreEstateSettings))
            Interlocked.Increment(ref m_stores);
        Type rt = method!.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
