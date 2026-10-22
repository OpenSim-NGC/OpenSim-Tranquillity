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

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Groups;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// A SessionSend instant message names the chat session it belongs to. Groups Module V2 handles it when that id
/// is a group's, and leaves it alone otherwise: an ad-hoc conference has its own session id, which the groups service
/// has no record of, and the conference is not the groups module's to deliver (or to echo back to the sender).
/// The SessionGroupStart message already behaves this way: it acts only when the group record exists.
/// </summary>
public class GroupsMessagingSessionSendTests : OpenSimTestCase
{
    private static readonly UUID GroupId = new("5e0c9a41-7b2d-4f86-a3c1-92d84b6e0f17");
    private static readonly UUID ConferenceId = new("c0f3e7a2-18d5-4b94-a6e0-3b72d9c15f48");
    private static readonly UUID SenderId = new("a19f3c6e-2d48-4b07-8e5a-c3f1d9027b64");

    /// <summary>A groups service that knows one group and has no members online.</summary>
    public class FakeGroupsConnector : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IGroupsServicesConnector.GetGroupRecord):
                    UUID id = (UUID)args![1]!;
                    return id == GroupId ? new ExtendedGroupRecord { GroupID = GroupId, GroupName = "Test Group" } : null;
                case nameof(IGroupsServicesConnector.GetGroupMembers):
                    return new List<GroupMembersData>();
                default:
                    Type rt = method.ReturnType;
                    return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }
    }

    /// <summary>Stands in for services the module only needs to find.</summary>
    public class InertService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Type rt = method!.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private readonly List<GridInstantMessage> m_received = new();
    private TestClient m_sender = null!;

    private void SetUpScene()
    {
        Scene scene = new SceneHelpers().SetupScene();

        scene.RegisterModuleInterface<IGroupsServicesConnector>(
            DispatchProxy.Create<IGroupsServicesConnector, FakeGroupsConnector>());
        scene.RegisterModuleInterface<IMessageTransferModule>(DispatchProxy.Create<IMessageTransferModule, InertService>());
        scene.RegisterModuleInterface<IUserManagement>(DispatchProxy.Create<IUserManagement, InertService>());

        GroupsMessagingModule module = new GroupsMessagingModule();
        IConfigSource config = new IniConfigSource();
        IConfig groupsConfig = config.AddConfig("Groups");
        groupsConfig.Set("Enabled", true);
        groupsConfig.Set("MessagingModule", module.Name);
        groupsConfig.Set("MessageOnlineUsersOnly", true);
        SceneHelpers.SetupSceneModules(scene, config, module);

        m_sender = (TestClient)SceneHelpers.AddScenePresence(scene, SenderId).ControllingClient;
        m_sender.OnReceivedInstantMessage += im => { lock (m_received) m_received.Add(im); };
    }

    private void SendSessionSend(UUID sessionId)
    {
        GridInstantMessage im = new GridInstantMessage
        {
            fromAgentID = SenderId.Guid,
            fromAgentName = "Test Sender",
            toAgentID = sessionId.Guid,
            imSessionID = sessionId.Guid,
            dialog = (byte)InstantMessageDialog.SessionSend,
            message = "hello",
            binaryBucket = Array.Empty<byte>(),
        };
        m_sender.HandleImprovedInstantMessage(im);
    }

    [Fact]
    public void ASessionSendForAConferenceIsNotEchoedBackToTheSender()
    {
        TestHelpers.InMethod();
        SetUpScene();

        SendSessionSend(ConferenceId);

        lock (m_received)
            Assert.Empty(m_received);
    }

    [Fact]
    public void ASessionSendForAGroupIsStillDeliveredToTheSender()
    {
        TestHelpers.InMethod();
        SetUpScene();

        SendSessionSend(GroupId);

        lock (m_received)
        {
            GridInstantMessage im = Assert.Single(m_received);
            Assert.Equal("hello", im.message);
            Assert.Equal(GroupId.Guid, im.imSessionID);
        }
    }
}
