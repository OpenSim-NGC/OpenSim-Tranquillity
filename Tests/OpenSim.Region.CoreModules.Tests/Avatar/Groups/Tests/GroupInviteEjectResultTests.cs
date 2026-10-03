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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

using XmlRpcGroups = OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups;
using V2Groups = OpenSim.Groups;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// IGroupsModule.InviteGroup and EjectGroupMember say whether the groups service did what was asked, in both
/// groups modules (the XML-RPC one and Groups Module V2). The groups service is a stand-in that refuses an
/// invite or an eject when the acting agent lacks the power, as the V2 service does
/// (Addons/OpenSim.Addons.Groups/Service/GroupsService.cs RemoveAgentFromGroup and AddAgentToGroupInvite).
/// </summary>
public class GroupInviteEjectResultTests : OpenSimTestCase
{
    private static readonly UUID GroupId = new("5e0c9a41-7b2d-4f86-a3c1-92d84b6e0f17");
    private static readonly UUID ActorId = new("a19f3c6e-2d48-4b07-8e5a-c3f1d9027b64");
    private static readonly UUID TargetId = new("0d7e2b95-c4a1-4f38-9b62-e815a3f7c049");

    /// <summary>The groups service's state, shared by both stand-in connectors.</summary>
    private sealed class FakeGroupsService
    {
        public bool ActorHasPowers;
        public readonly HashSet<UUID> Members = new();
        public readonly Dictionary<UUID, UUID> Invites = new();   // invite id -> invitee

        public bool Invite(UUID inviteId, UUID invitee)
        {
            if (!ActorHasPowers || Members.Contains(invitee))
                return false;
            Invites[inviteId] = invitee;
            return true;
        }

        public void Remove(UUID requester, UUID agent)
        {
            if (requester == agent || ActorHasPowers)
                Members.Remove(agent);
        }
    }

    /// <summary>
    /// A stand-in for an interface: each call goes to the handler, and a call the handler does not answer
    /// returns the return type's default.
    /// </summary>
    public class Stand<T> : DispatchProxy where T : class
    {
        private Func<string, object[], object> m_handler;

        public static T Create(Func<string, object[], object> handler)
        {
            T proxy = Create<T, Stand<T>>();
            ((Stand<T>)(object)proxy).m_handler = handler;
            return proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            object result = m_handler(method.Name, args ?? Array.Empty<object>());
            if (result is not null)
                return result;
            Type rt = method.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static IConfigSource GroupsConfig(string module)
    {
        IConfigSource source = new IniConfigSource();
        IConfig config = source.AddConfig("Groups");
        config.Set("Enabled", true);
        config.Set("Module", module);
        return source;
    }

    private static void RegisterCommonStandIns(TestScene scene)
    {
        scene.RegisterModuleInterface<IMessageTransferModule>(Stand<IMessageTransferModule>.Create((_, _) => null));
        scene.RegisterModuleInterface<IUserManagement>(Stand<IUserManagement>.Create((_, _) => null));
    }

    private static IGroupsModule XmlRpcModule(FakeGroupsService svc)
    {
        TestScene scene = new SceneHelpers().SetupScene();
        RegisterCommonStandIns(scene);
        scene.RegisterModuleInterface<XmlRpcGroups.IGroupsServicesConnector>(
            Stand<XmlRpcGroups.IGroupsServicesConnector>.Create((name, a) => name switch
            {
                "GetGroupRecord" => new GroupRecord { GroupID = GroupId, GroupName = "Example Group" },
                "AddAgentToGroupInvite" => Discard(svc.Invite((UUID)a[1], (UUID)a[4])),
                "GetAgentToGroupInvite" => svc.Invites.TryGetValue((UUID)a[1], out UUID who)
                    ? new XmlRpcGroups.GroupInviteInfo { InviteID = (UUID)a[1], GroupID = GroupId, AgentID = who }
                    : null,
                "RemoveAgentFromGroup" => Discard(Do(() => svc.Remove((UUID)a[0], (UUID)a[1]))),
                "GetAgentGroupMembership" => svc.Members.Contains((UUID)a[1])
                    ? new GroupMembershipData { GroupID = GroupId }
                    : null,
                _ => null
            }));

        XmlRpcGroups.GroupsModule gm = new();
        SceneHelpers.SetupSceneModules(scene, GroupsConfig("GroupsModule"), gm);
        return gm;
    }

    private static IGroupsModule V2Module(FakeGroupsService svc)
    {
        TestScene scene = new SceneHelpers().SetupScene();
        RegisterCommonStandIns(scene);
        scene.RegisterModuleInterface<V2Groups.IGroupsServicesConnector>(
            Stand<V2Groups.IGroupsServicesConnector>.Create((name, a) => name switch
            {
                "GetGroupRecord" => new V2Groups.ExtendedGroupRecord { GroupID = GroupId, GroupName = "Example Group" },
                "AddAgentToGroupInvite" => svc.Invite((UUID)a[1], UUID.Parse((string)a[4])),
                "RemoveAgentFromGroup" => Discard(Do(() => svc.Remove(UUID.Parse((string)a[0]), UUID.Parse((string)a[1])))),
                "GetAgentGroupMembership" => svc.Members.Contains(UUID.Parse((string)a[1]))
                    ? new V2Groups.ExtendedGroupMembershipData { GroupID = GroupId }
                    : null,
                _ => null
            }));

        V2Groups.GroupsModule gm = new();
        SceneHelpers.SetupSceneModules(scene, GroupsConfig("Groups Module V2"), gm);
        return gm;
    }

    private static object Discard(object _) => null;
    private static object Do(Action a) { a(); return null; }

    public static IEnumerable<object[]> Modules() => new[] { new object[] { "XmlRpc" }, new object[] { "V2" } };

    private static IGroupsModule Module(string which, FakeGroupsService svc)
        => which == "XmlRpc" ? XmlRpcModule(svc) : V2Module(svc);

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnInviteTheServiceRecordsReportsTrue(string which)
    {
        FakeGroupsService svc = new() { ActorHasPowers = true };
        IGroupsModule gm = Module(which, svc);

        Assert.True(gm.InviteGroup(null, ActorId, GroupId, TargetId, UUID.Zero));
        Assert.Single(svc.Invites);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnInviteTheServiceRefusesReportsFalse(string which)
    {
        FakeGroupsService svc = new() { ActorHasPowers = false };
        IGroupsModule gm = Module(which, svc);

        Assert.False(gm.InviteGroup(null, ActorId, GroupId, TargetId, UUID.Zero));
        Assert.Empty(svc.Invites);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnInviteOfSomeoneAlreadyInTheGroupReportsFalse(string which)
    {
        FakeGroupsService svc = new() { ActorHasPowers = true };
        svc.Members.Add(TargetId);
        IGroupsModule gm = Module(which, svc);

        Assert.False(gm.InviteGroup(null, ActorId, GroupId, TargetId, UUID.Zero));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnEjectThatRemovesTheMemberReportsTrue(string which)
    {
        FakeGroupsService svc = new() { ActorHasPowers = true };
        svc.Members.Add(TargetId);
        IGroupsModule gm = Module(which, svc);

        Assert.True(gm.EjectGroupMember(null, ActorId, GroupId, TargetId));
        Assert.DoesNotContain(TargetId, svc.Members);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnEjectTheServiceRefusesReportsFalse(string which)
    {
        FakeGroupsService svc = new() { ActorHasPowers = false };
        svc.Members.Add(TargetId);
        IGroupsModule gm = Module(which, svc);

        Assert.False(gm.EjectGroupMember(null, ActorId, GroupId, TargetId));
        Assert.Contains(TargetId, svc.Members);
    }
}
