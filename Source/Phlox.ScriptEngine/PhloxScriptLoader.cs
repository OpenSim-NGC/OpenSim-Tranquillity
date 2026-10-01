/*
 * Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon ScriptLoader.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 by Legion Builds for OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Serialization;
using ProtoBuf;

using Microsoft.Extensions.Logging;

namespace Phlox.ScriptEngine
{
    internal class PhloxScriptLoader
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string CACHE_DIR = "ScriptEngines/Phlox/bytecode";
        private const string SCRIPT_EXT = ".plx";
        private const int CACHE_PREFIX_LEN = 2;
        private const int MAX_UNLOADED_CACHE = 128;

        // Bump this any time SerializedScript / SerializedLSLPrimitive schema changes.
        // Old .plx files with a lower version stamp are rejected and recompiled.
        // History:
        //   1 — original Halcyon schema (Vector3/Quaternion as strings)
        //   2 — SerializedVector3 / SerializedQuaternion wrapper classes (protobuf-net 3.x)
        //   3 — compiler correctness fixes (assignments in expressions, +/- and && / || / | & ^
        //       chains, typed constants, statement promotions): bytecode from an earlier compiler
        //       computes the wrong values, so every cached script is recompiled once.
        //   4 — the 17 IW_POWER_* constants for group-power bits 31-48 load as minus their bit
        //       number (they overflowed to -1); cached bytecode still carries -1. A recompile keeps each
        //       script's saved state (SerializedRuntimeState.ToRuntimeStateFor).
        //   5 — one recompile for three changes: <<= and >>=, SL's Experience key-value form (610-616 return a
        //       request key; the answer arrives in dataserver), and exact float literals (more than 7 significant
        //       digits were rounded). State kept as in 4.
        //   6 — no format change. Earlier versions could store one save's bytecode in the cache file of another save's
        //       asset (a script saved again while its previous save was still loading), so a restart ran the wrong code.
        //       The loader no longer does; this bump purges such entries once. Every script recompiles once from its own
        //       source at the first start; saved state is kept as in 4.
        //   7 — one recompile for two compiler changes that alter the bytecode of some valid scripts: a string literal
        //       keeps its non-ASCII characters after a literal ending in an escaped backslash or a comment holding a
        //       quote (the paste clean-up lost track of strings there), and a name used before a local of that name is
        //       declared, or inside its own initialiser, means the parameter or global of that name (it read the unset
        //       local). Every script recompiles once from its own source at the first start; saved state is kept as in 4.
        private const int CACHE_SCHEMA_VERSION = 7;
        private const string VERSION_FILE_NAME = ".schema_version";

        // CACHE_DIR and its stamp, unless the engine was given another folder (a test seam; production never
        // sets one, so these are the constants above).
        private readonly string m_CacheDir;
        private readonly string m_VersionFile;

        private readonly IAssetService m_AssetService;
        private readonly PhloxExecutionScheduler m_ExeScheduler;
        private readonly WorkArrivedDelegate m_WorkArrived;
        private readonly PhloxEngine m_Engine;

        // Scripts loaded by asset UUID → compiled script + refcount
        private readonly Dictionary<UUID, LoadedScript> m_LoadedScripts = new();

        // Recently unloaded scripts — avoids recompile on rapid rez/derez
        private readonly Dictionary<UUID, CompiledScript> m_UnloadedCache = new();
        private readonly LinkedList<UUID> m_UnloadedCacheOrder = new();

        // Outstanding load requests (from outside thread, must lock)
        private readonly LinkedList<PhloxLoadRequest> m_PendingLoads = new();

        // Outstanding unload requests
        private readonly LinkedList<PhloxUnloadRequest> m_PendingUnloads = new();

        // Scripts waiting for asset server response (keyed by asset UUID)
        private readonly Dictionary<UUID, List<PhloxLoadRequest>> m_WaitingForAsset = new();

        // Scripts whose asset arrived and need compilation
        private readonly Queue<PendingCompile> m_WaitingForCompile = new();

        private readonly object m_AssetLock = new();

        // ── Compiles run on ONE long-lived "Phlox compile" thread, never on the master scheduler ──
        // DoWork hands a CompileJob to the thread and returns; the thread posts the finished job to
        // m_FinishedCompiles and wakes the scheduler; a later DoWork starts it. Everything below except the two
        // queues is touched only on the master scheduler thread (DoWork), as the rest of the loader always was.
        private readonly System.Collections.Concurrent.BlockingCollection<CompileJob> m_CompileQueue = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<CompileJob> m_FinishedCompiles = new();
        private readonly System.Threading.Thread m_CompileThread;
        private volatile bool m_Stopped;
        // The text-compile job in flight per asset: a second load of the same asset joins it instead of compiling again.
        private readonly Dictionary<UUID, CompileJob> m_InFlight = new();
        // Each load and each unload of an item bumps its generation; a finished compile starts only the requests
        // whose generation is still current (a re-save or a removal while compiling discards the stale result).
        private readonly Dictionary<UUID, long> m_ItemGeneration = new();
        // Start order within a prim: while a prim has a compile outstanding, its later loads wait here, in order.
        private readonly Dictionary<uint, int> m_PrimBlocks = new();
        private readonly Dictionary<uint, List<PhloxLoadRequest>> m_DeferredByPrim = new();
        // llSetScriptState / reset aimed at an item that is still loading: applied when it starts.
        private readonly Dictionary<UUID, PendingScriptOps> m_PendingOps = new();

        private sealed class CompileJob
        {
            public UUID AssetId;
            public string ScriptText;
            public bool FromAssetServer;
            public readonly List<PhloxLoadRequest> Requests = new();   // master thread only
            // Set by the compile thread before the job is posted back:
            public CompiledScript Compiled;
            public List<string> Errors = new();
            public Exception Failure;
            public long ElapsedMs;
        }

        private sealed class PendingScriptOps
        {
            public bool? Enable;
            public bool Reset;
        }

        private class LoadedScript
        {
            public CompiledScript Script;
            public int RefCount;
        }

        private class PendingCompile
        {
            public UUID AssetId;
            public string ScriptText;
            public List<PhloxLoadRequest> Requests;
        }

        public PhloxScriptLoader(IAssetService assetService, PhloxExecutionScheduler exeScheduler,
            WorkArrivedDelegate workArrived, PhloxEngine engine)
        {
            m_AssetService = assetService;
            m_ExeScheduler = exeScheduler;
            m_WorkArrived = workArrived;
            m_Engine = engine;
            m_CacheDir = engine?.BytecodeCacheDir ?? CACHE_DIR;
            m_VersionFile = Path.Combine(m_CacheDir, VERSION_FILE_NAME);

            Directory.CreateDirectory(m_CacheDir);
            EnsureCacheSchemaVersion();

            m_CompileThread = new System.Threading.Thread(CompileLoop, CompileStackSize)
            { IsBackground = true, Name = "Phlox compile" };
            m_CompileThread.Start();
        }

        // Every region's engine runs the stamp check when it starts, and on a fresh install several regions
        // start at once. One process-wide lock makes them take turns, so one purges and writes the stamp and the rest
        // read it. Held across folders too; the check is a stat, a small read and, once, a purge.
        private static readonly object s_SchemaCheckLock = new object();

        /// <summary>Test observation: this loader found the stamp old or missing and purged the cache.</summary>
        internal bool PurgedCache { get; private set; }

        /// <summary>
        /// If the on-disk cache was written with an older schema version, wipe it
        /// so stale .plx files don't cause null-ref crashes on deserialize.
        /// The whole check (read, compare, purge, write) is guarded and runs one engine at a time. Any failure,
        /// a stamp that cannot be read included, is logged and the engine starts, as a failed purge always was.
        /// </summary>
        private void EnsureCacheSchemaVersion()
        {
            lock (s_SchemaCheckLock)
            {
                try
                {
                    int diskVersion = 0;
                    if (File.Exists(m_VersionFile))
                    {
                        if (!int.TryParse(File.ReadAllText(m_VersionFile).Trim(), out diskVersion))
                            diskVersion = 0;
                    }

                    if (diskVersion < CACHE_SCHEMA_VERSION)
                    {
                        m_log.LogWarning(
                            "[PhloxLoader]: Cache schema version on disk ({0}) is older than current ({1}). " +
                            "Purging stale bytecode cache so scripts recompile cleanly.",
                            diskVersion, CACHE_SCHEMA_VERSION);
                        PurgedCache = true;

                        // Delete all .plx files; leave the directory structure.
                        foreach (string plx in Directory.GetFiles(m_CacheDir, "*.plx", SearchOption.AllDirectories))
                            File.Delete(plx);

                        File.WriteAllText(m_VersionFile, CACHE_SCHEMA_VERSION.ToString());
                        m_log.LogInformation("[PhloxLoader]: Bytecode cache purged and version stamp updated.");
                    }
                }
                catch (Exception ex)
                {
                    m_log.LogError("[PhloxLoader]: Failed to check or purge cache: {0}", ex.Message);
                }
            }
        }

        public void PostLoadRequest(PhloxLoadRequest req)
        {
            lock (m_Outcomes)
            {
                req.Serial = ++m_SerialCounter;
                m_LatestSerial[req.ItemID] = req.Serial;
            }
            // The region rezzes and then asks for the errors on the same thread (CreateScriptInstanceEr): that editor
            // waits for this load, not for one a later save posted in between.
            t_PostedOnThisThread = (req.ItemID, req.Serial);
            lock (m_PendingLoads)
                m_PendingLoads.AddLast(req);
            m_WorkArrived();
        }

        public void PostUnloadRequest(uint localID, UUID itemID)
        {
            lock (m_PendingUnloads)
                m_PendingUnloads.AddLast(new PhloxUnloadRequest { LocalID = localID, ItemID = itemID });
            m_WorkArrived();
        }

        /// <summary>
        /// The first-line rule gives this item to another engine. Now, on the caller's thread (the editor's
        /// GetScriptErrors follows on it): forget its load record, so no query answers with an earlier Phlox outcome,
        /// and drop its loads still queued. Then on the load worker, after any unload posted before this: unload a Phlox
        /// instance if there is one, with its normal save. The item's state row is kept, as YEngine keeps its own state
        /// file for a script it declines (XMREngine.OnRezScript): if the script comes back to Phlox with the same asset it
        /// resumes from that row, and an edited script has a new asset, so LoadState does not restore the row.
        /// </summary>
        internal void Disown(uint localID, UUID itemID)
        {
            lock (m_Outcomes)
            {
                m_LatestSerial.Remove(itemID);
                m_Outcomes.Remove(itemID);
                m_EarlierOutcomes.Remove(itemID);
                m_UnclaimedFailures.Remove(itemID);
                System.Threading.Monitor.PulseAll(m_Outcomes);
            }
            lock (m_PendingOps) m_PendingOps.Remove(itemID);
            lock (m_PendingLoads)
            {
                for (var n = m_PendingLoads.First; n != null;)
                {
                    var next = n.Next;
                    if (n.Value.ItemID == itemID) m_PendingLoads.Remove(n);
                    n = next;
                }
            }
            lock (m_PendingUnloads)
                m_PendingUnloads.AddLast(new PhloxUnloadRequest { LocalID = localID, ItemID = itemID });
            m_WorkArrived();
        }

        /// <summary>Is the item's latest load still wanted (false once the item was disowned)?</summary>
        private bool IsOwned(UUID itemID)
        {
            lock (m_Outcomes) return m_LatestSerial.ContainsKey(itemID);
        }

        public WorkStatus DoWork()
        {
            bool didWork = false;

            // Top-level backstop: the load worker must survive anything a single request throws.
            // PerformLoad already guards per-request; this covers the unload/compile paths too, so
            // one failure can never stop the worker from draining the queue (which would strand the
            // RegionReady LoginLock signal). Per-failure logging only — no summary/barrier machinery.
            if (m_Stopped) return new WorkStatus { WorkWasDone = false, WorkIsPending = false, NextWakeUpTime = ulong.MaxValue };
            try
            {
                didWork |= ProcessNextUnload();
                didWork |= ProcessNextLoad();
                didWork |= ProcessNextCompile();
                didWork |= ProcessFinishedCompiles();
            }
            catch (Exception ex)
            {
                m_log.LogError(ex, "[PhloxLoader]: unhandled exception in DoWork — swallowed to keep the load worker alive");
            }

            return new WorkStatus
            {
                WorkWasDone = didWork,
                WorkIsPending = HasPendingWork(),
                NextWakeUpTime = ulong.MaxValue
            };
        }

        private bool HasPendingWork()
        {
            lock (m_PendingLoads)
                if (m_PendingLoads.Count > 0) return true;
            lock (m_PendingUnloads)
                if (m_PendingUnloads.Count > 0) return true;
            lock (m_AssetLock)
                if (m_WaitingForCompile.Count > 0) return true;
            if (!m_FinishedCompiles.IsEmpty) return true;
            return false;
        }

        private bool ProcessNextUnload()
        {
            PhloxUnloadRequest req;
            lock (m_PendingUnloads)
            {
                if (m_PendingUnloads.Count == 0) return false;
                req = m_PendingUnloads.First.Value;
                m_PendingUnloads.RemoveFirst();
            }
            PerformUnload(req);
            return true;
        }

        private void PerformUnload(PhloxUnloadRequest req)
        {
            // A compile of this item still running is now stale.
            BumpGeneration(req.ItemID);
            lock (m_PendingOps) m_PendingOps.Remove(req.ItemID);
            m_ExeScheduler.DropDeferred(req.ItemID);   // Events held for a load that is now cancelled
            Interpreter script = m_ExeScheduler.FindScript(req.ItemID);
            if (script != null)
                UnloadScript(req, script);
        }

        private void UnloadScript(PhloxUnloadRequest req, Interpreter script)
        {
            m_ExeScheduler.DoUnload(req.ItemID);

            LoadedScript ls;
            if (m_LoadedScripts.TryGetValue(script.Script.AssetId, out ls))
            {
                if (--ls.RefCount <= 0)
                {
                    m_LoadedScripts.Remove(script.Script.AssetId);
                    AddToUnloadedCache(script.Script.AssetId, script.Script);
                }
            }
        }

        private bool ProcessNextLoad()
        {
            PhloxLoadRequest req;
            lock (m_PendingLoads)
            {
                if (m_PendingLoads.Count == 0) return false;
                req = m_PendingLoads.First.Value;
                m_PendingLoads.RemoveFirst();
            }
            // A prim with a compile outstanding starts its scripts in rez order - later loads wait.
            if (req.Prim != null && m_PrimBlocks.ContainsKey(req.Prim.LocalId))
            {
                if (!m_DeferredByPrim.TryGetValue(req.Prim.LocalId, out var list))
                    m_DeferredByPrim[req.Prim.LocalId] = list = new List<PhloxLoadRequest>();
                list.Add(req);
                return true;
            }
            PerformLoad(req);
            return true;
        }

        private void PerformLoad(PhloxLoadRequest req)
        {
            // Per-request guard: a throw from any start path (notably the previously-unguarded
            // BeginScriptRun in TryStartSharedScript / TryStartFromUnloadedCache) must not escape
            // and kill the load worker or abort the rest of the boot rez batch. One bad script
            // fails alone, with a full diagnostic; every other script still loads.
            try
            {
                // A load that waited behind its prim's compile while the item went to another engine.
                if (!IsOwned(req.ItemID)) return;

                // An instance of this item on another asset is still here: an earlier load started in the moment between a
                // save's unload and its new asset. The latest load replaces it, as that save's unload would have.
                if (req.AssetId != UUID.Zero && IsLatest(req))
                {
                    Interpreter leftover = m_ExeScheduler.FindScript(req.ItemID);
                    if (leftover != null && leftover.Script.AssetId != req.AssetId)
                        UnloadScript(new PhloxUnloadRequest { LocalID = req.LocalID, ItemID = req.ItemID }, leftover);
                }
                req.Generation = BumpGeneration(req.ItemID);

                // The asset this load's text came from. Not the item's asset now: a save while this load waited has
                // already given the item its new asset, and that asset's text is the newer load's.
                UUID assetId = req.AssetId != UUID.Zero ? req.AssetId : FindAssetId(req);
                if (assetId == UUID.Zero) { PublishOutcome(req, new List<string> { "script item not found" }); return; }

                // A later save of the item is already posted: this load must not start. Compiled already, it has no
                // errors to report; otherwise it still compiles below, for its own editor, and never starts.
                if (!IsLatest(req) && IsCompiled(assetId))
                {
                    m_log.LogInformation("[PhloxLoader]: Not starting {0} for item {1}: the item was saved again", assetId, req.ItemID);
                    PublishOutcome(req, new List<string>());
                    return;
                }

                // 1. Already loaded and running (shared script)
                if (TryStartSharedScript(assetId, req)) { Started(req); return; }

                // 2. Recently unloaded — still in memory
                if (TryStartFromUnloadedCache(assetId, req)) { Started(req); return; }

                // 3. Compiled bytecode on disk
                if (TryStartFromDiskCache(assetId, req)) { Started(req); return; }

                // 4. Need to compile from source text (already in req.ScriptText from OnRezScript).
                // Handed to the compile thread; a later DoWork starts it.
                if (!string.IsNullOrEmpty(req.ScriptText))
                {
                    SubmitTextCompile(assetId, req);
                    return;
                }

                // 5. Fallback: fetch from asset server
                SubmitAssetRequest(assetId, req);
            }
            catch (Exception ex)
            {
                LogLoadFailure(req, ex);
                PublishOutcome(req, new List<string> { "script load failed: " + ex.Message });   // Never leave an editor waiting
            }
        }

        // Per-failure diagnostic for a load that threw: item name + UUID, prim name + localID, and the
        // full exception with stack. Deliberately defensive — resolving the names must never itself throw.
        private void LogLoadFailure(PhloxLoadRequest req, Exception ex)
        {
            string itemName = "?";
            string primName = "?";
            uint localId = 0;
            UUID itemId = (req != null) ? req.ItemID : UUID.Zero;
            try
            {
                if (req != null && req.Prim != null)
                {
                    primName = req.Prim.Name;
                    localId = req.Prim.LocalId;
                    if (req.Prim.Inventory != null)
                    {
                        TaskInventoryItem item = req.Prim.Inventory.GetInventoryItem(req.ItemID);
                        if (item != null) itemName = item.Name;
                    }
                }
            }
            catch { /* diagnostics must not mask the original failure */ }

            m_log.LogError(ex, string.Format(
                "[PhloxLoader]: Script load FAILED — item '{0}' ({1}) in prim '{2}' (localID {3}). This script did not start; the rest of the batch continues.",
                itemName, itemId, primName, localId));
        }

        private UUID FindAssetId(PhloxLoadRequest req)
        {
            if (req.Prim == null)
            {
                m_log.LogError("[PhloxLoader]: Prim is null for item {0}", req.ItemID);
                return UUID.Zero;
            }

            TaskInventoryItem item = req.Prim.Inventory.GetInventoryItem(req.ItemID);
            if (item == null)
            {
                m_log.LogError("[PhloxLoader]: Inventory item {0} not found in prim {1}",
                    req.ItemID, req.Prim.Name);
                return UUID.Zero;
            }
            return item.AssetID;
        }

        private bool TryStartSharedScript(UUID assetId, PhloxLoadRequest req)
        {
            LoadedScript ls;
            if (!m_LoadedScripts.TryGetValue(assetId, out ls)) return false;

            ls.RefCount++;
            m_log.LogInformation("[PhloxLoader]: Starting shared script {0} item {1}", assetId, req.ItemID);
            BeginScriptRun(req, ls.Script);
            return true;
        }

        private bool TryStartFromUnloadedCache(UUID assetId, PhloxLoadRequest req)
        {
            CompiledScript script;
            if (!m_UnloadedCache.TryGetValue(assetId, out script)) return false;

            m_UnloadedCache.Remove(assetId);
            m_UnloadedCacheOrder.Remove(assetId);   // O(n) but cache is small

            m_log.LogInformation("[PhloxLoader]: Starting from unloaded cache {0} item {1}", assetId, req.ItemID);
            BeginScriptRun(req, script);
            m_LoadedScripts[assetId] = new LoadedScript { Script = script, RefCount = 1 };
            return true;
        }

        private bool TryStartFromDiskCache(UUID assetId, PhloxLoadRequest req)
        {
            string path = GetCachePath(assetId);
            if (!File.Exists(path)) return false;

            m_log.LogDebug("[PhloxLoader]: Attempting disk cache load for {0}", assetId);

            try
            {
                // ── Step 1: deserialize the protobuf blob ──────────────────────
                SerializedScript ser;
                m_log.LogDebug("[PhloxLoader]: Opening cache file {0}", path);
                using (var f = File.OpenRead(path))
                    ser = Serializer.Deserialize<SerializedScript>(f);

                if (ser == null)
                {
                    m_log.LogWarning("[PhloxLoader]: Deserializer returned null for {0} — purging", assetId);
                    SafeDeleteCache(path);
                    return false;
                }

                // ── Step 2: convert to runtime types ──────────────────────────
                m_log.LogDebug("[PhloxLoader]: Converting SerializedScript to CompiledScript for {0}", assetId);
                CompiledScript compiled = ser.ToCompiledScript();

                if (compiled == null)
                {
                    m_log.LogWarning("[PhloxLoader]: ToCompiledScript() returned null for {0} — purging", assetId);
                    SafeDeleteCache(path);
                    return false;
                }

                compiled.AssetId = assetId;

                // ── Step 3: hand off to scheduler ─────────────────────────────
                m_log.LogInformation("[PhloxLoader]: Starting from disk cache {0} item {1}", assetId, req.ItemID);
                BeginScriptRun(req, compiled);
                m_LoadedScripts[assetId] = new LoadedScript { Script = compiled, RefCount = 1 };
                return true;
            }
            catch (Exception e)
            {
                // Log the FULL exception (type + stack) so we can pinpoint the null-ref field
                m_log.LogError(
                    "[PhloxLoader]: Failed to load cached script {0}: [{1}] {2}\n{3}",
                    assetId, e.GetType().Name, e.Message, e.StackTrace);

                // Purge the corrupt/incompatible cache entry so we fall through to recompile
                SafeDeleteCache(path);
                return false;
            }
        }

        /// <summary>
        /// Tell the owner once, with the script's name, that their script did not compile.
        /// The log line above stays exactly as it was - this is in addition to it, not instead.
        /// </summary>
        private static void ReportCompileFailureToOwner(PhloxLoadRequest req, IReadOnlyList<string> errors)
        {
            if (req?.Prim is null || errors is null || errors.Count == 0) return;
            string scriptName = req.Prim.Inventory?.GetInventoryItem(req.ItemID)?.Name;
            PhloxCompileErrorReport.ToOwner(req.Prim, scriptName, errors);
        }

        private static void SafeDeleteCache(string path)
        {
            try { File.Delete(path); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// The stack a compile runs on. Both front ends recurse once per level of the
        /// script's nesting. The limit is COUNTED (InWorldz.Phlox.Compiler.NestingLimits -
        /// 1,000 expression levels, 500 blocks, 2,500 else-if branches, 64 chained assignments), the same
        /// in a cold or a warm process. The stack guard (DepthGuard) is only the backstop: how many levels
        /// 16 MB holds depends on JIT warm-up (~2,410 expression levels cold in the lowest pass, ~7,668
        /// warm), which is why an earlier "about 4,000" was never a real limit.
        /// </summary>
        internal const int CompileStackSize = 16 * 1024 * 1024;

        /// <summary>Tests only: milliseconds a compile of this script text should take extra (0 = none).</summary>
        internal static Func<string, int> CompileDelayForTest;

        /// <summary>Tests only: called on the compile thread with the script text just before this loader compiles it,
        /// so a test can hold one compile while it saves again. Per loader, so classes using it still run in parallel.</summary>
        internal Action<string> BeforeCompileForTest;

        /// <summary>
        /// Compile on a fresh thread with <see cref="CompileStackSize"/> and wait for it. The
        /// loader no longer uses this - DoWork never waits on a compile (<see cref="CompileLoop"/>); it stays for
        /// callers that want one compile on the loader's stack size, synchronously (RobustnessTests).
        /// </summary>
        internal static CompiledScript CompileOnCompilerThread(CompilerFrontend frontend, string scriptText)
        {
            CompiledScript result = null;
            Exception failure = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    result = CompileByLanguage(frontend, scriptText);
                }
                catch (Exception e) { failure = e; }
            }, CompileStackSize)
            { IsBackground = true, Name = "Phlox compile (sync)" };
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        /// <summary>
        /// Compile the script as LSL or SLua. A first line naming Phlox ("//InWorldz.Phlox:&lt;language&gt;", the header that
        /// gives a script to Phlox on any region, <see cref="PhloxEngineHeader"/>) decides by its language part, read as
        /// YEngine reads its own: "" or "lsl" is LSL; "slua" is SLua, compiled with the header line blanked (its newline
        /// kept) so error line numbers match the source; anything else is a compile error on line 1. The language part
        /// is lowercased first, as YEngine does, so "SLua" and "SLUA" are SLua too. Without that header, the source decides
        /// (<see cref="InWorldz.Phlox.SLua.SLuaCompiler.IsLuaScript"/>).
        /// </summary>
        internal static CompiledScript CompileByLanguage(CompilerFrontend frontend, string scriptText)
        {
            if (PhloxEngineHeader.NamedEngine(scriptText) == PhloxEngineHeader.PhloxName)
            {
                string language = PhloxEngineHeader.Language(scriptText);
                if (language == "slua")
                    return frontend.CompileLua(scriptText[scriptText.IndexOf('\n')..]);
                if (language.Length > 0 && language != "lsl")
                {
                    string header = scriptText[..scriptText.IndexOf('\n')].TrimEnd('\r');
                    frontend.Listener?.Error($"line 1:0 the first line {header} names the language \"{language}\"; " +
                                             "Phlox runs lsl or slua");
                    return null;
                }
                return frontend.Compile(scriptText);
            }
            return InWorldz.Phlox.SLua.SLuaCompiler.IsLuaScript(scriptText)
                ? frontend.CompileLua(scriptText)
                : frontend.Compile(scriptText);
        }

        /// <summary>Is the compile thread still running? (False after <see cref="Stop"/> once it has finished.)</summary>
        internal bool CompileThreadAlive => m_CompileThread != null && m_CompileThread.IsAlive;

        /// <summary>
        /// The compile thread. It compiles one job at a time on its <see cref="CompileStackSize"/> stack,
        /// saves the bytecode to the disk cache and posts the job back; it never touches the scheduler or a script.
        /// </summary>
        private void CompileLoop()
        {
            try
            {
                foreach (CompileJob job in m_CompileQueue.GetConsumingEnumerable())
                {
                    if (m_Stopped) break;
                    RunCompile(job);
                    if (m_Stopped) break;
                    m_FinishedCompiles.Enqueue(job);
                    m_WorkArrived();
                }
            }
            catch (Exception e) { m_log.LogError(e, "[PhloxLoader]: compile thread stopped by an exception"); }
        }

        private void RunCompile(CompileJob job)
        {
            var listener = new LogOutputListener(job.Requests.Count > 0 ? job.Requests[0].ItemID : UUID.Zero);
            var frontend = new CompilerFrontend(listener, ".");
            var sw = Stopwatch.StartNew();
            try
            {
                BeforeCompileForTest?.Invoke(job.ScriptText);
                int delay = CompileDelayForTest?.Invoke(job.ScriptText) ?? 0;
                if (delay > 0) System.Threading.Thread.Sleep(delay);
                job.Compiled = CompileByLanguage(frontend, job.ScriptText);
                if (job.Compiled != null)
                {
                    job.Compiled.AssetId = job.AssetId;
                    SaveToDiskCache(job.Compiled);
                }
            }
            catch (Exception e) { job.Failure = e; job.Compiled = null; }
            sw.Stop();
            job.ElapsedMs = sw.ElapsedMilliseconds;
            job.Errors = new List<string>(listener.Errors);
        }

        /// <summary>Compile this script's text off the scheduler, joining a compile of the same asset if one is running.</summary>
        private void SubmitTextCompile(UUID assetId, PhloxLoadRequest req)
        {
            BlockPrim(req);
            if (m_InFlight.TryGetValue(assetId, out CompileJob job))
            {
                job.Requests.Add(req);
                return;
            }
            job = new CompileJob { AssetId = assetId, ScriptText = req.ScriptText };
            job.Requests.Add(req);
            m_InFlight[assetId] = job;
            if (!m_Stopped) m_CompileQueue.Add(job);
        }

        private void BlockPrim(PhloxLoadRequest req)
        {
            if (req.Prim == null) return;
            m_PrimBlocks.TryGetValue(req.Prim.LocalId, out int n);
            m_PrimBlocks[req.Prim.LocalId] = n + 1;
        }

        /// <summary>The prim's outstanding compile is done: its waiting loads go back to the FRONT of the queue, in order.</summary>
        private void UnblockPrim(PhloxLoadRequest req)
        {
            if (req.Prim == null) return;
            uint id = req.Prim.LocalId;
            if (!m_PrimBlocks.TryGetValue(id, out int n)) return;
            if (n > 1) { m_PrimBlocks[id] = n - 1; return; }
            m_PrimBlocks.Remove(id);
            if (!m_DeferredByPrim.TryGetValue(id, out var waiting)) return;
            m_DeferredByPrim.Remove(id);
            lock (m_PendingLoads)
                for (int k = waiting.Count - 1; k >= 0; k--)
                    m_PendingLoads.AddFirst(waiting[k]);
        }

        private long BumpGeneration(UUID itemId)
        {
            m_ItemGeneration.TryGetValue(itemId, out long g);
            m_ItemGeneration[itemId] = ++g;
            return g;
        }

        private bool IsCurrent(PhloxLoadRequest req)
            => m_ItemGeneration.TryGetValue(req.ItemID, out long g) && g == req.Generation && IsLatest(req);

        /// <summary>
        /// Is this the item's latest load: no later load posted, not disowned, and the item still at this load's asset? A
        /// save gives the item its new asset before it posts the new load, so a load whose asset the item no longer has
        /// is superseded even while the new load is still on its way.
        /// </summary>
        private bool IsLatest(PhloxLoadRequest req)
        {
            lock (m_Outcomes)
                if (!m_LatestSerial.TryGetValue(req.ItemID, out long s) || s != req.Serial) return false;
            return req.AssetId == UUID.Zero || req.Prim?.Inventory.GetInventoryItem(req.ItemID)?.AssetID == req.AssetId;
        }

        /// <summary>Is there bytecode for this asset, loaded, recently unloaded or on disk?</summary>
        private bool IsCompiled(UUID assetId)
            => m_LoadedScripts.ContainsKey(assetId) || m_UnloadedCache.ContainsKey(assetId) || File.Exists(GetCachePath(assetId));

        /// <summary>Start the finished compiles (master scheduler thread).</summary>
        private bool ProcessFinishedCompiles()
        {
            bool any = false;
            while (m_FinishedCompiles.TryDequeue(out CompileJob job))
            {
                any = true;
                if (m_InFlight.TryGetValue(job.AssetId, out var current) && current == job) m_InFlight.Remove(job.AssetId);
                try { FinishJob(job); }
                catch (Exception e) { m_log.LogError(e, "[PhloxLoader]: starting compiled {0} failed", job.AssetId); }
                finally
                {
                    if (!job.FromAssetServer)
                        foreach (var req in job.Requests) UnblockPrim(req);
                }
            }
            return any;
        }

        private void FinishJob(CompileJob job)
        {
            var live = job.Requests.Where(IsCurrent).ToList();
            foreach (var stale in job.Requests.Where(r => !IsCurrent(r)))
            {
                m_log.LogInformation("[PhloxLoader]: Discarding stale compile of {0} for item {1} (re-saved or removed while compiling)", job.AssetId, stale.ItemID);
                // Never started, but its own save's editor still gets this compile's result. No owner alert: it never ran.
                if (IsOwned(stale.ItemID))
                    PublishOutcome(stale, job.Compiled != null ? new List<string>() : CompileErrors(job));
            }

            if (job.Compiled == null)
            {
                if (job.Failure != null)
                    m_log.LogError("[PhloxLoader]: Exception compiling {0}: {1}", job.AssetId, job.Failure);
                foreach (var req in live)
                {
                    m_log.LogError(job.FromAssetServer ? "[PhloxLoader]: Compilation failed (from asset server) for {0} item {1}" : "[PhloxLoader]: Compilation failed for {0} item {1}",
                        job.AssetId, req.ItemID);
                    var errors = CompileErrors(job);
                    // An editor save gets its errors in the editor; anything else is told as before.
                    if (!PublishOutcome(req, errors))
                        ReportUnlessClaimed(req, errors);
                }
                return;
            }

            m_log.LogInformation(job.FromAssetServer ? "[PhloxLoader]: Compiled (from asset) {0} ({1}ms)" : "[PhloxLoader]: Compiled {0} ({1}ms)", job.AssetId, job.ElapsedMs);
            int started = 0;
            foreach (var req in live)
            {
                BeginScriptRun(req, job.Compiled);
                started++;
                Started(req);
            }
            if (started > 0)
            {
                if (m_LoadedScripts.TryGetValue(job.AssetId, out var ls)) ls.RefCount += started;
                else m_LoadedScripts[job.AssetId] = new LoadedScript { Script = job.Compiled, RefCount = started };
            }
        }

        private static List<string> CompileErrors(CompileJob job)
            => job.Errors.Count > 0 ? job.Errors
                : new List<string> { job.Failure != null ? "internal compiler error: " + job.Failure.Message : "script failed to compile" };

        /// <summary>A request's script has started: publish success for an editor, then apply any state change made while it loaded.</summary>
        private void Started(PhloxLoadRequest req)
        {
            PublishOutcome(req, new List<string>());
            PendingScriptOps ops;
            lock (m_PendingOps)
            {
                if (!m_PendingOps.TryGetValue(req.ItemID, out ops)) return;
                m_PendingOps.Remove(req.ItemID);
            }
            // Queued on the execution scheduler: its next pass applies them before the new script's first timeslice.
            if (ops.Enable.HasValue) m_ExeScheduler.ChangeEnabledStatus(req.ItemID, ops.Enable.Value);
            if (ops.Reset) m_ExeScheduler.ResetScript(req.ItemID);
        }

        /// <summary>Is a load of this item posted, waiting or compiling (so the scheduler does not have it yet)?</summary>
        internal bool IsLoading(UUID itemId)
        {
            lock (m_Outcomes)
            {
                if (!m_LatestSerial.TryGetValue(itemId, out long serial)) return false;
                return !m_Outcomes.TryGetValue(itemId, out var o) || o.Serial < serial;
            }
        }

        /// <summary>llSetScriptState / the Running checkbox on an item still loading - applied when it starts.</summary>
        internal void NoteScriptState(UUID itemId, bool enable)
        {
            if (!IsLoading(itemId)) return;
            lock (m_PendingOps)
            {
                if (!m_PendingOps.TryGetValue(itemId, out var ops)) m_PendingOps[itemId] = ops = new PendingScriptOps();
                ops.Enable = enable;
            }
        }

        /// <summary>A reset of an item still loading - applied when it starts.</summary>
        internal void NoteReset(UUID itemId)
        {
            if (!IsLoading(itemId)) return;
            lock (m_PendingOps)
            {
                if (!m_PendingOps.TryGetValue(itemId, out var ops)) m_PendingOps[itemId] = ops = new PendingScriptOps();
                ops.Reset = true;
            }
        }

        // ── The result of an item's latest load, for GetScriptErrors (the script editor's Save) ──
        //
        // YEngine's GetScriptErrors (XMREngine.cs:1967) blocks until the compile of the item just rezzed has posted
        // its errors - an empty list for success. The region calls it synchronously from CreateScriptInstanceEr,
        // on the caps thread that answers the viewer's Save, right after OnRezScript posted the load; it is never
        // the scheduler thread. Phlox waits the same way, bounded by the region's own 15 s
        // (SceneObjectPartInventory.CreateScriptInstanceEr) and answering its "timedout waiting for errors".

        /// <summary>How long GetScriptErrors waits for a compile before answering the timeout.</summary>
        internal static TimeSpan ErrorWaitTimeout = TimeSpan.FromSeconds(15);
        internal const string ErrorWaitTimedOut = "timedout waiting for errors";

        private readonly Dictionary<UUID, (long Serial, List<string> Errors)> m_Outcomes = new();
        // Outcomes of an item's earlier loads, newest last, kept only for items loaded more than once, so each save's
        // editor gets its own compile's result. A few per item; guarded by m_Outcomes.
        private readonly Dictionary<UUID, List<(long Serial, List<string> Errors)>> m_EarlierOutcomes = new();
        private const int EarlierOutcomesKept = 4;
        // The item and serial of the load this thread posted last (see PostLoadRequest).
        [ThreadStatic] private static (UUID Item, long Serial) t_PostedOnThisThread;
        private readonly Dictionary<UUID, long> m_LatestSerial = new();
        private readonly Dictionary<UUID, int> m_EditorWaiters = new();
        private long m_SerialCounter;

        /// <summary>
        /// How long a compile failure no editor is waiting for is held before it goes to the owner as a pop-up. The
        /// editor's GetScriptErrors runs on the caps thread right after OnRezScript posts the load, but a compile can
        /// fail first (17 ms, seen in world); the editor then collects the stored outcome, and without
        /// this the owner got the same errors twice - in the editor and as a pop-up.
        /// </summary>
        internal static TimeSpan OwnerAlertGrace = TimeSpan.FromSeconds(2);

        /// <summary>Failures awaiting the owner pop-up, by item: the serial of the load that failed. Guarded by m_Outcomes.</summary>
        private readonly Dictionary<UUID, long> m_UnclaimedFailures = new();

        /// <summary>
        /// Tell the owner about a failed compile unless an editor collects its errors within <see cref="OwnerAlertGrace"/>
        /// (WaitForCompileErrors claims it). Nothing collects a rez or a restart, so those still get the pop-up.
        /// </summary>
        private void ReportUnlessClaimed(PhloxLoadRequest req, List<string> errors)
        {
            lock (m_Outcomes) m_UnclaimedFailures[req.ItemID] = req.Serial;
            Task.Delay(OwnerAlertGrace).ContinueWith(_ =>
            {
                lock (m_Outcomes)
                {
                    if (!m_UnclaimedFailures.TryGetValue(req.ItemID, out long serial) || serial != req.Serial) return;
                    m_UnclaimedFailures.Remove(req.ItemID);
                }
                ReportCompileFailureToOwner(req, errors);
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// Record the outcome of <paramref name="req"/> and wake a waiting editor. Returns true when an editor is
        /// waiting on this item (so the caller does not also send the owner an alert).
        /// </summary>
        private bool PublishOutcome(PhloxLoadRequest req, List<string> errors)
        {
            lock (m_Outcomes)
            {
                bool had = m_Outcomes.TryGetValue(req.ItemID, out var prev);
                if (!had || prev.Serial <= req.Serial)
                {
                    if (had && prev.Serial < req.Serial)
                        KeepEarlier(req.ItemID, prev);
                    m_Outcomes[req.ItemID] = (req.Serial, errors);
                }
                else
                    KeepEarlier(req.ItemID, (req.Serial, errors));
                System.Threading.Monitor.PulseAll(m_Outcomes);
                return m_EditorWaiters.TryGetValue(req.ItemID, out int n) && n > 0;
            }
        }

        /// <summary>
        /// The compile errors of this item's latest load - empty when it compiled and started - waiting
        /// for that load to finish. Null when Phlox has no load of this item (another engine's script).
        /// </summary>
        internal List<string> WaitForCompileErrors(UUID itemId, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            lock (m_Outcomes)
            {
                if (!m_LatestSerial.TryGetValue(itemId, out long wanted)) return null;
                // The load this thread posted, when it posted one for this item (the region's save does), else the latest.
                var posted = t_PostedOnThisThread;
                t_PostedOnThisThread = default;   // One answer per load posted; a later query on this thread is not this save's.
                if (posted.Item == itemId && posted.Serial <= wanted) wanted = posted.Serial;
                m_EditorWaiters.TryGetValue(itemId, out int n);
                m_EditorWaiters[itemId] = n + 1;
                try
                {
                    while (true)
                    {
                        if (!m_LatestSerial.ContainsKey(itemId)) return null;   // Disowned while waiting
                        if (OutcomeFor(itemId, wanted) is { } o)
                        {
                            // The editor has it: no pop-up for the same failure (see ReportUnlessClaimed).
                            if (m_UnclaimedFailures.TryGetValue(itemId, out long failed) && failed <= o.Serial) m_UnclaimedFailures.Remove(itemId);
                            return new List<string>(o.Errors);
                        }
                        if (m_Stopped) return new List<string> { ErrorWaitTimedOut };
                        var left = until - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero) return new List<string> { ErrorWaitTimedOut };
                        System.Threading.Monitor.Wait(m_Outcomes, left);
                    }
                }
                finally
                {
                    if (--m_EditorWaiters[itemId] <= 0) m_EditorWaiters.Remove(itemId);
                }
            }
        }

        /// <summary>
        /// The outcome an editor waiting for load <paramref name="wanted"/> gets: that load's own, or, if its load will not
        /// report (it never reached a compile), the newer load's. Null while neither is in. Under m_Outcomes.
        /// </summary>
        private (long Serial, List<string> Errors)? OutcomeFor(UUID itemId, long wanted)
        {
            bool have = m_Outcomes.TryGetValue(itemId, out var o);
            if (have && o.Serial == wanted) return o;
            if (m_EarlierOutcomes.TryGetValue(itemId, out var earlier))
                foreach (var e in earlier)
                    if (e.Serial == wanted) return e;
            if (have && o.Serial > wanted) return o;
            return null;
        }

        private void KeepEarlier(UUID itemId, (long Serial, List<string> Errors) outcome)
        {
            if (!m_EarlierOutcomes.TryGetValue(itemId, out var list)) m_EarlierOutcomes[itemId] = list = new();
            list.Add(outcome);
            if (list.Count > EarlierOutcomesKept) list.RemoveAt(0);
        }

        private bool ProcessNextCompile()
        {
            PendingCompile pending;
            lock (m_AssetLock)
            {
                if (m_WaitingForCompile.Count == 0) return false;
                pending = m_WaitingForCompile.Dequeue();
            }

            // To the compile thread like every other compile. Asset-server loads never kept rez order
            // (the fetch is asynchronous), so they block no prim.
            var job = new CompileJob { AssetId = pending.AssetId, ScriptText = pending.ScriptText, FromAssetServer = true };
            job.Requests.AddRange(pending.Requests);
            if (!m_Stopped) m_CompileQueue.Add(job);
            return true;
        }

        private void SubmitAssetRequest(UUID assetId, PhloxLoadRequest req)
        {
            lock (m_AssetLock)
            {
                if (m_WaitingForAsset.TryGetValue(assetId, out var waitList))
                {
                    waitList.Add(req);
                    return; // request already in flight
                }
                m_WaitingForAsset[assetId] = new List<PhloxLoadRequest> { req };
            }

            m_AssetService.Get(assetId.ToString(), this, AssetReceived);
        }

        private void AssetReceived(string id, object sender, AssetBase asset)
        {
            UUID assetId = new UUID(id);

            lock (m_AssetLock)
            {
                if (!m_WaitingForAsset.TryGetValue(assetId, out var requests))
                {
                    m_log.LogWarning("[PhloxLoader]: Received unexpected asset {0}", assetId);
                    return;
                }
                m_WaitingForAsset.Remove(assetId);

                if (asset == null)
                {
                    m_log.LogError("[PhloxLoader]: Asset {0} not found", assetId);
                    return;
                }

                string scriptText = OpenMetaverse.Utils.BytesToString(asset.Data);
                m_WaitingForCompile.Enqueue(new PendingCompile
                {
                    AssetId = assetId,
                    ScriptText = scriptText,
                    Requests = requests
                });
            }

            m_WorkArrived();
        }

        private void BeginScriptRun(PhloxLoadRequest req, CompiledScript compiled)
        {
            m_ExeScheduler.FinishedLoading(req, compiled);
        }

        private void SaveToDiskCache(CompiledScript compiled)
        {
            try
            {
                string path = GetCachePath(compiled.AssetId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                SerializedScript ser = SerializedScript.FromCompiledScript(compiled);
                using (var f = File.Open(path, FileMode.Create))
                    Serializer.Serialize(f, ser);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxLoader]: Failed to cache script {0}: {1}", compiled.AssetId, e.Message);
            }
        }

        private string GetCachePath(UUID assetId)
        {
            string prefix = assetId.ToString().Substring(0, CACHE_PREFIX_LEN);
            return Path.Combine(m_CacheDir, prefix, assetId.ToString() + SCRIPT_EXT);
        }

        private void AddToUnloadedCache(UUID assetId, CompiledScript script)
        {
            if (m_UnloadedCache.Count >= MAX_UNLOADED_CACHE && m_UnloadedCacheOrder.Count > 0)
            {
                UUID oldest = m_UnloadedCacheOrder.First.Value;
                m_UnloadedCacheOrder.RemoveFirst();
                m_UnloadedCache.Remove(oldest);
            }
            m_UnloadedCache[assetId] = script;
            m_UnloadedCacheOrder.AddLast(assetId);
        }

        /// <summary>
        /// Region shutdown. Queued compiles are dropped; one already running finishes on its thread and is
        /// thrown away, never half-started; the thread then ends. Does not wait for it.
        /// </summary>
        internal void Stop()
        {
            m_Stopped = true;
            try { m_CompileQueue.CompleteAdding(); } catch (ObjectDisposedException) { }
            lock (m_Outcomes) System.Threading.Monitor.PulseAll(m_Outcomes);
        }
    }

    /// <summary>
    /// Minimal ILSLListener that sends compilation errors to the log.
    /// Replace with a real listener adaptor once LSLSystemAPI is wired.
    /// </summary>
    internal class LogOutputListener : InWorldz.Phlox.Types.ILSLListener
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(LogOutputListener));
        private readonly UUID m_ItemId;
        private int m_ErrorCount;

        public LogOutputListener(UUID itemId) { m_ItemId = itemId; }

        /// <summary>Every error, in order, so the owner can be told what the log already says.</summary>
        private readonly List<string> m_Errors = new List<string>();

        /// <summary>The compiler's errors for this script, in the order it reported them.</summary>
        public IReadOnlyList<string> Errors => m_Errors;

        public void Error(string message)
        {
            m_ErrorCount++;
            m_Errors.Add(message);
            m_log.LogError("[PhloxCompile]: {0}: {1}", m_ItemId, message);
        }

        public void Info(string message)
            => m_log.LogInformation("[PhloxCompile]: {0}: {1}", m_ItemId, message);

        public void CompilationFinished()
            => m_log.LogDebug("[PhloxCompile]: Finished {0}", m_ItemId);

        public bool HasErrors() => m_ErrorCount > 0;
    }

    /// <summary>
    /// A script that will not compile has always been a log line and nothing else
    /// (<see cref="LogOutputListener.Error"/>), so the resident whose object is broken is never
    /// told and the object gives no sign. SL sends the owner the compiler's message; this is that.
    ///
    /// <para>The build is separated from the send so the wording is a unit test and the delivery
    /// is one line.</para>
    /// </summary>
    public static class PhloxCompileErrorReport
    {
        /// <summary>
        /// Compiler messages in YEngine's editor format, "(line,col) Error: message"
        /// (XMRInstCtor.ErrorHandler), which the viewer's script editor shows in its error pane. The
        /// "N syntax error(s)" summary is dropped - each error already has its own line.
        /// </summary>
        public static List<string> ForEditor(IEnumerable<string> errors)
        {
            var result = new List<string>();
            foreach (string e in errors ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(e)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(e, @"^\d+ syntax error\(s\)$")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(e, @"^line:?\s*(\d+):(\d+)\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success) { result.Add($"({m.Groups[1].Value},{m.Groups[2].Value}) Error: {m.Groups[3].Value}"); continue; }
                var lua = System.Text.RegularExpressions.Regex.Match(e, @"^SLua: (.*) \(line (\d+)\)$", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (lua.Success) { result.Add($"({lua.Groups[2].Value},0) Error: {lua.Groups[1].Value}"); continue; }
                result.Add(e == PhloxScriptLoader.ErrorWaitTimedOut ? e : "(0,0) Error: " + e);
            }
            if (result.Count == 0 && errors != null && errors.Any()) result.Add("(0,0) Error: script failed to compile");
            return result;
        }

        /// <summary>
        /// The message SL sends the owner: the object, the script, and each error with its
        /// position. One message per failed compile however many errors it carried - a script
        /// with twenty errors must not be twenty dialogs.
        /// </summary>
        public static string Build(string objectName, string scriptName, IReadOnlyList<string> errors)
        {
            // A compiler crash is not a script error and must not be reported as one.
            // The resident gets the exception TYPE and the fact that the operator has it; the
            // stack stays in the region log, where LogOutputListener already put it.
            string crash = errors is null ? null
                : errors.FirstOrDefault(InWorldz.Phlox.Types.CompilerCrash.IsCrash);
            if (crash != null)
            {
                return "Script " + (string.IsNullOrEmpty(scriptName) ? "Script" : scriptName)
                     + ": compiler error (not a script syntax error) \u2014 "
                     + InWorldz.Phlox.Types.CompilerCrash.TypeNameOf(crash)
                     + "; reported to the grid operator";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(string.IsNullOrEmpty(objectName) ? "Object" : objectName);
            sb.Append(" [");
            sb.Append(string.IsNullOrEmpty(scriptName) ? "Script" : scriptName);
            sb.Append("]: script failed to compile");

            if (errors is null || errors.Count == 0)
                return sb.ToString();

            // The compiler already prefixes "line <l>:<c> " where it knows the position, so the
            // errors are passed through rather than reformatted - reformatting would lose the
            // positions on the messages that carry them differently.
            const int Max = 10;
            for (int i = 0; i < errors.Count && i < Max; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append(errors[i]);
            }
            if (errors.Count > Max)
            {
                sb.Append(Environment.NewLine);
                sb.Append("... and ");
                sb.Append(errors.Count - Max);
                sb.Append(" more; the rest are in the region log.");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Send it, once, to the part's owner. Silent when the region has no dialog module or no
        /// part - a missing notification must never take down a script load.
        /// </summary>
        public static void ToOwner(SceneObjectPart part, string scriptName, IReadOnlyList<string> errors)
        {
            if (part is null || errors is null || errors.Count == 0) return;
            try
            {
                var dm = part.ParentGroup?.Scene?.RequestModuleInterface<IDialogModule>();
                if (dm is null) return;
                dm.SendAlertToUser(part.OwnerID, Build(part.Name, scriptName, errors), false);
            }
            catch (Exception ex)
            {
                m_reportLog.LogWarning(ex, "[PhloxCompile]: could not tell {Owner} that {Script} failed to compile",
                    part.OwnerID, scriptName);
            }
        }

        private static readonly ILogger m_reportLog = LoggerProvider.CreateLogger(typeof(PhloxCompileErrorReport));
    }
}
