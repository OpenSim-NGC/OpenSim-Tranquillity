/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A bounded queue of departures the grid call session service did not receive. A viewer who hangs up while
 * the service cannot be reached still leaves the call; the region keeps the departure and sends it when
 * the service answers again, oldest first. Anything the queue gives up (it is full, or the departure is
 * older than the age limit) is not lost for good: a region renews the seats of the agents it still has, and
 * the service returns the seat of an agent whose region stops renewing.
 *
 * Only departures are queued. A call that could not start or join is refused at once, because replaying
 * it later would put someone into a call they have already given up on.
 */
using System;
using System.Collections.Generic;
using System.Threading;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class DepartOutbox : IDisposable
    {
        private enum Kind { Depart, LeaveChat, DepartByViewerSession, DepartAll }

        private sealed class Item
        {
            public Kind Kind;
            public UUID Session;
            public UUID Agent;
            public string Viewer;
            public int Reason;
            public DateTime At;
        }

        private readonly IVoiceSessionService _service;
        private readonly int _max;
        private readonly TimeSpan _maxAge;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly List<Item> _items = new List<Item>();
        private readonly object _timerLock = new object();
        private Timer _timer;
        private int _running;
        private int _dropped;

        /// <summary>Raised when a departure is given up, with why ("full" or "expired").</summary>
        public event Action<string> Lost;

        public DepartOutbox(IVoiceSessionService service, int max, TimeSpan maxAge, Func<DateTime> clock = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _max = Math.Max(1, max);
            _maxAge = maxAge;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Count
        {
            get { lock (_lock) return _items.Count; }
        }

        /// <summary>Departures given up so far, because the queue was full or they had waited too long.</summary>
        public int Dropped => Volatile.Read(ref _dropped);

        public void EnqueueDepart(UUID sessionId, UUID agent, int reason)
            => Add(new Item { Kind = Kind.Depart, Session = sessionId, Agent = agent, Reason = reason });

        public void EnqueueLeaveChat(UUID sessionId, UUID agent)
            => Add(new Item { Kind = Kind.LeaveChat, Session = sessionId, Agent = agent });

        public void EnqueueDepartByViewerSession(UUID agent, string viewerSession, int reason)
            => Add(new Item { Kind = Kind.DepartByViewerSession, Agent = agent, Viewer = viewerSession, Reason = reason });

        public void EnqueueDepartAll(UUID agent, int reason)
            => Add(new Item { Kind = Kind.DepartAll, Agent = agent, Reason = reason });

        /// <summary>
        /// The agent has taken a seat in the session since a departure was queued, so the departure is stale:
        /// delivered late it would remove the seat just taken. A queued departure for that seat is dropped, and
        /// so is one that named every seat of the agent (the other seats it would have released come back with
        /// the seat lease when the agent leaves the region).
        /// </summary>
        public void Supersede(UUID agent, UUID sessionId)
        {
            lock (_lock)
            {
                if (_items.Count == 0) return;
                _items.RemoveAll(i => i.Agent == agent
                    && (i.Kind == Kind.DepartAll || i.Kind == Kind.DepartByViewerSession || i.Session == sessionId));
            }
        }

        /// <summary>
        /// Send what is queued, oldest first, until the service does not answer. Returns how many were delivered.
        /// A refusal from the service (an unknown session, a seat already gone) counts as delivered: the
        /// service has heard the departure.
        /// </summary>
        public int RunOnce()
        {
            if (Interlocked.Exchange(ref _running, 1) != 0)
                return 0;
            try
            {
                int delivered = 0;
                while (true)
                {
                    Item head;
                    int expired = 0;
                    lock (_lock)
                    {
                        DateTime now = _clock();
                        while (_items.Count > 0 && now - _items[0].At > _maxAge)
                        {
                            _items.RemoveAt(0);
                            expired++;
                        }
                        head = _items.Count > 0 ? _items[0] : null;
                    }
                    for (int i = 0; i < expired; i++) Drop("expired");
                    if (head == null)
                        return delivered;
                    if (Send(head) == null)
                        return delivered;
                    lock (_lock)
                        _items.Remove(head);
                    delivered++;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        public void Start(TimeSpan interval)
        {
            lock (_timerLock)
                if (_timer == null)
                    _timer = new Timer(_ => Tick(), null, interval, interval);
        }

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

        private void Tick()
        {
            try { RunOnce(); }
            catch { /* the timer must survive; the departures stay queued for the next tick */ }
        }

        private SessionReply Send(Item i)
        {
            switch (i.Kind)
            {
                case Kind.Depart: return _service.Depart(i.Session, i.Agent, i.Reason);
                case Kind.LeaveChat: return _service.LeaveChat(i.Session, i.Agent);
                case Kind.DepartByViewerSession: return _service.DepartByViewerSession(i.Agent, i.Viewer, i.Reason);
                default: return _service.DepartAll(i.Agent, i.Reason);
            }
        }

        private void Add(Item item)
        {
            bool full = false;
            lock (_lock)
            {
                item.At = _clock();
                if (_items.Count >= _max)
                {
                    _items.RemoveAt(0);
                    full = true;
                }
                _items.Add(item);
            }
            if (full) Drop("full");
        }

        private void Drop(string why)
        {
            Interlocked.Increment(ref _dropped);
            Lost?.Invoke(why);
        }
    }
}
