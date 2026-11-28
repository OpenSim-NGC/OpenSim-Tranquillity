/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * How many people one "start conference" or "invite" may ring. The invitee list is de-duplicated in
 * linear time, and a request that would add more new people than the conference has room for is
 * refused as a whole: it is never cut down to the first N. Refusals log counts, never the ids.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialInviteeBoundTests
    {
        private static List<UUID> People(int count)
        {
            List<UUID> ids = new List<UUID>();
            for (int i = 1; i <= count; i++)
                ids.Add(new UUID(new Guid(0x70000000 + i, 0, 0, new byte[8])));
            return ids;
        }

        private static OSDArray AsArray(IEnumerable<UUID> ids)
        {
            OSDArray arr = new OSDArray();
            foreach (UUID id in ids) arr.Add(OSD.FromUUID(id));
            return arr;
        }

        private static OSDMap StartBody(IEnumerable<UUID> invitees) => new OSDMap
        {
            ["method"] = "start conference",
            ["session-id"] = UUID.Random(),
            ["params"] = AsArray(invitees),
        };

        private static OSDMap InviteBody(UUID session, IEnumerable<UUID> invitees) => new OSDMap
        {
            ["method"] = "invite",
            ["session-id"] = session,
            ["params"] = AsArray(invitees),
        };

        // Wide limits so the rate limit and session caps never get in the way of what is being measured.
        private static NonSpatialVoiceSessionEngine Engine()
            => NewEngine(limits: new NonSpatialLimits { MaxNonSpatialSessions = 100000, MaxSessionsPerAgent = 100000 });

        [Fact(Timeout = 60000)]
        public void ParseInviteesDeDuplicatesInLinearTime_KeepingFirstSeenOrder()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<UUID> distinct = People(300000);
            List<UUID> asked = new List<UUID>(distinct);
            asked.Add(UUID.Zero);
            asked.AddRange(distinct.Take(1000));   // repeats

            OSDMap body = new OSDMap { ["params"] = AsArray(asked) };
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            List<UUID> parsed = AdhocVoiceChatSession.ParseInvitees(body);
            watch.Stop();

            // A scan of the list per entry takes tens of seconds for this many; a set takes milliseconds.
            Assert.True(watch.ElapsedMilliseconds < 2000);

            Assert.Equal(300000, parsed.Count());
            Assert.Equal(distinct.Take(3), parsed.Take(3));
            Assert.DoesNotContain(UUID.Zero, parsed);
        }

        [Fact(Timeout = 60000)]
        public void AStartWithMoreNewInviteesThanTheConferenceHasRoomForIsRefusedWhole()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            // The conference cap is 50 and it includes whoever starts the call, so 49 can be invited and 50 cannot.
            Assert.True(AdhocVoiceChatSession.TryHandleStartConference(StartBody(People(50)), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome));

            Assert.Equal(HttpStatusCode.Forbidden, outcome.Status);
            Assert.Null(outcome.Reply);
            Assert.Contains("decision=adhoc-refused-" + SessionOutcome.TooManyInvitees, outcome.Instrument);
            {
                var assertedValue = e.Store.All();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing was created, and nobody was rung");
            }
        }

        [Fact(Timeout = 60000)]
        public void AStartAtTheLimitIsAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            Assert.True(AdhocVoiceChatSession.TryHandleStartConference(StartBody(People(49)), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome));

            Assert.Equal(HttpStatusCode.OK, outcome.Status);
            Assert.Single(e.Store.All());
            {
                var assertedValue = e.Store.All()[0].Members;
                TestAssert.WithMessage(() => Assert.Equal(50, assertedValue.Count()), "the starter and 49 invitees fill the cap of 50");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheStarterCountsAgainstARequestedSmallerCapToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();

            {
                var assertedValue = e.StartAdhoc(Alice, UUID.Random(), RegionA, People(5), requestedCap: 5).Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.TooManyInvitees, assertedValue), "five invitees and the starter are six people");
            }
            Assert.True(e.StartAdhoc(Alice, UUID.Random(), RegionA, People(4), requestedCap: 5).Ok, "four invitees and the starter are five");
        }

        [Fact(Timeout = 60000)]
        public void RepeatsAndTheCreatorDoNotCountAgainstTheLimit()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            List<UUID> asked = People(49);
            asked.AddRange(People(49));      // every invitee twice
            asked.Add(Alice);                // the creator inviting themselves
            asked.Add(UUID.Zero);

            Assert.True(AdhocVoiceChatSession.TryHandleStartConference(StartBody(asked), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome));

            {
                var assertedValue = outcome.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), outcome.Instrument);
            }
        }

        [Fact(Timeout = 60000)]
        public void AnInviteThatWouldOverfillTheRoomIsRefusedWhole_NotTruncated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);   // two seats held, so 48 more people fit

            Assert.True(AdhocVoiceChatSession.TryHandleInvite(InviteBody(id, People(49)), Alice, e, true,
                out ChatSessionOutcome outcome, out List<UUID> rung));

            Assert.Equal(HttpStatusCode.Forbidden, outcome.Status);
            Assert.Contains("decision=adhoc-refused-" + SessionOutcome.TooManyInvitees, outcome.Instrument);
            Assert.Empty(rung);
            {
                var assertedValue = e.Store.Get(id).Members;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "not even the first 48 were invited");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnInviteThatJustFitsIsAccepted_AndPeopleAlreadySeatedAreNotCounted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            List<UUID> asked = People(48);
            asked.Add(Bob);   // already in the call: nothing new

            Assert.True(AdhocVoiceChatSession.TryHandleInvite(InviteBody(id, asked), Alice, e, true,
                out ChatSessionOutcome outcome, out List<UUID> rung));

            {
                var assertedValue = outcome.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), outcome.Instrument);
            }
            Assert.Equal(48, rung.Count());
        }

        [Fact(Timeout = 60000)]
        public void ARefusalLogsCountsAndNeverTheListOfIds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            List<string> warnings = new();
            e.LimitRefused += (agent, decision, detail) => warnings.Add(decision + " " + detail);
            List<UUID> asked = People(60);

            AdhocVoiceChatSession.TryHandleStartConference(StartBody(asked), Alice, e, RegionA, true, out ChatSessionOutcome outcome);

            Assert.Single(warnings);
            Assert.Contains("asked=60", warnings[0]);
            foreach (UUID id in asked.Take(5))
            {
                Assert.DoesNotContain(id.ToString(), warnings[0]);
                Assert.DoesNotContain(id.ToString(), outcome.Instrument);
            }
        }

        [Fact(Timeout = 60000)]
        public void AnInviteLineNamesHowManyWereRefusedButNotWho()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            UUID stranger = People(1)[0];
            // Bob is not in the call, so he cannot invite anyone: every invitee is refused individually.
            AdhocVoiceChatSession.TryHandleInvite(InviteBody(id, new[] { stranger }), Bob, e, true,
                out ChatSessionOutcome outcome, out _);

            Assert.Contains("refused=1", outcome.Instrument);
            Assert.DoesNotContain(stranger.ToString(), outcome.Instrument);
        }
    }
}
