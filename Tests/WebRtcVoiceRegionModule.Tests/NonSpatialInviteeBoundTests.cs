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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void ParseInviteesDeDuplicatesInLinearTime_KeepingFirstSeenOrder()
        {
            List<UUID> distinct = People(300000);
            List<UUID> asked = new List<UUID>(distinct);
            asked.Add(UUID.Zero);
            asked.AddRange(distinct.Take(1000));   // repeats

            OSDMap body = new OSDMap { ["params"] = AsArray(asked) };
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            List<UUID> parsed = AdhocVoiceChatSession.ParseInvitees(body);
            watch.Stop();

            // A scan of the list per entry takes tens of seconds for this many; a set takes milliseconds.
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2000));

            Assert.That(parsed, Has.Count.EqualTo(300000));
            Assert.That(parsed.Take(3), Is.EqualTo(distinct.Take(3)));
            Assert.That(parsed, Does.Not.Contain(UUID.Zero));
        }

        [Test]
        public void AStartWithMoreNewInviteesThanTheConferenceHasRoomForIsRefusedWhole()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            // The conference cap is 50 and it includes whoever starts the call, so 49 can be invited and 50 cannot.
            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(StartBody(People(50)), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome), Is.True);

            Assert.That(outcome.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(outcome.Reply, Is.Null);
            Assert.That(outcome.Instrument, Does.Contain("decision=adhoc-refused-" + SessionOutcome.TooManyInvitees));
            Assert.That(e.Store.All(), Is.Empty, "nothing was created, and nobody was rung");
        }

        [Test]
        public void AStartAtTheLimitIsAccepted()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(StartBody(People(49)), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome), Is.True);

            Assert.That(outcome.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(e.Store.All(), Has.Count.EqualTo(1));
            Assert.That(e.Store.All()[0].Members, Has.Count.EqualTo(50), "the starter and 49 invitees fill the cap of 50");
        }

        [Test]
        public void TheStarterCountsAgainstARequestedSmallerCapToo()
        {
            NonSpatialVoiceSessionEngine e = Engine();

            Assert.That(e.StartAdhoc(Alice, UUID.Random(), RegionA, People(5), requestedCap: 5).Decision,
                        Is.EqualTo(SessionOutcome.TooManyInvitees), "five invitees and the starter are six people");
            Assert.That(e.StartAdhoc(Alice, UUID.Random(), RegionA, People(4), requestedCap: 5).Ok, Is.True,
                        "four invitees and the starter are five");
        }

        [Test]
        public void RepeatsAndTheCreatorDoNotCountAgainstTheLimit()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            List<UUID> asked = People(49);
            asked.AddRange(People(49));      // every invitee twice
            asked.Add(Alice);                // the creator inviting themselves
            asked.Add(UUID.Zero);

            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(StartBody(asked), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome), Is.True);

            Assert.That(outcome.Status, Is.EqualTo(HttpStatusCode.OK), outcome.Instrument);
        }

        [Test]
        public void AnInviteThatWouldOverfillTheRoomIsRefusedWhole_NotTruncated()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);   // two seats held, so 48 more people fit

            Assert.That(AdhocVoiceChatSession.TryHandleInvite(InviteBody(id, People(49)), Alice, e, true,
                out ChatSessionOutcome outcome, out List<UUID> rung), Is.True);

            Assert.That(outcome.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(outcome.Instrument, Does.Contain("decision=adhoc-refused-" + SessionOutcome.TooManyInvitees));
            Assert.That(rung, Is.Empty);
            Assert.That(e.Store.Get(id).Members, Has.Count.EqualTo(2), "not even the first 48 were invited");
        }

        [Test]
        public void AnInviteThatJustFitsIsAccepted_AndPeopleAlreadySeatedAreNotCounted()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            List<UUID> asked = People(48);
            asked.Add(Bob);   // already in the call: nothing new

            Assert.That(AdhocVoiceChatSession.TryHandleInvite(InviteBody(id, asked), Alice, e, true,
                out ChatSessionOutcome outcome, out List<UUID> rung), Is.True);

            Assert.That(outcome.Status, Is.EqualTo(HttpStatusCode.OK), outcome.Instrument);
            Assert.That(rung, Has.Count.EqualTo(48));
        }

        [Test]
        public void ARefusalLogsCountsAndNeverTheListOfIds()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            List<string> warnings = new();
            e.LimitRefused += (agent, decision, detail) => warnings.Add(decision + " " + detail);
            List<UUID> asked = People(60);

            AdhocVoiceChatSession.TryHandleStartConference(StartBody(asked), Alice, e, RegionA, true, out ChatSessionOutcome outcome);

            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("asked=60"));
            foreach (UUID id in asked.Take(5))
            {
                Assert.That(warnings[0], Does.Not.Contain(id.ToString()));
                Assert.That(outcome.Instrument, Does.Not.Contain(id.ToString()));
            }
        }

        [Test]
        public void AnInviteLineNamesHowManyWereRefusedButNotWho()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            UUID stranger = People(1)[0];
            // Bob is not in the call, so he cannot invite anyone: every invitee is refused individually.
            AdhocVoiceChatSession.TryHandleInvite(InviteBody(id, new[] { stranger }), Bob, e, true,
                out ChatSessionOutcome outcome, out _);

            Assert.That(outcome.Instrument, Does.Contain("refused=1"));
            Assert.That(outcome.Instrument, Does.Not.Contain(stranger.ToString()));
        }
    }
}
