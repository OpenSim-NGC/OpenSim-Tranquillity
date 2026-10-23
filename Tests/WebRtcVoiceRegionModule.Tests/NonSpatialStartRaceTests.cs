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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialStartRaceTests
    {
        [Test]
        [Repeat(20)]
        public void SimultaneousRetriesOfOneAdhocStartMakeExactlyOneSession()
        {
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
                gate.SignalAndWait();
                return e.StartAdhoc(Alice, temp, RegionA);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(tasks);

            Assert.That(e.Store.All(), Has.Count.EqualTo(1), "one conference for one temp id");
            Assert.That(tasks.Select(t => t.Result.Session.SessionId).Distinct().Count(), Is.EqualTo(1),
                        "every retry was handed the same session");
            Assert.That(tasks.Count(t => t.Result.Created), Is.EqualTo(1), "exactly one attempt created it");
        }
    }
}
