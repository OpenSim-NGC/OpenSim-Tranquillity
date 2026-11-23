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
using OpenSim.Framework;
using OpenSim.Groups;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The group rules, through the real GroupsService on the SQLite store: each change needs its own ability, as the
/// viewer's group panels expect (llgroupmgr.h, LLPanelGroupGeneral, LLPanelGroupRoles), and a member's own settings
/// and active group are theirs alone.
/// </summary>
[Collection("SQLite stores")]
public class GroupRulesTests
{
    private static GroupsService Service => SqliteGroupsStoreTests.Service;

    private sealed record Group(UUID Id, string Owner, UUID Officers, string Officer, string Member);

    /// <summary>A group with its founder (an owner), an officer, and a plain member.</summary>
    private static Group NewGroup()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string owner = UUID.Random().ToString();
        UUID id = Service.CreateGroup(owner, "Rules " + tag, "the charter", true, UUID.Zero, 0, false, true, false,
            new UUID(owner), out string reason);
        Assert.True(id.IsNotZero(), reason);
        UUID officers = Service.GetGroupRoles(owner, id).Single(r => r.Name == "Officers").RoleID;

        string officer = UUID.Random().ToString(), member = UUID.Random().ToString();
        Assert.True(Service.AddAgentToGroup(owner, officer, id, officers, string.Empty, out reason), reason);
        Assert.True(Service.AddAgentToGroup(owner, member, id, UUID.Zero, string.Empty, out reason), reason);
        return new Group(id, owner, officers, officer, member);
    }

    /// <summary>A member in a role of their own with exactly these abilities.</summary>
    private static string MemberWith(Group g, GroupPowers powers)
    {
        UUID role = UUID.Random();
        Assert.True(Service.AddGroupRole(g.Owner, g.Id, role, "Custom " + role.ToString()[..6], "", "Custom", (ulong)powers, out string reason), reason);
        string who = UUID.Random().ToString();
        Assert.True(Service.AddAgentToGroup(g.Owner, who, g.Id, role, string.Empty, out reason), reason);
        return who;
    }

    private static void Update(Group g, string who, string charter, int fee, bool open)
        => Service.UpdateGroup(who, g.Id, charter, true, UUID.Zero, fee, open, true, false);

    [Fact]
    public void AnOfficerChangesTheCharterAndTheEnrollment()
    {
        // the default Officers role has Change Identity and Member Options, not Change Actions
        Group g = NewGroup();
        Update(g, g.Officer, "the officer's charter", 10, true);

        ExtendedGroupRecord r = Service.GetGroupRecord(g.Owner, g.Id);
        Assert.Equal("the officer's charter", r.Charter);
        Assert.Equal(10, r.MembershipFee);
        Assert.True(r.OpenEnrollment);
    }

    [Fact]
    public void EachPartOfTheSettingsNeedsItsOwnAbility()
    {
        Group g = NewGroup();
        string identityOnly = MemberWith(g, GroupPowers.ChangeIdentity);
        string enrollmentOnly = MemberWith(g, GroupPowers.ChangeOptions);

        Update(g, identityOnly, "identity's charter", 25, true);
        ExtendedGroupRecord r = Service.GetGroupRecord(g.Owner, g.Id);
        Assert.Equal("identity's charter", r.Charter);
        Assert.Equal(0, r.MembershipFee);
        Assert.False(r.OpenEnrollment);

        Update(g, enrollmentOnly, "enrollment's charter", 25, true);
        r = Service.GetGroupRecord(g.Owner, g.Id);
        Assert.Equal("identity's charter", r.Charter);
        Assert.Equal(25, r.MembershipFee);
        Assert.True(r.OpenEnrollment);
    }

    [Fact]
    public void APlainMemberOrAStrangerChangesNothing()
    {
        Group g = NewGroup();
        Update(g, g.Member, "the member's charter", 5, true);
        Update(g, UUID.Random().ToString(), "a stranger's charter", 5, true);

        ExtendedGroupRecord r = Service.GetGroupRecord(g.Owner, g.Id);
        Assert.Equal("the charter", r.Charter);
        Assert.Equal(0, r.MembershipFee);
        Assert.False(r.OpenEnrollment);
    }

    [Fact]
    public void AnOfficerRenamesARoleButCannotChangeItsAbilities()
    {
        // the default Officers role has Role Properties, not Change Actions
        Group g = NewGroup();
        GroupRolesData everyone = Service.GetGroupRoles(g.Owner, g.Id).Single(r => r.RoleID.IsZero());

        Assert.True(Service.UpdateGroupRole(g.Officer, g.Id, g.Officers, "Officers", "the officers", "Officer now",
            (ulong)GroupPowers.Eject));
        Assert.True(Service.UpdateGroupRole(g.Officer, g.Id, UUID.Zero, "Everyone", "", "Member now",
            everyone.Powers | (ulong)GroupPowers.Eject));

        List<GroupRolesData> roles = Service.GetGroupRoles(g.Owner, g.Id);
        GroupRolesData officers = roles.Single(r => r.RoleID == g.Officers);
        Assert.Equal("Officer now", officers.Title);
        Assert.NotEqual((ulong)GroupPowers.Eject, officers.Powers);                 // abilities kept
        Assert.True((officers.Powers & (ulong)GroupPowers.Invite) != 0);
        GroupRolesData everyoneNow = roles.Single(r => r.RoleID.IsZero());
        Assert.Equal("Member now", everyoneNow.Title);
        Assert.Equal(everyone.Powers, everyoneNow.Powers);                          // Everyone did not gain Eject
    }

    [Fact]
    public void AnAbilitiesOnlyMemberChangesAbilitiesButNotTheTitle()
    {
        Group g = NewGroup();
        string actions = MemberWith(g, GroupPowers.ChangeActions);
        GroupRolesData before = Service.GetGroupRoles(g.Owner, g.Id).Single(r => r.RoleID == g.Officers);

        Assert.True(Service.UpdateGroupRole(actions, g.Id, g.Officers, "Renamed", "", "Renamed title",
            before.Powers | (ulong)GroupPowers.ChangeActions));

        GroupRolesData after = Service.GetGroupRoles(g.Owner, g.Id).Single(r => r.RoleID == g.Officers);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Title, after.Title);
        Assert.True((after.Powers & (ulong)GroupPowers.ChangeActions) != 0);
    }

    [Fact]
    public void APlainMemberCannotChangeARole()
    {
        Group g = NewGroup();
        Assert.False(Service.UpdateGroupRole(g.Member, g.Id, g.Officers, "Mine", "", "Mine", GroupsService.AllPowers));
        Assert.Equal("Officers", Service.GetGroupRoles(g.Owner, g.Id).Single(r => r.RoleID == g.Officers).Name);
    }

    [Fact]
    public void AMembersNoticeAndProfileSettingsAreTheirsAlone()
    {
        Group g = NewGroup();
        Service.UpdateMembership(g.Owner, g.Member, g.Id, false, false);
        ExtendedGroupMembershipData m = Service.GetAgentGroupMembership(g.Member, g.Member, g.Id);
        Assert.True(m.AcceptNotices);
        Assert.True(m.ListInProfile);

        Service.UpdateMembership(g.Member, g.Member, g.Id, false, false);
        m = Service.GetAgentGroupMembership(g.Member, g.Member, g.Id);
        Assert.False(m.AcceptNotices);
        Assert.False(m.ListInProfile);
    }

    [Fact]
    public void AnAvatarChoosesItsOwnActiveGroupAmongItsGroups()
    {
        Group g = NewGroup();
        Group other = NewGroup();
        // a second group the member is in, made active by the member
        Assert.True(Service.AddAgentToGroup(other.Owner, g.Member, other.Id, UUID.Zero, string.Empty, out string reason), reason);
        Assert.NotNull(Service.SetAgentActiveGroup(g.Member, g.Member, other.Id));
        Assert.Equal(other.Id, Service.GetAgentActiveMembership(g.Member, g.Member).GroupID);

        Assert.Null(Service.SetAgentActiveGroup(g.Owner, g.Member, g.Id));            // someone else's choice
        Group third = NewGroup();
        Assert.Null(Service.SetAgentActiveGroup(g.Member, g.Member, third.Id));        // a group they are not in
        Assert.Equal(other.Id, Service.GetAgentActiveMembership(g.Member, g.Member).GroupID);

        Assert.NotNull(Service.SetAgentActiveGroup(g.Member, g.Member, g.Id));
        Assert.Equal(g.Id, Service.GetAgentActiveMembership(g.Member, g.Member).GroupID);
    }

    [Fact]
    public void AnInvitationIsRemovedOnlyByItsInviteeOrSomeoneWhoMayInvite()
    {
        Group g = NewGroup();
        string invitee = UUID.Random().ToString();
        UUID invite = UUID.Random();
        Assert.True(Service.AddAgentToGroupInvite(g.Owner, invite, g.Id, UUID.Zero, invitee));

        Service.RemoveAgentToGroupInvite(UUID.Random().ToString(), invite);   // a stranger
        Service.RemoveAgentToGroupInvite(g.Member, invite);                   // a member who may not invite
        Assert.NotNull(Service.GetAgentToGroupInvite(invitee, invite));

        Service.RemoveAgentToGroupInvite(invitee, invite);
        Assert.Null(Service.GetAgentToGroupInvite(invitee, invite));

        UUID second = UUID.Random();
        Assert.True(Service.AddAgentToGroupInvite(g.Owner, second, g.Id, UUID.Zero, invitee));
        Service.RemoveAgentToGroupInvite(g.Officer, second);                  // an officer may withdraw it
        Assert.Null(Service.GetAgentToGroupInvite(invitee, second));
    }

    [Fact]
    public void EjectionsFollowTheOwnerAndRoleRules()
    {
        Group g = NewGroup();
        string other = UUID.Random().ToString();
        Assert.True(Service.AddAgentToGroup(g.Owner, other, g.Id, UUID.Zero, string.Empty, out string reason), reason);

        Assert.False(Service.RemoveAgentFromGroup(g.Member, other, g.Id));     // no Eject
        Assert.False(Service.RemoveAgentFromGroup(g.Officer, g.Owner, g.Id));  // never an owner
        string inARole = MemberWith(g, GroupPowers.None);
        Assert.False(Service.RemoveAgentFromGroup(g.Officer, inARole, g.Id));  // in a role besides Everyone
        Assert.True(Service.RemoveAgentFromGroup(g.Officer, other, g.Id));     // only in Everyone
        Assert.True(Service.RemoveAgentFromGroup(g.Owner, g.Officer, g.Id));   // an owner reaches officers
        Assert.True(Service.RemoveAgentFromGroup(g.Member, g.Member, g.Id));   // anyone may leave
    }
}
