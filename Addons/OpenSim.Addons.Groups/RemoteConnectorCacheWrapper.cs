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

using OpenSim.Framework;
//using OpenSim.Region.Framework.Interfaces;
using OpenMetaverse;

using Microsoft.Extensions.Logging;

namespace OpenSim.Groups;

public delegate ExtendedGroupRecord GroupRecordDelegate();
public delegate GroupMembershipData GroupMembershipDelegate();
public delegate List<GroupMembershipData> GroupMembershipListDelegate();
public delegate List<ExtendedGroupMembersData> GroupMembersListDelegate();
public delegate List<GroupRolesData> GroupRolesListDelegate();
public delegate List<ExtendedGroupRoleMembersData> RoleMembersListDelegate();
public delegate GroupNoticeInfo NoticeDelegate();
public delegate List<ExtendedGroupNoticeData> NoticeListDelegate();
public delegate void VoidDelegate();
public delegate bool BooleanDelegate();

public class RemoteConnectorCacheWrapper
{
    private const int GROUPS_CACHE_TIMEOUT = 1 * 60; // 1 minutes

    private ForeignImporter m_ForeignImporter;
    private HashSet<string> m_ActiveRequests = new HashSet<string>();

    // This all important cache caches objects of different types:
    // group-<GroupID> or group-<Name>          => ExtendedGroupRecord
    // active-<AgentID>                         => GroupMembershipData
    // membership-<AgentID>-<GroupID>           => GroupMembershipData
    // memberships-<AgentID>                    => List<GroupMembershipData>
    // members-<RequestingAgentID>-<GroupID>    => List<ExtendedGroupMembersData>
    // role-<RoleID>                            => GroupRolesData
    // roles-<GroupID>                          => List<GroupRolesData> ; all roles in the group
    // roles-<GroupID>-<AgentID>                => List<GroupRolesData> ; roles that the agent has
    // rolemembers-<RequestingAgentID>-<GroupID> => List<ExtendedGroupRoleMembersData>
    // notice-<noticeID>                        => GroupNoticeInfo
    // notices-<GroupID>                        => List<ExtendedGroupNoticeData>
    private ExpiringCacheOS<string, object> m_Cache = new ExpiringCacheOS<string, object>(30000);

    // Which entries depend on what: "g:<GroupID>" and "a:<AgentID>" (lower case) to the keys cached with them, so a
    // change forgets only those entries. Keys of entries that have since expired stay until pruned.
    private readonly Dictionary<string, HashSet<string>> m_Tags = new Dictionary<string, HashSet<string>>();
    private int m_TaggedKeys;
    private const int PRUNE_TAGS_ABOVE = 20000;

    private static string GroupTag(UUID groupID) => groupID.IsZero() ? null : "g:" + groupID.ToString();

    private static string AgentTag(string agentID) => string.IsNullOrEmpty(agentID) ? null : "a:" + agentID.ToLowerInvariant();

    /// <summary>Cache an entry, noting the groups and avatars it depends on.</summary>
    private void Put(string key, object value, params string[] tags)
    {
        lock (m_Cache)
        {
            m_Cache.AddOrUpdate(key, value, GROUPS_CACHE_TIMEOUT);
            foreach (string tag in tags)
            {
                if (tag == null)
                    continue;
                if (!m_Tags.TryGetValue(tag, out HashSet<string> keys))
                    m_Tags[tag] = keys = new HashSet<string>();
                if (keys.Add(key))
                    m_TaggedKeys++;
            }
            if (m_TaggedKeys > PRUNE_TAGS_ABOVE)
                PruneTags();
        }
    }

    /// <summary>Drop the notes of entries no longer cached (under the cache's lock).</summary>
    private void PruneTags()
    {
        m_TaggedKeys = 0;
        foreach (string tag in m_Tags.Keys.ToList())
        {
            HashSet<string> keys = m_Tags[tag];
            keys.RemoveWhere(k => !m_Cache.Contains(k));
            if (keys.Count == 0)
                m_Tags.Remove(tag);
            else
                m_TaggedKeys += keys.Count;
        }
    }

    private void ForgetTag(string tag)
    {
        if (tag == null)
            return;
        lock (m_Cache)
        {
            if (!m_Tags.Remove(tag, out HashSet<string> keys))
                return;
            m_TaggedKeys -= keys.Count;
            foreach (string key in keys)
                m_Cache.Remove(key);
        }
    }

    /// <summary>
    /// Forget what is cached about a group: its record, members, roles and notices, and every avatar's memberships
    /// that carry it (each avatar's list of groups is noted under every group in it).
    /// </summary>
    public void ForgetGroup(UUID groupID) => ForgetTag(GroupTag(groupID));

    /// <summary>
    /// Forget what is cached about an avatar: their memberships and active group, and the lists they asked for (their
    /// own membership decides what those show).
    /// </summary>
    public void ForgetAgent(string agentID) => ForgetTag(AgentTag(agentID));

    public RemoteConnectorCacheWrapper(IUserManagement uman)
    {
        m_ForeignImporter = new ForeignImporter(uman);
        GroupsChangeNotice.Register(this);
    }



    public UUID CreateGroup(UUID RequestingAgentID, GroupRecordDelegate d)
    {
        //m_log.LogDebug("[Groups.RemoteConnector]: Creating group {0}", name);
        //reason = string.Empty;

        //ExtendedGroupRecord group = m_GroupsService.CreateGroup(RequestingAgentID.ToString(), name, charter, showInList, insigniaID,
        //    membershipFee, openEnrollment, allowPublish, maturePublish, founderID, out reason);
        ExtendedGroupRecord group = d();

        if (group == null)
            return UUID.Zero;

        if (!group.GroupID.IsZero())
        {
            Put("group-" + group.GroupID.ToString(), group, GroupTag(group.GroupID));
            ForgetAgent(RequestingAgentID.ToString());
        }
        return group.GroupID;
    }

    public bool UpdateGroup(UUID groupID, GroupRecordDelegate d)
    {
        ExtendedGroupRecord group = d();
        if (group == null)
            return false;   // the groups service did not answer

        // every member's memberships carry the group's settings
        ForgetGroup(groupID);
        if (!group.GroupID.IsZero())
            Put("group-" + group.GroupID.ToString(), group, GroupTag(group.GroupID));

        return true;
    }

    public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID, string GroupName, GroupRecordDelegate d)
    {
        //if (GroupID.IsZero() && string.IsNullOrEmpty(GroupName))
        //    return null;

        object group = null;
        bool firstCall = false;
        string cacheKey = "group-";
        if (GroupID.IsZero())
            cacheKey += GroupName;
        else
            cacheKey += GroupID.ToString();

        //m_log.LogDebug("[XXX]: GetGroupRecord {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out group))
                {
                    //m_log.LogDebug("[XXX]: GetGroupRecord {0} cached!", cacheKey);
                    return (ExtendedGroupRecord)group;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                    m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    //group = m_GroupsService.GetGroupRecord(RequestingAgentID, GroupID, GroupName);
                    group = d();

                    lock (m_Cache)
                    {
                        Put(cacheKey, group, GroupTag((group as ExtendedGroupRecord)?.GroupID ?? GroupID));
                        return (ExtendedGroupRecord)group;
                    }
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, GroupMembershipDelegate d)
    {
        GroupMembershipData membership = d();
        if (membership == null)
            return false;

        lock (m_Cache)
        {
            // first, remove everything! add a user is a heavy-duty op
            ForgetGroup(GroupID);
            ForgetAgent(AgentID);

            Put("active-" + AgentID.ToString(), membership, AgentTag(AgentID), GroupTag(GroupID));
            Put("membership-" + AgentID.ToString() + "-" + GroupID.ToString(), membership, AgentTag(AgentID), GroupTag(GroupID));
        }
        return true;
    }

    public void RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID, VoidDelegate d)
    {
        d();

        // the member is gone from every member list of the group, not only the requester's, and their own groups changed
        ForgetGroup(GroupID);
        ForgetAgent(AgentID);
    }

    public void SetAgentActiveGroup(string AgentID, GroupMembershipDelegate d)
    {
        GroupMembershipData activeGroup = d();
        string cacheKey = "active-" + AgentID.ToString();
        Put(cacheKey, activeGroup, AgentTag(AgentID), GroupTag(activeGroup?.GroupID ?? UUID.Zero));
    }

    public ExtendedGroupMembershipData GetAgentActiveMembership(string AgentID, GroupMembershipDelegate d)
    {
        object membership = null;
        bool firstCall = false;
        string cacheKey = "active-" + AgentID.ToString();

        //m_log.LogDebug("[XXX]: GetAgentActiveMembership {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out membership))
                {
                    //m_log.LogDebug("[XXX]: GetAgentActiveMembership {0} cached!", cacheKey);
                    return (ExtendedGroupMembershipData)membership;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    membership = d();
                    Put(cacheKey, membership, AgentTag(AgentID), GroupTag((membership as GroupMembershipData)?.GroupID ?? UUID.Zero));
                    return (ExtendedGroupMembershipData)membership;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }

    }

    public ExtendedGroupMembershipData GetAgentGroupMembership(string AgentID, UUID GroupID, GroupMembershipDelegate d)
    {
        object membership = null;
        bool firstCall = false;
        string cacheKey = "membership-" + AgentID.ToString() + "-" + GroupID.ToString();

        //m_log.LogDebug("[XXX]: GetAgentGroupMembership {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out membership))
                {
                    //m_log.LogDebug("[XXX]: GetAgentGroupMembership {0}", cacheKey);
                    return (ExtendedGroupMembershipData)membership;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    membership = d();
                    Put(cacheKey, membership, AgentTag(AgentID), GroupTag(GroupID));
                    return (ExtendedGroupMembershipData)membership;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public List<GroupMembershipData> GetAgentGroupMemberships(string AgentID, GroupMembershipListDelegate d)
    {
        object memberships = null;
        bool firstCall = false;
        string cacheKey = "memberships-" + AgentID.ToString();

        //m_log.LogDebug("[XXX]: GetAgentGroupMemberships {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out memberships))
                {
                    //m_log.LogDebug("[XXX]: GetAgentGroupMemberships {0} cached!", cacheKey);
                    return (List<GroupMembershipData>)memberships;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    memberships = d();
                    Put(cacheKey, memberships, ((memberships as List<GroupMembershipData>) ?? new List<GroupMembershipData>()).Select(m => GroupTag(m.GroupID)).Append(AgentTag(AgentID)).ToArray());
                    return (List<GroupMembershipData>)memberships;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public List<GroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID, GroupMembersListDelegate d)
    {
        object members = null;
        bool firstCall = false;
        // we need to key in also on the requester, because different ppl have different view privileges
        string cacheKey = "members-" + RequestingAgentID.ToString() + "-" + GroupID.ToString();

        //m_log.LogDebug("[XXX]: GetGroupMembers {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out members))
                {
                    List<ExtendedGroupMembersData> xx = (List<ExtendedGroupMembersData>)members;
                    return xx.ConvertAll<GroupMembersData>(new Converter<ExtendedGroupMembersData, GroupMembersData>(m_ForeignImporter.ConvertGroupMembersData));
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    List<ExtendedGroupMembersData> _members = d();

                    if (_members != null && _members.Count > 0)
                        members = _members.ConvertAll<GroupMembersData>(new Converter<ExtendedGroupMembersData, GroupMembersData>(m_ForeignImporter.ConvertGroupMembersData));
                    else
                        members = new List<GroupMembersData>();

                    Put(cacheKey, _members, GroupTag(GroupID), AgentTag(RequestingAgentID));
                    return (List<GroupMembersData>)members;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public bool AddGroupRole(UUID groupID, UUID roleID, string description, string name, ulong powers, string title, BooleanDelegate d)
    {
        if (d())
        {
            GroupRolesData role = new GroupRolesData();
            role.Description = description;
            role.Members = 0;
            role.Name = name;
            role.Powers = powers;
            role.RoleID = roleID;
            role.Title = title;

            Put("role-" + roleID.ToString(), role, GroupTag(groupID));

                // also remove this list
            m_Cache.Remove("roles-" + groupID.ToString());
            return true;
        }

        return false;
    }

    public bool UpdateGroupRole(UUID groupID, UUID roleID, string name, string description, string title, ulong powers, BooleanDelegate d)
    {
        if (d())
        {
            object role;
            lock (m_Cache)
                if (m_Cache.TryGetValue("role-" + roleID.ToString(), out role))
                {
                    GroupRolesData r = (GroupRolesData)role;
                    r.Description = description;
                    r.Name = name;
                    r.Powers = powers;
                    r.Title = title;

                    Put("role-" + roleID.ToString(), r, GroupTag(groupID));
                }
            // Every member who holds the role has new powers: their cached memberships are out of date.
            ForgetGroup(groupID);
            return true;
        }
        else
        {
            m_Cache.Remove("role-" + roleID.ToString());
            // also remove these lists, because they will have an outdated role
            m_Cache.Remove("roles-" + groupID.ToString());

            return false;
        }
    }

    public void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, VoidDelegate d)
    {
        d();

        lock (m_Cache)
        {
            m_Cache.Remove("role-" + roleID.ToString());
            // also remove the list, because it will have an removed role
            m_Cache.Remove("roles-" + groupID.ToString());
            m_Cache.Remove("roles-" + groupID.ToString() + "-" + RequestingAgentID.ToString());
            m_Cache.Remove("rolemembers-" + RequestingAgentID.ToString() + "-" + groupID.ToString());
            // the members who held it have lost its powers
            ForgetGroup(groupID);
        }
    }


    public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID, GroupRolesListDelegate d)
    {
        object roles = null;
        bool firstCall = false;
        string cacheKey = "roles-" + GroupID.ToString();

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out roles))
                    return (List<GroupRolesData>)roles;

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    roles = d();
                    if (roles != null)
                    {
                        Put(cacheKey, roles, GroupTag(GroupID));
                        return (List<GroupRolesData>)roles;
                    }
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public List<GroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID, RoleMembersListDelegate d)
    {
        object rmembers = null;
        bool firstCall = false;
        // we need to key in also on the requester, because different ppl have different view privileges
        string cacheKey = "rolemembers-" + RequestingAgentID.ToString() + "-" + GroupID.ToString();

        //m_log.LogDebug("[XXX]: GetGroupRoleMembers {0}", cacheKey);
        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out rmembers))
                {
                    List<ExtendedGroupRoleMembersData> xx = (List<ExtendedGroupRoleMembersData>)rmembers;
                    return xx.ConvertAll<GroupRoleMembersData>(m_ForeignImporter.ConvertGroupRoleMembersData);
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    List<ExtendedGroupRoleMembersData> _rmembers = d();

                    if (_rmembers != null && _rmembers.Count > 0)
                        rmembers = _rmembers.ConvertAll<GroupRoleMembersData>(new Converter<ExtendedGroupRoleMembersData, GroupRoleMembersData>(m_ForeignImporter.ConvertGroupRoleMembersData));
                    else
                        rmembers = new List<GroupRoleMembersData>();

                    // For some strange reason, when I cache the list of GroupRoleMembersData,
                    // it gets emptied out. The TryGet gets an empty list...
                    //m_Cache.AddOrUpdate(cacheKey, rmembers, GROUPS_CACHE_TIMEOUT);
                    // Caching the list of ExtendedGroupRoleMembersData doesn't show that issue
                    // I don't get it.
                    Put(cacheKey, _rmembers, GroupTag(GroupID), AgentTag(RequestingAgentID));
                    return (List<GroupRoleMembersData>)rmembers;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public void AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, BooleanDelegate d)
    {
        // the member's powers and title change in every member list, role list and membership of the group
        if (d())
        {
            ForgetGroup(GroupID);
            ForgetAgent(AgentID);
        }
    }

    public void RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, BooleanDelegate d)
    {
        if (d())
        {
            ForgetGroup(GroupID);
            ForgetAgent(AgentID);
        }
    }

    public List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID, GroupRolesListDelegate d)
    {
        object roles = null;
        bool firstCall = false;
        string cacheKey = "roles-" + GroupID.ToString() + "-" + AgentID.ToString();

        //m_log.LogDebug("[XXX]: GetAgentGroupRoles {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out roles))
                {
                    //m_log.LogDebug("[XXX]: GetAgentGroupRoles {0} cached!", cacheKey);
                    return (List<GroupRolesData>)roles;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    roles = d();
                    Put(cacheKey, roles, GroupTag(GroupID), AgentTag(AgentID));
                    return (List<GroupRolesData>)roles;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public void SetAgentActiveGroupRole(string AgentID, UUID GroupID, VoidDelegate d)
    {
        d();

        // the title the member shows is in every member list of the group
        ForgetGroup(GroupID);
        ForgetAgent(AgentID);
    }

    public void UpdateMembership(string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile, VoidDelegate d)
    {
        d();

        // whether the member takes notices is in every member list of the group, and notices are sent from it
        ForgetGroup(GroupID);
        ForgetAgent(AgentID);
    }

    public bool AddGroupNotice(UUID groupID, UUID noticeID, GroupNoticeInfo notice, BooleanDelegate d)
    {
        if (d())
        {
            Put("notice-" + noticeID.ToString(), notice, GroupTag(groupID));
            string cacheKey = "notices-" + groupID.ToString();
            m_Cache.Remove(cacheKey);

            return true;
        }

        return false;
    }

    public GroupNoticeInfo GetGroupNotice(UUID noticeID, NoticeDelegate d)
    {
        object notice = null;
        bool firstCall = false;
        string cacheKey = "notice-" + noticeID.ToString();

        //m_log.LogDebug("[XXX]: GetAgentGroupRoles {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out notice))
                {
                    return (GroupNoticeInfo)notice;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    GroupNoticeInfo _notice = d();

                    Put(cacheKey, _notice, GroupTag(_notice?.GroupID ?? UUID.Zero));
                    return _notice;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }

    public List<ExtendedGroupNoticeData> GetGroupNotices(UUID GroupID, NoticeListDelegate d)
    {
        object notices = null;
        bool firstCall = false;
        string cacheKey = "notices-" + GroupID.ToString();

        //m_log.LogDebug("[XXX]: GetGroupNotices {0}", cacheKey);

        while (true)
        {
            lock (m_Cache)
            {
                if (m_Cache.TryGetValue(cacheKey, out notices))
                {
                    //m_log.LogDebug("[XXX]: GetGroupNotices {0} cached!", cacheKey);
                    return (List<ExtendedGroupNoticeData>)notices;
                }

                // not cached
                if (!m_ActiveRequests.Contains(cacheKey))
                {
                   m_ActiveRequests.Add(cacheKey);
                    firstCall = true;
                }
            }

            if (firstCall)
            {
                try
                {
                    notices = d();

                    Put(cacheKey, notices, GroupTag(GroupID));
                    return (List<ExtendedGroupNoticeData>)notices;
                }
                finally
                {
                    // under the cache's lock, as every other use of the set
                    lock (m_Cache)
                        m_ActiveRequests.Remove(cacheKey);
                }
            }
            else
                Thread.Sleep(50);
        }
    }
}