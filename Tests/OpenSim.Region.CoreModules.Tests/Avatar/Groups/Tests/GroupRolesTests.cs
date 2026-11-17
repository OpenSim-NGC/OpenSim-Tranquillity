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

using System.Reflection;
using System.Runtime.CompilerServices;
using OpenMetaverse;
using Xunit;

using OpenSim.Data;
using OpenSim.Groups;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// The groups service's roles, as SL has them (https://wiki.secondlife.com/wiki/Group): a member's abilities are those
/// of every role they hold; "Owners always have all available abilities"; the title a member shows is one of their
/// roles and changes nothing else; an invite-only group is joined only by invitation; "An Owner can eject anyone
/// except another Owner", others only members who are in nothing but Everyone; "Owners can't be removed"; only owners
/// make owners; removing from roles is "Remove Members from Roles"; the owners role keeps its powers and name.
/// </summary>
public class GroupRolesTests : OpenSimTestCase
{
    private const ulong Build = (ulong)GroupPowers.ObjectManipulate;
    /// <summary>The viewer's GP_ALL_POWERS, which owners have.</summary>
    private const ulong AllPowers = ulong.MaxValue;

    private sealed class Fixture
    {
        public readonly InMemoryGroups Db = new();
        public readonly GroupsService Service;
        public readonly string Founder = Agent(0x1), Officer = Agent(0x2), Member = Agent(0x3), Other = Agent(0x4);
        public readonly UUID Group;
        public readonly UUID OwnersRole, OfficersRole, BuildersRole, AssignersRole;

        public Fixture(bool openEnrollment = false)
        {
            Service = (GroupsService)RuntimeHelpers.GetUninitializedObject(typeof(GroupsService));
            typeof(GroupsServiceBase).GetField("m_Database", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Service, Db);
            typeof(GroupsServiceBase).GetField("m_GridUserService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, NoGridUsers.Create());

            Group = Service.CreateGroup(Founder, "Example", "", true, UUID.Zero, 0, openEnrollment, false, false, new UUID(Founder), out _);
            OwnersRole = new UUID(Db.Groups[Group].Data["OwnerRoleID"]);
            OfficersRole = Db.Roles.Values.Single(r => r.GroupID == Group && r.Data["Name"] == "Officers").RoleID;

            BuildersRole = UUID.Random();
            AssignersRole = UUID.Random();
            Assert.True(Service.AddGroupRole(Founder, Group, BuildersRole, "Builders", "", "Builder", Build, out _));
            Assert.True(Service.AddGroupRole(Founder, Group, AssignersRole, "Assigners", "", "Assigner",
                (ulong)(GroupPowers.AssignMember | GroupPowers.ChangeActions), out _));

            Join(Officer, OfficersRole);
            Join(Member, UUID.Zero);
        }

        /// <summary>The founder invites <paramref name="agent"/> into <paramref name="role"/> and they accept.</summary>
        public void Join(string agent, UUID role)
        {
            Assert.True(Service.AddAgentToGroupInvite(Founder, UUID.Random(), Group, role, agent));
            Assert.True(Service.AddAgentToGroup(agent, agent, Group, role, "", out _));
            Db.Invites.Clear();
        }

        public ulong Powers(string agent) => Service.GetAgentGroupMembership(agent, agent, Group).GroupPowers;
        public string Title(string agent) => Service.GetAgentGroupMembership(agent, agent, Group).GroupTitle;
        public bool IsMember(string agent) => Db.Members.ContainsKey((Group, agent));
        public bool Holds(string agent, UUID role) => Db.RoleMembers.Contains((Group, role, agent));
    }

    private static string Agent(int tail) => TestHelpers.ParseTail(tail).ToString();

    // ── how roles combine ──────────────────────────────────────────────────────

    [Fact]
    public void AMembersPowersAreThoseOfEveryRoleTheyHold()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        Assert.True(f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole));

        ulong powers = f.Powers(f.Member);

        Assert.Equal(Build, powers & Build);
        Assert.Equal((ulong)GroupsService.DefaultEveryonePowers, powers & (ulong)GroupsService.DefaultEveryonePowers);
    }

    [Fact]
    public void ChangingTheTitleChangesNoPower()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole);

        f.Service.SetAgentActiveGroupRole(f.Member, f.Member, f.Group, f.BuildersRole);
        ulong asBuilder = f.Powers(f.Member);
        f.Service.SetAgentActiveGroupRole(f.Member, f.Member, f.Group, UUID.Zero);

        Assert.Equal(asBuilder, f.Powers(f.Member));
        Assert.Equal("Member of Example", f.Title(f.Member));
    }

    [Fact]
    public void OwnersHaveEveryPower()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        f.Service.SetAgentActiveGroupRole(f.Founder, f.Founder, f.Group, UUID.Zero);

        Assert.Equal(AllPowers, f.Powers(f.Founder));
    }

    // ── titles ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AMemberCannotShowTheTitleOfARoleTheyDoNotHold()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        f.Service.SetAgentActiveGroupRole(f.Member, f.Member, f.Group, f.OwnersRole);

        Assert.Equal("Member of Example", f.Title(f.Member));
        Assert.NotEqual(AllPowers, f.Powers(f.Member));
    }

    [Fact]
    public void ANewRoleDoesNotChangeTheTitleShown()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole);

        Assert.Equal("Member of Example", f.Title(f.Member));
    }

    // ── joining ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnInviteOnlyGroupIsJoinedOnlyByInvitation()
    {
        TestHelpers.InMethod();
        var f = new Fixture(openEnrollment: false);

        Assert.False(f.Service.AddAgentToGroup(f.Other, f.Other, f.Group, UUID.Zero, "", out _));
        Assert.False(f.IsMember(f.Other));

        Assert.True(f.Service.AddAgentToGroupInvite(f.Founder, UUID.Random(), f.Group, f.BuildersRole, f.Other));
        Assert.False(f.Service.AddAgentToGroup(f.Other, f.Other, f.Group, f.OwnersRole, "", out _));
        Assert.True(f.Service.AddAgentToGroup(f.Other, f.Other, f.Group, f.BuildersRole, "", out _));
        Assert.True(f.Holds(f.Other, f.BuildersRole));
    }

    [Fact]
    public void AHypergridVisitorAcceptsAnInviteMadeOutToTheirBareUuid()
    {
        TestHelpers.InMethod();
        var f = new Fixture(openEnrollment: false);
        string visitor = f.Other + ";http://home.example.org:8002/;Visiting Friend";

        Assert.True(f.Service.AddAgentToGroupInvite(f.Founder, UUID.Random(), f.Group, f.BuildersRole, f.Other));

        Assert.True(f.Service.AddAgentToGroup(visitor, visitor, f.Group, f.BuildersRole, "", out _));
        Assert.True(f.Holds(visitor, f.BuildersRole));
    }

    [Fact]
    public void AnOpenGroupIsJoinedIntoEveryoneOnly()
    {
        TestHelpers.InMethod();
        var f = new Fixture(openEnrollment: true);

        Assert.False(f.Service.AddAgentToGroup(f.Other, f.Other, f.Group, f.OwnersRole, "", out _));
        Assert.True(f.Service.AddAgentToGroup(f.Other, f.Other, f.Group, UUID.Zero, "", out _));
        Assert.False(f.Holds(f.Other, f.OwnersRole));
    }

    [Fact]
    public void OnlyOwnersInviteIntoTheOwnersRole()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        Assert.False(f.Service.AddAgentToGroupInvite(f.Officer, UUID.Random(), f.Group, f.OwnersRole, f.Other));
        Assert.True(f.Service.AddAgentToGroupInvite(f.Officer, UUID.Random(), f.Group, UUID.Zero, f.Other));
    }

    // ── ejecting ───────────────────────────────────────────────────────────────

    [Fact]
    public void NobodyEjectsAnOwner()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Join(f.Other, f.OwnersRole);

        Assert.False(f.Service.RemoveAgentFromGroup(f.Founder, f.Other, f.Group));
        Assert.False(f.Service.RemoveAgentFromGroup(f.Officer, f.Founder, f.Group));
        Assert.True(f.IsMember(f.Other));
        Assert.True(f.IsMember(f.Founder));
    }

    [Fact]
    public void ANonOwnerEjectsOnlyMembersInNothingButEveryone()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Join(f.Other, f.BuildersRole);

        Assert.False(f.Service.RemoveAgentFromGroup(f.Officer, f.Other, f.Group));
        Assert.True(f.Service.RemoveAgentFromGroup(f.Officer, f.Member, f.Group));
        Assert.True(f.Service.RemoveAgentFromGroup(f.Founder, f.Other, f.Group));
    }

    [Fact]
    public void AnyoneMayLeave()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        Assert.True(f.Service.RemoveAgentFromGroup(f.Officer, f.Officer, f.Group));
        Assert.False(f.IsMember(f.Officer));
    }

    // ── assigning and removing roles ───────────────────────────────────────────

    [Fact]
    public void OnlyOwnersMakeOwners()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Service.AddAgentToGroupRole(f.Founder, f.Officer, f.Group, f.AssignersRole);

        Assert.False(f.Service.AddAgentToGroupRole(f.Officer, f.Member, f.Group, f.OwnersRole));
        Assert.True(f.Service.AddAgentToGroupRole(f.Officer, f.Member, f.Group, f.BuildersRole));
        Assert.True(f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.OwnersRole));
    }

    [Fact]
    public void ARoleIsGivenOnlyToAMember()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        Assert.False(f.Service.AddAgentToGroupRole(f.Founder, f.Other, f.Group, f.BuildersRole));
        Assert.False(f.Holds(f.Other, f.BuildersRole));
    }

    [Fact]
    public void OwnersCannotBeRemovedFromTheOwnersRoleButMayLeaveIt()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Join(f.Other, f.OwnersRole);

        Assert.False(f.Service.RemoveAgentFromGroupRole(f.Founder, f.Other, f.Group, f.OwnersRole));
        Assert.True(f.Holds(f.Other, f.OwnersRole));

        Assert.True(f.Service.RemoveAgentFromGroupRole(f.Other, f.Other, f.Group, f.OwnersRole));
        Assert.False(f.Holds(f.Other, f.OwnersRole));
    }

    [Fact]
    public void RemovingFromARoleNeedsTheRemoveMemberAbility()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Service.AddAgentToGroupRole(f.Founder, f.Officer, f.Group, f.AssignersRole);
        f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole);

        // AssignMember lets the officer add to any role, not remove from one
        Assert.False(f.Service.RemoveAgentFromGroupRole(f.Officer, f.Member, f.Group, f.BuildersRole));
        Assert.True(f.Holds(f.Member, f.BuildersRole));

        Assert.True(f.Service.RemoveAgentFromGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole));
        Assert.False(f.Holds(f.Member, f.BuildersRole));
    }

    [Fact]
    public void LosingARoleKeepsTheTitleUnlessItWasThatRoles()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole);
        f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.AssignersRole);
        f.Service.SetAgentActiveGroupRole(f.Member, f.Member, f.Group, UUID.Zero);

        // losing another role leaves the Everyone title shown
        f.Service.RemoveAgentFromGroupRole(f.Founder, f.Member, f.Group, f.AssignersRole);
        Assert.Equal("Member of Example", f.Title(f.Member));

        // losing the role whose title is shown falls back to Everyone's
        f.Service.SetAgentActiveGroupRole(f.Member, f.Member, f.Group, f.BuildersRole);
        f.Service.RemoveAgentFromGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole);
        Assert.Equal("Member of Example", f.Title(f.Member));
    }

    // ── the owners role ────────────────────────────────────────────────────────

    [Fact]
    public void TheOwnersRoleKeepsItsPowersAndName()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Service.AddAgentToGroupRole(f.Founder, f.Officer, f.Group, f.AssignersRole);
        string powersBefore = f.Db.Roles[(f.Group, f.OwnersRole)].Data["Powers"];

        Assert.True(f.Service.UpdateGroupRole(f.Officer, f.Group, f.OwnersRole, "Nobodies", "desc", "Head", 0));

        RoleData owners = f.Db.Roles[(f.Group, f.OwnersRole)];
        Assert.Equal(powersBefore, owners.Data["Powers"]);
        Assert.Equal("Owners", owners.Data["Name"]);
        Assert.Equal("Head", owners.Data["Title"]);
        Assert.Equal(AllPowers, f.Powers(f.Founder));
    }

    // ── the member list ────────────────────────────────────────────────────────

    [Fact]
    public void TheMemberListGivesEachMembersCombinedPowersAndItsOwners()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Service.AddAgentToGroupRole(f.Founder, f.Member, f.Group, f.BuildersRole);
        // an owners role whose stored powers are not the default owner set still makes its members owners
        f.Db.Roles[(f.Group, f.OwnersRole)].Data["Powers"] = "1";

        var members = f.Service.GetGroupMembers(UUID.Zero.ToString(), f.Group);

        var member = members.Single(m => m.AgentID == f.Member);
        Assert.Equal(Build, member.AgentPowers & Build);
        var founder = members.Single(m => m.AgentID == f.Founder);
        Assert.True(founder.IsOwner);
        Assert.Equal(AllPowers, founder.AgentPowers);
    }

    // ── the region module ──────────────────────────────────────────────────────

    [Fact]
    public void GetFullGroupPowersIsTheAgentsOwnNotTheWholeGroups()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        var connector = (GroupsServiceLocalConnectorModule)RuntimeHelpers.GetUninitializedObject(typeof(GroupsServiceLocalConnectorModule));
        typeof(GroupsServiceLocalConnectorModule).GetField("m_GroupsService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connector, f.Service);
        var module = (OpenSim.Groups.GroupsModule)RuntimeHelpers.GetUninitializedObject(typeof(OpenSim.Groups.GroupsModule));
        typeof(OpenSim.Groups.GroupsModule).GetField("m_groupData", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, connector);

        ulong member = module.GetFullGroupPowers(new UUID(f.Member), f.Group);

        Assert.Equal(0ul, member & (ulong)GroupPowers.Eject);
        Assert.Equal(AllPowers, module.GetFullGroupPowers(new UUID(f.Founder), f.Group));
    }

    // ── an in-memory groups store ──────────────────────────────────────────────

    private sealed class InMemoryGroups : IGroupsData
    {
        public readonly Dictionary<UUID, GroupData> Groups = new();
        public readonly Dictionary<(UUID, string), MembershipData> Members = new();
        public readonly Dictionary<(UUID, UUID), RoleData> Roles = new();
        public readonly HashSet<(UUID Group, UUID Role, string Agent)> RoleMembers = new();
        public readonly Dictionary<string, PrincipalData> Principals = new();
        public readonly Dictionary<UUID, InvitationData> Invites = new();

        public bool StoreGroup(GroupData data) { Groups[data.GroupID] = data; return true; }
        public GroupData RetrieveGroup(UUID groupID) => Groups.GetValueOrDefault(groupID);
        public GroupData RetrieveGroup(string name) => Groups.Values.FirstOrDefault(g => g.Data["Name"] == name);
        public GroupData[] RetrieveGroups(string pattern) => Groups.Values.ToArray();
        public bool DeleteGroup(UUID groupID) => Groups.Remove(groupID);
        public int GroupsCount() => Groups.Count;

        public MembershipData RetrieveMember(UUID groupID, string pricipalID) => Members.GetValueOrDefault((groupID, pricipalID));
        public MembershipData[] RetrieveMembers(UUID groupID) => Members.Values.Where(m => m.GroupID == groupID).ToArray();
        public MembershipData[] RetrieveMemberships(string pricipalID) => Members.Values.Where(m => m.PrincipalID == pricipalID).ToArray();
        public bool StoreMember(MembershipData data) { Members[(data.GroupID, data.PrincipalID)] = data; return true; }
        public bool DeleteMember(UUID groupID, string pricipalID) => Members.Remove((groupID, pricipalID));
        public int MemberCount(UUID groupID) => RetrieveMembers(groupID).Length;

        public bool StoreRole(RoleData data) { Roles[(data.GroupID, data.RoleID)] = data; return true; }
        public RoleData RetrieveRole(UUID groupID, UUID roleID) => Roles.GetValueOrDefault((groupID, roleID));
        public RoleData[] RetrieveRoles(UUID groupID) => Roles.Values.Where(r => r.GroupID == groupID).ToArray();
        public bool DeleteRole(UUID groupID, UUID roleID) => Roles.Remove((groupID, roleID));
        public int RoleCount(UUID groupID) => RetrieveRoles(groupID).Length;

        private static RoleMembershipData Rm((UUID Group, UUID Role, string Agent) x) => new() { GroupID = x.Group, RoleID = x.Role, PrincipalID = x.Agent };
        public RoleMembershipData[] RetrieveRolesMembers(UUID groupID) => RoleMembers.Where(x => x.Group == groupID).Select(Rm).ToArray();
        public RoleMembershipData[] RetrieveRoleMembers(UUID groupID, UUID roleID) => RoleMembers.Where(x => x.Group == groupID && x.Role == roleID).Select(Rm).ToArray();
        public RoleMembershipData[] RetrieveMemberRoles(UUID groupID, string principalID) => RoleMembers.Where(x => x.Group == groupID && x.Agent == principalID).Select(Rm).ToArray();
        public RoleMembershipData RetrieveRoleMember(UUID groupID, UUID roleID, string principalID)
            => RoleMembers.Contains((groupID, roleID, principalID)) ? Rm((groupID, roleID, principalID)) : null;
        public int RoleMemberCount(UUID groupID, UUID roleID) => RetrieveRoleMembers(groupID, roleID).Length;
        public bool StoreRoleMember(RoleMembershipData data) => RoleMembers.Add((data.GroupID, data.RoleID, data.PrincipalID));
        public bool DeleteRoleMember(RoleMembershipData data) => RoleMembers.Remove((data.GroupID, data.RoleID, data.PrincipalID));
        public bool DeleteMemberAllRoles(UUID groupID, string principalID) { RoleMembers.RemoveWhere(x => x.Group == groupID && x.Agent == principalID); return true; }

        public bool StorePrincipal(PrincipalData data) { Principals[data.PrincipalID] = data; return true; }
        public PrincipalData RetrievePrincipal(string principalID) => Principals.GetValueOrDefault(principalID);
        public bool DeletePrincipal(string principalID) => Principals.Remove(principalID);

        public bool StoreInvitation(InvitationData data) { Invites[data.InviteID] = data; return true; }
        public InvitationData RetrieveInvitation(UUID inviteID) => Invites.GetValueOrDefault(inviteID);
        public InvitationData RetrieveInvitation(UUID groupID, string principalID) => Invites.Values.FirstOrDefault(i => i.GroupID == groupID && i.PrincipalID == principalID);
        public bool DeleteInvite(UUID inviteID) => Invites.Remove(inviteID);
        public void DeleteOldInvites() { }

        public bool StoreNotice(NoticeData data) => true;
        public NoticeData RetrieveNotice(UUID noticeID) => null;
        public NoticeData[] RetrieveNotices(UUID groupID) => Array.Empty<NoticeData>();
        public bool DeleteNotice(UUID noticeID) => true;
        public void DeleteOldNotices() { }

        public MembershipData RetrievePrincipalGroupMembership(string principalID, UUID groupID) => RetrieveMember(groupID, principalID);
        public MembershipData[] RetrievePrincipalGroupMemberships(string principalID) => RetrieveMemberships(principalID);
    }

    /// <summary>An IGridUserData that knows nobody (no one online).</summary>
    private class NoGridUsers : DispatchProxy
    {
        public static IGridUserData Create() => Create<IGridUserData, NoGridUsers>();
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }
}
