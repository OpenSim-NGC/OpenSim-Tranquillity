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

using System;
using System.Data;
using System.Reflection;
using MySqlConnector;
using OpenMetaverse;
using OpenSim.Framework.TrustedHypergrid;

namespace OpenSim.Data.MySQL;

/// <summary>
/// MySQL persistence for the Trusted Hypergrid trust registry (Design Brief §4).
/// </summary>
public class MySQLTrustedGridData : MySqlFramework, ITrustedGridData
{
    private const string Realm = "hg_trusted_grids";
    private const string AliasRealm = "hg_grid_aliases";

    protected virtual Assembly Assembly
    {
        get { return GetType().Assembly; }
    }

    public MySQLTrustedGridData(string connectionString) : base(connectionString)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            Migration m = new Migration(dbcon, Assembly, "TrustedGrid");
            m.Update();
            dbcon.Close();
        }
    }

    public TrustedGridData Get(UUID id)
    {
        return QuerySingle(
            $"select * from `{Realm}` where id = ?id",
            cmd => cmd.Parameters.AddWithValue("?id", id.ToString()));
    }

    public TrustedGridData GetByHomeUri(string homeUri)
    {
        string norm = HGUriNormalizer.Normalize(homeUri);
        return QuerySingle(
            $"select * from `{Realm}` where home_uri = ?home_uri",
            cmd => cmd.Parameters.AddWithValue("?home_uri", norm));
    }

    public TrustedGridData GetByFingerprint(string keyFingerprint)
    {
        return QuerySingle(
            $"select * from `{Realm}` where key_fingerprint = ?fp",
            cmd => cmd.Parameters.AddWithValue("?fp", keyFingerprint ?? string.Empty));
    }

    public TrustedGridData GetByAlias(string aliasUri)
    {
        string norm = HGUriNormalizer.Normalize(aliasUri);
        return QuerySingle(
            $"select g.* from `{Realm}` g join `{AliasRealm}` a on a.grid_id = g.id where a.alias_uri = ?alias",
            cmd => cmd.Parameters.AddWithValue("?alias", norm));
    }

    public bool Store(TrustedGridData data)
    {
        data.HomeUri = HGUriNormalizer.Normalize(data.HomeUri);

        using (MySqlCommand cmd = new MySqlCommand())
        {
            cmd.CommandText =
                $"insert into `{Realm}` " +
                "(id, home_uri, public_key, key_fingerprint, tier, state, first_seen, last_seen, approved_by, approved_at, notes) " +
                "values (?id, ?home_uri, ?public_key, ?key_fingerprint, ?tier, ?state, ?first_seen, ?last_seen, ?approved_by, ?approved_at, ?notes) " +
                "on duplicate key update " +
                "home_uri=?home_uri, public_key=?public_key, key_fingerprint=?key_fingerprint, tier=?tier, state=?state, " +
                "first_seen=?first_seen, last_seen=?last_seen, approved_by=?approved_by, approved_at=?approved_at, notes=?notes";

            BindRow(cmd, data);
            return ExecuteNonQuery(cmd) > 0;
        }
    }

    public bool StoreAlias(UUID gridId, string aliasUri)
    {
        string norm = HGUriNormalizer.Normalize(aliasUri);
        using (MySqlCommand cmd = new MySqlCommand())
        {
            cmd.CommandText =
                $"insert into `{AliasRealm}` (grid_id, alias_uri) values (?grid_id, ?alias_uri) " +
                "on duplicate key update grid_id=?grid_id";
            cmd.Parameters.AddWithValue("?grid_id", gridId.ToString());
            cmd.Parameters.AddWithValue("?alias_uri", norm);
            return ExecuteNonQuery(cmd) > 0;
        }
    }

    public TrustedGridData[] GetAll()
    {
        List<TrustedGridData> ret = new List<TrustedGridData>();
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand($"select * from `{Realm}` order by home_uri", dbcon))
            using (IDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    ret.Add(TrustedGridData.FromReader(reader));
            }
            dbcon.Close();
        }
        return ret.ToArray();
    }

    public string[] GetAliases(UUID gridId)
    {
        List<string> ret = new List<string>();
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand($"select alias_uri from `{AliasRealm}` where grid_id = ?grid_id order by alias_uri", dbcon))
            {
                cmd.Parameters.AddWithValue("?grid_id", gridId.ToString());
                using (IDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        ret.Add(reader["alias_uri"].ToString());
                }
            }
            dbcon.Close();
        }
        return ret.ToArray();
    }

    public bool Delete(UUID id)
    {
        using (MySqlCommand aliases = new MySqlCommand())
        {
            aliases.CommandText = $"delete from `{AliasRealm}` where grid_id = ?grid_id";
            aliases.Parameters.AddWithValue("?grid_id", id.ToString());
            ExecuteNonQuery(aliases);
        }
        using (MySqlCommand cmd = new MySqlCommand())
        {
            cmd.CommandText = $"delete from `{Realm}` where id = ?id";
            cmd.Parameters.AddWithValue("?id", id.ToString());
            return ExecuteNonQuery(cmd) > 0;
        }
    }

    private static void BindRow(MySqlCommand cmd, TrustedGridData d)
    {
        cmd.Parameters.AddWithValue("?id", d.Id.ToString());
        cmd.Parameters.AddWithValue("?home_uri", d.HomeUri);
        cmd.Parameters.AddWithValue("?public_key", (object)d.PublicKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("?key_fingerprint", d.KeyFingerprint ?? string.Empty);
        cmd.Parameters.AddWithValue("?tier", d.Tier);
        cmd.Parameters.AddWithValue("?state", d.State);
        cmd.Parameters.AddWithValue("?first_seen", TrustedGridData.ToDbUtc(d.FirstSeen));
        cmd.Parameters.AddWithValue("?last_seen", TrustedGridData.ToDbUtc(d.LastSeen));
        cmd.Parameters.AddWithValue("?approved_by", d.ApprovedBy ?? string.Empty);
        cmd.Parameters.AddWithValue("?approved_at", d.ApprovedAt.HasValue ? TrustedGridData.ToDbUtc(d.ApprovedAt.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("?notes", (object)d.Notes ?? DBNull.Value);
    }

    private TrustedGridData QuerySingle(string sql, Action<MySqlCommand> bind)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
            {
                bind(cmd);
                using (IDataReader reader = cmd.ExecuteReader())
                {
                    TrustedGridData ret = reader.Read() ? TrustedGridData.FromReader(reader) : null;
                    dbcon.Close();
                    return ret;
                }
            }
        }
    }
}
