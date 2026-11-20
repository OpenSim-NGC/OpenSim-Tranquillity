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
using V2Import = OpenSim.Groups.FlotsamGroupsImport;
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

    /// <summary>
    /// The group search: a listed group is found by part of its name, a hidden one is not, an empty search lists only
    /// listed groups, and a quote in the text is text.
    /// </summary>
    private static void SearchTreatsTheTextAsText(Backend b)
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string founder = UUID.Random().ToString();
        UUID listed = b.Service.CreateGroup(founder, "Searchable " + tag, "", true, UUID.Zero, 0, true, true, false, new UUID(founder), out string reason);
        Assert.True(listed.IsNotZero(), reason);
        UUID hidden = b.Service.CreateGroup(founder, "Hidden " + tag, "", false, UUID.Zero, 0, true, true, false, new UUID(founder), out reason);
        Assert.True(hidden.IsNotZero(), reason);

        Assert.Contains(b.Service.FindGroups(founder, "SEARCHABLE " + tag), g => g.groupID == listed);
        Assert.DoesNotContain(b.Service.FindGroups(founder, "Hidden " + tag), g => g.groupID == hidden);
        List<DirGroupsReplyData> all = b.Service.FindGroups(founder, string.Empty);
        Assert.Contains(all, g => g.groupID == listed);
        Assert.DoesNotContain(all, g => g.groupID == hidden);
        Assert.Empty(b.Service.FindGroups(founder, "' OR '1'='1"));
        Assert.Empty(b.Service.FindGroups(founder, tag + "' OR \"ShowInList\" = 0 OR '1'='1"));
    }

    [Fact]
    public void SQLiteSearchTreatsTheTextAsText() => SearchTreatsTheTextAsText(SQLite());

    [MySQLServerFact]
    public void MySQLSearchTreatsTheTextAsText() => SearchTreatsTheTextAsText(MySQL());

    [PGSQLServerFact]
    public void PGSQLSearchTreatsTheTextAsText() => SearchTreatsTheTextAsText(PGSQL());

    /// <summary>
    /// Bans (GroupAPIv1): only someone with "Manage ban list" sees or changes them (owners have it, officers do not by
    /// default); an owner and oneself are never banned; a banned avatar cannot join, cannot be invited, and loses a
    /// pending invitation; lifting the ban lets them join.
    /// </summary>
    private static void BansAreKeptAndEnforced(Backend b)
    {
        GroupsService s = b.Service;
        string tag = Guid.NewGuid().ToString("N")[..8];
        string owner = UUID.Random().ToString();
        UUID group = s.CreateGroup(owner, "Bans " + tag, "", true, UUID.Zero, 0, true, true, false, new UUID(owner), out string reason);
        Assert.True(group.IsNotZero(), reason);
        UUID officers = s.GetGroupRoles(owner, group).Single(r => r.Name == "Officers").RoleID;
        string officer = UUID.Random().ToString(), member = UUID.Random().ToString();
        string stranger = UUID.Random().ToString(), invited = UUID.Random().ToString();
        Assert.True(s.AddAgentToGroup(owner, officer, group, officers, string.Empty, out reason), reason);
        Assert.True(s.AddAgentToGroup(member, member, group, UUID.Zero, string.Empty, out reason), reason);
        UUID invite = UUID.Random();
        Assert.True(s.AddAgentToGroupInvite(owner, invite, group, UUID.Zero, invited));

        // nobody but someone who may manage bans sees or changes them
        Assert.Empty(s.GetGroupBans(owner, group));
        Assert.Null(s.GetGroupBans(officer, group));
        Assert.Null(s.GetGroupBans(member, group));
        Assert.False(s.BanAgents(officer, group, new[] { stranger }, out _));
        Assert.False(s.BanAgents(member, group, new[] { stranger }, out _));

        // the owner bans: not themself, not an owner
        Assert.False(s.BanAgents(owner, group, new[] { owner }, out _));
        Assert.True(s.BanAgents(owner, group, new[] { stranger, invited, member }, out reason), reason);
        Assert.Equal(new[] { invited, member, stranger }.OrderBy(x => x),
            s.GetGroupBans(owner, group).Select(x => x.PrincipalID).OrderBy(x => x));
        Assert.All(s.GetGroupBans(owner, group), x => Assert.True(int.Parse(x.Data["BanTime"]) > 0));

        // banned: no joining an open group, no invitation, the pending one withdrawn
        Assert.False(s.AddAgentToGroup(stranger, stranger, group, UUID.Zero, string.Empty, out reason));
        Assert.Contains("banned", reason);
        Assert.False(s.AddAgentToGroupInvite(owner, UUID.Random(), group, UUID.Zero, stranger));
        Assert.Null(s.GetAgentToGroupInvite(invited, invite));
        Assert.False(s.AddAgentToGroup(owner, stranger, group, UUID.Zero, string.Empty, out _));   // nor added by the owner
        // a banned member stays one until ejected (the viewer ejects them itself)
        Assert.NotNull(s.GetAgentGroupMembership(member, member, group));

        // lifted: they may join again
        Assert.False(s.UnbanAgents(member, group, new[] { stranger }));
        Assert.True(s.UnbanAgents(owner, group, new[] { stranger }));
        Assert.DoesNotContain(s.GetGroupBans(owner, group), x => x.PrincipalID == stranger);
        Assert.True(s.AddAgentToGroup(stranger, stranger, group, UUID.Zero, string.Empty, out reason), reason);
    }

    [Fact]
    public void SQLiteKeepsAndEnforcesBans() => BansAreKeptAndEnforced(SQLite());

    [MySQLServerFact]
    public void MySQLKeepsAndEnforcesBans() => BansAreKeptAndEnforced(MySQL());

    [PGSQLServerFact]
    public void PGSQLKeepsAndEnforcesBans() => BansAreKeptAndEnforced(PGSQL());

    #region the Flotsam import
    /// <summary>The Flotsam XmlRpcGroups server's tables (its groups.sql).</summary>
    private const string FlotsamSchema = """
        CREATE TABLE osagent (AgentID varchar(128) NOT NULL default '', ActiveGroupID varchar(128) NOT NULL default '',
          PRIMARY KEY (AgentID));
        CREATE TABLE osgroup (GroupID varchar(128) NOT NULL default '', Name varchar(255) NOT NULL default '',
          Charter text NOT NULL, InsigniaID varchar(128) NOT NULL default '', FounderID varchar(128) NOT NULL default '',
          MembershipFee int(11) NOT NULL default '0', OpenEnrollment varchar(255) NOT NULL default '',
          ShowInList tinyint(1) NOT NULL default '0', AllowPublish tinyint(1) NOT NULL default '0',
          MaturePublish tinyint(1) NOT NULL default '0', OwnerRoleID varchar(128) NOT NULL default '',
          PRIMARY KEY (GroupID), UNIQUE KEY Name (Name));
        CREATE TABLE osgroupinvite (InviteID varchar(128) NOT NULL default '', GroupID varchar(128) NOT NULL default '',
          RoleID varchar(128) NOT NULL default '', AgentID varchar(128) NOT NULL default '',
          TMStamp timestamp NOT NULL default CURRENT_TIMESTAMP, PRIMARY KEY (InviteID));
        CREATE TABLE osgroupmembership (GroupID varchar(128) NOT NULL default '', AgentID varchar(128) NOT NULL default '',
          SelectedRoleID varchar(128) NOT NULL default '', Contribution int(11) NOT NULL default '0',
          ListInProfile int(11) NOT NULL default '1', AcceptNotices int(11) NOT NULL default '1',
          PRIMARY KEY (GroupID, AgentID));
        CREATE TABLE osgroupnotice (GroupID varchar(128) NOT NULL default '', NoticeID varchar(128) NOT NULL default '',
          Timestamp int(10) unsigned NOT NULL default '0', FromName varchar(255) NOT NULL default '',
          Subject varchar(255) NOT NULL default '', Message text NOT NULL, BinaryBucket text NOT NULL,
          PRIMARY KEY (GroupID, NoticeID));
        CREATE TABLE osgrouprolemembership (GroupID varchar(128) NOT NULL default '', RoleID varchar(128) NOT NULL default '',
          AgentID varchar(128) NOT NULL default '', PRIMARY KEY (GroupID, RoleID, AgentID));
        CREATE TABLE osrole (GroupID varchar(128) NOT NULL default '', RoleID varchar(128) NOT NULL default '',
          Name varchar(255) NOT NULL default '', Description varchar(255) NOT NULL default '',
          Title varchar(255) NOT NULL default '', Powers bigint(20) unsigned NOT NULL default '0',
          PRIMARY KEY (GroupID, RoleID));
        """;

    /// <summary>A notice's bucket as XmlRpcGroups keeps it, in hex.</summary>
    private static string Bucket(UUID group, UUID owner, UUID item, string name, byte type)
    {
        byte[] n = System.Text.Encoding.UTF8.GetBytes(name);
        byte[] b = new byte[51 + n.Length];
        b[0] = 1; b[1] = type;
        group.ToBytes(b, 2); owner.ToBytes(b, 18); item.ToBytes(b, 34);
        n.CopyTo(b, 50);
        return Utils.BytesToHexString(b, "");
    }

    [Fact]
    public void AFlotsamNoticeBucketIsRead()
    {
        UUID g = UUID.Random(), owner = UUID.Random(), item = UUID.Random();
        var a = V2Import.AttachmentOf(Bucket(g, owner, item, "A landmark", 3));
        Assert.True(a.Has);
        Assert.Equal((byte)3, a.Type);
        Assert.Equal(owner, a.Owner);
        Assert.Equal(item, a.Item);
        Assert.Equal("A landmark", a.Name);
        Assert.False(V2Import.AttachmentOf("00000000").Has);
        Assert.False(V2Import.AttachmentOf("").Has);
        Assert.False(V2Import.AttachmentOf("not hex").Has);
    }

    /// <summary>A scratch database on the MySQL server with the Flotsam tables, filled by <paramref name="fill"/>.</summary>
    private string FlotsamDatabase(Action<Action<string>> fill)
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "flotsam_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ConnectionString;
        Admin(() => new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() => Admin(() => new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`"));
        Admin(() => new MySqlConnection(conn), FlotsamSchema);
        fill(sql => Admin(() => new MySqlConnection(conn), sql));
        return conn;
    }

    /// <summary>
    /// Two groups to copy (one without an Everyone role, one with a member missing from Everyone), a group whose name
    /// V2 already has, and rows of a group that does not exist: the dry run counts, the import copies, a second run
    /// copies nothing more.
    /// </summary>
    private void FlotsamGroupsAreCopied(Backend b)
    {
        GroupsService s = b.Service;
        string tag = Guid.NewGuid().ToString("N")[..8];
        UUID a = UUID.Random(), bare = UUID.Random(), clash = UUID.Random(), gone = UUID.Random();
        UUID officers = UUID.Random(), owners = UUID.Random();
        string founder = UUID.Random().ToString(), member = UUID.Random().ToString(), officer = UUID.Random().ToString();
        string invitee = UUID.Random().ToString();
        UUID invite = UUID.Random(), withItem = UUID.Random(), plain = UUID.Random(), item = UUID.Random();

        // a V2 group already has the name of the third
        string v2owner = UUID.Random().ToString();
        Assert.True(s.CreateGroup(v2owner, "Taken " + tag, "", true, UUID.Zero, 0, true, true, false, new UUID(v2owner), out string reason).IsNotZero(), reason);

        string conn = FlotsamDatabase(sql =>
        {
            sql($"INSERT INTO osgroup VALUES ('{a}', 'Flotsam A {tag}', 'Our charter', '{UUID.Zero}', '{founder}', 5, '1', 1, 1, 0, '{owners}')");
            sql($"INSERT INTO osgroup VALUES ('{bare}', 'Flotsam B {tag}', '', '{UUID.Zero}', '{founder}', 0, '0', 0, 0, 0, '{UUID.Zero}')");
            sql($"INSERT INTO osgroup VALUES ('{clash}', 'Taken {tag}', '', '{UUID.Zero}', '{founder}', 0, '0', 1, 0, 0, '{UUID.Zero}')");
            sql($"INSERT INTO osrole VALUES ('{a}', '{UUID.Zero}', 'Everyone', 'All', 'Member', {(ulong)GroupsService.DefaultEveryonePowers})");
            sql($"INSERT INTO osrole VALUES ('{a}', '{officers}', 'Officers', 'Run it', 'Officer', {(ulong)GroupsService.OfficersPowers})");
            sql($"INSERT INTO osrole VALUES ('{a}', '{owners}', 'Owners', 'Own it', 'Owner', {(ulong)GroupsService.OwnerPowers})");
            sql($"INSERT INTO osgroupmembership VALUES ('{a}', '{founder}', '{owners}', 0, 1, 1)");
            sql($"INSERT INTO osgroupmembership VALUES ('{a}', '{member}', '{UUID.Zero}', 0, 1, 0)");
            sql($"INSERT INTO osgroupmembership VALUES ('{a}', '{officer}', '{officers}', 0, 0, 1)");
            sql($"INSERT INTO osgroupmembership VALUES ('{bare}', '{founder}', '{UUID.Zero}', 0, 1, 1)");
            sql($"INSERT INTO osgroupmembership VALUES ('{gone}', '{member}', '{UUID.Zero}', 0, 1, 1)");
            sql($"INSERT INTO osgrouprolemembership VALUES ('{a}', '{UUID.Zero}', '{founder}')");
            sql($"INSERT INTO osgrouprolemembership VALUES ('{a}', '{owners}', '{founder}')");
            sql($"INSERT INTO osgrouprolemembership VALUES ('{a}', '{UUID.Zero}', '{member}')");
            sql($"INSERT INTO osgrouprolemembership VALUES ('{a}', '{officers}', '{officer}')");   // not in Everyone
            sql($"INSERT INTO osgroupinvite VALUES ('{invite}', '{a}', '{UUID.Zero}', '{invitee}', CURRENT_TIMESTAMP)");
            sql($"INSERT INTO osgroupnotice VALUES ('{a}', '{withItem}', {Util.UnixTimeSinceEpoch()}, 'Founder', 'With item', 'Look', '{Bucket(a, new UUID(founder), item, "A landmark", 3)}')");
            sql($"INSERT INTO osgroupnotice VALUES ('{a}', '{plain}', {Util.UnixTimeSinceEpoch()}, 'Founder', 'Plain', 'Hello', '0000')");
            sql($"INSERT INTO osagent VALUES ('{founder}', '{a}')");
        });

        V2Import.Report dry = new V2Import(() => new MySqlConnection(conn), b.Groups).Run(dryRun: true);
        Assert.Equal(2, dry.Groups);
        Assert.Equal(1, dry.NameTaken);
        Assert.Equal(1, dry.EveryoneRolesAdded);
        Assert.Null(s.GetGroupRecord(founder, a));   // nothing written

        V2Import.Report report = new V2Import(() => new MySqlConnection(conn), b.Groups).Run(dryRun: false);
        m_out.WriteLine($"{b.Name}: {report}");
        Assert.Equal(2, report.Groups);
        Assert.Equal(4, report.Members);
        Assert.Equal(1, report.Invites);
        Assert.Equal(2, report.Notices);
        Assert.Equal(1, report.Principals);
        Assert.True(report.RowsWithoutGroup >= 1);

        // the group, as V2 serves it
        ExtendedGroupRecord rec = s.GetGroupRecord(founder, a);
        Assert.Equal("Flotsam A " + tag, rec.GroupName);
        Assert.Equal("Our charter", rec.Charter);
        Assert.Equal(5, rec.MembershipFee);
        Assert.True(rec.OpenEnrollment);
        Assert.True(rec.ShowInList);
        Assert.Equal(owners, rec.OwnerRoleID);
        Assert.Null(s.GetGroupRecord(founder, clash));   // its name was taken

        List<ExtendedGroupMembersData> members = s.GetGroupMembers(founder, a);
        Assert.Equal(3, members.Count);
        Assert.True(members.Single(m => m.AgentID.ToString() == founder).IsOwner);
        Assert.False(members.Single(m => m.AgentID.ToString() == member).AcceptNotices);
        Assert.Equal("Officer", members.Single(m => m.AgentID.ToString() == officer).Title);
        // the officer was put in Everyone, which every V2 member holds
        Assert.Contains(s.GetAgentGroupRoles(founder, officer, a), r => r.RoleID.IsZero());
        // the founder, an owner, has every power; the group they were active in is active
        Assert.Equal(GroupsService.AllPowers, s.GetAgentGroupMembership(founder, founder, a).GroupPowers);
        Assert.Equal(a, s.GetAgentActiveMembership(founder, founder).GroupID);
        // the group without an Everyone role has one
        Assert.Contains(s.GetGroupRoles(founder, bare), r => r.RoleID.IsZero());

        Assert.NotNull(s.GetAgentToGroupInvite(invitee, invite));
        GroupNoticeInfo n = s.GetGroupNotice(founder, withItem);
        Assert.True(n.noticeData.HasAttachment);
        Assert.Equal("A landmark", n.noticeData.AttachmentName);
        Assert.Equal(item, n.noticeData.AttachmentItemID);
        Assert.False(s.GetGroupNotice(founder, plain).noticeData.HasAttachment);

        // again: nothing more
        V2Import.Report again = new V2Import(() => new MySqlConnection(conn), b.Groups).Run(dryRun: false);
        Assert.Equal(0, again.Groups);
        Assert.Equal(2, again.GroupsAlreadyThere);
    }

    [MySQLServerFact]
    public void FlotsamGroupsAreCopiedIntoSQLite() => FlotsamGroupsAreCopied(SQLite());

    [MySQLServerFact]
    public void FlotsamGroupsAreCopiedIntoMySQL() => FlotsamGroupsAreCopied(MySQL());

    /// <summary>Runs only with both OPENSIM_TEST_MYSQL (the Flotsam tables) and OPENSIM_TEST_PGSQL set.</summary>
    public sealed class MySQLAndPGSQLServerFactAttribute : FactAttribute
    {
        public MySQLAndPGSQLServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar)))
                Skip = "Requires a MySQL server (the Flotsam tables) and a PostgreSQL server; set OPENSIM_TEST_MYSQL and OPENSIM_TEST_PGSQL to run.";
        }
    }

    [MySQLAndPGSQLServerFact]
    public void FlotsamGroupsAreCopiedIntoPostgreSQL() => FlotsamGroupsAreCopied(PGSQL());
    #endregion

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
