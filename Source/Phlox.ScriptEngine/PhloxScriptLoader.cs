/*
 * Legion Grid — Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon ScriptLoader.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 for Legion Grid / OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
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
        private const int CACHE_SCHEMA_VERSION = 2;
        private const string VERSION_FILE = "ScriptEngines/Phlox/bytecode/.schema_version";

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
        private readonly Stopwatch m_CompileTimer = new();

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

            Directory.CreateDirectory(CACHE_DIR);
            EnsureCacheSchemaVersion();
        }

        /// <summary>
        /// If the on-disk cache was written with an older schema version, wipe it
        /// so stale .plx files don't cause null-ref crashes on deserialize.
        /// </summary>
        private void EnsureCacheSchemaVersion()
        {
            int diskVersion = 0;
            if (File.Exists(VERSION_FILE))
            {
                if (!int.TryParse(File.ReadAllText(VERSION_FILE).Trim(), out diskVersion))
                    diskVersion = 0;
            }

            if (diskVersion < CACHE_SCHEMA_VERSION)
            {
                m_log.LogWarning(
                    "[PhloxLoader]: Cache schema version on disk ({0}) is older than current ({1}). " +
                    "Purging stale bytecode cache so scripts recompile cleanly.",
                    diskVersion, CACHE_SCHEMA_VERSION);

                try
                {
                    // Delete all .plx files; leave the directory structure.
                    foreach (string plx in Directory.GetFiles(CACHE_DIR, "*.plx", SearchOption.AllDirectories))
                        File.Delete(plx);

                    File.WriteAllText(VERSION_FILE, CACHE_SCHEMA_VERSION.ToString());
                    m_log.LogInformation("[PhloxLoader]: Bytecode cache purged and version stamp updated.");
                }
                catch (Exception ex)
                {
                    m_log.LogError("[PhloxLoader]: Failed to purge cache: {0}", ex.Message);
                }
            }
        }

        public void PostLoadRequest(PhloxLoadRequest req)
        {
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

        public WorkStatus DoWork()
        {
            bool didWork = false;

            // Top-level backstop: the load worker must survive anything a single request throws.
            // PerformLoad already guards per-request; this covers the unload/compile paths too, so
            // one failure can never stop the worker from draining the queue (which would strand the
            // RegionReady LoginLock signal). Per-failure logging only — no summary/barrier machinery.
            try
            {
                didWork |= ProcessNextUnload();
                didWork |= ProcessNextLoad();
                didWork |= ProcessNextCompile();
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
            Interpreter script = m_ExeScheduler.FindScript(req.ItemID);
            if (script == null) return;

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
                // Find asset UUID from prim inventory
                UUID assetId = FindAssetId(req);
                if (assetId == UUID.Zero) return;

                // 1. Already loaded and running (shared script)
                if (TryStartSharedScript(assetId, req)) return;

                // 2. Recently unloaded — still in memory
                if (TryStartFromUnloadedCache(assetId, req)) return;

                // 3. Compiled bytecode on disk
                if (TryStartFromDiskCache(assetId, req)) return;

                // 4. Need to compile from source text (already in req.ScriptText from OnRezScript)
                if (!string.IsNullOrEmpty(req.ScriptText))
                {
                    CompileAndStart(assetId, req);
                    return;
                }

                // 5. Fallback: fetch from asset server
                SubmitAssetRequest(assetId, req);
            }
            catch (Exception ex)
            {
                LogLoadFailure(req, ex);
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

        private static void SafeDeleteCache(string path)
        {
            try { File.Delete(path); }
            catch { /* best-effort */ }
        }

        private void CompileAndStart(UUID assetId, PhloxLoadRequest req)
        {
            var frontend = new CompilerFrontend(new LogOutputListener(req.ItemID), ".");
            try
            {
                m_CompileTimer.Restart();
                CompiledScript compiled = InWorldz.Phlox.SLua.SLuaCompiler.IsLuaScript(req.ScriptText)
                    ? frontend.CompileLua(req.ScriptText)
                    : frontend.Compile(req.ScriptText);
                m_CompileTimer.Stop();

                if (compiled == null)
                {
                    m_log.LogError("[PhloxLoader]: Compilation failed for {0} item {1}", assetId, req.ItemID);
                    return;
                }

                compiled.AssetId = assetId;
                SaveToDiskCache(compiled);
                m_log.LogInformation("[PhloxLoader]: Compiled {0} ({1}ms)", assetId, m_CompileTimer.ElapsedMilliseconds);

                BeginScriptRun(req, compiled);
                m_LoadedScripts[assetId] = new LoadedScript { Script = compiled, RefCount = 1 };
            }
            catch (Exception e)
            {
                m_log.LogError("[PhloxLoader]: Exception compiling {0} item {1}: {2}", assetId, req.ItemID, e);
            }
            finally
            {
                m_CompileTimer.Reset();
            }
        }

        private bool ProcessNextCompile()
        {
            PendingCompile pending;
            lock (m_AssetLock)
            {
                if (m_WaitingForCompile.Count == 0) return false;
                pending = m_WaitingForCompile.Dequeue();
            }

            var frontend = new CompilerFrontend(new LogOutputListener(pending.Requests[0].ItemID), ".");
            try
            {
                m_CompileTimer.Restart();
                CompiledScript compiled = InWorldz.Phlox.SLua.SLuaCompiler.IsLuaScript(pending.ScriptText)
                    ? frontend.CompileLua(pending.ScriptText)
                    : frontend.Compile(pending.ScriptText);
                m_CompileTimer.Stop();

                if (compiled == null)
                {
                    m_log.LogError("[PhloxLoader]: Compilation failed (from asset server) for {0}", pending.AssetId);
                    return true;
                }

                compiled.AssetId = pending.AssetId;
                SaveToDiskCache(compiled);
                m_log.LogInformation("[PhloxLoader]: Compiled (from asset) {0} ({1}ms)", pending.AssetId, m_CompileTimer.ElapsedMilliseconds);

                foreach (var req in pending.Requests)
                    BeginScriptRun(req, compiled);

                m_LoadedScripts[pending.AssetId] = new LoadedScript
                {
                    Script = compiled,
                    RefCount = pending.Requests.Count
                };
            }
            catch (Exception e)
            {
                m_log.LogError("[PhloxLoader]: Exception compiling from asset {0}: {1}", pending.AssetId, e);
            }
            finally
            {
                m_CompileTimer.Reset();
            }

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
            return Path.Combine(CACHE_DIR, prefix, assetId.ToString() + SCRIPT_EXT);
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

        internal void Stop() { }
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

        public void Error(string message)
        {
            m_ErrorCount++;
            m_log.LogError("[PhloxCompile]: {0}: {1}", m_ItemId, message);
        }

        public void Info(string message)
            => m_log.LogInformation("[PhloxCompile]: {0}: {1}", m_ItemId, message);

        public void CompilationFinished()
            => m_log.LogDebug("[PhloxCompile]: Finished {0}", m_ItemId);

        public bool HasErrors() => m_ErrorCount > 0;
    }
}
