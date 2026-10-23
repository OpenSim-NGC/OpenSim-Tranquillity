/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * VoiceStateFeeder - produces the per-listener visibility matrix and a delta stream.
 *
 * Scene-free: it reads IFeederWorld, so it unit-tests without a Scene. The region module owns
 * one per region, forwards sim events into the On* / Invalidate hooks, and drives Tick() on a
 * ~250ms timer. One code path (Build -> Diff) => one matrix => one source of truth.
 *
 * The tick ALWAYS rebuilds, because child agents get no crossing event and must be re-derived
 * from position every tick (baseline 2.1). Parcel/estate events therefore need only mark the
 * feeder invalidated so the owner can choose to tick early for low latency; correctness does
 * not depend on them, since Build reads live world state each tick.
 *
 * Hardening: rule derivation can touch live sim state via the ParcelView ban/restrict delegates
 * (and, in production, an enumerating SnapshotAgents), so Tick() and the initial build catch and
 * report any derivation exception and keep the last good matrix - a bad tick must not kill the
 * timer; the next tick re-derives. Debug builds also assert the matrix is only ever mutated on a
 * single tick thread.
 */

using System;
using System.Diagnostics;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class VoiceStateFeeder : IVisibilityFeed
    {
        private readonly IFeederWorld _world;
        private readonly int _room;
        private readonly Action<Exception> _onError;   // log-and-continue sink (null = swallow)
        private VisibilityMatrix _current;
        private bool _pendingInvalidation;
        private int _tickThreadId;                      // 0 until the first Tick establishes it

        public VoiceStateFeeder(IFeederWorld world, int room, Action<Exception> onError = null)
        {
            _world = world;
            _room = room;
            _onError = onError;
            // Construction happens on the owner's thread (not necessarily the tick thread), so it
            // is not thread-guarded. A failed initial build still yields a usable feeder.
            try
            {
                _current = VisibilityMatrix.Build(world);
            }
            catch (Exception ex)
            {
                _current = VisibilityMatrix.Empty;
                _onError?.Invoke(ex);
            }
        }

        public VisibilityMatrix Current => _current;

        public event Action<VisibilityBatch> BatchProduced;

        /// The managed thread id that has driven ticks (0 before the first tick). Observability
        /// for the single-tick-thread invariant asserted in Debug builds.
        public int TickThreadId => _tickThreadId;

        // --- Sim event hooks (region module forwards these). They only flip a bool, so they are
        //     safe to call from any sim thread; they do NOT mutate the matrix. ---

        public void OnAvatarEnteringNewParcel() => Invalidate();   // root crossing
        public void OnLandChanged() => Invalidate();               // OnLandObjectAdded/Removed
        public void OnEstateChanged() => Invalidate();             // IEstateModule.OnEstateInfoChange

        public void Invalidate() => _pendingInvalidation = true;

        public bool HasPendingInvalidation => _pendingInvalidation;

        /// Rebuild from live world, diff against the previous matrix, raise at most one non-empty
        /// batch. On a derivation error: report it, keep the last matrix, emit nothing, and let
        /// the next tick retry. Returns the batch (empty on no-change or error).
        public VisibilityBatch Tick()
        {
            RecordAndCheckTickThread();

            VisibilityMatrix next;
            try
            {
                next = VisibilityMatrix.Build(_world);
            }
            catch (Exception ex)
            {
                // A live-collection enumeration blew up mid-derivation. Do not advance, do not
                // emit, do not throw - the timer keeps running and the next tick re-derives.
                _onError?.Invoke(ex);
                _pendingInvalidation = false;
                return VisibilityBatch.EmptyDelta(_room);
            }

            VisibilityBatch batch = DeltaComputer.Diff(_current, next, _room);
            _current = next;
            _pendingInvalidation = false;
            if (!batch.IsEmpty)
                BatchProduced?.Invoke(batch);
            return batch;
        }

        public VisibilityBatch SnapshotFor(UUID listener)
            => DeltaComputer.SnapshotFor(_current, listener, _room);

        // Capture the tick thread on first use; in Debug, assert every later mutation is on it.
        // (Debug.Assert is compiled out in Release; the id capture is cheap and always runs.)
        private void RecordAndCheckTickThread()
        {
            int cur = Environment.CurrentManagedThreadId;
            if (_tickThreadId == 0)
                _tickThreadId = cur;
            else
                Debug.Assert(_tickThreadId == cur,
                    "VoiceStateFeeder: matrix mutated off the feeder tick thread. Ticks must run " +
                    "on a single thread (scene heartbeat or a dedicated timer), never concurrently.");
        }
    }
}
