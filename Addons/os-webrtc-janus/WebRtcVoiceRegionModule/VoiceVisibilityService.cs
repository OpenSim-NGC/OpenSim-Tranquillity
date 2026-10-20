/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Per-region owner of a VoiceStateFeeder.
 *
 * Wires sim events (dirty-flag only — never mutates the matrix on an event/sim thread), and
 * drives Tick() on ONE dedicated, named background thread at the configured cadence. It is
 * deliberately NOT a System.Threading.Timer: pool callbacks hop threads and would trip the
 * feeder's Debug single-thread guard. Stop() signals the loop and joins the thread with a
 * timeout.
 *
 * No Janus sender exists yet (out of scope); produced batches are logged so the in-world DEBUG
 * smoke check can watch the feed run while the guard stays quiet.
 */

using System;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class VoiceVisibilityService
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string logHeader = "[VOICE VISIBILITY]";

        // Matrix scope placeholder until the (later) sender wires real Janus room numbers.
        private const int EstateRoomPlaceholder = -999;
        private const int JoinTimeoutMs = 2000;
        private const int StopHeartbeatWaitMs = 1000;

        private readonly Scene m_scene;
        private readonly int m_cadenceMs;
        private readonly bool m_emitEnabled;
        private readonly IPeerCtlBatchSink m_sink;   // injected by the region module (option c-new); may be null
        private readonly TimeSpan m_adminTimeout;    // for the sender's in-flight staleness self-heal
        private readonly VoiceStateFeeder m_feeder;
        private readonly ManualResetEventSlim m_wake = new ManualResetEventSlim(false);

        private Thread m_thread;
        private volatile bool m_running;
        /// <summary>Environment.TickCount64 at the end of the last completed feeder tick.
        /// The heartbeat reports how old this is; the MIXER decides whether that counts as live.</summary>
        private long m_lastTickMs;
        /// <summary>Tripped by the Watchdog's alarm when this feeder misses its 5000 ms deadline,
        /// consumed by the next tick, which then rebuilds from the scene instead of replaying a stale
        /// view. Its own type so the alarm-to-rebuild wiring is testable without a Scene.</summary>
        private readonly FeederResumeLatch m_resume = new FeederResumeLatch();
        /// <summary>The heartbeat's own timer. It does not ride the feeder tick, so a
        /// starved feeder does not look like a dead sim -- it looks like a sim reporting a stale feed.</summary>
        private Timer m_heartbeatTimer;
        private int m_heartbeatReentry;
        private IEstateModule m_estateModule;
        private VisibilityBatchSender m_sender;   // built in StartLoop from the injected sink
        private readonly AgentRoomTable m_rooms = new AgentRoomTable();   // agent -> joined mixer room (newest wins)
        private readonly FeederWorldFromScene m_world;                   // the snapshot's parcel-local-ids
        private readonly bool m_armingEnabled;    // [WebRtcVoice] VisibilityArmingEnabled (default false)
        private VisAuthority m_authority;         // one per StartLoop, so a new epoch per feeder start

        // The sink is passed in directly (NOT resolved via scene.RequestModuleInterface): the sink
        // and the sender live in this module's AssemblyLoadContext, so IPeerCtlBatchSink identity
        // matches. Routing it through the scene module-interface registry crossed an ALC boundary
        // with a non-shared type (VoiceVisibility.dll) and the two Types never matched. A null sink
        // is tolerated — the sender runs matrix-only and logs once.
        public VoiceVisibilityService(Scene scene, int cadenceMs, bool emitEnabled = false,
            IPeerCtlBatchSink sink = null, TimeSpan? adminTimeout = null, bool armingEnabled = false)
        {
            m_armingEnabled = armingEnabled;
            m_scene = scene;
            m_cadenceMs = cadenceMs;
            m_emitEnabled = emitEnabled;
            m_sink = sink;
            m_adminTimeout = adminTimeout ?? TimeSpan.FromSeconds(5);
            // Pass the moderation store to the adapter so ToParcelView can populate the source-side
            // moderation predicate. Moderation is an auto-property initialiser, so it is already set
            // before this constructor body runs.
            m_world = new FeederWorldFromScene(scene, Moderation);
            m_feeder = new VoiceStateFeeder(m_world, EstateRoomPlaceholder, OnDerivationError);
            m_feeder.BatchProduced += OnBatch;
            // The resolver the sink consumes. Null = no record; the SINK maps that to the
            // estate room (one policy for listeners and sources) — this service never guesses.
            RoomOf = m_rooms.Resolve;
            // "Resolve, don't guess". The room an agent is ADDRESSED at is its recorded room, else
            // the room an avatar at its current position would be provisioned into, else nothing at all - it is left
            // out of the batch and the heartbeat and counted. The estate number is never used as an address merely
            // because it is the default: on an estate-channel parcel the resolved number IS that number, and on a
            // parcel with its own channel it is the parcel's, which is where that avatar actually is.
            ResolveRoom = agent =>
            {
                int? recorded = m_rooms.Resolve(agent);
                if (recorded.HasValue)
                    return recorded;
                int? localId = m_world.ParcelLocalIdOf(agent);
                return localId.HasValue
                    ? JanusAudioBridge.CalcRoomNumber(string.Empty, m_scene.RegionInfo.RegionID.ToString(), "local",
                        localId.Value, string.Empty)
                    : (int?)null;
            };
            // Hand it to the sink HERE. The sink was constructed before this service
            // (WebRtcVoiceRegionModule.cs:174-176), so the resolver cannot be a ctor argument;
            // this assignment closes the window before Start() lets any tick emit. Concrete
            // type on purpose: IPeerCtlBatchSink stays a pure transport seam, so a sink that is
            // not the Janus one (a test double) needs no room knowledge at all.
            if (m_sink is JanusPeerCtlBatchSink janusSink)
                janusSink.RoomOf = ResolveRoom;   // the sink addresses what is resolved, never a default
        }

        /// The produced feed — the boundary the later Janus sender will consume.
        public IVisibilityFeed Feed => m_feeder;

        /// This region's Janus sink, or null when the region runs matrix-only (emission off,
        /// or the [JanusWebRtcVoice] admin endpoint/secret missing). Exposed ONLY so the console reader
        /// can print the sink's fallback counters and LastSendStats — which had no reader at all until
        /// the shadow soak needed them. Concrete type for the same reason the constructor uses one:
        /// IPeerCtlBatchSink stays a pure transport seam, and a test double has no counters to show.
        /// Read-only by contract: nothing may mutate the sink through this.
        public JanusPeerCtlBatchSink JanusSink => m_sink as JanusPeerCtlBatchSink;

        /// <summary>2: how many agents the last arming pass could not place, and so left out of the
        /// send and the heartbeat entirely. Read by the console reader; zero on a healthy region.</summary>
        public int Unplaced => m_sender?.Unplaced ?? 0;

        /// Sticky per-parcel voice-moderation state (in-memory / NON-PERSISTENT). Written
        /// by the region module's SpatialVoiceModerationRequest CAP handler via this per-region
        /// service; purged here on parcel removal (OnLandObjectRemoved). Read by the matrix via the
        /// FeederWorldFromScene constructed below — ToParcelView folds IsModerated plus the
        /// moderator exemption into ParcelView.IsVoiceModerated (rule 2b, source-side).
        public VoiceModerationStore Moderation { get; } = new VoiceModerationStore();

        /// Observability for the event→dirty wiring (used by tests).
        public bool HasPendingInvalidation => m_feeder.HasPendingInvalidation;

        public void Start()
        {
            WireEvents();
            StartLoop();
        }

        // Split from Start() so tests can assert event→dirty wiring without a running loop
        // consuming the flag.
        public void WireEvents()
        {
            m_scene.EventManager.OnAvatarEnteringNewParcel += OnAvatarEnteringNewParcel;
            m_scene.EventManager.OnLandObjectAdded += OnLandObjectAdded;
            m_scene.EventManager.OnLandObjectRemoved += OnLandObjectRemoved;
            m_estateModule = m_scene.RequestModuleInterface<IEstateModule>();
            if (m_estateModule != null)
                m_estateModule.OnEstateInfoChange += OnEstateInfoChange;
        }

        public void StartLoop()
        {
            // Build the sender from the injected sink (same ALC — no registry resolve). Null-tolerant:
            // a null sink makes the sender run matrix-only and log once. The sender's own
            // VisibilityEmitEnabled gate decides whether it emits at all.
            // With arming enabled, a NEW authority (and so a new room_epoch) for every feeder
            // start. With it disabled, nothing below is built and the sender takes its pre-0.2 paths.
            VisAuthority authority = null;
            Func<UUID, int?> resolveRoom = null;
            if (m_armingEnabled && m_emitEnabled && m_sink is JanusPeerCtlBatchSink armingSink)
            {
                authority = new VisAuthority(VisAuthority.NewEpoch(), m_scene.RegionInfo.RegionName);
                armingSink.Authority = authority;
                resolveRoom = ResolveRoom;
                m_authority = authority;
            }
            m_sender = new VisibilityBatchSender(m_feeder, m_sink, m_emitEnabled,
                m_adminTimeout, m_scene.RegionInfo.RegionName, authority: authority, resolveRoom: resolveRoom);

            m_running = true;
            // Register with the OpenSim Watchdog so a dead or non-heartbeating tick thread is
            // reported instead of failing silently. StartThread creates, names, registers, and
            // starts the thread. 5000ms timeout = 20x the 250ms cadence; Pump is fire-and-forget
            // so the tick thread never blocks on a send, leaving that headroom safe.
            // The feed is "fresh" the moment the loop starts, so a heartbeat that beats the first
            // tick does not report a bogus age measured from process start.
            Volatile.Write(ref m_lastTickMs, Environment.TickCount64);
            m_thread = WorkManager.StartThread(
                RunLoop,
                "VoiceVisibilityFeeder:" + m_scene.RegionInfo.RegionName,
                ThreadPriority.Normal,
                isBackground: true,
                alarmIfTimeout: true,
                // The Watchdog notices this feeder 3 s before the mixer's 8000 ms
                // window gives up on it. OnFeederAlarm records that so
                // the tick that finally runs knows it is a RESUME and rebuilds from the scene.
                alarmMethod: OnFeederAlarm,
                timeout: 5000);
            // The heartbeat runs on its own timer, at the authority's own cadence.
            if (authority != null)
            {
                authority.FeedAgeMs = () => Environment.TickCount64 - Volatile.Read(ref m_lastTickMs);
                m_heartbeatTimer = new Timer(OnHeartbeatTimer, null,
                    VisAuthority.HeartbeatIntervalMs, VisAuthority.HeartbeatIntervalMs);
            }
            m_log.LogInformation($"{logHeader} feeder started for {m_scene.RegionInfo.RegionName} @ {m_cadenceMs}ms (emit={m_emitEnabled})");
            if (authority != null)
                m_log.LogInformation($"{logHeader} arming ENABLED for {m_scene.RegionInfo.RegionName} ([WebRtcVoice] VisibilityArmingEnabled): " +
                    $"room_epoch {authority.EpochString}; heartbeats every {VisAuthority.HeartbeatIntervalMs} ms once the mixer advertises " +
                    $"vis_protocol {VisAuthority.HeartbeatProtocol}");
            else if (m_armingEnabled)
                m_log.LogWarning($"{logHeader} [WebRtcVoice] VisibilityArmingEnabled is true but {m_scene.RegionInfo.RegionName} has no Janus " +
                    $"peer_ctl sink (emission off or admin config missing): arming is inactive");
        }

        /// The mixer room each agent's latest successful provision joined, as a resolver for
        /// the sink. Null means no record here; the sink resolves null to the estate room
        /// (one policy for a missing room record). Newest provision wins.
        public Func<UUID, int?> RoomOf { get; }

        /// <summary>The room an agent is ADDRESSED at - its record, else the room its current parcel
        /// would provision it into, else null for "cannot be placed", which means omitted and counted.</summary>
        public Func<UUID, int?> ResolveRoom { get; }

        /// Forward a WebRTC provisioning result for a listener. If the result carried the joined
        /// room (the success map) record it; a failure or logout map has no room, so the caller
        /// passes null and the record is left untouched. Then hand the agent to the sender's
        /// pending-join path exactly as before — that queueing is deliberately
        /// unchanged here. Safe if the sender is not yet built or emission is disabled.
        public void OnListenerProvisioned(UUID listener, int? room)
        {
            if (room.HasValue)
            {
                m_rooms.Record(listener, room.Value);
                // A viewer provisioned INTO this room, which is proof it exists. Every
                // listener backing off for it may arm on the next tick.
                RoomExists(room.Value);
            }
            m_sender?.OnListenerProvisioned(listener);
        }

        /// Overload without room information. Delegates with null, so the record is untouched.
        public void OnListenerProvisioned(UUID listener)
            => OnListenerProvisioned(listener, null);

        /// <summary>Something proved this room exists - a connector's capability fetch ensured
        /// it, a viewer provisioned into it, or a batch to it applied. Clears that room's unknown_room backoff so
        /// arming goes out on the very next tick. Safe before the loop starts (no authority yet: nothing to clear).</summary>
        public void RoomExists(int room) => m_authority?.RoomExists(room);

        public void Stop()
        {
            m_running = false;
            m_wake.Set();

            // Stop the heartbeat timer first. Its callback and the graceful "stopping" heartbeat both
            // go through the same sender, and a timer tick arriving after that stop would re-assert an authority
            // this region has just given up.
            Timer hb = m_heartbeatTimer;
            m_heartbeatTimer = null;
            hb?.Dispose();

            UnwireEvents();

            Thread t = m_thread;
            m_thread = null;
            if (t != null && !t.Join(JoinTimeoutMs))
                m_log.LogWarning($"{logHeader} feeder thread for {m_scene.RegionInfo.RegionName} did not stop within {JoinTimeoutMs}ms");

            // Tell the mixer this authority is stopping, so it treats these rooms as stale at once. Bounded;
            // skipped when arming is off or the mixer never advertised heartbeats.
            if (m_authority != null && m_sender != null)
            {
                try
                {
                    if (!m_sender.PumpHeartbeatAsync(stopping: true).Wait(StopHeartbeatWaitMs))
                        m_log.LogWarning($"{logHeader} stopping heartbeat for {m_scene.RegionInfo.RegionName} did not complete within {StopHeartbeatWaitMs}ms");
                }
                catch (Exception e)
                {
                    m_log.LogWarning(e, $"{logHeader} stopping heartbeat for {m_scene.RegionInfo.RegionName} failed");
                }
            }

            // The service owns the injected sink's lifetime (it holds a JanusAdminClient/HttpClient).
            // Dispose AFTER the tick thread has joined so no in-flight send races the dispose.
            (m_sink as IDisposable)?.Dispose();
        }

        /// <summary>The Watchdog's alarm for this feeder. Records the stall for the next
        /// tick to act on. Deliberately does no work and takes no lock: it runs on the Watchdog's own thread, which
        /// can be starved too during a long stall, and anything slow here would delay every other thread it checks.</summary>
        private string OnFeederAlarm()
        {
            m_resume.Trip();
            long age = Environment.TickCount64 - Volatile.Read(ref m_lastTickMs);
            return $"visibility feeder for {m_scene.RegionInfo.RegionName} has not ticked for {age} ms; " +
                "the heartbeat keeps reporting that age and the mixer decides";
        }

        /// <summary>The heartbeat, on its own timer.
        ///
        /// It is a <see cref="Timer"/> callback, so it runs on the THREAD POOL rather than on the feeder thread.
        /// That is the point: the feeder is one named thread doing a scene walk, and when it is starved -- by a
        /// Phlox timeslice, by GC, by startup -- it is starved alone, while the pool keeps servicing work. The
        /// heartbeat therefore keeps its 1000 ms cadence through exactly the stalls that used to silence it.
        /// PumpHeartbeat is fire-and-forget with its own interval gate and single-flight, so a slow send cannot
        /// queue callbacks on top of each other; the re-entry guard here is belt and braces for a pool so starved
        /// that a second tick fires before the first returns.</summary>
        private void OnHeartbeatTimer(object _)
        {
            if (Interlocked.Exchange(ref m_heartbeatReentry, 1) != 0)
                return;
            try
            {
                m_sender?.PumpHeartbeat();
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, $"{logHeader} heartbeat timer for {m_scene.RegionInfo.RegionName} threw");
            }
            finally
            {
                Interlocked.Exchange(ref m_heartbeatReentry, 0);
            }
        }

        private void UnwireEvents()
        {
            m_scene.EventManager.OnAvatarEnteringNewParcel -= OnAvatarEnteringNewParcel;
            m_scene.EventManager.OnLandObjectAdded -= OnLandObjectAdded;
            m_scene.EventManager.OnLandObjectRemoved -= OnLandObjectRemoved;
            if (m_estateModule != null)
                m_estateModule.OnEstateInfoChange -= OnEstateInfoChange;
        }

        private void RunLoop()
        {
            while (m_running)
            {
                // The Watchdog says this feeder missed its deadline, so this tick is a
                // RESUME. Invalidate BEFORE deriving, which makes Tick() recompute the matrix from the scene and
                // emit a full REPLACE. Replaying the cached pre-stall view would be worse than useless: after the
                // a stall of 60 s or more, who is in the region and on which parcel may be entirely different.
                long stalledFor = Environment.TickCount64 - Volatile.Read(ref m_lastTickMs);
                if (m_resume.ResumeIfStalled(m_feeder.Invalidate))
                {
                    long age = stalledFor;
                    m_log.LogWarning($"{logHeader} feeder for {m_scene.RegionInfo.RegionName} resumed after {age} ms " +
                        "without a tick: rebuilding the matrix from the scene and re-arming");
                }
                VisibilityBatch batch = null;
                try
                {
                    batch = m_feeder.Tick();
                }
                catch (Exception e)
                {
                    // Tick already hardens derivation; this is a last-resort guard so the loop
                    // itself never dies.
                    m_log.LogError(e, $"{logHeader} tick failed");
                }

                // Drive emission off the tick: fire-and-forget, never awaited here, never throws.
                // Pump runs every tick (even an empty batch) so the pending-join path and a
                // snapshot-on-a-quiet-tick still get a chance.
                if (batch != null)
                    m_sender?.Pump(batch);
                // The heartbeat does NOT ride this tick -- OnHeartbeatTimer owns it. This
                // loop's only remaining duty to liveness is to stamp when it last completed, which the heartbeat
                // reports as feed_age_ms and the mixer judges.
                Volatile.Write(ref m_lastTickMs, Environment.TickCount64);

                m_wake.Wait(m_cadenceMs);
                m_wake.Reset();

                // Heartbeat on the always-executed path (even an idle tick with a null batch), so
                // the Watchdog only alarms when this loop truly stops ticking.
                Watchdog.UpdateThread();
            }

            // Deregister on clean exit so shutdown does not leave a stale tracked thread.
            Watchdog.RemoveThread();
        }

        // --- Event handlers: run on sim threads. Only flip the feeder's dirty flag (NOT the
        //     matrix) and wake the tick thread for an early recompute. Matrix mutation happens
        //     exclusively on RunLoop's thread, keeping the Debug single-thread guard quiet. ---

        private void OnAvatarEnteringNewParcel(ScenePresence sp, int localLandID, UUID regionID)
        {
            m_feeder.OnAvatarEnteringNewParcel();
            m_wake.Set();
        }

        private void OnLandObjectAdded(OpenSim.Framework.ILandObject parcel)
        {
            m_feeder.OnLandChanged();
            m_wake.Set();
        }

        private void OnLandObjectRemoved(UUID globalID)
        {
            // Purge any moderation state for a parcel that no longer exists (join /
            // delete) so orphaned GlobalIDs self-heal. Reuses this existing subscription.
            Moderation.Remove(globalID);
            m_feeder.OnLandChanged();
            m_wake.Set();
        }

        private void OnEstateInfoChange(UUID regionID)
        {
            if (regionID != m_scene.RegionInfo.RegionID)
                return;
            m_feeder.OnEstateChanged();
            m_wake.Set();
        }

        private void OnBatch(VisibilityBatch b)
        {
            if (!b.IsEmpty)
                // Report the MUTE channel too. A moderation-mute-only batch has empty excl
                // Added/Removed but a non-empty MuteAdded, and formerly logged "+0/-0 listeners" while
                // the mute was delivered (the 2026-08-28 trace). MuteAdded/MuteRemoved make it visible.
                m_log.LogDebug("{LogHeader} {RegionName}: +{AddedListenerCount}/-{RemovedListenerCount} listeners, +{AddedMuteCount}/-{RemovedMuteCount} muted",
                    logHeader, m_scene.RegionInfo.RegionName, b.Added.Count, b.Removed.Count, b.MuteAdded.Count, b.MuteRemoved.Count);
        }

        private void OnDerivationError(Exception e)
            => m_log.LogError(e, $"{logHeader} matrix derivation error (kept last matrix)");
    }
}
