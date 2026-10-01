/*
 * Phlox Script Engine
 * StateManager.cs — Script runtime state persistence
 *
 * Ported from halcyon-reference/InWorldz/InWorldz.Phlox.Engine/StateManager.cs
 * Adapted for .NET 8: System.Data.SQLite (ADO.NET provider)
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
using System.Data.SQLite;
using OpenMetaverse;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Serialization;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("InWorldz.Phlox.Tests")]

namespace Phlox.ScriptEngine
{
    /// <summary>The state row exists (or may) and could not be read: hold the script, keep the row.</summary>
    internal sealed class StateLoadFailedException : Exception
    {
        public UUID ItemId { get; }
        public StateLoadFailedException(UUID itemId, Exception inner)
            : base($"state load failed for {itemId}: {inner?.Message}", inner) { ItemId = itemId; }
    }

    internal class StateManager : IDisposable
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string DB_DIR  = "ScriptEngines/Phlox/state";
        private const string DB_FILE = "ScriptEngines/Phlox/state/script_state.db";
        private readonly string m_DbFile;

        // Diagnostics: what the log lines count, readable by a test.
        internal int LoadFailures;
        internal string LastLoadError;
        internal int FlushFailures;
        internal string LastFlushError;

        // In world, three regions restored in parallel against one script_state.db and
        // got "database is locked" on a load AND on the flush 300 ms later - and a failed load is a
        // script restarted from state_entry with its globals gone. Three things, all here:
        //   (a) journal_mode=WAL + synchronous=NORMAL, set ONCE per manager under the writer lock (the
        //       old code re-issued the journal pragma on every open; readers never block the writer);
        //   (b) busy_timeout 5000 ms on every connection, so a contended open waits instead of throwing;
        //   (c) ONE writer: every write in the process - all three engines' flush loops, SaveSingle,
        //       DeleteState - serialises on s_WriterLock; each manager keeps a persistent writer and a
        //       persistent reader open for its lifetime, so the WAL is never torn down and rebuilt
        //       between per-call connections (the wal-index recovery race is the one BUSY the provider
        //       does not retry); loads use the reader under the manager's own read lock.
        private const int BUSY_TIMEOUT_MS = 5000;
        private static readonly object s_WriterLock = new object();
        private readonly object m_ReadLock = new object();
        private SQLiteConnection m_Writer;
        private SQLiteConnection m_Reader;
        private readonly HashSet<UUID> m_LoadFailed = new HashSet<UUID>();

        /// <summary>Test seam: make LoadState fail for an item as the database would. Null in production.</summary>
        internal static Func<UUID, bool> FailLoadForTest;
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

        public StateManager(PhloxEngine engine) : this(engine, DB_FILE) { }

        /// <summary>The DB file is a parameter so a test can run against a temp file.</summary>
        internal StateManager(PhloxEngine engine, string dbFile)
        {
            m_Engine = engine;
            m_DbFile = dbFile;
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
            lock (s_WriterLock) { m_Writer?.Dispose(); m_Writer = null; }
            lock (m_ReadLock) { m_Reader?.Dispose(); m_Reader = null; }
        }

        /// <summary>
        /// A script whose saved state could not be READ must never be written: the row on
        /// disk is the only copy of its globals, and a fresh state_entry saved over it would destroy
        /// them. The scheduler holds such a script Disabled; this refuses every save for it until the
        /// next process, whose load will try again.
        /// </summary>
        public void MarkLoadFailed(UUID itemId)
        {
            lock (m_Lock) m_LoadFailed.Add(itemId);
        }

        internal bool IsLoadFailed(UUID itemId)
        {
            lock (m_Lock) return m_LoadFailed.Contains(itemId);
        }

        public void ScriptChanged(Interpreter interp)
        {
            lock (m_Lock)
            {
                if (m_LoadFailed.Contains(interp.ItemId)) return;   // Never overwrite an unread row
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
                if (m_LoadFailed.Contains(interp.ItemId))
                {
                    m_log.LogInformation("[PhloxState]: Not saving {0}: its state row could not be read this run and is kept as it was", interp.ItemId);
                    m_Dirty.Remove(interp.ItemId);
                    m_Live.Remove(interp.ItemId);
                    return;
                }
                SaveSingle(interp);
                m_Dirty.Remove(interp.ItemId);
                m_Live.Remove(interp.ItemId);
            }
        }

        /// <summary>
        /// Loads saved state for a script, validating that the asset ID matches.
        /// Returns null if no state exists or the script has been modified since last save.
        /// A DATABASE failure is not "no state" - the row may well be there. One retry after
        /// the busy timeout, then <see cref="StateLoadFailedException"/>, which the scheduler turns into
        /// a script held Disabled with the row untouched, never into a fresh start.
        /// </summary>
        public SerializedRuntimeState LoadState(UUID itemId, UUID assetId)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    if (FailLoadForTest != null && FailLoadForTest(itemId))
                        throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked (test)");
                    return LoadStateOnce(itemId, assetId);
                }
                catch (Exception e)
                {
                    last = e;
                    Interlocked.Increment(ref LoadFailures);
                    LastLoadError = e.Message;
                    m_log.LogWarning("[PhloxState]: Failed to load state for {0} (attempt {1} of 2): {2}", itemId, attempt, e.Message);
                }
            }
            m_log.LogError("[PhloxState]: State load FAILED for {0} after 2 attempts; the script will be held disabled and its row kept: {1}", itemId, last?.Message);
            MarkLoadFailed(itemId);
            throw new StateLoadFailedException(itemId, last);
        }

        private SerializedRuntimeState LoadStateOnce(UUID itemId, UUID assetId)
        {
            lock (m_ReadLock)
            {
                var conn = Reader();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT asset_id, state_data FROM script_state WHERE item_id = @id";
                cmd.Parameters.AddWithValue("@id", itemId.ToString());

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                string savedAssetId = reader.GetString(0);
                if (savedAssetId != assetId.ToString())
                {
                    m_log.LogDebug("[PhloxState]: Discarding stale state for {0} (saved asset {1}, current {2})",
                        itemId, savedAssetId, assetId);
                    return null;
                }

                byte[] blob = (byte[])reader[1];
                using var ms = new MemoryStream(blob);
                return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
            }
        }

        /// <summary>
        /// <see cref="DeleteState"/> when the item has a row (or unsaved state). Reading first keeps the common
        /// case - another engine's script that never had a row - off the writer. A read that fails deletes anyway.
        /// </summary>
        public void DeleteStateIfPresent(UUID itemId)
        {
            bool present = true;
            try
            {
                bool pending;
                lock (m_Lock) pending = m_Dirty.ContainsKey(itemId) || m_Live.ContainsKey(itemId) || m_LoadFailed.Contains(itemId);
                if (!pending)
                {
                    lock (m_ReadLock)
                    {
                        using var cmd = Reader().CreateCommand();
                        cmd.CommandText = "SELECT 1 FROM script_state WHERE item_id = @id";
                        cmd.Parameters.AddWithValue("@id", itemId.ToString());
                        using var reader = cmd.ExecuteReader();
                        present = reader.Read();
                    }
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxState]: Could not check for a state row of {0}, deleting: {1}", itemId, e.Message);
            }
            if (present) DeleteState(itemId);
        }

        public void DeleteState(UUID itemId)
        {
            try
            {
                lock (s_WriterLock)
                {
                    using var cmd = Writer().CreateCommand();
                    cmd.CommandText = "DELETE FROM script_state WHERE item_id = @id";
                    cmd.Parameters.AddWithValue("@id", itemId.ToString());
                    cmd.ExecuteNonQuery();
                }

                lock (m_Lock)
                {
                    m_Dirty.Remove(itemId);
                    m_Live.Remove(itemId);
                    m_LoadFailed.Remove(itemId);
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxState]: Failed to delete state for {0}: {1}", itemId, e.Message);
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
                lock (s_WriterLock)
                {
                    var conn = Writer();
                    using var tx = conn.BeginTransaction();
                    foreach (var entry in snapshot)
                    {
                        try { SaveSingleInTransaction(conn, entry.Script); }
                        catch (Exception e)
                        {
                            m_log.LogWarning("[PhloxState]: Failed to save {0}: {1}", entry.Script.ItemId, e.Message);
                        }
                    }
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref FlushFailures);
                LastFlushError = e.Message;
                m_log.LogError("[PhloxState]: Batch flush failed: {0}", e.Message);
            }
            
        }

        private void SaveSingle(Interpreter interp)
        {
            try
            {
                lock (s_WriterLock)
                {
                    var conn = Writer();
                    using var tx = conn.BeginTransaction();
                    SaveSingleInTransaction(conn, interp);
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxState]: Failed to save state for {0}: {1}", interp.ItemId, e.Message);
            }
        }

        private void SaveSingleInTransaction(SQLiteConnection conn, Interpreter interp)
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
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(m_DbFile)) ?? DB_DIR);
                lock (s_WriterLock)
                {
                    var conn = Writer();
                    using (var pragma = conn.CreateCommand())
                    {
                        // (a) once, under the one writer lock: the journal-mode change needs the file to
                        // itself, and it persists in the header - every later connection just inherits it.
                        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                        pragma.ExecuteNonQuery();
                    }
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        @"CREATE TABLE IF NOT EXISTS script_state (
                            item_id    TEXT    PRIMARY KEY,
                            asset_id   TEXT    NOT NULL DEFAULT '',
                            state_data BLOB    NOT NULL,
                            saved_at   INTEGER NOT NULL
                        )";
                    cmd.ExecuteNonQuery();
                }
                lock (m_ReadLock) Reader();
            }
            catch (Exception e)
            {
                m_log.LogError("[PhloxState]: Failed to initialize state database: {0}", e.Message);
            }
        }

        /// <summary>The manager's one writer, opened on first use under s_WriterLock and kept for its lifetime.</summary>
        private SQLiteConnection Writer()
        {
            if (m_Writer == null || m_Writer.State != System.Data.ConnectionState.Open)
            {
                m_Writer?.Dispose();
                m_Writer = OpenConnection();
            }
            return m_Writer;
        }

        /// <summary>The manager's one reader, opened on first use under m_ReadLock and kept for its lifetime.</summary>
        private SQLiteConnection Reader()
        {
            if (m_Reader == null || m_Reader.State != System.Data.ConnectionState.Open)
            {
                m_Reader?.Dispose();
                m_Reader = OpenConnection();
            }
            return m_Reader;
        }

        private SQLiteConnection OpenConnection()
        {
            var conn = new SQLiteConnection($"Data Source={m_DbFile};BusyTimeout={BUSY_TIMEOUT_MS}");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BUSY_TIMEOUT_MS}";   // (b) every connection waits instead of throwing
            pragma.ExecuteNonQuery();
            return conn;
        }
    }
}
