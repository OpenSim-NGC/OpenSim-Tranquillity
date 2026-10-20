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
using NUnit.Framework;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class SessionStoreReportTests
    {
        private static string Text(SessionStoreReport r) => string.Join("\n", r.Format());

        [Test]
        public void LocalMode_SaysSessionsAreKeptInThisProcess_AndHowManyAreHeld()
        {
            string text = Text(new SessionStoreReport { Grid = false, LocalSessions = 3 });

            Assert.That(text, Does.Contain("kept in this process"));
            Assert.That(text, Does.Contain("3 held"));
            Assert.That(text, Does.Contain("off (no carrier secret)"));
            Assert.That(text, Does.Not.Contain("grid session service"));
        }

        [Test]
        public void GridMode_NamesTheServiceItTalksTo_AndWhetherItAnswered()
        {
            string answered = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = true });
            string silent = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = false });
            string checking = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = null });

            Assert.That(answered, Does.Contain("http://robust.example.org:8003").And.Contain("answered when this region started"));
            Assert.That(silent, Does.Contain("did not answer when this region started"));
            Assert.That(checking, Does.Contain("being checked"));
        }

        [Test]
        public void GridMode_ReportsTheBreaker_TheSeatsHeld_AndTheQueuedDeparts()
        {
            string text = Text(new SessionStoreReport
            {
                Grid = true, ServerUri = "http://robust.example.org:8003", ProbeAnswered = true,
                Breaker = BreakerState.Open, HeldSeats = 4, QueuedDeparts = 2, DroppedDeparts = 1,
            });

            Assert.That(text, Does.Contain("Circuit breaker: Open"));
            Assert.That(text, Does.Contain("holds 4 seat(s)"));
            Assert.That(text, Does.Contain("2 departure(s) waiting, 1 given up"));
        }

        [Test]
        public void GridMode_SaysConferenceTextIsKeyedByTheService_AndWhetherASecretIsAlsoSet()
        {
            string without = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003" });
            string with = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", CarriersSigned = true });

            Assert.That(without, Does.Contain("Conference text: signed with a key the service keeps"));
            Assert.That(with, Does.Contain("also signed with the shared carrier secret"));
        }

        [Test]
        public void TheReportNeverPrintsASecret()
        {
            string all = Text(new SessionStoreReport { Grid = true, ServerUri = "http://robust.example.org:8003", CarriersSigned = true });

            Assert.That(all.ToLowerInvariant(), Does.Not.Contain("password"));
            Assert.That(new SessionStoreReport().Format().Any(), Is.True);
        }
    }
}
