/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A circuit breaker for calls to the grid call session service. A service that is down or slow makes
 * every call wait out its timeout; with several viewers asking at once, those waits stack up on the
 * threads that serve them. After a run of failures the breaker opens and calls are refused at once. After
 * a short period it lets one call through to find out whether the service is back: if that call is
 * answered the breaker closes, if not it opens again for another period.
 *
 * "Failure" means the service gave no answer. A refusal the service sends (a full room, an unknown
 * session) is an answer.
 */
using System;
using System.Collections.Generic;

namespace osWebRtcVoice.NonSpatial
{
    public enum BreakerState { Closed, Open, HalfOpen }

    public sealed class SessionCircuitBreaker
    {
        private readonly int _threshold;
        private readonly TimeSpan _window;
        private readonly TimeSpan _openFor;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Queue<DateTime> _failures = new Queue<DateTime>();
        private BreakerState _state = BreakerState.Closed;
        private DateTime _openedAt;
        private DateTime _probeStartedAt;

        /// <summary>Raised, outside the lock, each time the state changes.</summary>
        public event Action<BreakerState> Changed;

        public SessionCircuitBreaker(int failureThreshold, TimeSpan window, TimeSpan openFor, Func<DateTime> clock = null)
        {
            if (failureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
            _threshold = failureThreshold;
            _window = window;
            _openFor = openFor;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public BreakerState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>True when a call may go to the service. False means refuse it now, without calling.</summary>
        public bool TryEnter()
        {
            bool probe;
            lock (_lock)
            {
                DateTime now = _clock();
                switch (_state)
                {
                    case BreakerState.Closed:
                        return true;
                    case BreakerState.Open:
                        probe = now - _openedAt >= _openFor;
                        break;
                    default:
                        // One call at a time finds out whether the service is back. A probe that never reported
                        // (its caller died) must not hold the breaker shut for good.
                        probe = now - _probeStartedAt >= _openFor;
                        break;
                }
                if (probe)
                {
                    _state = BreakerState.HalfOpen;
                    _probeStartedAt = now;
                }
            }
            if (probe) Changed?.Invoke(BreakerState.HalfOpen);
            return probe;
        }

        public void RecordSuccess()
        {
            bool closed = false;
            lock (_lock)
            {
                _failures.Clear();
                if (_state == BreakerState.HalfOpen)
                {
                    _state = BreakerState.Closed;
                    closed = true;
                }
            }
            if (closed) Changed?.Invoke(BreakerState.Closed);
        }

        public void RecordFailure()
        {
            bool opened = false;
            lock (_lock)
            {
                DateTime now = _clock();
                if (_state == BreakerState.HalfOpen)
                {
                    Open(now);
                    opened = true;
                }
                else if (_state == BreakerState.Closed)
                {
                    while (_failures.Count > 0 && now - _failures.Peek() > _window)
                        _failures.Dequeue();
                    _failures.Enqueue(now);
                    if (_failures.Count >= _threshold)
                    {
                        Open(now);
                        opened = true;
                    }
                }
            }
            if (opened) Changed?.Invoke(BreakerState.Open);
        }

        private void Open(DateTime now)
        {
            _state = BreakerState.Open;
            _openedAt = now;
            _failures.Clear();
        }
    }
}
