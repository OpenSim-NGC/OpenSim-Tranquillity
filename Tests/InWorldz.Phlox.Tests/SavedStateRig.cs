/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Helpers for the saved-state, listen and loader tests: a rez with every OnRezScript argument, the state database's
/// rows, and members reached by name, so a test that names one the engine does not have fails with that name.
/// </summary>
internal static class SavedStateRig
{
    /// <summary>The state database every harness engine shares (StateManager's DB_FILE, relative to the test run).</summary>
    public const string DbFile = "ScriptEngines/Phlox/state/script_state.db";

    public static PhloxExecutionScheduler Exe(SchedulerHarness h)
        => (PhloxExecutionScheduler)Field(h.Engine, "m_ExeScheduler");

    public static StateManager States(SchedulerHarness h) => (StateManager)h.StateManagerOf();

    /// <summary>Put a script in the part's inventory and rez it as EventManager.OnRezScript does, with every argument given.</summary>
    public static UUID Rez(SchedulerHarness h, SceneObjectPart part, string source, UUID assetId, UUID itemId,
                           int startParam, bool postOnRez, int stateSource)
    {
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, part, itemId, assetId, "s" + itemId.ToString().Substring(0, 8), source);
        PostRez(h, part, itemId, source, startParam, postOnRez, stateSource);
        return itemId;
    }

    public static void PostRez(SchedulerHarness h, SceneObjectPart part, UUID itemId, string source,
                               int startParam, bool postOnRez, int stateSource)
    {
        var rez = h.Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rez);
        rez!.Invoke(h.Engine, new object[] { part.LocalId, itemId, source, startParam, postOnRez, h.Engine.Name, stateSource });
    }

    /// <summary>The region's OnRemoveScript, as the core raises it.</summary>
    public static void PostRemove(SchedulerHarness h, SceneObjectPart part, UUID itemId)
    {
        var rm = h.Engine.GetType().GetMethod("OnRemoveScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rm);
        rm!.Invoke(h.Engine, new object[] { part.LocalId, itemId });
    }

    /// <summary>The item's row, or null.</summary>
    public static (byte[] Blob, long SavedAt)? Row(UUID itemId)
    {
        using var conn = new SQLiteConnection("Data Source=" + DbFile);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state_data, saved_at FROM script_state WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ((byte[])r[0], r.GetInt64(1));
    }

    /// <summary>Rows moved aside for this item (0 when the table does not exist).</summary>
    public static long RejectedRows(UUID itemId)
    {
        using var conn = new SQLiteConnection("Data Source=" + DbFile);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'script_state_rejected'";
        if ((long)cmd.ExecuteScalar() == 0) return 0;
        cmd.CommandText = "SELECT count(*) FROM script_state_rejected WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        return (long)cmd.ExecuteScalar();
    }

    /// <summary>Write a row directly, as an earlier build or a damaged file left it.</summary>
    public static void PutRow(UUID itemId, UUID assetId, byte[] blob, long savedAt)
    {
        using var conn = new SQLiteConnection("Data Source=" + DbFile + ";BusyTimeout=5000");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"CREATE TABLE IF NOT EXISTS script_state (item_id TEXT PRIMARY KEY, asset_id TEXT NOT NULL DEFAULT '', state_data BLOB NOT NULL, saved_at INTEGER NOT NULL);
              INSERT INTO script_state (item_id, asset_id, state_data, saved_at) VALUES (@id, @asset, @data, @ts)
              ON CONFLICT(item_id) DO UPDATE SET asset_id = excluded.asset_id, state_data = excluded.state_data, saved_at = excluded.saved_at";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        cmd.Parameters.AddWithValue("@asset", assetId.ToString());
        cmd.Parameters.AddWithValue("@data", blob);
        cmd.Parameters.AddWithValue("@ts", savedAt);
        cmd.ExecuteNonQuery();
    }

    public static void SetSavedAt(UUID itemId, long savedAt)
    {
        using var conn = new SQLiteConnection("Data Source=" + DbFile + ";BusyTimeout=5000");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE script_state SET saved_at = @ts WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        cmd.Parameters.AddWithValue("@ts", savedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every write the engine's state manager has queued is done (nothing to wait for where writes are synchronous).</summary>
    public static void WaitForWrites(SchedulerHarness h)
    {
        var sm = h.StateManagerOf();
        sm?.GetType().GetMethod("WaitForWrites", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.Invoke(sm, null);
    }

    public static object Field(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)?.GetValue(o);

    /// <summary>A field, property or zero-argument method by name; the test fails naming it when the type has none.</summary>
    public static object Member(object o, string name, params object[] args)
    {
        const BindingFlags all = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var t = o.GetType();
        var f = t.GetField(name, all);
        if (f != null) return f.GetValue(o);
        var p = t.GetProperty(name, all);
        if (p != null) return p.GetValue(o);
        var m = t.GetMethod(name, all);
        Assert.True(m != null, $"{t.Name} has no member {name}");
        return m!.Invoke(o, args);
    }

    public static string SaidText(SchedulerHarness h) => "[" + string.Join(" | ", h.Said) + "]";
}
