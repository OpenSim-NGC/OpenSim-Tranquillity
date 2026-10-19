/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The periodic upkeep of the non-spatial session store.
 *
 * NonSpatialVoiceSessionEngine.Sweep() lapses unanswered invitations and collects idle and dead
 * sessions, but it only runs when something calls it. This class is that caller: one System.Threading.Timer
 * per engine, ticking every 30 seconds on the thread pool. Like the visibility service's heartbeat timer it
 * has its own re-entry guard, is disposed on Stop, and its callback never lets an exception out of
 * the pool thread.
 */
using System;
using System.Threading;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class NonSpatialMaintenance : IDisposable
    {
        /// <summary>How often the pass runs.</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

        private readonly NonSpatialVoiceSessionEngine _engine;
        private readonly Action<string> _debug;
        private readonly Action<string, Exception> _error;
        private readonly TimeSpan _interval;
        private readonly object _timerLock = new object();
        private Timer _timer;
        private int _reentry;

        /// <param name="debug">Receives one line per pass that expired something; never called for an idle pass.</param>
        /// <param name="error">Receives a failed pass; the next tick still runs.</param>
        public NonSpatialMaintenance(NonSpatialVoiceSessionEngine engine, Action<string> debug,
                                     Action<string, Exception> error, TimeSpan? interval = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _debug = debug;
            _error = error;
            _interval = interval ?? DefaultInterval;
        }

        /// <summary>Start the timer. A second Start while it runs does nothing.</summary>
        public void Start()
        {
            lock (_timerLock)
            {
                if (_timer != null) return;
                _timer = new Timer(OnTimer, null, _interval, _interval);
            }
        }

        /// <summary>Stop the timer. Safe to call twice, and Start may follow.</summary>
        public void Stop()
        {
            Timer t;
            lock (_timerLock)
            {
                t = _timer;
                _timer = null;
            }
            t?.Dispose();
        }

        public void Dispose() => Stop();

        private void OnTimer(object _) => RunOnce();

        /// <summary>
        /// One pass: sweep the store. Returns how many things expired (invitations plus sessions
        /// removed). Catches and reports any failure, because it runs on a pool thread where an
        /// escaped exception would take the process down.
        /// </summary>
        public int RunOnce()
        {
            if (Interlocked.Exchange(ref _reentry, 1) != 0)
                return 0;
            try
            {
                NonSpatialVoiceSessionEngine.SweepSummary s = _engine.SweepWithSummary();
                _engine.PruneTransientState();
                int expired = s.InvitationsExpired + s.Removed.Count;
                if (expired > 0)
                    _debug?.Invoke($"[NONSPATIAL SWEEP] invitations={s.InvitationsExpired} sessions={s.Removed.Count} seats={s.SeatsReleased}");
                return expired;
            }
            catch (Exception e)
            {
                try { _error?.Invoke("non-spatial session sweep failed", e); }
                catch { /* the log sink must not break the timer either */ }
                return 0;
            }
            finally
            {
                Interlocked.Exchange(ref _reentry, 0);
            }
        }
    }
}
