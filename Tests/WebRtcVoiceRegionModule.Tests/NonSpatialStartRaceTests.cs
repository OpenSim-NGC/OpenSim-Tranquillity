/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A retried "start conference" carries the same temp session id, and the engine finds the session the first
 * attempt made. Finding and creating used to be two steps, so simultaneous retries could each miss the other
 * and open a session apiece. They are one step now.
 */
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialStartRaceTests
    {
        public static TheoryData<int> Repetitions => new(Enumerable.Range(1, 20));

        [Theory(Timeout = 60000)]
        [MemberData(nameof(Repetitions))]
        public async Task SimultaneousRetriesOfOneAdhocStartMakeExactlyOneSession(int repetition)
        {
            TestContext.Current.TestOutputHelper.WriteLine("Repetition {0}", repetition);
            NonSpatialVoiceSessionEngine e = NewEngine(limits: new NonSpatialLimits
            {
                MaxNonSpatialSessions = 100000,
                MaxSessionsPerAgent = 100000,
            });
            UUID temp = UUID.Random();
            const int attempts = 64;
            using Barrier gate = new Barrier(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(_ => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait(TestContext.Current.CancellationToken);
                return e.StartAdhoc(Alice, temp, RegionA);
            }, TaskCreationOptions.LongRunning)).ToArray();
            SessionOutcome[] results = await Task.WhenAll(tasks);

            {
                var assertedValue = e.Store.All();
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "one conference for one temp id");
            }
            {
                var assertedValue = results.Select(t => t.Session.SessionId).Distinct().Count();
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "every retry was handed the same session");
            }
            {
                var assertedValue = results.Count(t => t.Created);
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "exactly one attempt created it");
            }
        }
    }
}
