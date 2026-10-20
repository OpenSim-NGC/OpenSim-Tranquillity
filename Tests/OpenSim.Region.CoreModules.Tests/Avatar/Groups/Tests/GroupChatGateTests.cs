/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
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
/// IGroupChatGate lets a module refuse a line of group chat before the groups module delivers it.
/// With no gate registered, delivery is unchanged; with one, the line is delivered only when it says yes.
/// </summary>
public class GroupChatGateTests : OpenSimTestCase
{
    private static readonly UUID GroupId = new("2b6e8d13-5c70-4a29-9f4e-81a3c7d05b92");
    private static readonly UUID SenderId = new("d4a7f180-39c2-4e65-b8d1-6f02a95c3e71");

    /// <summary>A groups service that knows one group and has no members online.</summary>
    public class KnowsOneGroup : DispatchProxy
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
    public class Inert : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Type rt = method!.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private sealed class RecordingGate : IGroupChatGate
    {
        public bool Allow;
        public readonly List<(UUID Group, UUID Sender)> Asked = new();
        public bool MaySend(UUID groupID, UUID senderID) { Asked.Add((groupID, senderID)); return Allow; }
    }

    private readonly List<GridInstantMessage> m_received = new();
    private TestClient m_sender = null!;

    private void SetUpScene(IGroupChatGate? gate)
    {
        Scene scene = new SceneHelpers().SetupScene();

        scene.RegisterModuleInterface<IGroupsServicesConnector>(DispatchProxy.Create<IGroupsServicesConnector, KnowsOneGroup>());
        scene.RegisterModuleInterface<IMessageTransferModule>(DispatchProxy.Create<IMessageTransferModule, Inert>());
        scene.RegisterModuleInterface<IUserManagement>(DispatchProxy.Create<IUserManagement, Inert>());
        if (gate is not null)
            scene.RegisterModuleInterface<IGroupChatGate>(gate);

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

    private void SendToGroup()
    {
        m_sender.HandleImprovedInstantMessage(new GridInstantMessage
        {
            fromAgentID = SenderId.Guid,
            fromAgentName = "Test Sender",
            toAgentID = GroupId.Guid,
            imSessionID = GroupId.Guid,
            dialog = (byte)InstantMessageDialog.SessionSend,
            message = "hello",
            binaryBucket = Array.Empty<byte>(),
        });
    }

    [Fact]
    public void WithNoGateALineIsDelivered()
    {
        TestHelpers.InMethod();
        SetUpScene(null);

        SendToGroup();

        lock (m_received)
            Assert.Equal("hello", Assert.Single(m_received).message);
    }

    [Fact]
    public void AGateThatSaysYesDeliversTheLine_AndWasAskedWhoSentItToWhichGroup()
    {
        TestHelpers.InMethod();
        RecordingGate gate = new() { Allow = true };
        SetUpScene(gate);

        SendToGroup();

        lock (m_received)
            Assert.Equal("hello", Assert.Single(m_received).message);
        Assert.Equal((GroupId, SenderId), Assert.Single(gate.Asked));
    }

    [Fact]
    public void AGateThatSaysNoStopsTheLine()
    {
        TestHelpers.InMethod();
        RecordingGate gate = new() { Allow = false };
        SetUpScene(gate);

        SendToGroup();

        Assert.Single(gate.Asked);
        lock (m_received)
            Assert.Empty(m_received);
    }
}
