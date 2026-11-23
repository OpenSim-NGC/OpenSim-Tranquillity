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

using System.Diagnostics;
using System.Reflection;
using MySqlConnector;
using Nini.Config;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Groups;
using Xunit;
using Xunit.Abstractions;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Groups V2's member list and an avatar's memberships, on SQLite and (when their servers are named) MySQL and
/// PostgreSQL: the same answers as before, now in a fixed number of statements whatever the size. The statement
/// counts are checked through a proxy that counts the calls the service makes on its stores.
/// </summary>
[Collection("SQLite stores")]
public sealed class GroupsBatchedQueriesTests : IDisposable
{
    private readonly List<Action> m_cleanup = new();
    private readonly ITestOutputHelper m_out;

    public GroupsBatchedQueriesTests(ITestOutputHelper output) => m_out = output;

    public void Dispose()
    {
        foreach (Action a in m_cleanup)
            a();
    }

    #region the backends
    private sealed record Backend(string Name, GroupsService Service, IGroupsData Groups, IGridUserData GridUsers);

    private static readonly FieldInfo s_groupsField =
        typeof(GroupsServiceBase).GetField("m_Database", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo s_gridUsersField =
        typeof(GroupsServiceBase).GetField("m_GridUserService", BindingFlags.Instance | BindingFlags.NonPublic);

    private static Backend Of(string name, GroupsService service)
        => new(name, service, (IGroupsData)s_groupsField.GetValue(service), (IGridUserData)s_gridUsersField.GetValue(service));

    private static GroupsService ServiceOn(string dll, string conn)
    {
        var config = new IniConfigSource();
        IConfig groups = config.AddConfig("Groups");
        groups.Set("StorageProvider", dll);
        groups.Set("ConnectionString", conn);
        IConfig users = config.AddConfig("GridUserService");
        users.Set("StorageProvider", dll);
        users.Set("ConnectionString", conn);
        return new GroupsService(config, "Groups");
    }

    // a SQLite store keeps one connection per table type for the process, so SQLite shares the store tests' database
    private static Backend SQLite() => Of("SQLite", SqliteGroupsStoreTests.Service);

    private Backend MySQL()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "groupsbq_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        Admin(() => new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (var c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Admin(() => new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        });
        m_mysql = conn;
        return Of("MySQL", ServiceOn("OpenSim.Data.MySQL.dll", conn));
    }

    private Backend PGSQL()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "groupsbq_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        Admin(() => new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (var c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(() => new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        });
        m_pgsql = conn;
        return Of("PostgreSQL", ServiceOn("OpenSim.Data.PGSQL.dll", conn));
    }

    private string m_mysql, m_pgsql;

    private static void Admin(Func<System.Data.Common.DbConnection> open, string sql)
    {
        using var c = open();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(Func<System.Data.Common.DbConnection> open, string sql)
    {
        using var c = open();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
    #endregion

    #region counting the statements
    /// <summary>Counts the calls made through an interface, then passes them on.</summary>
    public class Counting<T> : DispatchProxy where T : class
    {
        public T Inner;
        public readonly Dictionary<string, int> Calls = new();
        public int Total => Calls.Values.Sum();

        protected override object Invoke(MethodInfo method, object[] args)
        {
            Calls[method.Name] = Calls.GetValueOrDefault(method.Name) + 1;
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }

        public static (T Proxy, Counting<T> Counter) Around(T inner)
        {
            T proxy = Create<T, Counting<T>>();
            var counter = (Counting<T>)(object)proxy;
            counter.Inner = inner;
            return (proxy, counter);
        }
    }

    /// <summary>Runs <paramref name="act"/> with the service's stores counted; restores them afterwards.</summary>
    private static (int Groups, int GridUsers, string Detail) Count(Backend b, Action act)
    {
        var (groups, gc) = Counting<IGroupsData>.Around(b.Groups);
        var (users, uc) = Counting<IGridUserData>.Around(b.GridUsers);
        s_groupsField.SetValue(b.Service, groups);
        s_gridUsersField.SetValue(b.Service, users);
        try { act(); }
        finally
        {
            s_groupsField.SetValue(b.Service, b.Groups);
            s_gridUsersField.SetValue(b.Service, b.GridUsers);
        }
        string detail = string.Join(", ", gc.Calls.Concat(uc.Calls).Select(kv => $"{kv.Key} {kv.Value}"));
        return (gc.Total, uc.Total, detail);
    }
    #endregion

    #region the scenario
    private const string Foreign = ";http://elsewhere.example:8002/;Visiting Person";

    /// <summary>
    /// A group with a founder, members who joined (one of them from another grid), a Builders role held by one
    /// member, and grid user rows for some: the member list and the memberships give what the viewer needs.
    /// </summary>
    private void TheMemberListAndMembershipsAreRight(Backend b)
    {
        GroupsService s = b.Service;
        string tag = Guid.NewGuid().ToString("N")[..8];
        string founder = UUID.Random().ToString();
        UUID group = s.CreateGroup(founder, "Batched " + tag, "charter", true, UUID.Zero, 0, true, true, false,
            new UUID(founder), out string reason);
        Assert.True(group.IsNotZero(), reason);

        UUID builders = UUID.Random();
        Assert.True(s.AddGroupRole(founder, group, builders, "Builders", "", "Builder", (ulong)GroupPowers.AllowRez, out reason), reason);

        var joiners = Enumerable.Range(0, 30).Select(_ => UUID.Random().ToString()).ToList();
        string visitor = UUID.Random().ToString() + Foreign;
        joiners.Add(visitor);
        foreach (string j in joiners)
            Assert.True(s.AddAgentToGroup(j, j, group, UUID.Zero, string.Empty, out reason), reason);

        string builder = joiners[0];
        Assert.True(s.AddAgentToGroupRole(founder, builder, group, builders));
        s.SetAgentActiveGroupRole(builder, builder, group, builders);

        // grid user rows: the builder online, another member seen on a known day, the rest never seen
        string seen = joiners[1];
        b.GridUsers.Store(new GridUserData { UserID = builder, Data = GridUser(online: true, login: 1700000000) });
        b.GridUsers.Store(new GridUserData { UserID = seen, Data = GridUser(online: false, login: 1700000000) });

        // the builder is also in two more groups, as an owner of one
        var others = Enumerable.Range(0, 2).Select(i =>
        {
            string by = i == 0 ? builder : founder;
            UUID g = s.CreateGroup(by, $"Batched {tag} other {i}", "", true, UUID.Zero, 0, true, true, false,
                new UUID(by), out string r);
            Assert.True(g.IsNotZero(), r);
            if (i == 1)
                Assert.True(s.AddAgentToGroup(builder, builder, g, UUID.Zero, string.Empty, out r), r);
            return g;
        }).ToList();

        // the member list, as the founder asks for it
        List<ExtendedGroupMembersData> members = null;
        var listed = Count(b, () => members = s.GetGroupMembers(founder, group));
        Assert.Equal(joiners.Count + 1, members.Count);
        Assert.True(members.Single(m => m.AgentID == founder).IsOwner);
        Assert.Equal(GroupsServiceAllPowers(), members.Single(m => m.AgentID == founder).AgentPowers);
        ExtendedGroupMembersData bm = members.Single(m => m.AgentID == builder);
        Assert.False(bm.IsOwner);
        Assert.Equal("Builder", bm.Title);
        Assert.True((bm.AgentPowers & (ulong)GroupPowers.AllowRez) != 0, $"builder powers {bm.AgentPowers:x}");
        Assert.Equal("Online", bm.OnlineStatus);
        Assert.Equal(Util.ToDateTime(1700000000).ToString("MM/dd/yyyy"), members.Single(m => m.AgentID == seen).OnlineStatus);
        Assert.Contains(members, m => m.AgentID == visitor);
        Assert.Equal(0UL, members.Single(m => m.AgentID == joiners[2]).AgentPowers & (ulong)GroupPowers.AllowRez);

        // fixed, whatever the size: group, roles, members, role memberships; grid users in one batch
        m_out.WriteLine($"{b.Name} member list of {members.Count}: {listed.Detail}");
        Assert.True(listed.Groups <= 4, $"{listed.Groups} groups-store calls: {listed.Detail}");
        Assert.Equal(1, listed.GridUsers);

        // the builder's memberships: three groups, with the right title and powers in each
        List<GroupMembershipData> mine = null;
        var arrival = Count(b, () => mine = s.GetAgentGroupMemberships(builder, builder));
        Assert.Equal(3, mine.Count);
        GroupMembershipData inGroup = mine.Single(m => m.GroupID == group);
        Assert.Equal("Builder", inGroup.GroupTitle);
        Assert.Equal(builders, inGroup.ActiveRole);
        Assert.True((inGroup.GroupPowers & (ulong)GroupPowers.AllowRez) != 0);
        Assert.Equal(GroupsServiceAllPowers(), mine.Single(m => m.GroupID == others[0]).GroupPowers);   // founder, so owner
        Assert.Equal(0UL, mine.Single(m => m.GroupID == others[1]).GroupPowers & (ulong)GroupPowers.Eject);

        m_out.WriteLine($"{b.Name} memberships of 3 groups: {arrival.Detail}");
        Assert.Equal(4, arrival.Groups);
        Assert.Equal(0, arrival.GridUsers);

        // the one-group lookup still agrees with the list
        ExtendedGroupMembershipData one = s.GetAgentGroupMembership(builder, builder, group);
        Assert.Equal(inGroup.GroupPowers, one.GroupPowers);
        Assert.Equal(inGroup.GroupTitle, one.GroupTitle);

        // someone outside the group sees no title of a role whose members are hidden
        var outsider = s.GetGroupMembers(UUID.Random().ToString(), group);
        Assert.Null(outsider.Single(m => m.AgentID == builder).Title);
    }

    private static Dictionary<string, string> GridUser(bool online, int login) => new()
    {
        ["HomeRegionID"] = UUID.Zero.ToString(), ["HomePosition"] = "<0,0,0>", ["HomeLookAt"] = "<0,0,0>",
        ["LastRegionID"] = UUID.Zero.ToString(), ["LastPosition"] = "<0,0,0>", ["LastLookAt"] = "<0,0,0>",
        ["Online"] = online.ToString(), ["Login"] = login.ToString(), ["Logout"] = "0",
    };

    private static ulong GroupsServiceAllPowers()
        => (ulong)typeof(GroupsService).GetField("AllPowers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);
    #endregion

    [Fact]
    public void SQLiteListsMembersAndMembershipsInAFewStatements() => TheMemberListAndMembershipsAreRight(SQLite());

    [MySQLServerFact]
    public void MySQLListsMembersAndMembershipsInAFewStatements()
    {
        TheMemberListAndMembershipsAreRight(MySQL());
        // the index's two columns
        Assert.Equal(2L, Convert.ToInt64(Scalar(() => new MySqlConnection(m_mysql),
            "select count(*) from information_schema.statistics where table_schema = database() " +
            "and table_name = 'os_groups_rolemembership' and index_name = 'GroupPrincipal'")));
    }

    [PGSQLServerFact]
    public void PGSQLListsMembersAndMembershipsInAFewStatements()
    {
        TheMemberListAndMembershipsAreRight(PGSQL());
        Assert.Equal(5L, Convert.ToInt64(Scalar(() => new NpgsqlConnection(m_pgsql),
            "select count(*) from pg_indexes where indexname in ('os_groups_rolemembership_member', " +
            "'os_groups_rolemembership_principal', 'os_groups_membership_principal', 'os_groups_notices_group', " +
            "'os_groups_notices_stamp')")));
    }

    #region at scale (opt-in)
    /// <summary>
    /// With OPENSIM_TEST_GROUPS_SCALE=n, a group of n members is seeded straight into each store named (SQLite
    /// always), and the member list and a busy avatar's memberships are timed. A measurement, not a check.
    /// </summary>
    [Fact]
    public void TimingsAtScale()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("OPENSIM_TEST_GROUPS_SCALE"), out int n) || n <= 0)
            return;

        var backends = new List<Backend> { SQLite() };
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar)))
            backends.Add(MySQL());
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar)))
            backends.Add(PGSQL());

        foreach (Backend b in backends)
        {
            string founder = UUID.Random().ToString();
            string tag = Guid.NewGuid().ToString("N")[..8];
            UUID group = b.Service.CreateGroup(founder, "Scale " + tag, "", true, UUID.Zero, 0, true, true, false,
                new UUID(founder), out string reason);
            Assert.True(group.IsNotZero(), reason);

            var seed = Stopwatch.StartNew();
            string busy = null;
            for (int i = 0; i < n; i++)
            {
                string p = UUID.Random().ToString();
                busy ??= p;
                b.Groups.StoreMember(new MembershipData { GroupID = group, PrincipalID = p, Data = new()
                {
                    ["SelectedRoleID"] = UUID.Zero.ToString(), ["Contribution"] = "0", ["ListInProfile"] = "1",
                    ["AcceptNotices"] = "1", ["AccessToken"] = UUID.Random().ToString(),
                } });
                b.Groups.StoreRoleMember(new RoleMembershipData { GroupID = group, RoleID = UUID.Zero, PrincipalID = p });
                if (i % 2 == 0)
                    b.GridUsers.Store(new GridUserData { UserID = p, Data = GridUser(i % 4 == 0, 1700000000 + i) });
            }
            // the busy avatar is in 25 more groups
            for (int g = 0; g < 25; g++)
            {
                UUID other = b.Service.CreateGroup(founder, $"Scale {tag} {g}", "", true, UUID.Zero, 0, true, true,
                    false, new UUID(founder), out reason);
                Assert.True(b.Service.AddAgentToGroup(busy, busy, other, UUID.Zero, string.Empty, out reason), reason);
            }
            m_out.WriteLine($"{b.Name}: seeded {n} members in {seed.Elapsed.TotalSeconds:0.0} s");

            for (int run = 0; run < 3; run++)
            {
                var t = Stopwatch.StartNew();
                int count = b.Service.GetGroupMembers(founder, group).Count;
                double list = t.Elapsed.TotalMilliseconds;
                t.Restart();
                int groups = b.Service.GetAgentGroupMemberships(busy, busy).Count;
                double arrive = t.Elapsed.TotalMilliseconds;
                m_out.WriteLine($"{b.Name} run {run}: member list of {count} in {list:0} ms; memberships of {groups} groups in {arrive:0.0} ms");
            }
        }
    }
    #endregion
}
