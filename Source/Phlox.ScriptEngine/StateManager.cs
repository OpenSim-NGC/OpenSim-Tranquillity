/*
 * Legion Grid — Phlox Script Engine
 * StateManager.cs — Script runtime state persistence
 *
 * Ported from halcyon-reference/InWorldz/InWorldz.Phlox.Engine/StateManager.cs
 * Adapted for .NET 8: System.Data.SQLite → Microsoft.Data.Sqlite
 *                     IndexedPriorityQueue → SortedDictionary
 *                     ThreadTracker → plain Thread
 *
 * Saves/restores LSL global variable state, current LSL state name,
 * timer interval, and event queue across region restarts.
 *
 * The serialization format (protobuf-net via SerializedRuntimeState) is
 * already implemented in InWorldz.Phlox.dll — we just call it here.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using log4net;
using Microsoft.Data.Sqlite;
using OpenMetaverse;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Serialization;

namespace Phlox.ScriptEngine
{
    internal class StateManager : IDisposable
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string DB_DIR  = "ScriptEngines/Phlox/state";
        private const string DB_FILE = "ScriptEngines/Phlox/state/script_state.db";
        private const int FLUSH_INTERVAL_MS = 2500;
        private const int MAX_DIRTY_EXECUTIONS = 200;

        private readonly PhloxEngine m_Engine;
        private readonly SortedDictionary<UUID, DirtyEntry> m_Dirty = new SortedDictionary<UUID, DirtyEntry>();
        private readonly Dictionary<UUID, Interpreter> m_Live = new Dictionary<UUID, Interpreter>();
        private readonly object m_Lock = new object();
        private Thread m_Thread;
        private volatile bool m_Stop;
        private readonly ManualResetEventSlim m_WakeEvent = new ManualResetEventSlim(false);

        private class DirtyEntry
        {
            public Interpreter Script;
            public int ExecutionCount;
        }

        public StateManager(PhloxEngine engine)
        {
            m_Engine = engine;
            EnsureDatabase();
        }

        public void Start()
        {
            m_Thread = new Thread(FlushLoop)
            {
                Name = "PhloxStateManager",
                IsBackground = true,
                Priority = ThreadPriority.Lowest
            };
            m_Thread.Start();
        }

        public void Stop()
        {
            m_Stop = true;
            m_WakeEvent.Set();
            m_Thread?.Join(5000);
            lock (m_Lock)
            {
                FlushAllDirty();
            }
        }

        public void Dispose()
        {
            if (!m_Stop) Stop();
            m_WakeEvent.Dispose();
        }

        public void ScriptChanged(Interpreter interp)
        {
            lock (m_Lock)
            {
                if (m_Dirty.TryGetValue(interp.ItemId, out var entry))
                {
                    entry.ExecutionCount++;
                    if (entry.ExecutionCount >= MAX_DIRTY_EXECUTIONS)
                    {
                        SaveSingle(interp);
                        m_Dirty.Remove(interp.ItemId);
                    }
                }
                else
                {
                    m_Dirty[interp.ItemId] = new DirtyEntry { Script = interp, ExecutionCount = 1 };
                    m_Live[interp.ItemId] = interp;
                }
                m_WakeEvent.Set();
            }
        }

        public void ScriptUnloaded(Interpreter interp)
        {
            lock (m_Lock)
            {
                SaveSingle(interp);
                m_Dirty.Remove(interp.ItemId);
                m_Live.Remove(interp.ItemId);
            }
        }

        /// <summary>
        /// Loads saved state for a script, validating that the asset ID matches.
        /// Returns null if no state exists or the script has been modified since last save.
        /// </summary>
        public SerializedRuntimeState LoadState(UUID itemId, UUID assetId)
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText = "SELECT asset_id, state_data FROM script_state WHERE item_id = @id";
                cmd.Parameters.AddWithValue("@id", itemId.ToString());

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                string savedAssetId = reader.GetString(0);
                if (savedAssetId != assetId.ToString())
                {
                    m_log.DebugFormat("[PhloxState]: Discarding stale state for {0} (saved asset {1}, current {2})",
                        itemId, savedAssetId, assetId);
                    return null;
                }

                byte[] blob = (byte[])reader[1];
                using var ms = new MemoryStream(blob);
                return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[PhloxState]: Failed to load state for {0}: {1}", itemId, e.Message);
                return null;
            }
        }

        public void DeleteState(UUID itemId)
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM script_state WHERE item_id = @id";
                cmd.Parameters.AddWithValue("@id", itemId.ToString());
                cmd.ExecuteNonQuery();

                lock (m_Lock)
                {
                    m_Dirty.Remove(itemId);
                    m_Live.Remove(itemId);
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[PhloxState]: Failed to delete state for {0}: {1}", itemId, e.Message);
            }
        }

        private void FlushLoop()
        {
            while (!m_Stop)
            {
                m_WakeEvent.Wait(FLUSH_INTERVAL_MS);
                m_WakeEvent.Reset();
                if (m_Stop) break;
                lock (m_Lock)
                {
                    FlushAllDirty();
                }
            }
        }

        private void FlushAllDirty()
        {
            if (m_Dirty.Count == 0) return;
            var snapshot = new List<DirtyEntry>(m_Dirty.Values);
            m_Dirty.Clear();
            try
            {
                using var conn = OpenConnection();
                using var tx   = conn.BeginTransaction();
                foreach (var entry in snapshot)
                {
                    try { SaveSingleInTransaction(conn, entry.Script); }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("[PhloxState]: Failed to save {0}: {1}", entry.Script.ItemId, e.Message);
                    }
                }
                tx.Commit();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[PhloxState]: Batch flush failed: {0}", e.Message);
            }
            
        }

        private void SaveSingle(Interpreter interp)
        {
            try
            {
                using var conn = OpenConnection();
                using var tx   = conn.BeginTransaction();
                SaveSingleInTransaction(conn, interp);
                tx.Commit();
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[PhloxState]: Failed to save state for {0}: {1}", interp.ItemId, e.Message);
            }
        }

        private static void SaveSingleInTransaction(SqliteConnection conn, Interpreter interp)
        {
            SerializedRuntimeState srs = SerializedRuntimeState.FromRuntimeState(interp.ScriptState);
            byte[] blob;
            using (var ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, srs);
                blob = ms.ToArray();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO script_state (item_id, asset_id, state_data, saved_at)
                  VALUES (@id, @assetid, @data, @ts)
                  ON CONFLICT(item_id) DO UPDATE SET
                      asset_id   = excluded.asset_id,
                      state_data = excluded.state_data,
                      saved_at   = excluded.saved_at";
            cmd.Parameters.AddWithValue("@id",      interp.ItemId.ToString());
            cmd.Parameters.AddWithValue("@assetid", interp.Script.AssetId.ToString());
            cmd.Parameters.AddWithValue("@data",    blob);
            cmd.Parameters.AddWithValue("@ts",      DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }

        private void EnsureDatabase()
        {
            SQLitePCL.Batteries_V2.Init();
            try
            {
                Directory.CreateDirectory(DB_DIR);
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText =
                    @"CREATE TABLE IF NOT EXISTS script_state (
                        item_id    TEXT    PRIMARY KEY,
                        asset_id   TEXT    NOT NULL DEFAULT '',
                        state_data BLOB    NOT NULL,
                        saved_at   INTEGER NOT NULL
                    )";
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[PhloxState]: Failed to initialize state database: {0}", e.Message);
            }
        }

        private static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={DB_FILE};Mode=ReadWriteCreate");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteNonQuery();
            return conn;
        }
    }
}
