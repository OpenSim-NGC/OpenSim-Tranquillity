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

using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite;

/// <summary>
/// The Groups V2 store (IGroupsData) on SQLite, so a SQLite standalone can run "Groups Module V2" with the local
/// connector. Tables and columns are MySQL's os_groups_Store; the queries are the MySQL store's, one for one.
/// </summary>
public class SQLiteGroupsData : IGroupsData
{
    private readonly SqliteGroupsHandler<GroupData> m_Groups;
    private readonly SqliteGroupsHandler<MembershipData> m_Membership;
    private readonly SqliteGroupsHandler<RoleData> m_Roles;
    private readonly SqliteGroupsHandler<RoleMembershipData> m_RoleMembership;
    private readonly SqliteGroupsHandler<InvitationData> m_Invites;
    private readonly SqliteGroupsHandler<NoticeData> m_Notices;
    private readonly SqliteGroupsHandler<PrincipalData> m_Principals;

    public SQLiteGroupsData(string connectionString, string realm)
    {
        DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

        // the groups table's handler first: it runs the migration that makes every table
        m_Groups = new SqliteGroupsHandler<GroupData>(connectionString, realm + "_groups", realm + "_Store");
        m_Membership = new SqliteGroupsHandler<MembershipData>(connectionString, realm + "_membership");
        m_Roles = new SqliteGroupsHandler<RoleData>(connectionString, realm + "_roles");
        m_RoleMembership = new SqliteGroupsHandler<RoleMembershipData>(connectionString, realm + "_rolemembership");
        m_Invites = new SqliteGroupsHandler<InvitationData>(connectionString, realm + "_invites");
        m_Notices = new SqliteGroupsHandler<NoticeData>(connectionString, realm + "_notices");
        m_Principals = new SqliteGroupsHandler<PrincipalData>(connectionString, realm + "_principals");
    }

    private static T First<T>(T[] rows) where T : class => rows is { Length: > 0 } ? rows[0] : null;

    #region groups table
    public bool StoreGroup(GroupData data) => m_Groups.Store(data);

    public GroupData RetrieveGroup(UUID groupID) => First(m_Groups.Get("GroupID", groupID.ToString()));

    public GroupData RetrieveGroup(string name) => First(m_Groups.Get("Name", name));

    public GroupData[] RetrieveGroups(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return m_Groups.Get("ShowInList = 1");

        // the search text is a parameter, never part of the statement
        return m_Groups.Query("ShowInList = 1 AND lower(Name) LIKE lower(:pattern) ESCAPE '\\'",
            new SQLiteParameter(":pattern", "%" + EscapeLike(pattern) + "%"));
    }

    public bool DeleteGroup(UUID groupID) => m_Groups.Delete("GroupID", groupID.ToString());

    public int GroupsCount() => m_Groups.Count("Location = ''");
    #endregion

    #region membership table
    public MembershipData[] RetrieveMembers(UUID groupID) => m_Membership.Get("GroupID", groupID.ToString());

    public MembershipData RetrieveMember(UUID groupID, string pricipalID)
        => First(m_Membership.Get(new[] { "GroupID", "PrincipalID" }, new[] { groupID.ToString(), pricipalID }));

    public MembershipData[] RetrieveMemberships(string pricipalID) => m_Membership.Get("PrincipalID", pricipalID);

    public bool StoreMember(MembershipData data) => m_Membership.Store(data);

    public bool DeleteMember(UUID groupID, string pricipalID)
        => m_Membership.Delete(new[] { "GroupID", "PrincipalID" }, new[] { groupID.ToString(), pricipalID });

    public int MemberCount(UUID groupID) => m_Membership.Count(("GroupID", groupID.ToString()));
    #endregion

    #region roles table
    public bool StoreRole(RoleData data) => m_Roles.Store(data);

    public RoleData RetrieveRole(UUID groupID, UUID roleID)
        => First(m_Roles.Get(new[] { "GroupID", "RoleID" }, new[] { groupID.ToString(), roleID.ToString() }));

    public RoleData[] RetrieveRoles(UUID groupID) => m_Roles.Get("GroupID", groupID.ToString());

    public bool DeleteRole(UUID groupID, UUID roleID)
        => m_Roles.Delete(new[] { "GroupID", "RoleID" }, new[] { groupID.ToString(), roleID.ToString() });

    public int RoleCount(UUID groupID) => m_Roles.Count(("GroupID", groupID.ToString()));
    #endregion

    #region rolememberhip table
    public RoleMembershipData[] RetrieveRolesMembers(UUID groupID) => m_RoleMembership.Get("GroupID", groupID.ToString());

    public RoleMembershipData[] RetrieveRoleMembers(UUID groupID, UUID roleID)
        => m_RoleMembership.Get(new[] { "GroupID", "RoleID" }, new[] { groupID.ToString(), roleID.ToString() });

    public RoleMembershipData[] RetrieveMemberRoles(UUID groupID, string principalID)
        => m_RoleMembership.Get(new[] { "GroupID", "PrincipalID" }, new[] { groupID.ToString(), principalID });

    public RoleMembershipData RetrieveRoleMember(UUID groupID, UUID roleID, string principalID)
        => First(m_RoleMembership.Get(new[] { "GroupID", "RoleID", "PrincipalID" },
                                      new[] { groupID.ToString(), roleID.ToString(), principalID }));

    public int RoleMemberCount(UUID groupID, UUID roleID)
        => m_RoleMembership.Count(("GroupID", groupID.ToString()), ("RoleID", roleID.ToString()));

    public bool StoreRoleMember(RoleMembershipData data) => m_RoleMembership.Store(data);

    public bool DeleteRoleMember(RoleMembershipData data)
        => m_RoleMembership.Delete(new[] { "GroupID", "RoleID", "PrincipalID" },
                                   new[] { data.GroupID.ToString(), data.RoleID.ToString(), data.PrincipalID });

    public bool DeleteMemberAllRoles(UUID groupID, string principalID)
        => m_RoleMembership.Delete(new[] { "GroupID", "PrincipalID" }, new[] { groupID.ToString(), principalID });
    #endregion

    #region principals table
    public bool StorePrincipal(PrincipalData data) => m_Principals.Store(data);

    public PrincipalData RetrievePrincipal(string principalID) => First(m_Principals.Get("PrincipalID", principalID));

    public bool DeletePrincipal(string principalID) => m_Principals.Delete("PrincipalID", principalID);
    #endregion

    #region invites table
    public bool StoreInvitation(InvitationData data) => m_Invites.Store(data);

    public InvitationData RetrieveInvitation(UUID inviteID) => First(m_Invites.Get("InviteID", inviteID.ToString()));

    public InvitationData RetrieveInvitation(UUID groupID, string principalID)
        => First(m_Invites.Get(new[] { "GroupID", "PrincipalID" }, new[] { groupID.ToString(), principalID }));

    public bool DeleteInvite(UUID inviteID) => m_Invites.Delete("InviteID", inviteID.ToString());

    // invites keep the column's CURRENT_TIMESTAMP (UTC text), as MySQL's
    public void DeleteOldInvites() => m_Invites.Execute("TMStamp < datetime('now', '-14 days')");
    #endregion

    #region notices table
    public bool StoreNotice(NoticeData data) => m_Notices.Store(data);

    public NoticeData RetrieveNotice(UUID noticeID) => First(m_Notices.Get("NoticeID", noticeID.ToString()));

    public NoticeData[] RetrieveNotices(UUID groupID) => m_Notices.Get("GroupID", groupID.ToString());

    public bool DeleteNotice(UUID noticeID) => m_Notices.Delete("NoticeID", noticeID.ToString());

    // notices carry a Unix time, written by the service
    public void DeleteOldNotices()
        => m_Notices.Execute("TMStamp < :tstamp",
            new SQLiteParameter(":tstamp", (long)Util.UnixTimeSinceEpoch() - 14 * 24 * 60 * 60));
    #endregion

    #region combinations
    // unused by the service, as in the MySQL and PostgreSQL stores
    public MembershipData RetrievePrincipalGroupMembership(string principalID, UUID groupID) => null;

    public MembershipData[] RetrievePrincipalGroupMemberships(string principalID) => null;
    #endregion

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <summary>A groups table: the generic handler, plus counts and deletes by a condition.</summary>
public class SqliteGroupsHandler<T> : SQLiteGenericTableHandler<T> where T : class, new()
{
    protected override Assembly Assembly => GetType().Assembly;

    public SqliteGroupsHandler(string connectionString, string realm, string storeName = "")
        : base(connectionString, realm, storeName)
    {
    }

    /// <summary>Rows matching a condition whose values are parameters.</summary>
    public T[] Query(string where, params SQLiteParameter[] parameters)
    {
        using var cmd = new SQLiteCommand($"select * from {m_Realm} where {where}");
        cmd.Parameters.AddRange(parameters);
        return DoQuery(cmd);
    }

    /// <summary>How many rows have these column values.</summary>
    public int Count(params (string Field, string Value)[] equals)
    {
        using var cmd = new SQLiteCommand();
        var terms = new List<string>();
        for (int i = 0; i < equals.Length; i++)
        {
            terms.Add($"`{equals[i].Field}` = :p{i}");
            cmd.Parameters.Add(new SQLiteParameter($":p{i}", equals[i].Value));
        }
        return Count(cmd, string.Join(" and ", terms));
    }

    /// <summary>How many rows match a fixed condition.</summary>
    public int Count(string where)
    {
        using var cmd = new SQLiteCommand();
        return Count(cmd, where);
    }

    private int Count(SQLiteCommand cmd, string where)
    {
        cmd.CommandText = $"select count(*) from {m_Realm} where {where}";
        lock (m_Connection)
        {
            cmd.Connection = m_Connection;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Delete the rows matching a condition.</summary>
    public void Execute(string where, params SQLiteParameter[] parameters)
    {
        using var cmd = new SQLiteCommand($"delete from {m_Realm} where {where}");
        cmd.Parameters.AddRange(parameters);
        ExecuteNonQuery(cmd, m_Connection);
    }
}
