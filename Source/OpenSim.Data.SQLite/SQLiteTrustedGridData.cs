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
using OpenMetaverse;
using OpenSim.Framework.TrustedHypergrid;
using System.Data.SQLite;

namespace OpenSim.Data.SQLite;

/// <summary>
/// SQLite persistence for the Trusted Hypergrid trust registry (Design Brief §4).
/// </summary>
public class SQLiteTrustedGridData : SQLiteFramework, ITrustedGridData, IDisposable
{
    private const string Realm = "hg_trusted_grids";
    private const string AliasRealm = "hg_grid_aliases";

    private readonly SQLiteConnection m_Connection;

    protected virtual Assembly Assembly
    {
        get { return GetType().Assembly; }
    }

    public SQLiteTrustedGridData(string connectionString) : base(connectionString)
    {
        m_Connection = new SQLiteConnection(connectionString);
        m_Connection.Open();

        Migration m = new Migration(m_Connection, Assembly, "TrustedGrid");
        m.Update();
    }

    public TrustedGridData Get(UUID id)
    {
        return QuerySingle(
            $"select * from {Realm} where id = :id",
            cmd => cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString())));
    }

    public TrustedGridData GetByHomeUri(string homeUri)
    {
        string norm = HGUriNormalizer.Normalize(homeUri);
        return QuerySingle(
            $"select * from {Realm} where home_uri = :home_uri",
            cmd => cmd.Parameters.Add(new SQLiteParameter(":home_uri", norm)));
    }

    public TrustedGridData GetByFingerprint(string keyFingerprint)
    {
        return QuerySingle(
            $"select * from {Realm} where key_fingerprint = :fp",
            cmd => cmd.Parameters.Add(new SQLiteParameter(":fp", keyFingerprint ?? string.Empty)));
    }

    public TrustedGridData GetByAlias(string aliasUri)
    {
        string norm = HGUriNormalizer.Normalize(aliasUri);
        return QuerySingle(
            $"select g.* from {Realm} g join {AliasRealm} a on a.grid_id = g.id where a.alias_uri = :alias",
            cmd => cmd.Parameters.Add(new SQLiteParameter(":alias", norm)));
    }

    public bool Store(TrustedGridData data)
    {
        data.HomeUri = HGUriNormalizer.Normalize(data.HomeUri);

        bool exists;
        using (SQLiteCommand check = new SQLiteCommand($"select 1 from {Realm} where id = :id"))
        {
            check.Parameters.Add(new SQLiteParameter(":id", data.Id.ToString()));
            using (IDataReader r = ExecuteReader(check, m_Connection))
                exists = r.Read();
        }

        using (SQLiteCommand cmd = new SQLiteCommand())
        {
            if (exists)
            {
                cmd.CommandText =
                    $"update {Realm} set home_uri=:home_uri, public_key=:public_key, key_fingerprint=:key_fingerprint, " +
                    "tier=:tier, state=:state, first_seen=:first_seen, last_seen=:last_seen, " +
                    "approved_by=:approved_by, approved_at=:approved_at, notes=:notes where id=:id";
            }
            else
            {
                cmd.CommandText =
                    $"insert into {Realm} " +
                    "(id, home_uri, public_key, key_fingerprint, tier, state, first_seen, last_seen, approved_by, approved_at, notes) " +
                    "values (:id, :home_uri, :public_key, :key_fingerprint, :tier, :state, :first_seen, :last_seen, :approved_by, :approved_at, :notes)";
            }

            BindRow(cmd, data);
            return ExecuteNonQuery(cmd, m_Connection) > 0;
        }
    }

    public bool StoreAlias(UUID gridId, string aliasUri)
    {
        string norm = HGUriNormalizer.Normalize(aliasUri);
        using (SQLiteCommand cmd = new SQLiteCommand(
            $"insert or replace into {AliasRealm} (grid_id, alias_uri) values (:grid_id, :alias_uri)"))
        {
            cmd.Parameters.Add(new SQLiteParameter(":grid_id", gridId.ToString()));
            cmd.Parameters.Add(new SQLiteParameter(":alias_uri", norm));
            return ExecuteNonQuery(cmd, m_Connection) > 0;
        }
    }

    private static void BindRow(SQLiteCommand cmd, TrustedGridData d)
    {
        cmd.Parameters.Add(new SQLiteParameter(":id", d.Id.ToString()));
        cmd.Parameters.Add(new SQLiteParameter(":home_uri", d.HomeUri));
        cmd.Parameters.Add(new SQLiteParameter(":public_key", (object)d.PublicKey ?? DBNull.Value));
        cmd.Parameters.Add(new SQLiteParameter(":key_fingerprint", d.KeyFingerprint ?? string.Empty));
        cmd.Parameters.Add(new SQLiteParameter(":tier", d.Tier));
        cmd.Parameters.Add(new SQLiteParameter(":state", d.State));
        cmd.Parameters.Add(new SQLiteParameter(":first_seen", d.FirstSeen));
        cmd.Parameters.Add(new SQLiteParameter(":last_seen", d.LastSeen));
        cmd.Parameters.Add(new SQLiteParameter(":approved_by", d.ApprovedBy ?? string.Empty));
        cmd.Parameters.Add(new SQLiteParameter(":approved_at", (object)d.ApprovedAt ?? DBNull.Value));
        cmd.Parameters.Add(new SQLiteParameter(":notes", (object)d.Notes ?? DBNull.Value));
    }

    private TrustedGridData QuerySingle(string sql, Action<SQLiteCommand> bind)
    {
        using (SQLiteCommand cmd = new SQLiteCommand(sql))
        {
            bind(cmd);
            using (IDataReader reader = ExecuteReader(cmd, m_Connection))
                return reader.Read() ? TrustedGridData.FromReader(reader) : null;
        }
    }

    public void Dispose()
    {
        m_Connection?.Close();
        m_Connection?.Dispose();
        GC.SuppressFinalize(this);
    }
}
