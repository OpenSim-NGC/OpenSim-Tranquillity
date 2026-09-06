/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
 * CONTRIBUTORS BE LIABLE FOR ANY DAMAGES ARISING IN ANY WAY OUT OF THE USE OF
 * THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

/// <summary>
/// Trust-registry persistence round-trips for both shipping backends.
///
/// SQLite runs in-process against a throwaway temp database, so it always executes.
/// MySQL runs only when a connection string is supplied via the
/// <c>TRUSTED_HG_MYSQL_CONN</c> environment variable and the server is reachable;
/// otherwise the MySQL cases dynamically skip.
/// </summary>
public class TrustedGridDataTests : IDisposable
{
    private readonly List<IDisposable> m_disposables = new();
    private readonly List<string> m_tempFiles = new();

    static TrustedGridDataTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public void Dispose()
    {
        foreach (IDisposable d in m_disposables)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
        m_disposables.Clear();

        foreach (string f in m_tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
        m_tempFiles.Clear();
    }

    // ---- backend factories -------------------------------------------------

    private ITrustedGridData NewSqlite()
    {
        string file = Path.GetTempFileName() + ".db";
        m_tempFiles.Add(file);
        var store = new SQLiteTrustedGridData("URI=file:" + file + ",version=3");
        m_disposables.Add(store);
        return store;
    }

    private ITrustedGridData NewMySql()
    {
        string conn = Environment.GetEnvironmentVariable("TRUSTED_HG_MYSQL_CONN");
        if (string.IsNullOrWhiteSpace(conn))
            throw new InvalidOperationException("TRUSTED_HG_MYSQL_CONN is not set.");
        return new MySQLTrustedGridData(conn);
    }

    // ---- SQLite (always) ---------------------------------------------------

    [Fact]
    public void SQLite_InsertLookupUpdate_RoundTrips() => RunRoundTrip(NewSqlite());

    [Fact]
    public void SQLite_DifferentKey_IsFlaggedNotOverwritten() => RunKeyChange(NewSqlite());

    [Fact]
    public void SQLite_Alias_RoundTrips() => RunAlias(NewSqlite());

    // ---- MySQL (gated) -----------------------------------------------------
    // Gated at discovery time by [MySqlFact]: these EXECUTE when TRUSTED_HG_MYSQL_CONN points
    // at a scratch database and are skipped (never silently passed) when it is unset. A static
    // skip cannot fire against a live database by accident; neither can this, because the
    // connection string is only ever taken from the environment.

    [MySqlFact]
    public void MySql_InsertLookupUpdate_RoundTrips() => RunRoundTrip(NewMySql());

    [MySqlFact]
    public void MySql_DifferentKey_IsFlaggedNotOverwritten() => RunKeyChange(NewMySql());

    [MySqlFact]
    public void MySql_Alias_RoundTrips() => RunAlias(NewMySql());

    [MySqlFact]
    public void MySql_ListDeleteAliases_RoundTrip() => RunListDeleteAliases(NewMySql());

    [Fact]
    public void SQLite_ListDeleteAliases_RoundTrip() => RunListDeleteAliases(NewSqlite());

    private static void RunListDeleteAliases(ITrustedGridData store)
    {
        string uri = "http://list-delete-" + Guid.NewGuid().ToString("N") + ".example:8002/";
        TrustedGridData rec = store.RecordPresentedKey(uri, NewKey(0x51), new string('5', 64), DateTime.UtcNow);
        Assert.NotNull(rec);
        Assert.True(store.StoreAlias(rec.Id, "HTTP://ALIAS-" + Guid.NewGuid().ToString("N") + ".example:8002"));

        TrustedGridData[] all = store.GetAll();
        Assert.Contains(all, g => g.Id == rec.Id);
        string[] aliases = store.GetAliases(rec.Id);
        Assert.Single(aliases);
        Assert.StartsWith("http://alias-", aliases[0]);   // normalised by the shared normaliser

        Assert.True(store.Delete(rec.Id));
        Assert.Null(store.Get(rec.Id));
        Assert.Empty(store.GetAliases(rec.Id));
        Assert.DoesNotContain(store.GetAll(), g => g.Id == rec.Id);
        Assert.False(store.Delete(rec.Id));
    }

    // ---- shared bodies -----------------------------------------------------

    private static void RunRoundTrip(ITrustedGridData store)
    {
        string host = "Grid-" + Guid.NewGuid().ToString("N") + ".Example";
        string rawUri = $"http://{host}:80/";                      // deliberately un-normalised on write
        string altSpelling = $"http://{host.ToLowerInvariant()}/"; // different spelling on lookup
        string expected = $"http://{host.ToLowerInvariant()}:80/";

        byte[] key = NewKey(0x11);
        var rec = new TrustedGridData
        {
            Id = UUID.Random(),
            HomeUri = rawUri,
            PublicKey = key,
            KeyFingerprint = "fp-" + host.ToLowerInvariant(),
            Tier = (int)TrustTier.Open,
            State = (int)TrustState.Pending,
            FirstSeen = new DateTime(2026, 8, 20, 12, 0, 0),
            LastSeen = new DateTime(2026, 8, 20, 12, 0, 0),
            ApprovedBy = string.Empty,
            ApprovedAt = null,
            Notes = "first contact",
        };

        Assert.True(store.Store(rec), "insert should succeed");

        // Lookup via a different spelling proves the single shared normaliser is
        // applied on both write and lookup.
        TrustedGridData byHome = store.GetByHomeUri(altSpelling);
        Assert.NotNull(byHome);
        Assert.Equal(rec.Id, byHome.Id);
        Assert.Equal(expected, byHome.HomeUri);       // URI stored in normalised form
        Assert.Equal(key, byHome.PublicKey);
        Assert.Equal("first contact", byHome.Notes);
        Assert.Null(byHome.ApprovedAt);
        Assert.Equal((int)TrustTier.Open, byHome.Tier);

        // Update: promote to Trusted/approved.
        rec.Tier = (int)TrustTier.Trusted;
        rec.State = (int)TrustState.Approved;
        rec.ApprovedBy = "operator";
        rec.ApprovedAt = new DateTime(2026, 8, 20, 13, 30, 0);
        rec.Notes = "approved after OOB agreement";
        Assert.True(store.Store(rec), "update should succeed");

        TrustedGridData after = store.Get(rec.Id);
        Assert.NotNull(after);
        Assert.Equal((int)TrustTier.Trusted, after.Tier);
        Assert.Equal((int)TrustState.Approved, after.State);
        Assert.Equal("operator", after.ApprovedBy);
        Assert.NotNull(after.ApprovedAt);
        Assert.Equal("approved after OOB agreement", after.Notes);

        // Fingerprint lookup resolves the same row.
        TrustedGridData byFp = store.GetByFingerprint("fp-" + host.ToLowerInvariant());
        Assert.NotNull(byFp);
        Assert.Equal(rec.Id, byFp.Id);
    }

    private static void RunKeyChange(ITrustedGridData store)
    {
        string host = "grid-" + Guid.NewGuid().ToString("N") + ".example";
        string uri = $"http://{host}/";
        byte[] keyA = NewKey(0xAA);
        byte[] keyB = NewKey(0xBB);
        var t0 = new DateTime(2026, 8, 20, 9, 0, 0);

        // First contact: adopt keyA, tier Open, state Pending.
        TrustedGridData r1 = store.RecordPresentedKey(uri, keyA, "fpA", t0);
        Assert.NotNull(r1);
        Assert.Equal((int)TrustTier.Open, r1.Tier);
        Assert.Equal((int)TrustState.Pending, r1.State);
        Assert.Equal(keyA, r1.PublicKey);

        // Same key again (different URI spelling): still the same row, still Pending.
        TrustedGridData r1b = store.RecordPresentedKey($"http://{host.ToUpperInvariant()}:80/", keyA, "fpA", t0.AddMinutes(5));
        Assert.Equal(r1.Id, r1b.Id);
        Assert.Equal((int)TrustState.Pending, r1b.State);

        // Different key on the established relationship: state -> key-changed-pending-reapproval,
        // and the stored key is preserved (NOT silently overwritten).
        TrustedGridData r2 = store.RecordPresentedKey(uri, keyB, "fpB", t0.AddMinutes(10));
        Assert.Equal(r1.Id, r2.Id);
        Assert.Equal((int)TrustState.KeyChangedPendingReapproval, r2.State);
        Assert.Equal(2, r2.State);
        Assert.Equal(keyA, r2.PublicKey); // existing key preserved, not overwritten
    }

    private static void RunAlias(ITrustedGridData store)
    {
        string host = "grid-" + Guid.NewGuid().ToString("N") + ".example";
        TrustedGridData grid = store.RecordPresentedKey($"http://{host}/", NewKey(0x22), "fp", new DateTime(2026, 8, 20, 10, 0, 0));

        string aliasHost = "alias-" + Guid.NewGuid().ToString("N") + ".example";
        Assert.True(store.StoreAlias(grid.Id, $"http://{aliasHost}:80/"));

        TrustedGridData viaAlias = store.GetByAlias($"http://{aliasHost.ToUpperInvariant()}/");
        Assert.NotNull(viaAlias);
        Assert.Equal(grid.Id, viaAlias.Id);
    }

    private static byte[] NewKey(byte fill)
    {
        var k = new byte[32];
        for (int i = 0; i < k.Length; i++)
            k[i] = fill;
        return k;
    }
}
