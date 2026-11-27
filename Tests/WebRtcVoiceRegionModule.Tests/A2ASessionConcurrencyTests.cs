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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class A2ASessionConcurrencyTests
    {
        private static readonly UUID Caller = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly UUID Callee = new UUID("aaaaaaaa-0000-0000-0000-000000000002");

        private const int Rounds = 300;
        private const int Joiners = 8;

        [Fact(Timeout = 60000)]
        public async Task ConcurrentCalleeJoins_ExactlyOneBecomesActive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int tooMany = 0, none = 0;
            for (int round = 0; round < Rounds; round++)
            {
                A2ARegistry reg = new A2ARegistry();
                A2ASession s = reg.Record(Caller, Callee, out _);
                int winners = 0;
                using Barrier gate = new Barrier(Joiners);
                Task[] joins = Enumerable.Range(0, Joiners).Select(n => Task.Run(() =>
                {
                    gate.SignalAndWait(TestContext.Current.CancellationToken);
                    reg.MarkProvisioned(s.SessionId, Callee, "vs-" + n, out bool becameActive);
                    if (becameActive) Interlocked.Increment(ref winners);
                })).ToArray();
                await Task.WhenAll(joins);
                if (winners > 1) tooMany++;
                if (winners == 0) none++;
            }
            {
                var assertedValue = tooMany;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "rounds in which more than one join was told it made the call active");
            }
            {
                var assertedValue = none;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "rounds in which no join was told it made the call active");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARejoinOfAnActiveCall_IsNotATransition()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry reg = new A2ARegistry();
            A2ASession s = reg.Record(Caller, Callee, out _);
            reg.MarkProvisioned(s.SessionId, Callee, "vs-1", out bool first);
            reg.MarkProvisioned(s.SessionId, Callee, "vs-2", out bool again);
            reg.MarkProvisioned(s.SessionId, Caller, "vs-3", out bool callerJoin);
            Assert.True(first);
            Assert.False(again, "a reconnect of an already active call is not a new accept");
            Assert.False(callerJoin, "the caller joining never makes the call active");
        }

        [Fact(Timeout = 60000)]
        public async Task ReadingARecordWhileItChanges_NeverFails()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry reg = new A2ARegistry();
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
            await Task.WhenAll(readers);
            Assert.Empty(failures.Select(f => f.GetType().Name + ": " + f.Message).Distinct().ToList());
        }
    }
}
