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

using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Groups;

/// <summary>
/// A one-time copy of a grid's groups from the Flotsam XmlRpcGroups server's tables (osgroup, osrole,
/// osgroupmembership, osgrouprolemembership, osgroupinvite, osgroupnotice, osagent) into Groups V2's store, so a grid
/// moving off XmlRpcGroups keeps its groups. Columns are read by name, as text. Nothing in V2 is overwritten: a group
/// already there (by id) is left as it is, and a group whose name another V2 group has is not copied; both are
/// counted. Running it again copies only what is new. A dry run counts without writing.
/// </summary>
public sealed class FlotsamGroupsImport
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>What an import did (or, dry, would do).</summary>
    public sealed class Report
    {
        public int Groups, Roles, Members, RoleMembers, Invites, Notices, Principals;
        public int GroupsAlreadyThere, NameTaken, RowsWithoutGroup, EveryoneRolesAdded;
        public readonly List<string> Skipped = new();

        public override string ToString() =>
            $"{Groups} groups, {Roles} roles, {Members} memberships, {RoleMembers} role memberships, {Invites} invitations, " +
            $"{Notices} notices, {Principals} active groups; {GroupsAlreadyThere} groups already in V2, {NameTaken} with a " +
            $"name taken in V2, {RowsWithoutGroup} rows of groups not copied, {EveryoneRolesAdded} Everyone roles added";
    }

    private readonly Func<DbConnection> m_source;
    private readonly IGroupsData m_target;

    /// <param name="source">Opens a connection to the database holding the Flotsam tables.</param>
    /// <param name="target">Groups V2's store.</param>
    public FlotsamGroupsImport(Func<DbConnection> source, IGroupsData target)
    {
        m_source = source;
        m_target = target;
    }

    private static List<Dictionary<string, string>> Rows(DbConnection c, string table)
    {
        var rows = new List<Dictionary<string, string>>();
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM " + table;
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }

    private static string Get(Dictionary<string, string> row, string column, string otherwise = "")
        => row.TryGetValue(column, out string v) && v != null ? v : otherwise;

    private static string Flag(Dictionary<string, string> row, string column, string otherwise = "0")
    {
        string v = Get(row, column, otherwise).Trim();
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
    }

    private static bool TryId(string s, out UUID id) => UUID.TryParse(s?.Trim(), out id);

    /// <summary>Copy the groups (or, with <paramref name="dryRun"/>, only count what would be copied).</summary>
    public Report Run(bool dryRun)
    {
        var report = new Report();
        using DbConnection c = m_source();
        if (c.State != ConnectionState.Open)
            c.Open();

        // groups: those V2 has (by id, or by name) are left as they are
        var copied = new HashSet<UUID>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(c, "osgroup"))
        {
            if (!TryId(Get(row, "GroupID"), out UUID id) || id.IsZero())
            {
                report.Skipped.Add("a group with no id");
                continue;
            }
            string name = Get(row, "Name").Trim();
            if (m_target.RetrieveGroup(id) != null)
            {
                report.GroupsAlreadyThere++;
                continue;
            }
            if (name.Length == 0 || !names.Add(name) || m_target.RetrieveGroup(name) != null)
            {
                report.NameTaken++;
                report.Skipped.Add($"group {id}: the name \"{name}\" is taken in V2 or empty");
                continue;
            }

            copied.Add(id);
            report.Groups++;
            if (!dryRun)
                m_target.StoreGroup(new GroupData
                {
                    GroupID = id,
                    Data = new Dictionary<string, string>
                    {
                        ["Location"] = string.Empty,
                        ["Name"] = name,
                        ["Charter"] = Get(row, "Charter"),
                        ["InsigniaID"] = TryId(Get(row, "InsigniaID"), out UUID ins) ? ins.ToString() : UUID.Zero.ToString(),
                        ["FounderID"] = TryId(Get(row, "FounderID"), out UUID f) ? f.ToString() : UUID.Zero.ToString(),
                        ["MembershipFee"] = int.TryParse(Get(row, "MembershipFee"), out int fee) ? fee.ToString() : "0",
                        ["OpenEnrollment"] = Flag(row, "OpenEnrollment"),
                        ["ShowInList"] = Flag(row, "ShowInList"),
                        ["AllowPublish"] = Flag(row, "AllowPublish"),
                        ["MaturePublish"] = Flag(row, "MaturePublish"),
                        ["OwnerRoleID"] = TryId(Get(row, "OwnerRoleID"), out UUID o) ? o.ToString() : UUID.Zero.ToString(),
                    },
                });
        }

        // roles, and an Everyone role for a group without one
        var everyone = new HashSet<UUID>();
        foreach (var row in Rows(c, "osrole"))
        {
            if (!TryId(Get(row, "GroupID"), out UUID g) || !copied.Contains(g) || !TryId(Get(row, "RoleID"), out UUID role))
            {
                report.RowsWithoutGroup++;
                continue;
            }
            if (role.IsZero())
                everyone.Add(g);
            report.Roles++;
            if (!dryRun)
                m_target.StoreRole(new RoleData
                {
                    GroupID = g,
                    RoleID = role,
                    Data = new Dictionary<string, string>
                    {
                        ["Name"] = Get(row, "Name"),
                        ["Description"] = Get(row, "Description"),
                        ["Title"] = Get(row, "Title"),
                        ["Powers"] = ulong.TryParse(Get(row, "Powers"), out ulong p) ? p.ToString() : "0",
                    },
                });
        }
        foreach (UUID g in copied.Where(g => !everyone.Contains(g)))
        {
            report.EveryoneRolesAdded++;
            if (!dryRun)
                m_target.StoreRole(new RoleData
                {
                    GroupID = g,
                    RoleID = UUID.Zero,
                    Data = new Dictionary<string, string>
                    {
                        ["Name"] = "Everyone", ["Description"] = "Everyone in the group is in the everyone role.",
                        ["Title"] = "Member", ["Powers"] = ((ulong)GroupsService.DefaultEveryonePowers).ToString(),
                    },
                });
        }

        // memberships, each with a new access token, and each member in the Everyone role
        var members = new HashSet<(UUID, string)>();
        foreach (var row in Rows(c, "osgroupmembership"))
        {
            string agent = Get(row, "AgentID").Trim();
            if (!TryId(Get(row, "GroupID"), out UUID g) || !copied.Contains(g) || agent.Length == 0)
            {
                report.RowsWithoutGroup++;
                continue;
            }
            members.Add((g, agent.ToLowerInvariant()));
            report.Members++;
            if (!dryRun)
                m_target.StoreMember(new MembershipData
                {
                    GroupID = g,
                    PrincipalID = agent,
                    Data = new Dictionary<string, string>
                    {
                        ["SelectedRoleID"] = TryId(Get(row, "SelectedRoleID"), out UUID sel) ? sel.ToString() : UUID.Zero.ToString(),
                        ["Contribution"] = int.TryParse(Get(row, "Contribution"), out int con) ? con.ToString() : "0",
                        ["ListInProfile"] = Flag(row, "ListInProfile", "1"),
                        ["AcceptNotices"] = Flag(row, "AcceptNotices", "1"),
                        ["AccessToken"] = UUID.Random().ToString(),
                    },
                });
        }

        var inEveryone = new HashSet<(UUID, string)>();
        foreach (var row in Rows(c, "osgrouprolemembership"))
        {
            string agent = Get(row, "AgentID").Trim();
            if (!TryId(Get(row, "GroupID"), out UUID g) || !TryId(Get(row, "RoleID"), out UUID role)
                || !members.Contains((g, agent.ToLowerInvariant())))
            {
                report.RowsWithoutGroup++;
                continue;
            }
            if (role.IsZero())
                inEveryone.Add((g, agent.ToLowerInvariant()));
            report.RoleMembers++;
            if (!dryRun)
                m_target.StoreRoleMember(new RoleMembershipData { GroupID = g, RoleID = role, PrincipalID = agent });
        }
        foreach (var (g, agent) in members.Where(m => !inEveryone.Contains(m)))
        {
            report.RoleMembers++;
            if (!dryRun)
                m_target.StoreRoleMember(new RoleMembershipData { GroupID = g, RoleID = UUID.Zero, PrincipalID = agent });
        }

        // invitations still pending
        foreach (var row in Rows(c, "osgroupinvite"))
        {
            string agent = Get(row, "AgentID").Trim();
            if (!TryId(Get(row, "GroupID"), out UUID g) || !copied.Contains(g) || !TryId(Get(row, "InviteID"), out UUID invite)
                || agent.Length == 0)
            {
                report.RowsWithoutGroup++;
                continue;
            }
            report.Invites++;
            if (!dryRun)
                m_target.StoreInvitation(new InvitationData
                {
                    InviteID = invite,
                    GroupID = g,
                    RoleID = TryId(Get(row, "RoleID"), out UUID role) ? role : UUID.Zero,
                    PrincipalID = agent,
                    Data = new Dictionary<string, string>(),
                });
        }

        // notices, with what their binary bucket says of an attachment
        foreach (var row in Rows(c, "osgroupnotice"))
        {
            if (!TryId(Get(row, "GroupID"), out UUID g) || !copied.Contains(g) || !TryId(Get(row, "NoticeID"), out UUID notice))
            {
                report.RowsWithoutGroup++;
                continue;
            }
            Attachment a = AttachmentOf(Get(row, "BinaryBucket"));
            report.Notices++;
            if (!dryRun)
                m_target.StoreNotice(new NoticeData
                {
                    GroupID = g,
                    NoticeID = notice,
                    Data = new Dictionary<string, string>
                    {
                        ["TMStamp"] = uint.TryParse(Get(row, "Timestamp"), out uint t) ? t.ToString() : "0",
                        ["FromName"] = Get(row, "FromName"),
                        ["Subject"] = Get(row, "Subject"),
                        ["Message"] = Get(row, "Message"),
                        ["HasAttachment"] = a.Has ? "1" : "0",
                        ["AttachmentType"] = a.Type.ToString(),
                        ["AttachmentName"] = a.Name,
                        ["AttachmentItemID"] = a.Item.ToString(),
                        ["AttachmentOwnerID"] = a.Owner.ToString(),
                    },
                });
        }

        // the active group of each avatar, for a group copied
        foreach (var row in Rows(c, "osagent"))
        {
            string agent = Get(row, "AgentID").Trim();
            if (agent.Length == 0 || !TryId(Get(row, "ActiveGroupID"), out UUID active) || (active.IsNotZero() && !copied.Contains(active)))
                continue;
            if (m_target.RetrievePrincipal(agent) != null)
                continue;   // V2 already knows their active group
            report.Principals++;
            if (!dryRun)
                m_target.StorePrincipal(new PrincipalData { PrincipalID = agent, ActiveGroupID = active });
        }

        m_log.LogInformation("[Groups]: Flotsam import{0}: {1}", dryRun ? " (dry run)" : string.Empty, report);
        return report;
    }

    /// <summary>A notice's attachment as XmlRpcGroups keeps it: the bucket, in hex.</summary>
    public readonly record struct Attachment(bool Has, byte Type, UUID Owner, UUID Item, string Name);

    /// <summary>
    /// Read XmlRpcGroups' notice bucket (GroupsModule.OnInstantMessage): 0 has an attachment, 1 its type, 2 the group,
    /// 18 the owner, 34 the item, 50 the name (NUL-terminated); kept as hex.
    /// </summary>
    public static Attachment AttachmentOf(string hex)
    {
        byte[] b;
        try
        {
            b = string.IsNullOrWhiteSpace(hex) ? [] : Utils.HexStringToBytes(hex.Trim(), true);
        }
        catch
        {
            return new Attachment(false, 0, UUID.Zero, UUID.Zero, string.Empty);
        }
        if (b.Length < 51 || b[0] == 0)
            return new Attachment(false, 0, UUID.Zero, UUID.Zero, string.Empty);
        int end = Array.IndexOf(b, (byte)0, 50);
        string name = System.Text.Encoding.UTF8.GetString(b, 50, (end < 0 ? b.Length : end) - 50);
        return new Attachment(true, b[1], new UUID(b, 18), new UUID(b, 34), name);
    }
}
