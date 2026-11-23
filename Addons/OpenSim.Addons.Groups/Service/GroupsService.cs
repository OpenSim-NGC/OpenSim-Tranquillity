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
using System.Timers;
using Nini.Config;

using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;

using Microsoft.Extensions.Logging;

namespace OpenSim.Groups;

public class GroupsService : GroupsServiceBase
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const GroupPowers DefaultEveryonePowers =
        GroupPowers.AllowSetHome |
        GroupPowers.Accountable |
        GroupPowers.JoinChat |
        GroupPowers.AllowVoiceChat |
        GroupPowers.ReceiveNotices |
        GroupPowers.StartProposal |
        GroupPowers.VoteOnProposal;

    public const GroupPowers OfficersPowers = DefaultEveryonePowers |
        GroupPowers.AllowFly |
        GroupPowers.AllowLandmark |
        GroupPowers.AllowRez |
        GroupPowers.AssignMemberLimited |
        GroupPowers.ChangeIdentity |
        GroupPowers.ChangeMedia |
        GroupPowers.ChangeOptions |
        GroupPowers.DeedObject |
        GroupPowers.Eject |
        GroupPowers.FindPlaces |
        GroupPowers.Invite |
        GroupPowers.LandChangeIdentity |
        GroupPowers.LandDeed |
        GroupPowers.LandDivideJoin |
        GroupPowers.LandEdit |
        GroupPowers.AllowEnvironment |
        GroupPowers.LandEjectAndFreeze |
        GroupPowers.LandGardening |
        GroupPowers.LandManageAllowed |
        GroupPowers.LandManageBanned |
        GroupPowers.LandManagePasses |
        GroupPowers.LandOptions |
        GroupPowers.LandRelease |
        GroupPowers.LandSetSale |
        GroupPowers.MemberVisible |
        GroupPowers.ModerateChat |
        GroupPowers.ObjectManipulate |
        GroupPowers.ObjectSetForSale |
        GroupPowers.ReturnGroupOwned |
        GroupPowers.ReturnGroupSet |
        GroupPowers.ReturnNonGroup |
        GroupPowers.RoleProperties |
        GroupPowers.SendNotices |
        GroupPowers.SetLandingPoint;

    public const GroupPowers OwnerPowers = OfficersPowers | 
        GroupPowers.Accountable |
        GroupPowers.AllowEditLand |
        GroupPowers.AssignMember |
        GroupPowers.ChangeActions |
        GroupPowers.CreateRole |
        GroupPowers.DeleteRole |
        GroupPowers.ExperienceAdmin |
        GroupPowers.ExperienceCreator |
        GroupPowers.GroupBanAccess |
        GroupPowers.HostEvent |
        GroupPowers.RemoveMember;

    #region Daily Cleanup

    private System.Timers.Timer m_CleanupTimer;

    public GroupsService(IConfigSource config, string configName)
        : base(config, configName)
    {
        RegisterImportCommand(config);
    }

    private static int s_importCommand;

    /// <summary>
    /// "groups import flotsam [dry-run]": copy a Flotsam XmlRpcGroups server's groups into this store (see
    /// <see cref="FlotsamGroupsImport"/>), from the MySQL database [Groups] FlotsamImportConnectionString names (kept in
    /// the config, so no password goes through the console). Once per process.
    /// </summary>
    private void RegisterImportCommand(IConfigSource config)
    {
        if (MainConsole.Instance == null || Interlocked.Exchange(ref s_importCommand, 1) == 1)
            return;
        MainConsole.Instance.Commands.AddCommand("Groups", false, "groups import flotsam",
            "groups import flotsam [dry-run]",
            "Copy the groups of a Flotsam XmlRpcGroups server's tables into Groups V2, from the database [Groups] " +
            "FlotsamImportConnectionString names; groups V2 already has are left as they are. dry-run only counts.",
            (module, args) =>
            {
                string conn = config.Configs["Groups"]?.GetString("FlotsamImportConnectionString", string.Empty) ?? string.Empty;
                if (conn.Length == 0)
                {
                    MainConsole.Instance.Output("Set [Groups] FlotsamImportConnectionString to the database with the Flotsam tables (osgroup, ...).");
                    return;
                }
                bool dry = args.Length > 3 && args[3].Equals("dry-run", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var report = new FlotsamGroupsImport(() => new MySqlConnector.MySqlConnection(conn), m_Database).Run(dry);
                    MainConsole.Instance.Output((dry ? "Would copy: " : "Copied: ") + report);
                    foreach (string skipped in report.Skipped.Take(20))
                        MainConsole.Instance.Output("  " + skipped);
                }
                catch (Exception e)
                {
                    MainConsole.Instance.Output("The import stopped: " + e.Message);
                }
            });
    }

    public GroupsService(IConfigSource config)
        : this(config, string.Empty)
    {
        // Once a day
        m_CleanupTimer = new System.Timers.Timer(24 * 60 * 60 * 1000);
        m_CleanupTimer.AutoReset = true;
        m_CleanupTimer.Elapsed += new ElapsedEventHandler(m_CleanupTimer_Elapsed);
        m_CleanupTimer.Enabled = true;
        m_CleanupTimer.Start();
    }

    private void m_CleanupTimer_Elapsed(object sender, ElapsedEventArgs e)
    {
        m_Database.DeleteOldNotices();
        m_Database.DeleteOldInvites();
    }

    #endregion

    public UUID CreateGroup(string RequestingAgentID, string name, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment,
        bool allowPublish, bool maturePublish, UUID founderID, out string reason)
    {
        reason = string.Empty;

        // Check if the group already exists
        if (m_Database.RetrieveGroup(name) != null)
        {
            reason = "A group with that name already exists";
            return UUID.Zero;
        }

        // Create the group
        GroupData data = new GroupData();
        data.GroupID = UUID.Random();
        data.Data = new Dictionary<string, string>();
        data.Data["Name"] = name;
        data.Data["Charter"] = charter;
        data.Data["InsigniaID"] = insigniaID.ToString();
        data.Data["FounderID"] = founderID.ToString();
        data.Data["MembershipFee"] = membershipFee.ToString();
        data.Data["OpenEnrollment"] = openEnrollment ? "1" : "0";
        data.Data["ShowInList"] = showInList ? "1" : "0";
        data.Data["AllowPublish"] = allowPublish ? "1" : "0";
        data.Data["MaturePublish"] = maturePublish ? "1" : "0";
        UUID ownerRoleID = UUID.Random();
        data.Data["OwnerRoleID"] = ownerRoleID.ToString();

        if (!m_Database.StoreGroup(data))
            return UUID.Zero;

        // Create Everyone role
        _AddOrUpdateGroupRole(RequestingAgentID, data.GroupID, UUID.Zero, "Everyone", "Everyone in the group is in the everyone role.", "Member of " + name, (ulong)DefaultEveryonePowers, true);

        // Create Officers role
        UUID officersRoleID = UUID.Random();
        _AddOrUpdateGroupRole(RequestingAgentID, data.GroupID, officersRoleID, "Officers", "The officers of the group, with more powers than regular members.", "Officer of " + name, (ulong)OfficersPowers, true);

        // Create Owner role
        _AddOrUpdateGroupRole(RequestingAgentID, data.GroupID, ownerRoleID, "Owners", "Owners of the group", "Owner of " + name, (ulong)OwnerPowers, true);

        // Add founder to group
        _AddAgentToGroup(RequestingAgentID, founderID.ToString(), data.GroupID, ownerRoleID);
        _AddAgentToGroup(RequestingAgentID, founderID.ToString(), data.GroupID, officersRoleID);

        return data.GroupID;
    }

    public void UpdateGroup(string RequestingAgentID, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish)
    {
        GroupData data = m_Database.RetrieveGroup(groupID);
        if (data == null)
            return;

        // Each part of the settings by its own ability; the viewer sends them all, whichever its user may change.
        var (identity, enrollment) = GroupRules.For(m_Database, groupID, RequestingAgentID).MayChangeGroup();
        if (!identity && !enrollment)
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at updating group {1} denied because of lack of permission", RequestingAgentID, groupID);
            return;
        }

        data.GroupID = groupID;
        if (identity)
        {
            data.Data["Charter"] = charter;
            data.Data["ShowInList"] = showInList ? "1" : "0";
            data.Data["InsigniaID"] = insigniaID.ToString();
            data.Data["AllowPublish"] = allowPublish ? "1" : "0";
            data.Data["MaturePublish"] = maturePublish ? "1" : "0";
        }
        if (enrollment)
        {
            data.Data["MembershipFee"] = membershipFee.ToString();
            data.Data["OpenEnrollment"] = openEnrollment ? "1" : "0";
        }

        m_Database.StoreGroup(data);

    }

    public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID)
    {
        GroupData data = m_Database.RetrieveGroup(GroupID);

        return _GroupDataToRecord(data);
    }

    public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, string GroupName)
    {
        GroupData data = m_Database.RetrieveGroup(GroupName);

        return _GroupDataToRecord(data);
    }

    public List<DirGroupsReplyData> FindGroups(string RequestingAgentID, string search)
    {
        List<DirGroupsReplyData> groups = new List<DirGroupsReplyData>();

        GroupData[] data = m_Database.RetrieveGroups(search);

        if (data != null && data.Length > 0)
        {
            foreach (GroupData d in data)
            {
                // Don't list group proxies
                if (d.Data.ContainsKey("Location") && d.Data["Location"] != string.Empty)
                    continue;

                int nmembers = m_Database.MemberCount(d.GroupID);
                if(nmembers == 0)
                    continue;

                DirGroupsReplyData g = new DirGroupsReplyData();

                if (d.Data.ContainsKey("Name"))
                    g.groupName = d.Data["Name"];
                else
                {
                    m_log.LogDebug("[Groups]: Key Name not found");
                    continue;
                }

                g.groupID = d.GroupID;
                g.members = nmembers;

                groups.Add(g);
            }
        }

        return groups;
    }

    public List<ExtendedGroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID)
    {
        // A fixed number of statements whatever the group's size: the group, its roles, its members, their roles,
        // and their grid user rows in batches. It used to be two statements per member.
        List<ExtendedGroupMembersData> members = new List<ExtendedGroupMembersData>();

        GroupData group = m_Database.RetrieveGroup(GroupID);
        if (group == null)
            return members;

        RoleData[] roles = m_Database.RetrieveRoles(GroupID);
        if (roles == null)
            // something wrong with this group
            return members;

        MembershipData[] datas = m_Database.RetrieveMembers(GroupID);
        if (datas == null || datas.Length == 0)
            return members;

        // The owners role is the one the group names, whatever its stored powers.
        UUID ownerRoleID = OwnerRoleOf(group);
        List<RoleData> allRoles = new List<RoleData>(roles);
        List<RoleData> rolesList = new List<RoleData>(roles);

        // Check visibility? When we don't want to check visibility, we pass it "all" as the requestingAgentID.
        // A requester outside the group sees only the titles of roles whose members are visible.
        bool checkVisibility = !RequestingAgentID.Equals(UUID.Zero.ToString());
        if (checkVisibility && !datas.Any(d => SamePrincipal(d.PrincipalID, RequestingAgentID)))
            rolesList = rolesList.FindAll(r => (UInt64.Parse(r.Data["Powers"]) & (ulong)GroupPowers.MemberVisible) != 0);

        ILookup<string, RoleMembershipData> heldBy = (m_Database.RetrieveRolesMembers(GroupID) ?? [])
            .ToLookup(r => r.PrincipalID, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, GridUserData> gridUsers = new Dictionary<string, GridUserData>(StringComparer.OrdinalIgnoreCase);
        foreach (GridUserData gud in m_GridUserService.GetMany(datas.Select(d => d.PrincipalID).ToArray()) ?? [])
            gridUsers.TryAdd(gud.UserID, gud);

        foreach (MembershipData d in datas)
        {
            List<RoleMembershipData> rolemembershipsList = heldBy[d.PrincipalID].ToList();

            ExtendedGroupMembersData m = new ExtendedGroupMembersData();

            // What's this person's current role in the group?
            UUID selectedRole = new UUID(d.Data["SelectedRoleID"]);
            RoleData selected = rolesList.Find(r => r.RoleID == selectedRole);

            if (selected != null)
                m.Title = selected.Data["Title"];
            // A member's powers are those of every role they hold, not only the one whose title they show.
            m.AgentPowers = PowersOf(rolemembershipsList, allRoles, ownerRoleID);

            m.AgentID = d.PrincipalID;
            m.AcceptNotices = d.Data["AcceptNotices"] == "1" ? true : false;
            m.Contribution = Int32.Parse(d.Data["Contribution"]);
            m.ListInProfile = d.Data["ListInProfile"] == "1" ? true : false;

            if (gridUsers.TryGetValue(d.PrincipalID, out GridUserData gud))
            {
                if (bool.Parse(gud.Data["Online"]))
                {
                    m.OnlineStatus = @"Online";
                }
                else
                {
                    int unixtime = int.Parse(gud.Data["Login"]);
                    // The viewer is very picky about how these strings are formed. Eg. it will crash on malformed dates!
                    m.OnlineStatus = (unixtime == 0) ? @"unknown" : Util.ToDateTime(unixtime).ToString("MM/dd/yyyy");
                }
            }

            // Is this person an owner of the group?
            m.IsOwner = !ownerRoleID.IsZero() && rolemembershipsList.Exists(r => r.RoleID == ownerRoleID);

            members.Add(m);
        }

        return members;
    }

    /// <summary>The same principal: ids compare as the stores' text columns do, without regard to case.</summary>
    private static bool SamePrincipal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public bool AddGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, out string reason)
    {
        reason = string.Empty;
        // check that the requesting agent has permissions to add role
        if (!HasPower(RequestingAgentID, groupID, GroupPowers.CreateRole))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at creating role in group {1} denied because of lack of permission", RequestingAgentID, groupID);
            reason = "Insufficient permission to create role";
            return false;
        }

        return _AddOrUpdateGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers, true);

    }

    public bool UpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers)
    {
        // A role's name, description and title need RoleProperties; its abilities need ChangeActions. The viewer
        // sends the whole role, so what the requester may not change is kept as it is.
        GroupRules rules = GroupRules.For(m_Database, groupID, RequestingAgentID);
        var (properties, abilities) = rules.MayChangeRole();
        RoleData current = m_Database.RetrieveRole(groupID, roleID);
        if (current == null || (!properties && !abilities))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at changing role in group {1} denied because of lack of permission", RequestingAgentID, groupID);
            return false;
        }
        if (!properties)
        {
            name = current.Data["Name"];
            description = current.Data["Description"];
            title = current.Data["Title"];
        }
        if (!abilities)
            powers = UInt64.Parse(current.Data["Powers"]);

        // SL (wiki Group): "Owners always have all available abilities in the group", and "Owner and Everyone role are
        // the only two roles which can't be renamed".
        if (roleID == rules.OwnerRoleID)
        {
            powers = UInt64.Parse(current.Data["Powers"]);
            name = current.Data["Name"];
        }
        else if (roleID.IsZero())
            name = current.Data["Name"];

        return _AddOrUpdateGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers, false);
    }

    public void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID)
    {
        // check perms
        if (!HasPower(RequestingAgentID, groupID, GroupPowers.DeleteRole))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at deleting role from group {1} denied because of lack of permission", RequestingAgentID, groupID);
            return;
        }

        // Can't delete Everyone and Owners roles
        if (roleID.IsZero())
        {
            m_log.LogDebug("[Groups]: Attempt at deleting Everyone role from group {0} denied", groupID);
            return;
        }

        GroupData group = m_Database.RetrieveGroup(groupID);
        if (group == null)
        {
            m_log.LogDebug("[Groups]: Attempt at deleting role from non-existing group {0}", groupID);
            return;
        }

        if (roleID == new UUID(group.Data["OwnerRoleID"]))
        {
            m_log.LogDebug("[Groups]: Attempt at deleting Owners role from group {0} denied", groupID);
            return;
        }

        _RemoveGroupRole(groupID, roleID);
    }

    public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID)
    {
        // TODO: check perms
        return _GetGroupRoles(GroupID);
    }

    public List<ExtendedGroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID)
    {
        // TODO: check perms

        // Is the requester a member of the group?
        bool isInGroup = false;
        if (m_Database.RetrieveMember(GroupID, RequestingAgentID) != null)
            isInGroup = true;

        return _GetGroupRoleMembers(GroupID, isInGroup);
    }

    public bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string token, out string reason)
    {
        reason = string.Empty;

        GroupData group = m_Database.RetrieveGroup(GroupID);
        if (group == null)
        {
            reason = "No such group";
            return false;
        }

        if (!RoleID.IsZero() && m_Database.RetrieveRole(GroupID, RoleID) == null)
        {
            reason = "No such role";
            return false;
        }

        // a banned avatar joins neither by itself nor through anyone, invited or not
        if (IsBanned(GroupID, AgentID))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at adding {1}, who is banned, to group {2} denied", RequestingAgentID, AgentID, GroupID);
            reason = "You are banned from this group";
            return false;
        }

        // An agent joins through an invitation (to the role it names), or into the Everyone role of a group open to
        // enrollment. Adding someone else needs the power to invite and to assign the role.
        InvitationData invite = FindInvitation(GroupID, AgentID);
        bool invited = invite != null && invite.RoleID == RoleID;
        if (!invited)
        {
            if (SameAgent(RequestingAgentID, AgentID))
            {
                if (!RoleID.IsZero() || !group.Data.TryGetValue("OpenEnrollment", out string open) || open != "1")
                {
                    m_log.LogDebug("[Groups]: ({0}) Attempt at joining group {1} denied: not open to enrollment and no invitation", AgentID, GroupID);
                    reason = "This group is not open to enrollment";
                    return false;
                }
            }
            else if (!HasPower(RequestingAgentID, GroupID, GroupPowers.Invite) || !CanAssignRole(RequestingAgentID, GroupID, RoleID))
            {
                m_log.LogDebug("[Groups]: ({0}) Attempt at adding {1} to group {2} denied because of lack of permission", RequestingAgentID, AgentID, GroupID);
                reason = "Insufficient permission";
                return false;
            }
        }

        _AddAgentToGroup(RequestingAgentID, AgentID, GroupID, RoleID, token);

        return true;
    }

    public bool RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
    {
        // check perms. Anyone may leave. SL (wiki Group): "An Owner can eject anyone except another Owner", and "a
        // Member can be ejected from a group if, and only if, they're only in the Everyone Role" by anyone else.
        bool self = SameAgent(RequestingAgentID, AgentID);
        if (!self)
        {
            GroupRules ejector = GroupRules.For(m_Database, GroupID, RequestingAgentID);
            if (!ejector.CanEject(ejector.ForOther(m_Database, AgentID), self))
                return false;
        }

        _RemoveAgentFromGroup(RequestingAgentID, AgentID, GroupID);

        return true;
    }

    public bool AddAgentToGroupInvite(string RequestingAgentID, UUID inviteID, UUID groupID, UUID roleID, string agentID)
    {
        // a banned avatar is not invited
        if (IsBanned(groupID, agentID))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at inviting {1}, who is banned, to group {2} denied", RequestingAgentID, agentID, groupID);
            return false;
        }

        // Check whether the invitee is already a member of the group
        MembershipData m = m_Database.RetrieveMember(groupID, agentID);
        if (m != null)
            return false;

        // Check permission to invite, and to assign the role the invitation is for
        if (!HasPower(RequestingAgentID, groupID, GroupPowers.Invite) || !CanAssignRole(RequestingAgentID, groupID, roleID))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at inviting to group {1} denied because of lack of permission", RequestingAgentID, groupID);
            return false;
        }

        // Check whether there are pending invitations and delete them
        InvitationData invite = m_Database.RetrieveInvitation(groupID, agentID);
        if (invite != null)
            m_Database.DeleteInvite(invite.InviteID);

        invite = new InvitationData();
        invite.InviteID = inviteID;
        invite.PrincipalID = agentID;
        invite.GroupID = groupID;
        invite.RoleID = roleID;
        invite.Data = new Dictionary<string, string>();

        return m_Database.StoreInvitation(invite);
    }

    public GroupInviteInfo GetAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
    {
        InvitationData data = m_Database.RetrieveInvitation(inviteID);

        if (data == null)
            return null;

        GroupInviteInfo inviteInfo = new GroupInviteInfo();
        inviteInfo.AgentID = data.PrincipalID;
        inviteInfo.GroupID = data.GroupID;
        inviteInfo.InviteID = data.InviteID;
        inviteInfo.RoleID = data.RoleID;

        return inviteInfo;
    }

    public void RemoveAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
    {
        // The invitee answers their invitation; someone who may invite to the group may withdraw it.
        InvitationData invite = m_Database.RetrieveInvitation(inviteID);
        if (invite == null)
            return;
        if (!SameInvitee(invite.PrincipalID, RequestingAgentID)
            && !GroupRules.For(m_Database, invite.GroupID, RequestingAgentID).Has(GroupPowers.Invite))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at removing invitation {1}, sent to {2}, denied", RequestingAgentID, inviteID, invite.PrincipalID);
            return;
        }
        m_Database.DeleteInvite(inviteID);
    }

    /// <summary>The invitee: as stored, or by its UUID when one side names a hypergrid visitor by uuid;url;name.</summary>
    private static bool SameInvitee(string invitee, string agentID)
    {
        if (SameAgent(invitee, agentID))
            return true;
        return Util.ParseUniversalUserIdentifier(invitee ?? "", out UUID a) && Util.ParseUniversalUserIdentifier(agentID ?? "", out UUID b)
            && a.IsNotZero() && a == b;
    }

    public bool AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
    {
        //if (!m_Database.CheckOwnerRole(RequestingAgentID, GroupID, RoleID))
        //    return;

        // check permissions
        if (!CanAssignRole(RequestingAgentID, GroupID, RoleID))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at assigning {1} to role {2} denied because of lack of permission", RequestingAgentID, AgentID, RoleID);
            return false;
        }

        // only a member of the group can hold one of its roles
        if (m_Database.RetrieveMember(GroupID, AgentID) == null || m_Database.RetrieveRole(GroupID, RoleID) == null)
            return false;

        // A new role does not change the title the member shows.
        _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID, false);

        return true;
    }

    public bool RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
    {
        // Don't remove from Everyone role!
        if (RoleID.IsZero())
            return false;

        // check permissions. SL (wiki Group): "Owners can't be removed"; the founder, like any owner, may leave the
        // owners role. Removing members from other roles is the "Remove Members from Roles" ability (RemoveMember).
        if (!GroupRules.For(m_Database, GroupID, RequestingAgentID).CanRemoveFromRole(RoleID, SameAgent(RequestingAgentID, AgentID)))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at removing {1} from role {2} denied because of lack of permission", RequestingAgentID, AgentID, RoleID);
            return false;
        }

        RoleMembershipData rolemember = m_Database.RetrieveRoleMember(GroupID, RoleID, AgentID);

        if (rolemember == null)
            return false;

        m_Database.DeleteRoleMember(rolemember);

        // The member keeps the title they show, unless it was this role's: then another role they hold, or Everyone.
        MembershipData member = m_Database.RetrieveMember(GroupID, AgentID);
        if (member != null && member.Data.TryGetValue("SelectedRoleID", out string selected) && selected == RoleID.ToString())
        {
            UUID newRoleID = UUID.Zero; // Everyone
            RoleMembershipData[] rdata = m_Database.RetrieveMemberRoles(GroupID, AgentID);
            if (rdata != null)
                foreach (RoleMembershipData r in rdata)
                {
                    if (r != null && !r.RoleID.IsZero())
                    {
                        newRoleID = r.RoleID;
                        break;
                    }
                }

            member.Data["SelectedRoleID"] = newRoleID.ToString();
            m_Database.StoreMember(member);
        }

        return true;
    }

    public List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID)
    {
        List<GroupRolesData> roles = new List<GroupRolesData>();
        // TODO: check permissions

        RoleMembershipData[] data = m_Database.RetrieveMemberRoles(GroupID, AgentID);
        if (data == null || (data != null && data.Length ==0))
            return roles;

        foreach (RoleMembershipData d in data)
        {
            RoleData rdata = m_Database.RetrieveRole(GroupID, d.RoleID);
            if (rdata == null) // hippos
                continue;

            GroupRolesData r = new GroupRolesData();
            r.Name = rdata.Data["Name"];
            r.Powers = UInt64.Parse(rdata.Data["Powers"]);
            r.RoleID = rdata.RoleID;
            r.Title = rdata.Data["Title"];

            roles.Add(r);
        }

        return roles;
    }

    public ExtendedGroupMembershipData SetAgentActiveGroup(string RequestingAgentID, string AgentID, UUID GroupID)
    {
        // An avatar chooses its own active group, among the groups it is in (or none).
        if (!SameAgent(RequestingAgentID, AgentID))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at choosing the active group of {1} denied", RequestingAgentID, AgentID);
            return null;
        }
        if (!GroupID.IsZero() && m_Database.RetrieveMember(GroupID, AgentID) == null)
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at making group {1}, which they are not in, active denied", AgentID, GroupID);
            return null;
        }

        PrincipalData principal = new PrincipalData();
        principal.PrincipalID = AgentID;
        principal.ActiveGroupID = GroupID;
        m_Database.StorePrincipal(principal);

        return GetAgentGroupMembership(RequestingAgentID, AgentID, GroupID);
    }

    public ExtendedGroupMembershipData GetAgentActiveMembership(string RequestingAgentID, string AgentID)
    {
        // 1. get the principal data for the active group
        PrincipalData principal = m_Database.RetrievePrincipal(AgentID);
        if (principal == null)
            return null;

        return GetAgentGroupMembership(RequestingAgentID, AgentID, principal.ActiveGroupID);
    }

    public ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID)
    {
        return GetAgentGroupMembership(RequestingAgentID, AgentID, GroupID, null);
    }

    private ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID, MembershipData membership)
    {
        // 2. get the active group
        GroupData group = m_Database.RetrieveGroup(GroupID);
        if (group == null)
            return null;

        // 3. get the membership info if we don't have it already
        if (membership == null)
        {
            membership = m_Database.RetrieveMember(group.GroupID, AgentID);
            if (membership == null)
                return null;
        }

        return MembershipOf(group, membership, m_Database.RetrieveRoles(group.GroupID) ?? [],
            m_Database.RetrieveMemberRoles(group.GroupID, AgentID) ?? []);
    }

    /// <summary>A member's view of one group, from rows already read: the group's roles and those the member holds.</summary>
    private static ExtendedGroupMembershipData MembershipOf(GroupData group, MembershipData membership,
        IEnumerable<RoleData> groupRoles, IEnumerable<RoleMembershipData> held)
    {
        List<RoleData> roles = groupRoles.ToList();

        // 4. get the active role
        UUID activeRoleID = new UUID(membership.Data["SelectedRoleID"]);
        RoleData role = roles.Find(r => r.RoleID == activeRoleID);

        ExtendedGroupMembershipData data = new ExtendedGroupMembershipData();
        data.AcceptNotices = membership.Data["AcceptNotices"] == "1" ? true : false;
        data.AccessToken = membership.Data["AccessToken"];
        data.Active = true;
        data.ActiveRole = activeRoleID;
        data.AllowPublish = group.Data["AllowPublish"] == "1" ? true : false;
        data.Charter = group.Data["Charter"];
        data.Contribution = Int32.Parse(membership.Data["Contribution"]);
        data.FounderID = new UUID(group.Data["FounderID"]);
        data.GroupID = new UUID(group.GroupID);
        data.GroupName = group.Data["Name"];
        data.GroupPicture = new UUID(group.Data["InsigniaID"]);
        if (role != null)
            data.GroupTitle = role.Data["Title"];
        // A member's powers are those of every role they hold, not only the one whose title they show; owners have all.
        data.GroupPowers = held.Any() ? PowersOf(held, roles, OwnerRoleOf(group)) : 0;
        data.ListInProfile = membership.Data["ListInProfile"] == "1" ? true : false;
        data.MaturePublish = group.Data["MaturePublish"] == "1" ? true : false;
        data.MembershipFee = Int32.Parse(group.Data["MembershipFee"]);
        data.OpenEnrollment = group.Data["OpenEnrollment"] == "1" ? true : false;
        data.ShowInList = group.Data["ShowInList"] == "1" ? true : false;

        return data;
    }

    public List<GroupMembershipData> GetAgentGroupMemberships(string RequestingAgentID, string AgentID)
    {
        // Four statements whatever the number of groups (it used to be four per group): the avatar's memberships,
        // those groups, their roles, and the roles the avatar holds in them.
        List<GroupMembershipData> memberships = new List<GroupMembershipData>();

        // 1. Get all the groups that this person is a member of
        MembershipData[] mdata = m_Database.RetrieveMemberships(AgentID);
        if (mdata == null || mdata.Length == 0)
            return memberships;

        UUID[] groupIDs = mdata.Select(d => d.GroupID).Distinct().ToArray();
        Dictionary<UUID, GroupData> groups = new Dictionary<UUID, GroupData>();
        foreach (GroupData g in m_Database.RetrieveGroupsByID(groupIDs) ?? [])
            groups.TryAdd(g.GroupID, g);
        ILookup<UUID, RoleData> roles = (m_Database.RetrieveRolesOfGroups(groupIDs) ?? []).ToLookup(r => r.GroupID);
        ILookup<UUID, RoleMembershipData> held = (m_Database.RetrievePrincipalRoles(AgentID) ?? [])
            .Where(r => SamePrincipal(r.PrincipalID, AgentID)).ToLookup(r => r.GroupID);

        foreach (MembershipData d in mdata)
        {
            if (groups.TryGetValue(d.GroupID, out GroupData group))
                memberships.Add(MembershipOf(group, d, roles[d.GroupID], held[d.GroupID]));
        }

        return memberships;
    }

    public void SetAgentActiveGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
    {
        // A member chooses their own title, from the roles they hold.
        if (!SameAgent(RequestingAgentID, AgentID))
            return;

        MembershipData data = m_Database.RetrieveMember(GroupID, AgentID);
        if (data == null)
            return;

        if (!RoleID.IsZero() && m_Database.RetrieveRoleMember(GroupID, RoleID, AgentID) == null)
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at showing the title of role {1} in group {2}, which they do not hold, denied", AgentID, RoleID, GroupID);
            return;
        }

        data.Data["SelectedRoleID"] = RoleID.ToString();
        m_Database.StoreMember(data);
    }

    public void UpdateMembership(string RequestingAgentID, string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile)
    {
        // Whether to take notices and to list the group in one's profile are the member's own to choose.
        if (!SameAgent(RequestingAgentID, AgentID))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at changing the membership settings of {1} denied", RequestingAgentID, AgentID);
            return;
        }

        MembershipData membership = m_Database.RetrieveMember(GroupID, AgentID);
        if (membership == null)
            return;

        membership.Data["AcceptNotices"] = AcceptNotices ? "1" : "0";
        membership.Data["ListInProfile"] = ListInProfile ? "1" : "0";

        m_Database.StoreMember(membership);
    }

    public bool AddGroupNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
        bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
    {
        // Check perms
        if (!HasPower(RequestingAgentID, groupID, GroupPowers.SendNotices))
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at sending notice to group {1} denied because of lack of permission", RequestingAgentID, groupID);
            return false;
        }

        return _AddNotice(groupID, noticeID, fromName, subject, message, hasAttachment, attType, attName, attItemID, attOwnerID);
    }

    public GroupNoticeInfo GetGroupNotice(string RequestingAgentID, UUID noticeID)
    {
        NoticeData data = m_Database.RetrieveNotice(noticeID);

        if (data == null)
            return null;

        return _NoticeDataToInfo(data);
    }

    public List<ExtendedGroupNoticeData> GetGroupNotices(string RequestingAgentID, UUID groupID)
    {
        NoticeData[] data = m_Database.RetrieveNotices(groupID);
        List<ExtendedGroupNoticeData> infos = new List<ExtendedGroupNoticeData>();

        if (data == null || (data != null && data.Length == 0))
            return infos;

        foreach (NoticeData d in data)
        {
            ExtendedGroupNoticeData info = _NoticeDataToData(d);
            infos.Add(info);
        }

        return infos;
    }

    public void ResetAgentGroupChatSessions(string agentID)
    {
    }

    public bool hasAgentBeenInvitedToGroupChatSession(string agentID, UUID groupID)
    {
        return false;
    }

    public bool hasAgentDroppedGroupChatSession(string agentID, UUID groupID)
    {
        return false;
    }

    public void AgentDroppedFromGroupChatSession(string agentID, UUID groupID)
    {
    }

    public void AgentInvitedToGroupChatSession(string agentID, UUID groupID)
    {
    }

    #region Actions without permission checks

    protected void _AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
    {
        _AddAgentToGroup(RequestingAgentID, AgentID, GroupID, RoleID, string.Empty);
    }

    protected void _RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
    {
        // 1. Delete membership
        m_Database.DeleteMember(GroupID, AgentID);

        // 2. Remove from rolememberships
        m_Database.DeleteMemberAllRoles(GroupID, AgentID);

        // 3. if it was active group, inactivate it
        PrincipalData principal = m_Database.RetrievePrincipal(AgentID);
        if (principal != null && principal.ActiveGroupID == GroupID)
        {
            principal.ActiveGroupID = UUID.Zero;
            m_Database.StorePrincipal(principal);
        }
    }

    protected void _AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string accessToken)
    {
        // Check if it's already there
        MembershipData data = m_Database.RetrieveMember(GroupID, AgentID);
        if (data != null)
            return;

        // Add the membership
        data = new MembershipData();
        data.PrincipalID = AgentID;
        data.GroupID = GroupID;
        data.Data = new Dictionary<string, string>();
        data.Data["SelectedRoleID"] = RoleID.ToString();
        data.Data["Contribution"] = "0";
        data.Data["ListInProfile"] = "1";
        data.Data["AcceptNotices"] = "1";
        data.Data["AccessToken"] = accessToken;

        m_Database.StoreMember(data);

        // Add principal to everyone role
        _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, UUID.Zero);

        // Add principal to role, if different from everyone role
        if (!RoleID.IsZero())
            _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);

        // Make this the active group
        PrincipalData pdata = new PrincipalData();
        pdata.PrincipalID = AgentID;
        pdata.ActiveGroupID = GroupID;
        m_Database.StorePrincipal(pdata);

    }

    protected bool _AddOrUpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, bool add)
    {
        RoleData data = m_Database.RetrieveRole(groupID, roleID);

        if (add && data != null) // it already exists, can't create
        {
            m_log.LogDebug("[Groups]: Group {0} already exists. Can't create it again", groupID);
            return false;
        }

        if (!add && data == null) // it doesn't exist, can't update
        {
            m_log.LogDebug("[Groups]: Group {0} doesn't exist. Can't update it", groupID);
            return false;
        }

        if (add)
            data = new RoleData();

        data.GroupID = groupID;
        data.RoleID = roleID;
        data.Data = new Dictionary<string, string>();
        data.Data["Name"] = name;
        data.Data["Description"] = description;
        data.Data["Title"] = title;
        data.Data["Powers"] = powers.ToString();

        return m_Database.StoreRole(data);
    }

    protected void _RemoveGroupRole(UUID groupID, UUID roleID)
    {
        m_Database.DeleteRole(groupID, roleID);
    }

    protected void _AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
    {
        _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID, true);
    }

    /// <param name="select">Whether the role becomes the one whose title the member shows (when they join).</param>
    protected void _AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, bool select)
    {
        RoleMembershipData data = m_Database.RetrieveRoleMember(GroupID, RoleID, AgentID);
        if (data != null)
            return;

        data = new RoleMembershipData();
        data.GroupID = GroupID;
        data.PrincipalID = AgentID;
        data.RoleID = RoleID;
        m_Database.StoreRoleMember(data);

        if (!select)
            return;

        // Make it the SelectedRoleID
        MembershipData membership = m_Database.RetrieveMember(GroupID, AgentID);
        if (membership == null)
        {
            m_log.LogDebug("[Groups]: ({0}) No such member in group {1}", AgentID, GroupID);
            return;
        }

        membership.Data["SelectedRoleID"] = RoleID.ToString();
        m_Database.StoreMember(membership);

    }

    protected List<GroupRolesData> _GetGroupRoles(UUID groupID)
    {
        List<GroupRolesData> roles = new List<GroupRolesData>();

        RoleData[] data = m_Database.RetrieveRoles(groupID);

        if (data == null || (data != null && data.Length == 0))
            return roles;

        foreach (RoleData d in data)
        {
            GroupRolesData r = new GroupRolesData();
            r.Description = d.Data["Description"];
            r.Members = m_Database.RoleMemberCount(groupID, d.RoleID);
            r.Name = d.Data["Name"];
            r.Powers = UInt64.Parse(d.Data["Powers"]);
            r.RoleID = d.RoleID;
            r.Title = d.Data["Title"];

            roles.Add(r);
        }

        return roles;
    }

    protected List<ExtendedGroupRoleMembersData> _GetGroupRoleMembers(UUID GroupID, bool isInGroup)
    {
        List<ExtendedGroupRoleMembersData> rmembers = new List<ExtendedGroupRoleMembersData>();

        RoleData[] rdata = new RoleData[0];
        if (!isInGroup)
        {
            rdata = m_Database.RetrieveRoles(GroupID);
            if (rdata == null || (rdata != null && rdata.Length == 0))
                return rmembers;
        }
        List<RoleData> rlist = new List<RoleData>(rdata);
        if (!isInGroup)
            rlist = rlist.FindAll(r => (UInt64.Parse(r.Data["Powers"]) & (ulong)GroupPowers.MemberVisible) != 0);

        RoleMembershipData[] data = m_Database.RetrieveRolesMembers(GroupID);

        if (data == null || (data != null && data.Length == 0))
            return rmembers;

        foreach (RoleMembershipData d in data)
        {
            if (!isInGroup)
            {
                RoleData rd = rlist.Find(_r => _r.RoleID == d.RoleID); // visible role
                if (rd == null)
                    continue;
            }

            ExtendedGroupRoleMembersData r = new ExtendedGroupRoleMembersData();
            r.MemberID = d.PrincipalID;
            r.RoleID = d.RoleID;

            rmembers.Add(r);
        }

        return rmembers;
    }

    protected bool _AddNotice(UUID groupID, UUID noticeID, string fromName, string subject, string message,
        bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
    {
        NoticeData data = new NoticeData();
        data.GroupID = groupID;
        data.NoticeID = noticeID;
        data.Data = new Dictionary<string, string>();
        data.Data["FromName"] = fromName;
        data.Data["Subject"] = subject;
        data.Data["Message"] = message;
        data.Data["HasAttachment"] = hasAttachment ? "1" : "0";
        if (hasAttachment)
        {
            data.Data["AttachmentType"] = attType.ToString();
            data.Data["AttachmentName"] = attName;
            data.Data["AttachmentItemID"] = attItemID.ToString();
            data.Data["AttachmentOwnerID"] = attOwnerID;
        }
        data.Data["TMStamp"] = ((uint)Util.UnixTimeSinceEpoch()).ToString();

        return m_Database.StoreNotice(data);
    }

    #endregion

    #region structure translations
    ExtendedGroupRecord _GroupDataToRecord(GroupData data)
    {
        if (data == null)
            return null;

        ExtendedGroupRecord rec = new ExtendedGroupRecord();
        rec.AllowPublish = data.Data["AllowPublish"] == "1" ? true : false;
        rec.Charter = data.Data["Charter"];
        rec.FounderID = new UUID(data.Data["FounderID"]);
        rec.GroupID = data.GroupID;
        rec.GroupName = data.Data["Name"];
        rec.GroupPicture = new UUID(data.Data["InsigniaID"]);
        rec.MaturePublish = data.Data["MaturePublish"] == "1" ? true : false;
        rec.MembershipFee = Int32.Parse(data.Data["MembershipFee"]);
        rec.OpenEnrollment = data.Data["OpenEnrollment"] == "1" ? true : false;
        rec.OwnerRoleID = new UUID(data.Data["OwnerRoleID"]);
        rec.ShowInList = data.Data["ShowInList"] == "1" ? true : false;
        rec.ServiceLocation = data.Data["Location"];
        rec.MemberCount = m_Database.MemberCount(data.GroupID);
        rec.RoleCount = m_Database.RoleCount(data.GroupID);

        return rec;
    }

    GroupNoticeInfo _NoticeDataToInfo(NoticeData data)
    {
        GroupNoticeInfo notice = new GroupNoticeInfo();
        notice.GroupID = data.GroupID;
        notice.Message = data.Data["Message"];
        notice.noticeData = _NoticeDataToData(data);

        return notice;
    }

    ExtendedGroupNoticeData _NoticeDataToData(NoticeData data)
    {
        ExtendedGroupNoticeData notice = new ExtendedGroupNoticeData();
        notice.FromName = data.Data["FromName"];
        notice.NoticeID = data.NoticeID;
        notice.Subject = data.Data["Subject"];
        notice.Timestamp = uint.Parse((string)data.Data["TMStamp"]);
        notice.HasAttachment = data.Data["HasAttachment"] == "1" ? true : false;
        if (notice.HasAttachment)
        {
            notice.AttachmentName = data.Data["AttachmentName"];
            notice.AttachmentItemID = new UUID(data.Data["AttachmentItemID"].ToString());
            notice.AttachmentType = byte.Parse(data.Data["AttachmentType"].ToString());
            notice.AttachmentOwnerID = data.Data["AttachmentOwnerID"].ToString();
        }


        return notice;
    }

    #endregion

    #region permissions

    /// <summary>The viewer's GP_ALL_POWERS: every ability, which owners always have.</summary>
    public const ulong AllPowers = GroupRules.AllPowers;

    private static UUID OwnerRoleOf(GroupData group) => GroupRules.OwnerRoleOf(group);

    /// <summary>
    /// The pending invitation of <paramref name="agentID"/> to the group. A hypergrid visitor may be named by a
    /// universal identifier (uuid;url;name) on one side and by the bare UUID on the other, so the UUID is tried too.
    /// </summary>
    private InvitationData FindInvitation(UUID groupID, string agentID)
    {
        InvitationData invite = m_Database.RetrieveInvitation(groupID, agentID);
        if (invite == null && agentID != null && agentID.Contains(';')
            && Util.ParseUniversalUserIdentifier(agentID, out UUID id))
            invite = m_Database.RetrieveInvitation(groupID, id.ToString());
        return invite;
    }

    private static bool SameAgent(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ulong PowersOf(IEnumerable<RoleMembershipData> held, List<RoleData> roles, UUID ownerRoleID)
        => GroupRules.PowersOf(held, roles, ownerRoleID);

    #region bans (GroupAPIv1)
    /// <summary>The most bans a group keeps, as SL's ban list does.</summary>
    public const int MaxBans = 500;

    /// <summary>The group's bans, for someone who may manage them; null otherwise.</summary>
    public List<BanData> GetGroupBans(string RequestingAgentID, UUID GroupID)
    {
        if (!GroupRules.For(m_Database, GroupID, RequestingAgentID).CanManageBans)
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at reading the bans of group {1} denied because of lack of permission", RequestingAgentID, GroupID);
            return null;
        }
        return new List<BanData>(m_Database.RetrieveBans(GroupID) ?? []);
    }

    /// <summary>
    /// Ban avatars from the group: someone who may manage bans; never an owner or oneself; at most
    /// <see cref="MaxBans"/>. A pending invitation of a banned avatar is withdrawn. The viewer ejects banned members
    /// itself (LLPanelGroupMembersSubTab), so a member stays one until ejected. False when nothing was banned.
    /// </summary>
    public bool BanAgents(string RequestingAgentID, UUID GroupID, string[] AgentIDs, out string reason)
    {
        reason = string.Empty;
        GroupRules rules = GroupRules.For(m_Database, GroupID, RequestingAgentID);
        if (!rules.GroupExists || !rules.CanManageBans)
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at banning from group {1} denied because of lack of permission", RequestingAgentID, GroupID);
            reason = "Insufficient permission";
            return false;
        }

        int count = (m_Database.RetrieveBans(GroupID) ?? []).Length;
        bool any = false;
        foreach (string agent in (AgentIDs ?? []).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SameAgent(agent, RequestingAgentID) || rules.ForOther(m_Database, agent).IsOwner)
                continue;   // not oneself, not an owner
            if (m_Database.RetrieveBan(GroupID, agent) != null)
                continue;   // banned already
            if (count >= MaxBans)
            {
                reason = "The group's ban list is full";
                break;
            }

            BanData ban = new BanData
            {
                GroupID = GroupID,
                PrincipalID = agent,
                Data = new Dictionary<string, string> { ["BanTime"] = Util.UnixTimeSinceEpoch().ToString() },
            };
            if (!m_Database.StoreBan(ban))
                continue;
            count++;
            any = true;

            InvitationData invite = FindInvitation(GroupID, agent);
            if (invite != null)
                m_Database.DeleteInvite(invite.InviteID);
        }
        return any;
    }

    /// <summary>Lift bans: someone who may manage bans. False when nothing was lifted.</summary>
    public bool UnbanAgents(string RequestingAgentID, UUID GroupID, string[] AgentIDs)
    {
        if (!GroupRules.For(m_Database, GroupID, RequestingAgentID).CanManageBans)
        {
            m_log.LogDebug("[Groups]: ({0}) Attempt at unbanning from group {1} denied because of lack of permission", RequestingAgentID, GroupID);
            return false;
        }
        bool any = false;
        foreach (string agent in (AgentIDs ?? []).Where(a => !string.IsNullOrEmpty(a)))
            any |= m_Database.DeleteBan(GroupID, agent);
        return any;
    }

    /// <summary>
    /// Banned from the group. A hypergrid visitor may be named by a universal identifier (uuid;url;name) on one side
    /// and by the bare UUID on the other (the viewer bans by UUID), so the UUID is tried too.
    /// </summary>
    private bool IsBanned(UUID groupID, string agentID)
    {
        if (string.IsNullOrEmpty(agentID))
            return false;
        if (m_Database.RetrieveBan(groupID, agentID) != null)
            return true;
        return agentID.Contains(';') && Util.ParseUniversalUserIdentifier(agentID, out UUID id)
            && m_Database.RetrieveBan(groupID, id.ToString()) != null;
    }
    #endregion

    private bool HasPower(string agentID, UUID groupID, GroupPowers power)
        => GroupRules.For(m_Database, groupID, agentID).Has(power);

    private bool IsOwner(string agentID, UUID groupID)
        => GroupRules.For(m_Database, groupID, agentID).IsOwner;

    /// <summary>Whether <paramref name="agentID"/> may put a member in <paramref name="roleID"/>; see <see cref="GroupRules.CanAssign"/>.</summary>
    private bool CanAssignRole(string agentID, UUID groupID, UUID roleID)
        => GroupRules.For(m_Database, groupID, agentID).CanAssign(roleID);
    #endregion

}
