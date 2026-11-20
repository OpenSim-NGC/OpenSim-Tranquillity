/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Data.SQLite;
using System.Reflection;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.UserStatistics;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

/// <summary>
/// The /SStats home page's totals, read from the web stats module's own session table.
/// </summary>
public class SessionStatsReportTests
{
    [Fact]
    public void TheTotalsAreZeroWhenNoSessionIsRecorded()
    {
        using SQLiteConnection db = SessionTable();

        stats_default_page_values totals = new Default_Report().rep_DefaultReport_data(db, new List<Scene>());

        Assert.Equal(0, totals.total_num_sessions);
        Assert.Equal(0f, totals.avg_client_fps);
        Assert.Equal(0f, totals.total_kb_in);
        Assert.Equal(0f, totals.total_kb_out);
    }

    [Fact]
    public void TheRegionsKbInIsWhatTheViewerSent()
    {
        using SQLiteConnection db = SessionTable();
        using (SQLiteCommand insert = new(
            "INSERT INTO stats_session_data (session_id, agent_id, avg_fps, n_in_kb, n_out_kb) VALUES ('s', 'a', 30, 100, 7)", db))
            insert.ExecuteNonQuery();

        stats_default_page_values totals = new Default_Report().rep_DefaultReport_data(db, new List<Scene>());

        Assert.Equal(1, totals.total_num_sessions);
        Assert.Equal(30f, totals.avg_client_fps);
        Assert.Equal(7f, totals.total_kb_in);
        Assert.Equal(100f, totals.total_kb_out);
    }

    private static SQLiteConnection SessionTable()
    {
        SQLiteConnection db = new("Data Source=:memory:");
        db.Open();
        string create = (string)typeof(WebStatsModule)
            .GetField("SQL_STATS_TABLE_CREATE", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        using (SQLiteCommand cmd = new(create, db))
            cmd.ExecuteNonQuery();
        return db;
    }
}
