/*
 * Copyright (c) Legion Builds
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
using System.Collections.Generic;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The SQLite estate store keeps an estate's three Experience lists (EstateSettings.AllowedExperiences,
/// KeyExperiences, BlockedExperiences) the way the MySQL store does: one table per list, rows of (EstateID, uuid),
/// so the lists are still there after the region closes the store and opens it again on its next start.
///
/// SQLite runs against a throwaway in-memory database per test (shared-cache, kept alive by an anchor connection so
/// that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// </summary>
public class SQLiteEstateExperienceListsTests : OpenSimTestCase
{
    private static readonly UUID s_region = new UUID("8c0f6a3e-2d7b-4e19-b5a4-61f3c9d2e087");
    private static readonly UUID s_allowedA = new UUID("1f6b2c9d-4e3a-4b7f-8d21-5a9c0e7b3f64");
    private static readonly UUID s_allowedB = new UUID("7a3d9e1c-6b2f-4c85-9e07-2d4f8a1b6c53");
    private static readonly UUID s_key = new UUID("c4e81b7a-0f3d-4a96-b2c5-8e1d7f3a9b20");
    private static readonly UUID s_blocked = new UUID("5d9a2f4e-8c1b-4f73-a6e0-3b7c9d2e1f48");

    private readonly List<IDisposable> m_disposables = new();

    static SQLiteEstateExperienceListsTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public override void Dispose()
    {
        for (int i = m_disposables.Count - 1; i >= 0; i--)
        {
            try { m_disposables[i].Dispose(); } catch { /* best effort */ }
        }
        m_disposables.Clear();
        base.Dispose();
    }

    private string NewMemoryDatabase()
    {
        string conn = "FullUri=file:estatexp_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_disposables.Add(anchor);
        return conn;
    }

    private SQLiteEstateStore OpenStore(string conn)
    {
        SQLiteEstateStore store = new SQLiteEstateStore(conn);
        m_disposables.Add(ConnectionOf(store));
        return store;
    }

    // The store has no close of its own: its connection lives as long as the region process does.
    private static SQLiteConnection ConnectionOf(SQLiteEstateStore store)
    {
        FieldInfo field = typeof(SQLiteEstateStore).GetField("m_connection", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (SQLiteConnection)field.GetValue(store);
    }

    private static void Close(SQLiteEstateStore store)
    {
        ConnectionOf(store).Close();
    }

    // Writes the estate the way the region does: change the settings, then EstateSettings.Save (OnSave -> store).
    private static EstateSettings NewEstateWithLists(SQLiteEstateStore store, UUID region, UUID[] allowed, UUID[] key, UUID[] blocked)
    {
        EstateSettings es = store.LoadEstateSettings(region, true);
        es.EstateName = "Example Estate";
        es.AllowedExperiences = allowed;
        es.KeyExperiences = key;
        es.BlockedExperiences = blocked;
        es.Save();
        return es;
    }

    [Fact]
    public void AllowedExperiencesSurviveClosingAndReopeningTheStore()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore first = OpenStore(conn);
        NewEstateWithLists(first, s_region, new[] { s_allowedA, s_allowedB }, Array.Empty<UUID>(), Array.Empty<UUID>());
        Close(first);

        EstateSettings reloaded = OpenStore(conn).LoadEstateSettings(s_region, false);

        Assert.Equal(new HashSet<UUID> { s_allowedA, s_allowedB }, new HashSet<UUID>(reloaded.AllowedExperiences));
    }

    [Fact]
    public void KeyExperiencesSurviveClosingAndReopeningTheStore()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore first = OpenStore(conn);
        NewEstateWithLists(first, s_region, Array.Empty<UUID>(), new[] { s_key }, Array.Empty<UUID>());
        Close(first);

        EstateSettings reloaded = OpenStore(conn).LoadEstateSettings(s_region, false);

        Assert.Equal(new[] { s_key }, reloaded.KeyExperiences);
    }

    [Fact]
    public void BlockedExperiencesSurviveClosingAndReopeningTheStore()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore first = OpenStore(conn);
        NewEstateWithLists(first, s_region, Array.Empty<UUID>(), Array.Empty<UUID>(), new[] { s_blocked });
        Close(first);

        EstateSettings reloaded = OpenStore(conn).LoadEstateSettings(s_region, false);

        Assert.Equal(new[] { s_blocked }, reloaded.BlockedExperiences);
    }

    [Fact]
    public void EachListKeepsOnlyItsOwnExperiencesAndOnlyForItsOwnEstate()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore first = OpenStore(conn);
        EstateSettings es = NewEstateWithLists(first, s_region, new[] { s_allowedA }, new[] { s_key }, new[] { s_blocked });
        UUID otherRegion = new UUID("2b7e4d1a-9c3f-4e68-a0d5-7f1c3b9e2a46");
        EstateSettings other = NewEstateWithLists(first, otherRegion, new[] { s_allowedB }, Array.Empty<UUID>(), Array.Empty<UUID>());
        Assert.NotEqual(es.EstateID, other.EstateID);
        Close(first);

        SQLiteEstateStore second = OpenStore(conn);
        EstateSettings reloaded = second.LoadEstateSettings((int)es.EstateID);
        Assert.Equal(new[] { s_allowedA }, reloaded.AllowedExperiences);
        Assert.Equal(new[] { s_key }, reloaded.KeyExperiences);
        Assert.Equal(new[] { s_blocked }, reloaded.BlockedExperiences);

        EstateSettings otherReloaded = second.LoadEstateSettings((int)other.EstateID);
        Assert.Equal(new[] { s_allowedB }, otherReloaded.AllowedExperiences);
        Assert.Empty(otherReloaded.KeyExperiences);
        Assert.Empty(otherReloaded.BlockedExperiences);
    }

    [Fact]
    public void AnExperienceTakenOffAListStaysOffAfterReopening()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore first = OpenStore(conn);
        EstateSettings es = NewEstateWithLists(first, s_region, new[] { s_allowedA, s_allowedB }, new[] { s_key }, new[] { s_blocked });
        es.RemoveAllowedExperience(s_allowedA);
        es.RemoveKeyExperience(s_key);
        es.RemoveBlockedExperience(s_blocked);
        es.Save();
        Close(first);

        EstateSettings reloaded = OpenStore(conn).LoadEstateSettings(s_region, false);

        Assert.Equal(new[] { s_allowedB }, reloaded.AllowedExperiences);
        Assert.Empty(reloaded.KeyExperiences);
        Assert.Empty(reloaded.BlockedExperiences);
    }

    [Fact]
    public void AnEstateWrittenBeforeTheListTablesExistedLoadsWithEmptyListsAfterTheMigration()
    {
        string conn = NewMemoryDatabase();
        UUID manager = new UUID("9e2c7a4b-1d6f-4b38-8c05-4a7e1f9d3b62");
        UUID resident = new UUID("3c8f1e6d-7a2b-4d94-b1e3-0f5a9c2d7e81");

        SQLiteEstateStore first = OpenStore(conn);
        EstateSettings es = first.LoadEstateSettings(s_region, true);
        es.EstateName = "Example Estate";
        es.EstateManagers = new[] { manager };
        es.EstateAccess = new[] { resident };
        es.Save();
        Close(first);

        // Put the database back to the schema before this change (EstateStore 12: no Experience list tables), with
        // the estate rows the older code wrote.
        using (SQLiteConnection c = new SQLiteConnection(conn))
        {
            c.Open();
            using SQLiteCommand cmd = c.CreateCommand();
            cmd.CommandText =
                "DROP TABLE IF EXISTS estate_allowed_experiences; " +
                "DROP TABLE IF EXISTS estate_key_experiences; " +
                "DROP TABLE IF EXISTS estate_blocked_experiences; " +
                "UPDATE migrations SET version = 12 WHERE name = 'EstateStore';";
            cmd.ExecuteNonQuery();
        }

        SQLiteEstateStore second = OpenStore(conn);
        EstateSettings reloaded = second.LoadEstateSettings(s_region, false);

        Assert.Equal(13, new Migration(ConnectionOf(second), typeof(SQLiteEstateStore).Assembly, "EstateStore").Version);
        Assert.Equal(es.EstateID, reloaded.EstateID);
        Assert.Equal("Example Estate", reloaded.EstateName);
        Assert.Equal(new[] { manager }, reloaded.EstateManagers);
        Assert.Equal(new[] { resident }, reloaded.EstateAccess);
        Assert.Empty(reloaded.AllowedExperiences);
        Assert.Empty(reloaded.KeyExperiences);
        Assert.Empty(reloaded.BlockedExperiences);

        // The new tables are usable at once: a list saved after the migration comes back.
        reloaded.AllowedExperiences = new[] { s_allowedA };
        reloaded.Save();
        Close(second);
        Assert.Equal(new[] { s_allowedA }, OpenStore(conn).LoadEstateSettings(s_region, false).AllowedExperiences);
    }
}
