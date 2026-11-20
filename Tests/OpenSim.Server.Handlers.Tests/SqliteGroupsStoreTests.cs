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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Groups;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Groups V2 on a SQLite standalone: the real GroupsService over SQLiteGroupsData. A SQLite store keeps one
/// connection per table type for the process, so every test here shares one database, with groups of its own.
/// </summary>
[Collection("SQLite stores")]
public class SqliteGroupsStoreTests
{
    private static readonly string s_dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR")
        is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;
    private static readonly string s_run = Guid.NewGuid().ToString("N");
    private static string ConnectionFor(string store) =>
        "Data Source=" + Path.Combine(s_dir, "groups-" + store + "-" + s_run + ".db") + ";Version=3";

    private static readonly System.Lazy<GroupsService> s_service = new(() =>
    {
        var config = new IniConfigSource();
        IConfig groups = config.AddConfig("Groups");
        groups.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        groups.Set("ConnectionString", ConnectionFor("store"));
        IConfig users = config.AddConfig("GridUserService");
        users.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        users.Set("ConnectionString", ConnectionFor("griduser"));
        return new GroupsService(config, "Groups");
    });

    private static readonly System.Lazy<SQLiteGroupsData> s_store = new(() =>
    {
        _ = s_service.Value;   // the service's store runs the migration on this database
        return new SQLiteGroupsData(ConnectionFor("store"), "os_groups");
    });

    internal static GroupsService Service => s_service.Value;

    private static (UUID Group, string Founder) NewGroup(string name, bool open = false, bool show = true)
    {
        string founder = UUID.Random().ToString();
        UUID group = Service.CreateGroup(founder, name, "charter", show, UUID.Zero, 0, open, true, false,
            new UUID(founder), out string reason);
        Assert.True(group.IsNotZero(), reason);
        return (group, founder);
    }

    [Fact]
    public void AGroupIsCreatedWithItsFounderAsOwner()
    {
        var (group, founder) = NewGroup("Sqlite founders " + s_run[..8]);

        ExtendedGroupRecord record = Service.GetGroupRecord(founder, group);
        Assert.Equal(1, record.MemberCount);
        Assert.Equal(3, record.RoleCount);     // Everyone, Officers and Owners, as SL makes them
        Assert.Equal(founder, record.FounderID.ToString());

        ExtendedGroupMembershipData m = Service.GetAgentGroupMembership(founder, founder, group);
        Assert.NotNull(m);
        Assert.Equal(record.OwnerRoleID, m.ActiveRole.IsZero() ? record.OwnerRoleID : m.ActiveRole);
        Assert.True((m.GroupPowers & (ulong)GroupPowers.Eject) != 0);

        // a second group of the same name is refused
        Assert.Equal(UUID.Zero, Service.CreateGroup(founder, "Sqlite founders " + s_run[..8], "", true, UUID.Zero, 0,
            false, true, false, new UUID(founder), out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void AnOpenGroupIsJoinedAndListsItsMembers()
    {
        var (group, founder) = NewGroup("Sqlite open " + s_run[..8], open: true);
        string joiner = UUID.Random().ToString();

        Assert.True(Service.AddAgentToGroup(joiner, joiner, group, UUID.Zero, string.Empty, out string reason), reason);

        List<ExtendedGroupMembersData> members = Service.GetGroupMembers(founder, group);
        Assert.Equal(2, members.Count);
        Assert.True(members.Single(x => x.AgentID == founder).IsOwner);
        Assert.False(members.Single(x => x.AgentID == joiner).IsOwner);
        Assert.Equal(2, Service.GetGroupRecord(founder, group).MemberCount);
        Assert.Contains(Service.GetAgentGroupMemberships(joiner, joiner), g => g.GroupID == group);
    }

    [Fact]
    public void SearchMatchesPartOfTheNameAndTreatsTheTextAsText()
    {
        string tag = s_run[..8];
        var (listed, founder) = NewGroup("Searchable 100% " + tag);
        NewGroup("Hidden from search " + tag, show: false);

        Assert.Contains(Service.FindGroups(founder, "SEARCHABLE 100% " + tag), g => g.groupID == listed);
        Assert.DoesNotContain(Service.FindGroups(founder, "Hidden from search " + tag), g => g.groupName.Contains(tag));
        // the wildcards and the quote are matched as characters: "%" alone is not "everything"
        Assert.DoesNotContain(Service.FindGroups(founder, "Searchable _00"), g => g.groupID == listed);
        Assert.Empty(Service.FindGroups(founder, "' OR '1'='1"));
    }

    [Fact]
    public void CountsAndRoleMembershipsAreRead()
    {
        var (group, founder) = NewGroup("Sqlite roles " + s_run[..8]);
        UUID role = UUID.Random();
        Assert.True(Service.AddGroupRole(founder, group, role, "Builders", "", "Builder", (ulong)GroupPowers.AllowRez, out string reason), reason);

        Assert.Equal(4, s_store.Value.RoleCount(group));   // Everyone, Officers, Owners, Builders
        Assert.Equal(1, s_store.Value.RoleMemberCount(group, UUID.Zero));   // the founder, in Everyone
        Assert.Equal(0, s_store.Value.RoleMemberCount(group, role));
        Assert.Single(s_store.Value.RetrieveMemberRoles(group, founder), r => r.RoleID == UUID.Zero);
    }

    [Fact]
    public void OldNoticesAndInvitesAreDeletedAndRecentOnesKept()
    {
        var (group, _) = NewGroup("Sqlite notices " + s_run[..8]);
        NoticeData Notice(uint stamp) => new()
        {
            GroupID = group,
            NoticeID = UUID.Random(),
            Data = new Dictionary<string, string>
            {
                ["TMStamp"] = stamp.ToString(), ["FromName"] = "Test", ["Subject"] = "s", ["Message"] = "m",
                ["HasAttachment"] = "0", ["AttachmentType"] = "0", ["AttachmentName"] = "",
                ["AttachmentItemID"] = UUID.Zero.ToString(), ["AttachmentOwnerID"] = "",
            },
        };
        uint now = (uint)Util.UnixTimeSinceEpoch();
        NoticeData old = Notice(now - 15 * 24 * 3600), fresh = Notice(now);
        Assert.True(s_store.Value.StoreNotice(old));
        Assert.True(s_store.Value.StoreNotice(fresh));

        var invite = new InvitationData { InviteID = UUID.Random(), GroupID = group, RoleID = UUID.Zero,
            PrincipalID = UUID.Random().ToString(), Data = new Dictionary<string, string>() };
        Assert.True(s_store.Value.StoreInvitation(invite));

        s_store.Value.DeleteOldNotices();
        s_store.Value.DeleteOldInvites();

        Assert.Null(s_store.Value.RetrieveNotice(old.NoticeID));
        Assert.NotNull(s_store.Value.RetrieveNotice(fresh.NoticeID));
        Assert.NotNull(s_store.Value.RetrieveInvitation(invite.InviteID));   // stamped now by the column default
    }
}
