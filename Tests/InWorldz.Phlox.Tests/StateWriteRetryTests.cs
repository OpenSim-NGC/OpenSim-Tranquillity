/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Data.SQLite;
using System.IO;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A state database write that did not commit is not counted as done: it stays queued, in order, and is tried again; a
/// load of its item sees it. A database locked by another connection only delays a save or a delete.
/// </summary>
// Runs in parallel: each test has its own state database file and its own managers; nothing process-wide is changed.
public class StateWriteRetryTests
{
    /// <summary>A locked database is given up on after this, statement retries included, so a test waits briefly.</summary>
    private const int ShortBusyMs = 200;

    private static InWorldz.Phlox.VM.Interpreter Script(InWorldz.Phlox.VM.CompiledScript compiled)
    {
        var api = RecordingSystemApi.Create(out _);
        var shim = new InWorldz.Phlox.Glue.SyscallShim(call => call());
        shim.SystemAPI = api;
        var interp = new InWorldz.Phlox.VM.Interpreter(compiled, shim) { ItemId = UUID.Random() };
        shim.Interpreter = interp;
        return interp;
    }

    private static InWorldz.Phlox.VM.CompiledScript Compiled()
    {
        var compiled = PhloxCompiler.CompileTo("integer g = 7; default { state_entry() { g = 8; } }", out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        compiled.AssetId = UUID.Random();
        return compiled;
    }

    private static string NewDb()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "state-write-retry");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    }

    private static bool HasRow(string db, UUID item)
    {
        using var c = new SQLiteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM script_state WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", item.ToString());
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>Another connection holds the database's write lock until disposed.</summary>
    private static SQLiteConnection Lock(string db)
    {
        var c = new SQLiteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "BEGIN EXCLUSIVE";
        cmd.ExecuteNonQuery();
        return c;
    }

    [Fact]
    public void ASaveWhileTheDatabaseIsLockedLandsOnceTheLockGoes()
    {
        string db = NewDb();
        var compiled = Compiled();
        using var m = new StateManager(null, db, ShortBusyMs);
        var s = Script(compiled);
        using (Lock(db))
        {
            m.ScriptUnloaded(s);
            Assert.False(HasRow(db, s.ItemId));
        }
        Assert.Equal(0, m.WritesDone);   // not counted as done
        Assert.True(m.WaitForWrites());
        Assert.True(HasRow(db, s.ItemId), "the save was dropped");
        Assert.Equal(1, m.WritesDone);
    }

    [Fact]
    public void ADeleteWhileTheDatabaseIsLockedIsNotForgotten()
    {
        string db = NewDb();
        var compiled = Compiled();
        using var m = new StateManager(null, db, ShortBusyMs);
        var s = Script(compiled);
        m.ScriptUnloaded(s);
        Assert.True(HasRow(db, s.ItemId));
        using (Lock(db))
        {
            m.DeleteState(s.ItemId);
            Assert.False(m.WaitForWrites());
            Assert.True(HasRow(db, s.ItemId));
        }
        Assert.True(m.WaitForWrites());
        Assert.False(HasRow(db, s.ItemId), "the delete was dropped");
    }

    [Fact]
    public void ABatchThatDoesNotCommitAppliesNoneOfItsWritesAndRetriesThemAll()
    {
        string db = NewDb();
        var compiled = Compiled();
        using var m = new StateManager(null, db, ShortBusyMs);
        var a = Script(compiled);
        var b = Script(compiled);
        bool fail = true;
        m.FailWriteForTest = id => fail && id == b.ItemId;
        m.QueueUnloadSave(a);
        m.QueueUnloadSave(b);
        Assert.False(m.WaitForWrites());
        Assert.False(HasRow(db, a.ItemId));   // rolled back with the write that failed
        Assert.False(HasRow(db, b.ItemId));
        Assert.Equal(0, m.WritesDone);

        fail = false;
        Assert.True(m.WaitForWrites());
        Assert.True(HasRow(db, a.ItemId));
        Assert.True(HasRow(db, b.ItemId));
        Assert.Equal(2, m.WritesDone);
        Assert.Equal(0, m.WritesAbandoned);
    }

    [Fact]
    public void AWriteThatKeepsFailingIsGivenUpAtTheBoundAndTheOthersStillLand()
    {
        string db = NewDb();
        var compiled = Compiled();
        using var m = new StateManager(null, db, ShortBusyMs);
        var bad = Script(compiled);
        var good = Script(compiled);
        m.FailWriteForTest = id => id == bad.ItemId;
        m.QueueUnloadSave(bad);
        m.QueueUnloadSave(good);
        for (int i = 1; i < StateManager.MaxWriteAttempts; i++)
        {
            Assert.False(m.WaitForWrites());
            Assert.False(HasRow(db, good.ItemId));
        }
        Assert.True(m.WaitForWrites());   // the last attempt gives the failing write up, and the rest is written
        Assert.False(HasRow(db, bad.ItemId));
        Assert.True(HasRow(db, good.ItemId));
        Assert.Equal(1, m.WritesDone);
        Assert.Equal(1, m.WritesAbandoned);
    }

    [Fact]
    public void ShutdownTriesEveryWriteToTheBoundAndReturns()
    {
        string db = NewDb();
        var compiled = Compiled();
        var m = new StateManager(null, db, ShortBusyMs);
        var bad = Script(compiled);
        var good = Script(compiled);
        m.FailWriteForTest = id => id == bad.ItemId;
        m.QueueUnloadSave(bad);
        m.QueueUnloadSave(good);
        m.Stop();
        m.Dispose();
        Assert.True(HasRow(db, good.ItemId));
        Assert.False(HasRow(db, bad.ItemId));
        Assert.Equal(1, m.WritesAbandoned);
    }

    /// <summary>A load of an item whose save the database has not taken yet restores that save, not the older row.</summary>
    [Fact]
    public void ALoadSeesASaveTheDatabaseHasNotTakenYet()
    {
        string db = NewDb();
        var compiled = Compiled();
        using var m = new StateManager(null, db, ShortBusyMs);
        var s = Script(compiled);
        m.ScriptUnloaded(s);   // the older row
        s.ScriptState.Globals[0] = 99;
        m.FailWriteForTest = id => id == s.ItemId;
        m.QueueUnloadSave(s);
        var back = m.LoadState(s.ItemId, compiled.AssetId);
        Assert.NotNull(back);
        Assert.Equal(99, back.ToRuntimeState().Globals[0]);

        m.DeleteState(s.ItemId);
        Assert.Null(m.LoadState(s.ItemId, compiled.AssetId));   // a removal not yet written leaves no state
        m.FailWriteForTest = null;
        Assert.True(m.WaitForWrites());
        Assert.False(HasRow(db, s.ItemId));
    }
}
