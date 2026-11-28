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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void TheDefaultsAreABurstOfFiveRefilledEveryTenSeconds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(5, NonSpatialLimits.DefaultConferenceRequestBurst);
            Assert.Equal(10, NonSpatialLimits.DefaultConferenceRequestRefillSeconds);
            Assert.Equal(5, NonSpatialLimits.Default.ConferenceRequestBurst);
            Assert.Equal(10, NonSpatialLimits.Default.ConferenceRequestRefillSeconds);
        }

        [Fact(Timeout = 60000)]
        public void TheBurstPassesAndTheNextRequestIsRefused_ThenOneTokenReturnsPerRefillInterval()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits());

            for (int i = 0; i < 5; i++)
                Assert.True(e.TryTakeConferenceRequest(Alice), "request " + (i + 1));
            Assert.False(e.TryTakeConferenceRequest(Alice), "the sixth in the same instant");

            clock.Now = clock.Now.AddSeconds(9);
            Assert.False(e.TryTakeConferenceRequest(Alice), "nine seconds is not a refill");
            clock.Now = clock.Now.AddSeconds(1);
            Assert.True(e.TryTakeConferenceRequest(Alice), "ten seconds returns one token");
            Assert.False(e.TryTakeConferenceRequest(Alice));

            clock.Now = clock.Now.AddMinutes(10);
            for (int i = 0; i < 5; i++)
                Assert.True(e.TryTakeConferenceRequest(Alice), "the bucket refills to the burst and no further");
            Assert.False(e.TryTakeConferenceRequest(Alice));
        }

        [Fact(Timeout = 60000)]
        public void EachAgentHasItsOwnBucket()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(burst: 1));

            Assert.True(e.TryTakeConferenceRequest(Alice));
            Assert.False(e.TryTakeConferenceRequest(Alice));
            Assert.True(e.TryTakeConferenceRequest(Bob));
        }

        [Fact(Timeout = 60000)]
        public void StartAndInviteDrawFromTheSameBucket()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits());
            UUID conference = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            for (int i = 0; i < 3; i++)
                Assert.True(AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true,
                    out ChatSessionOutcome started));
            for (int i = 0; i < 2; i++)
            {
                Assert.True(AdhocVoiceChatSession.TryHandleInvite(InviteBody(conference), Alice, e, true,
                    out ChatSessionOutcome invited, out _));
                {
                    var assertedValue = invited.Status;
                    TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), "invite " + (i + 1));
                }
            }

            // Five tokens are spent: the sixth request is refused whichever kind it is.
            AdhocVoiceChatSession.TryHandleInvite(InviteBody(conference), Alice, e, true, out ChatSessionOutcome refusedInvite, out List<UUID> rung);
            Assert.Equal(HttpStatusCode.Forbidden, refusedInvite.Status);
            Assert.Contains("decision=adhoc-refused-" + SessionOutcome.RateLimited, refusedInvite.Instrument);
            Assert.Empty(rung);

            AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true, out ChatSessionOutcome refusedStart);
            Assert.Equal(HttpStatusCode.Forbidden, refusedStart.Status);
            Assert.Null(refusedStart.Reply);
            Assert.Contains("decision=adhoc-refused-" + SessionOutcome.RateLimited, refusedStart.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void ARefusedStartCreatesNoSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(burst: 1));
            AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true, out _);
            int before = e.Store.All().Count;

            AdhocVoiceChatSession.TryHandleStartConference(StartBody(), Alice, e, RegionA, true, out ChatSessionOutcome refused);

            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Equal(before, e.Store.All().Count);
        }

        [Fact(Timeout = 60000)]
        public void ARefusalIsWarnedAtMostOncePerAgentPerMinute_WithCountsOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(burst: 1));
            List<string> lines = new();
            e.LimitRefused += (agent, decision, detail) => lines.Add(decision + " " + detail);
            e.TryTakeConferenceRequest(Alice);

            for (int i = 0; i < 10; i++) e.TryTakeConferenceRequest(Alice);

            Assert.Single(lines);
            Assert.StartsWith(SessionOutcome.RateLimited, lines[0]);
        }

        [Fact(Timeout = 60000)]
        public void TheMaintenancePassDropsTheBucketsOfAgentsUnseenForTenMinutes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits());
            NonSpatialMaintenance m = new NonSpatialMaintenance(e, _ => { }, (_, _) => { });
            e.TryTakeConferenceRequest(Alice);
            clock.Now = clock.Now.AddMinutes(9);
            e.TryTakeConferenceRequest(Bob);
            Assert.Equal(2, e.RequestLimiter.TrackedAgents);

            clock.Now = clock.Now.AddMinutes(2);   // Alice 11 minutes ago, Bob 2
            m.RunOnce();

            {
                var assertedValue = e.RequestLimiter.TrackedAgents;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "only Bob is still tracked");
            }
            Assert.True(e.TryTakeConferenceRequest(Alice), "and Alice starts again with a full bucket");
        }

        [Fact(Timeout = 60000)]
        public void ADroppedBucketIsNeverMoreGenerousThanAKeptOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Dropping the state of an agent unseen for ten minutes is only safe because a bucket left
            // alone for the refill-to-full time is full anyway: forgetting it changes nothing.
            double fullAfterSeconds = NonSpatialLimits.DefaultConferenceRequestBurst
                                      * NonSpatialLimits.DefaultConferenceRequestRefillSeconds;
            Assert.True(fullAfterSeconds < NonSpatialVoiceSessionEngine.TransientStateMaxAge.TotalSeconds);
        }
    }
}
