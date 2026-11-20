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
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Groups;

/// <summary>
/// The group rules for one avatar in one group, decided from three reads: the group, its roles, and the roles the
/// avatar holds. Every permission the groups service checks comes from here, so each rule is written once.
/// The rules are SL's (wiki "Group", the viewer's llgroupmgr.h and LLPanelGroup*): an owner has every ability; a
/// member has the abilities of every role they hold, Everyone included; someone outside the group has none.
/// </summary>
internal sealed class GroupRules
{
    public const ulong AllPowers = ulong.MaxValue;

    public GroupData Group { get; }
    public UUID OwnerRoleID { get; }
    public IReadOnlyList<RoleData> Roles { get; }
    public IReadOnlyList<RoleMembershipData> Held { get; }
    public ulong Powers { get; }

    private GroupRules(GroupData group, IReadOnlyList<RoleData> roles, IReadOnlyList<RoleMembershipData> held)
    {
        Group = group;
        OwnerRoleID = OwnerRoleOf(group);
        Roles = roles;
        Held = held;
        Powers = held.Count == 0 ? 0 : PowersOf(held, roles, OwnerRoleID);
    }

    /// <summary>The rules for <paramref name="agentID"/> in the group; a missing group gives an avatar with no abilities.</summary>
    public static GroupRules For(IGroupsData db, UUID groupID, string agentID)
    {
        GroupData group = db.RetrieveGroup(groupID);
        if (group == null)
            return new GroupRules(null, [], []);
        RoleData[] roles = db.RetrieveRoles(groupID) ?? [];
        RoleMembershipData[] held = string.IsNullOrEmpty(agentID) ? [] : db.RetrieveMemberRoles(groupID, agentID) ?? [];
        return new GroupRules(group, roles, held.Where(r => r != null).ToArray());
    }

    /// <summary>The same rules for another avatar of the same group: only that avatar's roles are read.</summary>
    public GroupRules ForOther(IGroupsData db, string agentID)
    {
        if (Group == null || string.IsNullOrEmpty(agentID))
            return new GroupRules(Group, Roles, []);
        RoleMembershipData[] held = db.RetrieveMemberRoles(Group.GroupID, agentID) ?? [];
        return new GroupRules(Group, Roles, held.Where(r => r != null).ToArray());
    }

    public bool GroupExists => Group != null;

    /// <summary>In the group: every member holds the Everyone role.</summary>
    public bool IsMember => Held.Count > 0;

    public bool IsOwner => !OwnerRoleID.IsZero() && Holds(OwnerRoleID);

    public bool Holds(UUID roleID) => Held.Any(r => r.RoleID == roleID);

    /// <summary>Has every ability in <paramref name="power"/>.</summary>
    public bool Has(GroupPowers power) => (Powers & (ulong)power) == (ulong)power;

    public bool RoleExists(UUID roleID) => Roles.Any(r => r.RoleID == roleID);

    /// <summary>
    /// May put a member in the role: anyone may for Everyone; only an owner for the owners role ("Assign Members to
    /// Any Role" reaches the other roles); otherwise "Assign Members to Any Role" (AssignMember), or "Assign Members
    /// to Assigner's Roles" (AssignMemberLimited) for a role the assigner holds.
    /// </summary>
    public bool CanAssign(UUID roleID)
    {
        if (roleID.IsZero())
            return true;
        if (roleID == OwnerRoleID)
            return IsOwner;
        return Has(GroupPowers.AssignMember) || (Has(GroupPowers.AssignMemberLimited) && Holds(roleID));
    }

    /// <summary>
    /// May take a member out of the role: an owner leaves the owners role only by their own hand ("Owners can't be
    /// removed"); any other role needs "Remove Members from Roles" (RemoveMember); nobody leaves Everyone but by
    /// leaving the group.
    /// </summary>
    public bool CanRemoveFromRole(UUID roleID, bool self)
    {
        if (roleID.IsZero())
            return false;
        if (roleID == OwnerRoleID)
            return self;
        return Has(GroupPowers.RemoveMember);
    }

    /// <summary>
    /// May eject the member: anyone may leave; ejecting someone else needs Eject, never reaches an owner, and, unless
    /// the ejector is an owner, only reaches a member in no role but Everyone ("a Member can be ejected ... if, and
    /// only if, they're only in the Everyone Role").
    /// </summary>
    public bool CanEject(GroupRules member, bool self)
    {
        if (self)
            return true;
        if (!Has(GroupPowers.Eject) || member.IsOwner)
            return false;
        return IsOwner || member.Held.All(r => r.RoleID.IsZero());
    }

    /// <summary>
    /// Which parts of the group's settings the avatar may change: its identity (charter, insignia, listing, mature,
    /// publishing) needs "Change Charter, Insignia, ..." (ChangeIdentity); enrollment and fee need "Change Open
    /// Enrollment and Sign-up Fee" (ChangeOptions, the viewer's GP_MEMBER_OPTIONS).
    /// </summary>
    public (bool Identity, bool Enrollment) MayChangeGroup()
        => (Has(GroupPowers.ChangeIdentity), Has(GroupPowers.ChangeOptions));

    /// <summary>
    /// Which parts of a role the avatar may change: its name, description and title need "Change Role Names, Titles
    /// and Descriptions" (RoleProperties); its abilities need "Assign Abilities to Roles" (ChangeActions).
    /// </summary>
    public (bool Properties, bool Abilities) MayChangeRole()
        => (Has(GroupPowers.RoleProperties), Has(GroupPowers.ChangeActions));

    public static UUID OwnerRoleOf(GroupData group)
    {
        if (group != null && group.Data != null && group.Data.TryGetValue("OwnerRoleID", out string id) && UUID.TryParse(id, out UUID ownerRoleID))
            return ownerRoleID;
        return UUID.Zero;
    }

    /// <summary>The abilities of the roles held: every ability for the owners role.</summary>
    public static ulong PowersOf(IEnumerable<RoleMembershipData> held, IEnumerable<RoleData> roles, UUID ownerRoleID)
    {
        ulong powers = 0;
        foreach (RoleMembershipData r in held)
        {
            if (r == null)
                continue;
            if (!ownerRoleID.IsZero() && r.RoleID == ownerRoleID)
                return AllPowers;
            RoleData role = roles.FirstOrDefault(x => x.RoleID == r.RoleID);
            if (role != null && UInt64.TryParse(role.Data["Powers"], out ulong p))
                powers |= p;
        }
        return powers;
    }
}
