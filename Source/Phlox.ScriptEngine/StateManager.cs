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
        private readonly int m_BusyTimeoutMs = BUSY_TIMEOUT_MS;
        private readonly bool m_ShortTimeout;
        private static readonly object s_WriterLock = new object();
        private readonly object m_ReadLock = new object();
        private SQLiteConnection m_Writer;
        private SQLiteConnection m_Reader;
        private readonly HashSet<UUID> m_LoadFailed = new HashSet<UUID>();

        /// <summary>Test seam: make LoadState fail for an item as the database would. Null in production.</summary>
        internal static Func<UUID, bool> FailLoadForTest;
        private const int FLUSH_INTERVAL_MS = 2500;

        private readonly PhloxEngine m_Engine;
        /// <summary>Scripts that ran since their last capture: marked by the scheduler after every timeslice.</summary>
        private readonly Dictionary<UUID, Interpreter> m_Dirty = new Dictionary<UUID, Interpreter>();
        /// <summary>Scripts loaded in this engine (their state was looked up), for the purge.</summary>
        private readonly HashSet<UUID> m_Live = new HashSet<UUID>();
        private readonly object m_Lock = new object();
        private Thread m_Thread;
        private volatile bool m_Stop;
        private readonly ManualResetEventSlim m_WakeEvent = new ManualResetEventSlim(false);

        // The write queue. Every database write - a captured state, a delete, a corrupt row moved aside, a load's
        // refresh of saved_at - is queued here and done by the state thread in the order it was queued, outside m_Lock.
        // The scheduler thread never waits on SQLite: Halcyon's ScriptChanged and ScriptUnloaded only touched in-memory
        // sets, and its state thread did the writes (Halcyon StateManager.cs, DoSaveDirtyScripts).
        private enum WriteKind { Save, Delete, MoveAside, Touch }
        private sealed class WriteOp
        {
            public WriteKind Kind;
            public UUID ItemId;
            public UUID AssetId;
            public byte[] Blob;
            public string Reason;
            public long Seq;
            /// <summary>Times this write was in a transaction that did not commit.</summary>
            public int Attempts;
        }
        private readonly Queue<WriteOp> m_Writes = new Queue<WriteOp>();
        private long m_WritesQueued;
        private long m_WritesDone;
        private long m_WritesAbandoned;
        private readonly object m_DrainLock = new object();

        /// <summary>
        /// A write that did not commit stays queued, in its place, and is tried again with the writes after it; after this
        /// many failed attempts it is given up with an error that names it. Each attempt against a locked database waits
        /// out the busy timeout first, so this bounds a lock of several busy timeouts.
        /// </summary>
        internal const int MaxWriteAttempts = 5;

        /// <summary>Writes committed, and writes given up after <see cref="MaxWriteAttempts"/> (diagnostics and tests).</summary>
        internal long WritesDone { get { lock (m_Lock) return m_WritesDone; } }
        internal long WritesAbandoned { get { lock (m_Lock) return m_WritesAbandoned; } }

        /// <summary>Test seam: a write for this item fails as a failed statement would. Null in production.</summary>
        internal Func<UUID, bool> FailWriteForTest;

        // Items with a write still queued, across every manager in the process, and how many each manager holds. The
        // managers share one file, and a script unloaded in one region and loaded in another of the same simulator (a
        // crossing, an engine switched back) must read the row its unload wrote, not the one before it.
        private static readonly Dictionary<UUID, Dictionary<StateManager, int>> s_Pending = new Dictionary<UUID, Dictionary<StateManager, int>>();
        private static readonly object s_PendingLock = new object();
        // The order writes were queued in, across every manager, so a load sees the newest queued write for its item.
        private static long s_WriteSeq;

        // Scripts loaded in any engine of this process, and the manager of the engine that loaded each last. The purge
        // never deletes their rows, and an unload in any other manager does not write over the row (a crossing object's
        // scripts load in the region it entered before the region it left unloads them, or after: nothing orders the two).
        private static readonly Dictionary<UUID, StateManager> s_Live = new Dictionary<UUID, StateManager>();
        private static readonly object s_LiveLock = new object();

        /// <summary>The script is loaded in an engine of this process.</summary>
        internal static bool IsLoadedAnywhere(UUID itemId)
        {
            lock (s_LiveLock) return s_Live.ContainsKey(itemId);
        }

        // The scheduler's wake: the state thread asks for a capture through it, and the scheduler takes the snapshots
        // on its own thread, between timeslices (Halcyon: ExecutionScheduler.RequestStateData).
        private Action m_RequestCapture;
        private volatile bool m_CaptureRequested;

        /// <summary>Snapshots taken on the scheduler thread, and by this manager itself (only at Stop, or with no engine).</summary>
        internal int SchedulerCaptures;
        internal int SelfCaptures;
        /// <summary>Rows that could not be restored and were moved to script_state_rejected.</summary>
        internal int RowsMovedAside;
        internal int RowsPurged;

        /// <summary>
        /// [InWorldz.Phlox] StateRowMaxAgeDays: a row not saved or loaded for this many days, of a script not loaded in
        /// this simulator, is deleted. 0 (the default) never deletes one, as before.
        /// </summary>
        internal int StateRowMaxAgeDays;
        private const long PURGE_FIRST_DELAY_MS = 60L * 60 * 1000;
        private const long PURGE_INTERVAL_MS = 6L * 60 * 60 * 1000;
        private long m_NextPurgeTick;

        public StateManager(PhloxEngine engine) : this(engine, DB_FILE) { }

        /// <summary>The DB file is a parameter so a test can run against a temp file.</summary>
        internal StateManager(PhloxEngine engine, string dbFile) : this(engine, dbFile, BUSY_TIMEOUT_MS) { }

        /// <summary>
        /// A test's manager whose connections give up on a locked database after <paramref name="busyTimeoutMs"/>, statement
        /// retries included, instead of the production busy timeout.
        /// </summary>
        internal StateManager(PhloxEngine engine, string dbFile, int busyTimeoutMs)
        {
            m_BusyTimeoutMs = busyTimeoutMs;
            m_ShortTimeout = busyTimeoutMs != BUSY_TIMEOUT_MS;
            m_Engine = engine;
            m_DbFile = dbFile;
            StateRowMaxAgeDays = Math.Max(0, engine?.Config?.GetInt("StateRowMaxAgeDays", 0) ?? 0);
            EnsureDatabase();
        }

        public void Start()
        {
            m_NextPurgeTick = Environment.TickCount64 + PURGE_FIRST_DELAY_MS;
            m_Thread = new Thread(FlushLoop)
            {
                Name = "PhloxStateManager",
                IsBackground = true,
                Priority = ThreadPriority.Lowest
            };
            m_Thread.Start();
        }

        /// <summary>
        /// The final save. The caller stops the scheduler first (PhloxEngine.OnShutdown, RemoveRegion), as Halcyon's
        /// MasterScheduler.Stop joined its thread before the state manager's backup, so no script runs while the dirty
        /// scripts are captured here, on this thread. Then every queued write is tried until it commits or is given up
        /// (<see cref="MaxWriteAttempts"/>), and every write given up while this manager ran is named in the log.
        /// </summary>
        public void Stop() => StopExcept(UUID.Zero);

        /// <summary>
        /// As <see cref="Stop()"/>, except that <paramref name="stillRunning"/>, a script the scheduler is still running
        /// because its thread did not stop, is not captured: its state may be changing, and its last saved row stays.
        /// </summary>
        public void StopExcept(UUID stillRunning)
        {
            m_Stop = true;
            m_WakeEvent.Set();
            m_Thread?.Join(5000);
            if (!stillRunning.IsZero())
                lock (m_Lock) m_Dirty.Remove(stillRunning);
            CaptureDirty(self: true);
            while (!DrainWrites()) { }   // each pass counts an attempt against what failed, so this ends
            List<string> givenUp;
            lock (m_Lock) givenUp = new List<string>(m_GivenUp);
            if (givenUp.Count > 0)
                m_log.LogError("[PhloxState]: {0} state writes were not saved: {1}", givenUp.Count, string.Join(", ", givenUp));
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

        /// <summary>The scheduler gives its wake, so the state thread can ask it for a capture.</summary>
        internal void AttachScheduler(Action requestCapture) => m_RequestCapture = requestCapture;

        /// <summary>
        /// The script ran (a timeslice, a stop, a crash). Only marks it: Halcyon marked every timeslice, so a script in a
        /// long loop, an llSleep or a blocking call is saved as it is, not as it was at the end of its last event.
        /// </summary>
        public void ScriptChanged(Interpreter interp)
        {
            lock (m_Lock)
            {
                if (m_LoadFailed.Contains(interp.ItemId)) return;   // Never overwrite an unread row
                m_Dirty[interp.ItemId] = interp;
            }
        }

        /// <summary>Scheduler thread, from DoWork: take the snapshots the state thread asked for.</summary>
        internal void CaptureRequestedStates()
        {
            if (!m_CaptureRequested) return;
            m_CaptureRequested = false;
            CaptureDirty(self: false);
            m_WakeEvent.Set();
        }

        private void CaptureDirty(bool self)
        {
            List<Interpreter> scripts;
            lock (m_Lock)
            {
                if (m_Dirty.Count == 0) return;
                scripts = new List<Interpreter>(m_Dirty.Values);
                m_Dirty.Clear();
            }
            foreach (var interp in scripts)
            {
                byte[] blob;
                try { blob = CaptureRow(interp); }
                catch (Exception e)
                {
                    CaptureFailed(interp.ItemId, e);
                    continue;
                }
                lock (m_Lock) m_CaptureFailing.Remove(interp.ItemId);
                if (self) Interlocked.Increment(ref SelfCaptures);
                else Interlocked.Increment(ref SchedulerCaptures);
                Queue(new WriteOp { Kind = WriteKind.Save, ItemId = interp.ItemId, AssetId = interp.Script.AssetId, Blob = blob });
            }
        }

        /// <summary>
        /// A capture of the script failed. A part that could not be copied passes: the last saved row is kept and the next
        /// capture tries again. Any other failure means the script's state cannot be captured as it is now (tables nested
        /// past the bound, a closure cycle), and it fails at every flush while it stays so. Its older row would resume the
        /// script at an earlier moment after a restart with nothing to say so; it is moved to script_state_rejected
        /// instead, once, so a restart starts the script fresh. A later capture that works writes a normal row again. The
        /// warning is written once, until a capture of the script succeeds.
        /// </summary>
        private void CaptureFailed(UUID itemId, Exception e)
        {
            Interlocked.Increment(ref FlushFailures);
            LastFlushError = e.Message;
            if (e is SerializedRuntimeState.PartNotCopiedException)
            {
                m_log.LogWarning("[PhloxState]: Failed to capture {0}: {1}; its last saved state is kept", itemId, e.Message);
                return;
            }
            bool first, loadFailed;
            lock (m_Lock)
            {
                first = m_CaptureFailing.Add(itemId);
                loadFailed = m_LoadFailed.Contains(itemId);
            }
            if (!first)
            {
                m_log.LogDebug("[PhloxState]: Failed to capture {0} again: {1}", itemId, e.Message);
                return;
            }
            Interlocked.Increment(ref CaptureFailureWarnings);
            if (loadFailed)   // Never touch a row that could not be read
            {
                m_log.LogWarning("[PhloxState]: Failed to capture {0}: {1}", itemId, e.Message);
                return;
            }
            m_log.LogError("[PhloxState]: The state of {0} cannot be captured ({1}); its older saved state cannot be restored in its place and is moved to script_state_rejected, so a restart starts the script fresh. This is not logged again until a capture of it succeeds",
                itemId, e.Message);
            Interlocked.Increment(ref RowsMovedAside);
            Queue(new WriteOp { Kind = WriteKind.MoveAside, ItemId = itemId, Reason = "a newer state could not be captured: " + e.Message });
            m_WakeEvent.Set();
        }

        /// <summary>Scripts whose last capture failed, and so were warned of once.</summary>
        private readonly HashSet<UUID> m_CaptureFailing = new HashSet<UUID>();
        /// <summary>Capture failures written to the log as warnings (diagnostics and tests).</summary>
        internal int CaptureFailureWarnings;

        /// <summary>
        /// The script's state for its database row, with the grant its item holds now (none held, none saved). A grant
        /// from carried state still waiting for its granter is not written: a row's grant comes back whole.
        /// </summary>
        private byte[] CaptureRow(Interpreter interp)
        {
            m_Engine?.NoteGrantForRow(interp);
            return Capture(interp);
        }

        private static byte[] Capture(Interpreter interp)
        {
            SerializedRuntimeState srs = SerializedRuntimeState.FromRuntimeState(interp.ScriptState);
            using var ms = new MemoryStream();
            ProtoBuf.Serializer.Serialize(ms, srs);
            return ms.ToArray();
        }

        /// <summary>
        /// The script leaves this engine and its item stays where it is (a derez, take or crossing, a recompile, another
        /// engine taking it). Called on the scheduler thread: the state is captured here and the row written later by the
        /// state thread. The row is kept until state travels with objects, so a crossing inside this simulator and a
        /// switch back from another engine still restore it.
        /// </summary>
        public void QueueUnloadSave(Interpreter interp)
        {
            bool wasFailing;
            lock (m_Lock)
            {
                m_Dirty.Remove(interp.ItemId);
                m_Live.Remove(interp.ItemId);
                wasFailing = m_CaptureFailing.Remove(interp.ItemId);
                if (m_LoadFailed.Contains(interp.ItemId))
                {
                    m_log.LogInformation("[PhloxState]: Not saving {0}: its state row could not be read this run and is kept as it was", interp.ItemId);
                    return;
                }
            }
            if (!ForgetLive(interp.ItemId))
            {
                // Loaded since in another region of this simulator (a crossing whose arrival loaded first): that region's
                // state is the newer one, and the row is its to write.
                m_log.LogDebug("[PhloxState]: Not saving {0} at unload: it is loaded in another region now", interp.ItemId);
                return;
            }
            byte[] blob;
            try { blob = CaptureRow(interp); }
            catch (Exception e)
            {
                // Its older row was moved aside already when the failing began; otherwise it is now.
                if (wasFailing) m_log.LogDebug("[PhloxState]: Failed to capture {0} at unload: {1}", interp.ItemId, e.Message);
                else CaptureFailed(interp.ItemId, e);
                lock (m_Lock) m_CaptureFailing.Remove(interp.ItemId);
                return;
            }
            Queue(new WriteOp { Kind = WriteKind.Save, ItemId = interp.ItemId, AssetId = interp.Script.AssetId, Blob = blob });
            m_WakeEvent.Set();
        }

        /// <summary>The unload save, written before this returns. For a caller that is not the scheduler thread.</summary>
        public void ScriptUnloaded(Interpreter interp)
        {
            QueueUnloadSave(interp);
            WaitForWrites();
        }

        /// <summary>
        /// The script item left its prim (deleted from the inventory, moved or given away) while the prim stays in the
        /// region: its row can never be restored again and goes, as Halcyon and YEngine delete theirs on removal.
        /// </summary>
        public void QueueUnloadDelete(UUID itemId)
        {
            lock (m_Lock)
            {
                m_Dirty.Remove(itemId);
                m_Live.Remove(itemId);
                m_CaptureFailing.Remove(itemId);
                m_LoadFailed.Remove(itemId);
            }
            ForgetLive(itemId);
            Queue(new WriteOp { Kind = WriteKind.Delete, ItemId = itemId });
            m_WakeEvent.Set();
        }

        /// <summary>
        /// Loads saved state for a script, validating that the asset ID matches.
        /// Returns null if no state exists or the script has been modified since last save.
        /// A DATABASE failure is not "no state" - the row may well be there. One retry after
        /// the busy timeout, then <see cref="StateLoadFailedException"/>, which the scheduler turns into
        /// a script held Disabled with the row untouched, never into a fresh start.
        /// A row that was read but cannot be decoded is bad data, not a busy database: it is moved to
        /// script_state_rejected and null comes back, so the script starts fresh (Halcyon: "Could not load state ...
        /// script will be reset"; YEngine deletes the bad state file and resets).
        /// </summary>
        public SerializedRuntimeState LoadState(UUID itemId, UUID assetId) => LoadState(itemId, assetId, out _);

        /// <summary>As <see cref="LoadState(UUID, UUID)"/>; <paramref name="carried"/> is true when the state came with the
        /// object rather than from this simulator's database, and is to be checked as input from outside.</summary>
        public SerializedRuntimeState LoadState(UUID itemId, UUID assetId, out bool carried)
        {
            FinishPendingWrites(itemId);
            NoteLive(itemId);

            SerializedRuntimeState fromObject = TakeCarried(itemId, assetId);
            carried = fromObject != null;
            if (carried) return fromObject;

            // A write for this item the database has not taken yet is its newest state: a save is restored from what it
            // holds, a removal leaves no state. The row on disk is older than either.
            if (UnwrittenFor(itemId) is WriteOp unwritten)
            {
                m_log.LogWarning("[PhloxState]: The state database has not taken the last {0} of {1} yet; the script is restored from it",
                    unwritten.Kind, itemId);
                if (unwritten.Kind != WriteKind.Save || unwritten.AssetId != assetId) return null;
                try { return Decode(unwritten.Blob); }
                catch (Exception e)
                {
                    m_log.LogWarning("[PhloxState]: The queued state of {0} does not decode; it starts fresh: {1}", itemId, e.Message);
                    return null;
                }
            }

            byte[] blob = null;
            bool read = false;
            Exception last = null;
            for (int attempt = 1; attempt <= 2 && !read; attempt++)
            {
                try
                {
                    if (FailLoadForTest != null && FailLoadForTest(itemId))
                        throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked (test)");
                    blob = ReadRow(itemId, assetId);
                    read = true;
                }
                catch (InvalidDataException e)
                {
                    RejectRow(itemId, e.Message);
                    return null;
                }
                catch (Exception e)
                {
                    last = e;
                    Interlocked.Increment(ref LoadFailures);
                    LastLoadError = e.Message;
                    m_log.LogWarning("[PhloxState]: Failed to load state for {0} (attempt {1} of 2): {2}", itemId, attempt, e.Message);
                }
            }
            if (!read)
            {
                m_log.LogError("[PhloxState]: State load FAILED for {0} after 2 attempts; the script will be held disabled and its row kept: {1}", itemId, last?.Message);
                MarkLoadFailed(itemId);
                throw new StateLoadFailedException(itemId, last);
            }
            if (blob == null) return null;

            if (StateRowMaxAgeDays > 0) Queue(new WriteOp { Kind = WriteKind.Touch, ItemId = itemId });
            try
            {
                using var ms = new MemoryStream(blob);
                return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
            }
            catch (Exception e)
            {
                RejectRow(itemId, "the saved state does not decode: " + e.Message);
                return null;
            }
        }

        // State that came with the object (a rez from inventory, an attach, a crossing or a teleport), by the item id it
        // has in this region. It is used before the database row, as YEngine's SetXMLState writes the carried state over
        // the state file its load then reads (XMREngine.SetXMLState).
        private readonly Dictionary<UUID, (UUID AssetId, byte[] Blob)> m_Carried = new Dictionary<UUID, (UUID, byte[])>();

        /// <summary>The object brought this script's state with it; the next load of the item uses it.</summary>
        internal void Carry(UUID itemId, UUID assetId, byte[] blob)
        {
            lock (m_Lock) m_Carried[itemId] = (assetId, blob);
        }

        internal bool HasCarried(UUID itemId)
        {
            lock (m_Lock) return m_Carried.ContainsKey(itemId);
        }

        /// <summary>
        /// The carried state for this item, once: null when none came, or when it was saved for another asset (the script
        /// was edited since), which is dropped so the database row is consulted as before.
        /// </summary>
        private SerializedRuntimeState TakeCarried(UUID itemId, UUID assetId)
        {
            (UUID AssetId, byte[] Blob) c;
            lock (m_Lock)
            {
                if (!m_Carried.TryGetValue(itemId, out c)) return null;
                m_Carried.Remove(itemId);
            }
            if (c.AssetId != assetId)
            {
                m_log.LogDebug("[PhloxState]: Dropping the state {0} brought for asset {1}; the script is now asset {2}", itemId, c.AssetId, assetId);
                return null;
            }
            try { return Decode(c.Blob); }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxState]: The state {0} brought with its object does not decode; it starts fresh: {1}", itemId, e.Message);
                return null;
            }
        }

        /// <summary>A saved state, as the state database and the object envelope hold it.</summary>
        internal static SerializedRuntimeState Decode(byte[] blob)
        {
            using var ms = new MemoryStream(blob);
            return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms)
                   ?? throw new InvalidDataException("the saved state is empty");
        }

        /// <summary>The script's state as the state database and the object envelope hold it. Scheduler thread.</summary>
        internal static byte[] CaptureBlob(Interpreter interp) => Capture(interp);

        /// <summary>
        /// Save every given script now: captured here (the caller is the scheduler thread, or the scheduler is not running)
        /// and written before this returns.
        /// </summary>
        internal void SaveNow(IEnumerable<Interpreter> scripts)
        {
            lock (m_Lock)
                foreach (var interp in scripts)
                    if (!m_LoadFailed.Contains(interp.ItemId)) m_Dirty[interp.ItemId] = interp;
            CaptureDirty(self: false);
            if (!WaitForWrites())
                m_log.LogWarning("[PhloxState]: Not every state could be written now; the rest stay queued and are tried again");
        }

        /// <summary>The row's blob; null when there is no row or it is for another asset. InvalidDataException: not a blob.</summary>
        private byte[] ReadRow(UUID itemId, UUID assetId)
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

                if (reader[1] is byte[] blob) return blob;
                throw new InvalidDataException("the saved state is not a blob");
            }
        }

        /// <summary>
        /// The row exists and cannot be restored: move it to script_state_rejected (kept for an operator, never read
        /// again) and let the script start fresh.
        /// </summary>
        public void RejectRow(UUID itemId, string reason)
        {
            m_log.LogError("[PhloxState]: The saved state of {0} cannot be restored ({1}); the row is moved to script_state_rejected and the script starts fresh",
                itemId, reason);
            Interlocked.Increment(ref RowsMovedAside);
            Queue(new WriteOp { Kind = WriteKind.MoveAside, ItemId = itemId, Reason = reason });
            m_WakeEvent.Set();
        }

        /// <summary>A reset: the row goes. Queued, so a reset on the scheduler thread does not wait on the database.</summary>
        public void DeleteState(UUID itemId)
        {
            lock (m_Lock)
            {
                m_Dirty.Remove(itemId);
                m_LoadFailed.Remove(itemId);
            }
            Queue(new WriteOp { Kind = WriteKind.Delete, ItemId = itemId });
            m_WakeEvent.Set();
        }

        private void Queue(WriteOp op)
        {
            lock (m_Lock)
            {
                if (op.Kind == WriteKind.Save && m_LoadFailed.Contains(op.ItemId)) return;   // Never overwrite an unread row
                op.Seq = Interlocked.Increment(ref s_WriteSeq);
                m_Writes.Enqueue(op);
                m_WritesQueued++;
                lock (s_PendingLock)
                {
                    if (!s_Pending.TryGetValue(op.ItemId, out var holders))
                        s_Pending[op.ItemId] = holders = new Dictionary<StateManager, int>();
                    holders.TryGetValue(this, out int count);
                    holders[this] = count + 1;
                }
            }
        }

        /// <summary>
        /// A load must see every write already queued for its item, in whichever engine of this simulator queued it. The
        /// owner's queue is written now, on this thread; only a script reloaded right after its unload ever waits here.
        /// </summary>
        private static void FinishPendingWrites(UUID itemId)
        {
            foreach (StateManager holder in HoldersOf(itemId)) holder.DrainWrites();
        }

        /// <summary>The managers holding a queued write for the item, the one whose write for it was queued first first.</summary>
        private static List<StateManager> HoldersOf(UUID itemId)
        {
            List<StateManager> holders;
            lock (s_PendingLock)
            {
                if (!s_Pending.TryGetValue(itemId, out var h)) return new List<StateManager>();
                holders = new List<StateManager>(h.Keys);
            }
            if (holders.Count > 1) holders.Sort((x, y) => x.FirstQueuedFor(itemId).CompareTo(y.FirstQueuedFor(itemId)));
            return holders;
        }

        private long FirstQueuedFor(UUID itemId)
        {
            lock (m_Lock)
                foreach (var op in m_Writes)
                    if (op.ItemId == itemId) return op.Seq;
            return long.MaxValue;
        }

        /// <summary>
        /// Every write queued before this call has been tried when it returns. True when each one committed or was given up;
        /// false when some are still queued because the database refused them (they are tried again later).
        /// </summary>
        internal bool WaitForWrites()
        {
            long target;
            lock (m_Lock) target = m_WritesQueued;
            if (m_Thread != null && m_Thread.IsAlive)
            {
                m_WakeEvent.Set();
                long until = Environment.TickCount64 + 2 * m_BusyTimeoutMs;
                lock (m_Lock)
                {
                    while (m_WritesDone + m_WritesAbandoned < target)
                    {
                        long left = until - Environment.TickCount64;
                        if (left <= 0) break;
                        Monitor.Wait(m_Lock, (int)left);
                    }
                    if (m_WritesDone + m_WritesAbandoned >= target) return true;
                }
            }
            return DrainWrites();
        }

        /// <summary>
        /// Write the queue in order. A batch is one transaction: when it does not commit none of it is applied, each write in
        /// it that took part counts an attempt, the ones at <see cref="MaxWriteAttempts"/> are given up with an error naming
        /// them, and the rest go back to the front of the queue, before any write queued since. Only a committed write is
        /// counted done and clears its item's pending mark. False when writes are left queued for a later pass.
        /// </summary>
        private bool DrainWrites()
        {
            lock (m_DrainLock)
            {
                while (true)
                {
                    List<WriteOp> batch;
                    lock (m_Lock)
                    {
                        if (m_Writes.Count == 0) return true;
                        batch = new List<WriteOp>(m_Writes);
                        m_Writes.Clear();
                    }
                    if (WriteBatch(batch))
                    {
                        Settle(batch, committed: true);
                        continue;
                    }
                    var givenUp = batch.FindAll(op => op.Attempts >= MaxWriteAttempts);
                    batch.RemoveAll(op => op.Attempts >= MaxWriteAttempts);
                    foreach (var op in givenUp)
                        m_log.LogError("[PhloxState]: Gave up the {0} of the state of {1} after {2} failed attempts ({3}); the state database does not hold it",
                            op.Kind, op.ItemId, op.Attempts, LastFlushError);
                    lock (m_Lock)
                    {
                        var later = new List<WriteOp>(m_Writes);
                        m_Writes.Clear();
                        foreach (var op in batch) m_Writes.Enqueue(op);
                        foreach (var op in later) m_Writes.Enqueue(op);
                    }
                    Settle(givenUp, committed: false);
                    // A write given up may have been what failed: the rest are tried again now. Otherwise the next pass does.
                    if (givenUp.Count == 0 || batch.Count == 0) return false;
                }
            }
        }

        /// <summary>The writes are finished, committed or given up: their pending marks go and the waiters are woken.</summary>
        private void Settle(List<WriteOp> ops, bool committed)
        {
            if (ops.Count == 0) return;
            lock (s_PendingLock)
            {
                foreach (var op in ops)
                {
                    if (!s_Pending.TryGetValue(op.ItemId, out var holders) || !holders.TryGetValue(this, out int count)) continue;
                    if (count > 1) holders[this] = count - 1;
                    else if (holders.Remove(this) && holders.Count == 0) s_Pending.Remove(op.ItemId);
                }
            }
            lock (m_Lock)
            {
                if (committed) m_WritesDone += ops.Count;
                else
                {
                    m_WritesAbandoned += ops.Count;
                    foreach (var op in ops) m_GivenUp.Add(op.Kind + " " + op.ItemId);
                }
                Monitor.PulseAll(m_Lock);
            }
        }

        /// <summary>Writes given up since this manager started, as "Kind item" (named again at shutdown).</summary>
        private readonly List<string> m_GivenUp = new List<string>();

        /// <summary>
        /// The write still queued for this item in whichever manager of this simulator holds it, a save or a removal: the
        /// item's newest state, which the database could not take yet. Null when none is queued.
        /// </summary>
        private static WriteOp UnwrittenFor(UUID itemId)
        {
            WriteOp last = null;
            foreach (StateManager holder in HoldersOf(itemId))
                lock (holder.m_Lock)
                    foreach (var op in holder.m_Writes)
                        if (op.ItemId == itemId && op.Kind != WriteKind.Touch && (last == null || op.Seq > last.Seq)) last = op;
            return last;
        }

        private void FlushLoop()
        {
            while (!m_Stop)
            {
                m_WakeEvent.Wait(FLUSH_INTERVAL_MS);
                m_WakeEvent.Reset();
                if (m_Stop) break;
                try
                {
                    RequestCapture();
                    DrainWrites();
                    MaybePurge();
                }
                catch (Exception e)
                {
                    m_log.LogError("[PhloxState]: State thread pass failed: {0}", e.Message);
                }
            }
        }

        /// <summary>
        /// Ask the scheduler to capture the dirty scripts. A manager with no engine has no scheduler and no running
        /// scripts (a test of the database alone); it captures them itself.
        /// </summary>
        private void RequestCapture()
        {
            lock (m_Lock) if (m_Dirty.Count == 0) return;
            if (m_Engine == null) { CaptureDirty(self: true); return; }
            if (m_CaptureRequested) return;
            m_CaptureRequested = true;
            m_RequestCapture?.Invoke();
        }

        /// <summary>
        /// The batch in one transaction; true when it committed. A write that fails ends the transaction, which rolls back:
        /// a busy or locked database counts an attempt against every write in the batch, any other failure only against
        /// the write that failed.
        /// </summary>
        private bool WriteBatch(List<WriteOp> batch)
        {
            WriteOp current = null;
            try
            {
                lock (s_WriterLock)
                {
                    var conn = Writer();
                    using var tx = conn.BeginTransaction();
                    foreach (var op in batch)
                    {
                        current = op;
                        if (FailWriteForTest != null && FailWriteForTest(op.ItemId))
                            throw new SQLiteException(SQLiteErrorCode.IoErr, "write failed (test)");
                        Apply(conn, op);
                    }
                    current = null;
                    tx.Commit();
                }
                return true;
            }
            catch (Exception e)
            {
                // SQLITE_BUSY (5) and SQLITE_LOCKED (6), with their extended codes.
                bool busy = e is SQLiteException se && ((int)se.ResultCode & 0xFF) is 5 or 6;
                if (current == null || busy) foreach (var op in batch) op.Attempts++;
                else current.Attempts++;
                Interlocked.Increment(ref FlushFailures);
                LastFlushError = e.Message;
                m_log.LogWarning("[PhloxState]: A batch of {0} state writes did not commit{1}; none of it is applied and it is tried again: {2}",
                    batch.Count, current == null ? "" : " at the " + current.Kind + " of " + current.ItemId, e.Message);
                return false;
            }
        }

        private static void Apply(SQLiteConnection conn, WriteOp op)
        {
            using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("@id", op.ItemId.ToString());
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            switch (op.Kind)
            {
                case WriteKind.Save:
                    cmd.CommandText =
                        @"INSERT INTO script_state (item_id, asset_id, state_data, saved_at)
                          VALUES (@id, @assetid, @data, @ts)
                          ON CONFLICT(item_id) DO UPDATE SET
                              asset_id   = excluded.asset_id,
                              state_data = excluded.state_data,
                              saved_at   = excluded.saved_at";
                    cmd.Parameters.AddWithValue("@assetid", op.AssetId.ToString());
                    cmd.Parameters.AddWithValue("@data", op.Blob);
                    break;
                case WriteKind.Delete:
                    cmd.CommandText = "DELETE FROM script_state WHERE item_id = @id";
                    break;
                case WriteKind.MoveAside:
                    cmd.CommandText =
                        @"INSERT INTO script_state_rejected (item_id, asset_id, state_data, saved_at, rejected_at, reason)
                              SELECT item_id, asset_id, state_data, saved_at, @ts, @reason FROM script_state WHERE item_id = @id;
                          DELETE FROM script_state WHERE item_id = @id";
                    cmd.Parameters.AddWithValue("@reason", op.Reason ?? string.Empty);
                    break;
                case WriteKind.Touch:
                    cmd.CommandText = "UPDATE script_state SET saved_at = @ts WHERE item_id = @id";
                    break;
            }
            cmd.ExecuteNonQuery();
        }

        private void NoteLive(UUID itemId)
        {
            lock (m_Lock) m_Live.Add(itemId);
            lock (s_LiveLock) s_Live[itemId] = this;
        }

        /// <summary>
        /// The script is no longer loaded in this manager's engine. False when another manager's engine has loaded it
        /// since, which keeps it on the list.
        /// </summary>
        private bool ForgetLive(UUID itemId)
        {
            lock (s_LiveLock)
            {
                if (s_Live.TryGetValue(itemId, out StateManager owner) && owner != this) return false;
                s_Live.Remove(itemId);
                return true;
            }
        }

        private void MaybePurge()
        {
            if (StateRowMaxAgeDays <= 0 || Environment.TickCount64 < m_NextPurgeTick) return;
            m_NextPurgeTick = Environment.TickCount64 + PURGE_INTERVAL_MS;
            PurgeOldRows(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - StateRowMaxAgeDays * 86400L);
        }

        /// <summary>
        /// Refresh the rows of this engine's loaded scripts, then delete every row saved before the cutoff whose script
        /// is not loaded anywhere in this simulator: an object taken, derezzed or crossed away and never back. Rows of
        /// scripts loaded here are refreshed when they load and at every purge, so an idle script is never taken for one.
        /// </summary>
        internal int PurgeOldRows(long cutoffUnixSeconds)
        {
            List<UUID> mine;
            lock (m_Lock) mine = new List<UUID>(m_Live);
            foreach (var id in mine) Queue(new WriteOp { Kind = WriteKind.Touch, ItemId = id });
            DrainWrites();

            var old = new List<string>();
            int purged = 0;
            try
            {
                lock (s_WriterLock)
                {
                    var conn = Writer();
                    using (var sel = conn.CreateCommand())
                    {
                        sel.CommandText = "SELECT item_id FROM script_state WHERE saved_at < @cut";
                        sel.Parameters.AddWithValue("@cut", cutoffUnixSeconds);
                        using var r = sel.ExecuteReader();
                        while (r.Read()) old.Add(r.GetString(0));
                    }
                    using var tx = conn.BeginTransaction();
                    foreach (string id in old)
                    {
                        if (UUID.TryParse(id, out UUID item))
                            lock (s_LiveLock) if (s_Live.ContainsKey(item)) continue;
                        using var del = conn.CreateCommand();
                        del.CommandText = "DELETE FROM script_state WHERE item_id = @id";
                        del.Parameters.AddWithValue("@id", id);
                        purged += del.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxState]: Purge of old state rows failed: {0}", e.Message);
                return 0;
            }
            Interlocked.Add(ref RowsPurged, purged);
            if (purged > 0)
                m_log.LogInformation("[PhloxState]: Purged {0} state rows not saved or loaded for {1} days", purged, StateRowMaxAgeDays);
            return purged;
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
                    // Rows that could not be restored, moved here instead of being overwritten by a fresh start.
                    using var rej = conn.CreateCommand();
                    rej.CommandText =
                        @"CREATE TABLE IF NOT EXISTS script_state_rejected (
                            item_id     TEXT    NOT NULL,
                            asset_id    TEXT    NOT NULL DEFAULT '',
                            state_data  BLOB,
                            saved_at    INTEGER,
                            rejected_at INTEGER NOT NULL,
                            reason      TEXT    NOT NULL DEFAULT ''
                        )";
                    rej.ExecuteNonQuery();
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
            string extra = m_ShortTimeout ? ";DefaultTimeout=1" : "";   // a test's short wait: statement retries end within a second
            var conn = new SQLiteConnection($"Data Source={m_DbFile};BusyTimeout={m_BusyTimeoutMs}{extra}");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={m_BusyTimeoutMs}";   // (b) every connection waits instead of throwing
            pragma.ExecuteNonQuery();
            return conn;
        }
    }
}
