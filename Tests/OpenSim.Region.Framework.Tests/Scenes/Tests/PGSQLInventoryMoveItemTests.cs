/*
 * Copyright (c) Legion Builds
 * All rights reserved.
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
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Moves an inventory item to another folder through the PostgreSQL inventory store and reads it back through a
/// second store on the same database. The inventoryitems columns are created with quoted mixed-case names
/// ("inventoryID", "parentFolderID"), so a statement must quote them: PostgreSQL folds an unquoted name to lower
/// case, and no column inventoryid exists.
///
/// The test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped when
/// it ends. No process-wide state: it drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class PGSQLInventoryMoveItemTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_MoveItem_PutsTheItemInTheNewFolder()
    {
        string conn = NewDatabase();
        UUID agent = UUID.Random();
        XInventoryFolder from = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = UUID.Zero, folderName = "From", type = -1, version = 1 };
        XInventoryFolder to = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = UUID.Zero, folderName = "To", type = -1, version = 1 };
        XInventoryItem item = new XInventoryItem
        {
            inventoryID = UUID.Random(), avatarID = agent, parentFolderID = from.folderID, assetID = UUID.Random(),
            assetType = 7, invType = 7, inventoryName = "Moved note", inventoryDescription = "",
            creatorID = agent.ToString(), creationDate = 1700002000
        };

        PGSQLXInventoryData writer = new PGSQLXInventoryData(conn, string.Empty);
        Assert.True(writer.StoreFolder(from));
        Assert.True(writer.StoreFolder(to));
        Assert.True(writer.StoreItem(item));

        Assert.True(writer.MoveItem(item.inventoryID.ToString(), to.folderID.ToString()));

        PGSQLXInventoryData reader = new PGSQLXInventoryData(conn, string.Empty);
        XInventoryItem loaded = Assert.Single(reader.GetItems(new[] { "inventoryID" }, new[] { item.inventoryID.ToString() }));
        Assert.Equal(to.folderID, loaded.parentFolderID);
        Assert.Equal("Moved note", loaded.inventoryName);
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "invmove_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static void Admin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
