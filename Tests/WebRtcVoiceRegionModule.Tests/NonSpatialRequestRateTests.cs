/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A per-agent token bucket on "start conference" and "invite". The two draw from the same bucket, a request
 * over the limit is refused with its own decision, and the maintenance pass drops the bucket of an agent
 * not seen for ten minutes.
 */
using System;
using System.Collections.Generic;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialRequestRateTests
    {
        // Session caps wide open so only the rate limit is in play.
        private static NonSpatialLimits Limits(int burst = 5, double refillSeconds = 10)
            => new NonSpatialLimits
            {
                MaxNonSpatialSessions = 100000,
                MaxSessionsPerAgent = 100000,
                ConferenceRequestBurst = burst,
                ConferenceRequestRefillSeconds = refillSeconds,
            };

        private static OSDMap StartBody() => new OSDMap
        {
            ["method"] = "start conference",
            ["session-id"] = UUID.Random(),
            ["params"] = new OSDArray { OSD.FromUUID(Bob) },
        };

        private static OSDMap InviteBody(UUID session) => new OSDMap
        {
            ["method"] = "invite",
            ["session-id"] = session,
            ["params"] = new OSDArray { OSD.FromUUID(Carol) },
        };

        [Test]
        public void TheDefaultsAreABurstOfFiveRefilledEveryTenSeconds()
        {
            Assert.That(NonSpatialLimits.DefaultConferenceRequestBurst, Is.EqualTo(5));
            Assert.That(NonSpatialLimits.DefaultConferenceRequestRefillSeconds, Is.EqualTo(10));
            Assert.That(NonSpatialLimits.Default.ConferenceRequestBurst, Is.EqualTo(5));
            Assert.That(NonSpatialLimits.Default.ConferenceRequestRefillSeconds, Is.EqualTo(10));
        }

        [Test]
        public void TheBurstPassesAndTheNextRequestIsRefused_ThenOneTokenReturnsPerRefillInterval()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits());

            for (int i = 0; i < 5; i++)
                Assert.That(e.TryTakeConferenceRequest(Alice), Is.True, "request " + (i + 1));
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.False, "the sixth in the same instant");

            clock.Now = clock.Now.AddSeconds(9);
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.False, "nine seconds is not a refill");
            clock.Now = clock.Now.AddSeconds(1);
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.True, "ten seconds returns one token");
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.False);

            clock.Now = clock.Now.AddMinutes(10);
            for (int i = 0; i < 5; i++)
                Assert.That(e.TryTakeConferenceRequest(Alice), Is.True, "the bucket refills to the burst and no further");
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.False);
        }

        [Test]
        public void EachAgentHasItsOwnBucket()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(burst: 1));

            Assert.That(e.TryTakeConferenceRequest(Alice), Is.True);
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.False);
            Assert.That(e.TryTakeConferenceRequest(Bob), Is.True);
        }

        [Test]
        public void StartAndInviteDrawFromTheSameBucket()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits());
            UUID conference = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            for (int i = 0; i < 3; i++)
                Assert.That(AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true,
                    out ChatSessionOutcome started), Is.True);
            for (int i = 0; i < 2; i++)
            {
                Assert.That(AdhocVoiceChatSession.TryHandleInvite(InviteBody(conference), Alice, e, true,
                    out ChatSessionOutcome invited, out _), Is.True);
                Assert.That(invited.Status, Is.EqualTo(HttpStatusCode.OK), "invite " + (i + 1));
            }

            // Five tokens are spent: the sixth request is refused whichever kind it is.
            AdhocVoiceChatSession.TryHandleInvite(InviteBody(conference), Alice, e, true, out ChatSessionOutcome refusedInvite, out List<UUID> rung);
            Assert.That(refusedInvite.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(refusedInvite.Instrument, Does.Contain("decision=adhoc-refused-" + SessionOutcome.RateLimited));
            Assert.That(rung, Is.Empty);

            AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true, out ChatSessionOutcome refusedStart);
            Assert.That(refusedStart.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(refusedStart.Reply, Is.Null);
            Assert.That(refusedStart.Instrument, Does.Contain("decision=adhoc-refused-" + SessionOutcome.RateLimited));
        }

        [Test]
        public void ARefusedStartCreatesNoSession()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(burst: 1));
            AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true, out _);
            int before = e.Store.All().Count;

            AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true, out ChatSessionOutcome refused);

            Assert.That(refused.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(e.Store.All().Count, Is.EqualTo(before));
        }

        [Test]
        public void ARefusalIsWarnedAtMostOncePerAgentPerMinute_WithCountsOnly()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(burst: 1));
            List<string> lines = new();
            e.LimitRefused += (agent, decision, detail) => lines.Add(decision + " " + detail);
            e.TryTakeConferenceRequest(Alice);

            for (int i = 0; i < 10; i++) e.TryTakeConferenceRequest(Alice);

            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0], Does.StartWith(SessionOutcome.RateLimited));
        }

        [Test]
        public void TheMaintenancePassDropsTheBucketsOfAgentsUnseenForTenMinutes()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits());
            NonSpatialMaintenance m = new NonSpatialMaintenance(e, _ => { }, (_, _) => { });
            e.TryTakeConferenceRequest(Alice);
            clock.Now = clock.Now.AddMinutes(9);
            e.TryTakeConferenceRequest(Bob);
            Assert.That(e.RequestLimiter.TrackedAgents, Is.EqualTo(2));

            clock.Now = clock.Now.AddMinutes(2);   // Alice 11 minutes ago, Bob 2
            m.RunOnce();

            Assert.That(e.RequestLimiter.TrackedAgents, Is.EqualTo(1), "only Bob is still tracked");
            Assert.That(e.TryTakeConferenceRequest(Alice), Is.True, "and Alice starts again with a full bucket");
        }

        [Test]
        public void ADroppedBucketIsNeverMoreGenerousThanAKeptOne()
        {
            // Dropping the state of an agent unseen for ten minutes is only safe because a bucket left
            // alone for the refill-to-full time is full anyway: forgetting it changes nothing.
            double fullAfterSeconds = NonSpatialLimits.DefaultConferenceRequestBurst
                                      * NonSpatialLimits.DefaultConferenceRequestRefillSeconds;
            Assert.That(fullAfterSeconds, Is.LessThan(NonSpatialVoiceSessionEngine.TransientStateMaxAge.TotalSeconds));
        }
    }
}
