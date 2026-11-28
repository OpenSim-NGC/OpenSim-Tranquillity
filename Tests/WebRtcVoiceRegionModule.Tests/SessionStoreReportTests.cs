/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What "show voice sessions" tells an operator about where call sessions are kept.
 */
using System.Linq;
using Xunit;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    public class SessionStoreReportTests
    {
        private static string Text(SessionStoreReport r) => string.Join("\n", r.Format());

        [Fact(Timeout = 60000)]
        public void LocalMode_SaysSessionsAreKeptInThisProcess_AndHowManyAreHeld()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string text = Text(new SessionStoreReport { Grid = false, LocalSessions = 3 });

            Assert.Contains("kept in this process", text);
            Assert.Contains("3 held", text);
            Assert.Contains("off (no carrier secret)", text);
            Assert.DoesNotContain("grid session service", text);
        }

        [Fact(Timeout = 60000)]
        public void GridMode_NamesTheServiceItTalksTo_AndWhetherItAnswered()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string answered = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = true });
            string silent = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = false });
            string checking = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = null });

            {
                var assertedValue = answered;
                Assert.Contains("http://robust.example.org:8003", assertedValue);
                Assert.Contains("answered when this region started", assertedValue);
            }
            Assert.Contains("did not answer when this region started", silent);
            Assert.Contains("being checked", checking);
        }

        [Fact(Timeout = 60000)]
        public void GridMode_ReportsTheBreaker_TheSeatsHeld_AndTheQueuedDeparts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string text = Text(new SessionStoreReport
            {
                Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = true,
                Breaker = BreakerState.Open, HeldSeats = 4, QueuedDeparts = 2, DroppedDeparts = 1,
            });

            Assert.Contains("Circuit breaker: Open", text);
            Assert.Contains("holds 4 seat(s)", text);
            Assert.Contains("2 departure(s) waiting, 1 given up", text);
        }

        [Fact(Timeout = 60000)]
        public void GridMode_SaysConferenceTextIsKeyedByTheService_AndWhetherASecretIsAlsoSet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string without = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003" });
            string with = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", CarriersSigned = true });

            Assert.Contains("Conference text: signed with a key the service keeps", without);
            Assert.Contains("also signed with the shared carrier secret", with);
        }

        [Fact(Timeout = 60000)]
        public void TheReportNeverPrintsASecret()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string all = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", CarriersSigned = true });

            Assert.DoesNotContain("password", all.ToLowerInvariant());
            Assert.True(new SessionStoreReport().Format().Any());
        }
    }
}
