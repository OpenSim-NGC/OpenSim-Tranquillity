/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The registry hands out the call record it keeps, and requests read it without the registry's lock.
 * The record holds no collection, so a read cannot throw, but a decision made from two reads can be
 * wrong when another request changes the record in between. The one that matters is "did this join
 * make the call active?": it decides whether the two parties are told about each other, and it must
 * be answered once, inside the lock, however many requests join at the same moment.
 */
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2ASessionConcurrencyTests
    {
        private static readonly UUID Caller = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly UUID Callee = new UUID("aaaaaaaa-0000-0000-0000-000000000002");

        private const int Rounds = 300;
        private const int Joiners = 8;

        [Test]
        public void ConcurrentCalleeJoins_ExactlyOneBecomesActive()
        {
            int tooMany = 0, none = 0;
            for (int round = 0; round < Rounds; round++)
            {
                A2ASessionRegistry reg = new A2ASessionRegistry();
                A2ASession s = reg.Record(Caller, Callee, out _);
                int winners = 0;
                using Barrier gate = new Barrier(Joiners);
                Task[] joins = Enumerable.Range(0, Joiners).Select(n => Task.Run(() =>
                {
                    gate.SignalAndWait();
                    reg.MarkProvisioned(s.SessionId, Callee, "vs-" + n, out bool becameActive);
                    if (becameActive) Interlocked.Increment(ref winners);
                })).ToArray();
                Task.WaitAll(joins);
                if (winners > 1) tooMany++;
                if (winners == 0) none++;
            }
            Assert.That(tooMany, Is.EqualTo(0), "rounds in which more than one join was told it made the call active");
            Assert.That(none, Is.EqualTo(0), "rounds in which no join was told it made the call active");
        }

        [Test]
        public void ARejoinOfAnActiveCall_IsNotATransition()
        {
            A2ASessionRegistry reg = new A2ASessionRegistry();
            A2ASession s = reg.Record(Caller, Callee, out _);
            reg.MarkProvisioned(s.SessionId, Callee, "vs-1", out bool first);
            reg.MarkProvisioned(s.SessionId, Callee, "vs-2", out bool again);
            reg.MarkProvisioned(s.SessionId, Caller, "vs-3", out bool callerJoin);
            Assert.That(first, Is.True);
            Assert.That(again, Is.False, "a reconnect of an already active call is not a new accept");
            Assert.That(callerJoin, Is.False, "the caller joining never makes the call active");
        }

        [Test]
        public void ReadingARecordWhileItChanges_NeverFails()
        {
            A2ASessionRegistry reg = new A2ASessionRegistry();
            A2ASession s = reg.Record(Caller, Callee, out _);
            ConcurrentQueue<Exception> failures = new ConcurrentQueue<Exception>();
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task[] readers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        A2ASession r = reg.TryGet(s.SessionId);
                        if (r is null) continue;
                        _ = r.State; _ = r.Token; _ = r.InviteSent; _ = r.IsProvisioned(Callee);
                    }
                    catch (Exception ex) { failures.Enqueue(ex); }
                }
            })).ToArray();
            for (int i = 0; i < 2000; i++)
            {
                reg.IssueToken(s.SessionId, Caller);
                reg.MarkProvisioned(s.SessionId, Callee, "vs-" + i, out _);
                reg.MarkGone(Callee, null);
                reg.Record(Caller, Callee, out _);
            }
            stop.Cancel();
            Task.WaitAll(readers);
            Assert.That(failures.Select(f => f.GetType().Name + ": " + f.Message).Distinct().ToList(), Is.Empty);
        }
    }
}
