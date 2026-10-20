/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The grid call session service and the region client that talks to it. Two simulated regions
 * (two GridNonSpatialSessions) share one VoiceSessionService, which is what two region processes
 * sharing the one service in Robust are. Each grid-wide claim is also shown to fail when every
 * region has a service of its own, which is how the sessions behave today with one store per process.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceSessionServiceTests
    {
        private const string GridId = "example-grid";
        private static readonly UUID GroupId = NonSpatialIdentityTests.Group;

        internal static VoiceSessionService NewService(FakeClock clock = null, NonSpatialLimits limits = null,
                                                       TimeSpan? seatLease = null, TimeSpan? inviteTtl = null)
            => new VoiceSessionService(GridId, limits, (clock ?? new FakeClock()).Get, inviteTtl: inviteTtl, seatLease: seatLease);

        internal static GridNonSpatialSessions NewRegion(IVoiceSessionService service)
            => new GridNonSpatialSessions(service, new INonSpatialAdmission[]
            {
                new P2PAdmission(),
                new AdhocAdmission(),
                new GroupAdmission((agent, group) => true, (agent, group) => true),
            });

        private static UUID[] Agents(int n) => Enumerable.Range(0, n).Select(_ => UUID.Random()).ToArray();

        private static NonSpatialLimits Unlimited() => new NonSpatialLimits
        {
            MaxNonSpatialSessions = 1000000,
            MaxSessionsPerAgent = 1000000,
        };

        // ---- the seat cap is counted once for the whole grid ----------------------------------------

        [Test]
        public void TheSeatCapHoldsAcrossTwoRegions()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);

            SessionOutcome started = a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Assert.That(started.Ok, Is.True, started.Decision);

            int accepted = 0, refused = 0;
            UUID[] members = Agents(60);
            for (int i = 0; i < members.Length; i++)
            {
                GridNonSpatialSessions region = i % 2 == 0 ? a : b;
                SessionOutcome o = region.Accept(GroupId, members[i], i % 2 == 0 ? RegionA : RegionB);
                if (o.Ok) accepted++;
                else if (o.Decision == SessionOutcome.Capacity) refused++;
            }

            Assert.That(accepted, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap - 1), "49 more seats after the creator");
            Assert.That(refused, Is.EqualTo(members.Length - accepted), "everyone else is told the room is full");
            Assert.That(a.Get(GroupId).SeatsHeld, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap));
            Assert.That(b.Get(GroupId).SeatsHeld, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap), "both regions see the one count");
        }

        [Test]
        public void WithoutASharedService_EachRegionCountsItsOwnCap()
        {
            // The behaviour the grid service replaces: a record per region process, so 50 seats in each.
            GridNonSpatialSessions a = NewRegion(NewService());
            GridNonSpatialSessions b = NewRegion(NewService());
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            b.Start(NonSpatialSessionType.Group, Bob, GroupId, GroupId, RegionB);
            int seats = 0;
            foreach (UUID m in Agents(60))
            {
                if (a.Accept(GroupId, m, RegionA).Ok) seats++;
                if (b.Accept(GroupId, m, RegionB).Ok) seats++;
            }
            Assert.That(seats + 2, Is.GreaterThan(NonSpatialCaps.DefaultConferenceCap), "one cap per process, not per call");
        }

        // ---- a group is rung once ------------------------------------------------------------------

        [Test]
        public void AGroupRingsOnce_WhoeverJoinsFromWhichRegion()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);

            SessionOutcome opened = a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            SessionOutcome joined = b.Accept(GroupId, Bob, RegionB);
            SessionOutcome joinedAgain = b.Start(NonSpatialSessionType.Group, Carol, GroupId, GroupId, RegionB);

            Assert.That(opened.StartedRinging, Is.True, "the opener's region rings the group");
            Assert.That(joined.Ok, Is.True, joined.Decision);
            Assert.That(joined.StartedRinging, Is.False, "a join from another region does not ring it again");
            Assert.That(joinedAgain.StartedRinging, Is.False);
        }

        [Test]
        public void WithoutASharedService_BothRegionsRingTheGroup()
        {
            GridNonSpatialSessions a = NewRegion(NewService());
            GridNonSpatialSessions b = NewRegion(NewService());
            bool aRang = a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA).StartedRinging;
            bool bRang = b.Start(NonSpatialSessionType.Group, Bob, GroupId, GroupId, RegionB).StartedRinging;
            Assert.That(aRang && bRang, Is.True, "the double ring the shared record prevents");
        }

        [Test]
        public void TheGroupRingsAgain_AfterTheRoomEmpties()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            b.Accept(GroupId, Bob, RegionB);
            a.Depart(GroupId, Alice, DepartureReason.VoiceTeardown);
            b.Depart(GroupId, Bob, DepartureReason.VoiceTeardown);

            b.Start(NonSpatialSessionType.Group, Carol, GroupId, GroupId, RegionB);
            SessionOutcome again = b.Accept(GroupId, Carol, RegionB);

            Assert.That(again.StartedRinging, Is.True, "the first seat of a later call is a new ring cycle");
        }

        // ---- two regions racing -------------------------------------------------------------------

        [Test]
        [Repeat(10)]
        public void TwoRegionsRacingToStartOneGroupCallGetOneSession()
        {
            VoiceSessionService service = NewService(limits: Unlimited());
            GridNonSpatialSessions[] regions = { NewRegion(service), NewRegion(service) };
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            UUID[] callers = Agents(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                return regions[i % 2].Start(NonSpatialSessionType.Group, callers[i], GroupId, GroupId, i % 2 == 0 ? RegionA : RegionB);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(tasks);

            Assert.That(service.SessionCount, Is.EqualTo(1));
            Assert.That(tasks.Count(t => t.Result.Created), Is.EqualTo(1), "one start made the session");
            Assert.That(tasks.Count(t => t.Result.StartedRinging), Is.EqualTo(1), "one start rang the group");
        }

        [Test]
        [Repeat(10)]
        public void TwoRegionsRetryingOneConferenceStartGetOneSession()
        {
            VoiceSessionService service = NewService(limits: Unlimited());
            GridNonSpatialSessions[] regions = { NewRegion(service), NewRegion(service) };
            UUID temp = UUID.Random();
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                return regions[i % 2].StartAdhoc(Alice, temp, i % 2 == 0 ? RegionA : RegionB);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(tasks);

            Assert.That(service.SessionCount, Is.EqualTo(1));
            Assert.That(tasks.Select(t => t.Result.Session.SessionId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(tasks.Count(t => t.Result.Created), Is.EqualTo(1));
        }

        // ---- a region restarting ------------------------------------------------------------------

        [Test]
        public void ARestartedRegionReleasesItsSeatsAndNoOthers()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            UUID onB = UUID.Random();
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA);
            b.Accept(GroupId, onB, RegionB);

            int released = a.DepartRegion(RegionA);

            Assert.That(released, Is.EqualTo(2), "the creator's seat and Bob's, both taken from region A");
            NonSpatialVoiceSession s = b.Get(GroupId);
            Assert.That(s.SeatsHeld, Is.EqualTo(1));
            Assert.That(s.Find(onB).HoldsSeat, Is.True, "region B's seat is untouched");
            Assert.That(s.Find(Bob).Departure, Is.EqualTo(DepartureReason.PresenceLost));
        }

        [Test]
        public void ASeatNobodyRenews_IsReleasedWhenItsLeaseRunsOut_AndARenewedSeatIsKept()
        {
            FakeClock clock = new FakeClock();
            VoiceSessionService service = NewService(clock, seatLease: TimeSpan.FromSeconds(180));
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA);

            clock.Now += TimeSpan.FromSeconds(120);
            a.RenewSeats(new List<SeatKey> { new SeatKey(GroupId, Alice) });
            clock.Now += TimeSpan.FromSeconds(100);          // 220 s since Bob was seated, 100 s since Alice renewed
            VoiceSessionService.SweepResult swept = service.Sweep();

            NonSpatialVoiceSession s = a.Get(GroupId);
            Assert.That(swept.LeasesExpired, Is.EqualTo(1));
            Assert.That(s.Find(Alice).HoldsSeat, Is.True, "renewed, so kept");
            Assert.That(s.Find(Bob).HoldsSeat, Is.False, "never renewed, so released");
            Assert.That(s.Find(Bob).Departure, Is.EqualTo(DepartureReason.PresenceLost));
        }

        [Test]
        public void ACallWhoseMembersAreRenewed_IsNotCollectedAsIdle()
        {
            FakeClock clock = new FakeClock();
            VoiceSessionService service = NewService(clock, seatLease: TimeSpan.FromSeconds(180));
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            for (int hour = 0; hour < 9; hour++)
            {
                for (int step = 0; step < 6; step++)
                {
                    clock.Now += TimeSpan.FromMinutes(10);
                    a.RenewSeats(new List<SeatKey> { new SeatKey(GroupId, Alice) });
                    service.Sweep();
                }
            }

            Assert.That(a.Get(GroupId), Is.Not.Null, "nine hours of renewed seats is not idle");
        }

        // ---- the service does not answer ----------------------------------------------------------

        private sealed class DownService : IVoiceSessionService
        {
            public int Calls;
            private SessionReply Down() { Calls++; return null; }
            public SessionReply Start(int type, UUID creator, UUID otherOrOwner, UUID tempSessionId, UUID originRegion, int requestedCap) => Down();
            public SessionReply StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion, IReadOnlyList<UUID> invitees, int requestedCap) => Down();
            public SessionReply CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyList<UUID> asked) => Down();
            public SessionReply Invite(UUID sessionId, UUID inviter, UUID invitee) => Down();
            public SessionReply Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession) => Down();
            public SessionReply MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession) => Down();
            public SessionReply Decline(UUID sessionId, UUID agent) => Down();
            public SessionReply Depart(UUID sessionId, UUID agent, int reason) => Down();
            public SessionReply LeaveChat(UUID sessionId, UUID agent) => Down();
            public SessionReply DepartByViewerSession(UUID agent, string viewerSession, int reason) => Down();
            public SessionReply DepartAll(UUID agent, int reason) => Down();
            public SessionReply DepartRegion(UUID region) => Down();
            public SessionReply RenewSeats(IReadOnlyList<SeatKey> seats) => Down();
            public SessionReply MarkInvited(UUID sessionId, IReadOnlyList<UUID> agents) => Down();
            public SessionReply VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, int type) => Down();
            public SessionReply CarrierKey(UUID sessionId, UUID agent) => Down();
            public SessionReply IssueToken(UUID sessionId, UUID agent) => Down();
            public SessionReply TakeConferenceRequest(UUID agent) => Down();
            public SessionReply Get(UUID sessionId, UUID agent) => Down();
            public SessionReply GetByRoomKey(string roomKey, UUID agent) => Down();
        }

        [Test]
        public void WithTheServiceUnreachable_NewCallsAreRefusedCleanly_WithNoFallback()
        {
            DownService down = new DownService();
            GridNonSpatialSessions region = NewRegion(down);
            List<string> unavailable = new List<string>();
            region.Unavailable += unavailable.Add;

            SessionOutcome group = default, adhoc = default, p2p = default, accept = default, invite = default;
            Assert.DoesNotThrow(() =>
            {
                group = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
                adhoc = region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
                p2p = region.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);
                accept = region.Accept(GroupId, Bob, RegionA);
                invite = region.Invite(UUID.Random(), Alice, Bob);
            });

            foreach (SessionOutcome o in new[] { group, adhoc, p2p, accept, invite })
            {
                Assert.That(o.Ok, Is.False);
                Assert.That(o.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            }
            Assert.That(region.Get(GroupId), Is.Null, "no session was made anywhere");
            Assert.That(region.GetByRoomKey("any"), Is.Null);
            Assert.That(region.IssueToken(GroupId, Alice), Is.Null);
            Assert.That(region.TryTakeConferenceRequest(Alice), Is.False);
            Assert.That(region.DepartAll(Alice, DepartureReason.PresenceLost), Is.Empty);
            Assert.That(down.Calls, Is.GreaterThan(0), "every request went to the service and none to a local store");
            Assert.That(unavailable, Is.Not.Empty.And.Count.LessThanOrEqualTo(1), "one log line a minute, not one per request");
        }

        [Test]
        public void ACallThatFailsAdmissionNeverReachesTheService()
        {
            DownService down = new DownService();
            GridNonSpatialSessions region = new GridNonSpatialSessions(down, new INonSpatialAdmission[]
            {
                new P2PAdmission(), new AdhocAdmission(), new GroupAdmission((a, g) => false, (a, g) => false),
            });

            SessionOutcome o = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo(AdmissionVerdict.NotAMember));
            Assert.That(down.Calls, Is.EqualTo(0));
        }

        // ---- the engine's behaviour through the service ---------------------------------------------

        [Test]
        public void ASessionReturnedToARegionIsACopy()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA);

            NonSpatialVoiceSession copy = a.Get(GroupId);
            copy.Find(Bob).State = MemberState.Departed;
            copy.Upsert(Carol, MemberState.Accepted, RegionA, DateTime.UtcNow);

            NonSpatialVoiceSession again = a.Get(GroupId);
            Assert.That(again.Find(Bob).HoldsSeat, Is.True);
            Assert.That(again.Find(Carol), Is.Null);
            Assert.That(again.SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void ACopyCarriesWhatTheRegionReads()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            SessionOutcome started = a.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            a.MarkInvited(started.Session.SessionId, new List<UUID> { Bob });
            string token = a.IssueToken(started.Session.SessionId, Alice);

            NonSpatialVoiceSession s = a.Get(started.Session.SessionId, Alice);

            Assert.That(s.Type, Is.EqualTo(NonSpatialSessionType.Adhoc));
            Assert.That(s.Creator, Is.EqualTo(Alice));
            Assert.That(s.RoomKey, Is.EqualTo(NonSpatialRoomKey.Derive(GridId, NonSpatialSessionType.Adhoc, s.SessionId)));
            Assert.That(a.GetByRoomKey(s.RoomKey).SessionId, Is.EqualTo(s.SessionId));
            Assert.That(s.Token, Is.EqualTo(token));
            Assert.That(s.Find(Bob).State, Is.EqualTo(MemberState.Invited));
            Assert.That(s.WasInvited(Bob), Is.True);
            Assert.That(s.WasInvited(Carol), Is.False);
            Assert.That(s.Find(Alice).InChat, Is.True);
            Assert.That(s.RingSent, Is.True);
        }

        [Test]
        public void ADepartedMemberCannotBeIssuedTheCredential()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.That(a.IssueToken(GroupId, Alice), Is.Not.Null.And.Length.EqualTo(NonSpatialVoiceSessionEngine.TokenBytes * 2));
            Assert.That(a.IssueToken(GroupId, Carol), Is.Null, "not a member");
        }

        // ---- the credential goes only to the call's own people ---------------------------------------

        [Test]
        public void AMemberAndAnInviteeAreGivenTheCredential()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            SessionOutcome started = a.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            UUID id = started.Session.SessionId;
            a.MarkInvited(id, new List<UUID> { Bob });
            string token = a.IssueToken(id, Alice);

            Assert.That(token, Is.Not.Null);
            Assert.That(a.Get(id, Alice).Token, Is.EqualTo(token), "a member");
            Assert.That(a.Get(id, Bob).Token, Is.EqualTo(token), "an invitee");
            Assert.That(a.GetByRoomKey(started.Session.RoomKey, Alice).Token, Is.EqualTo(token), "found by room key");
            Assert.That(service.Get(id, Alice).Session.Token, Is.EqualTo(token), "on the service's own reply");
        }

        [Test]
        public void AStrangerAndARequestNamingNoAgentAreNotGivenTheCredential()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            SessionOutcome started = a.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            UUID id = started.Session.SessionId;
            a.MarkInvited(id, new List<UUID> { Bob });
            Assert.That(a.IssueToken(id, Alice), Is.Not.Null);

            foreach (SessionReply r in new[]
            {
                service.Get(id), service.Get(id, Carol), service.Get(id, UUID.Zero),
                service.GetByRoomKey(started.Session.RoomKey), service.GetByRoomKey(started.Session.RoomKey, Carol),
            })
            {
                Assert.That(r.Ok, Is.True, "the session itself is still returned");
                Assert.That(r.Session.Token, Is.Null.Or.Empty, "without the credential");
                Assert.That(r.Session.Members, Has.Count.EqualTo(2));
                Assert.That(r.Session.RoomKey, Is.EqualTo(started.Session.RoomKey));
            }
            Assert.That(a.Get(id).Token, Is.Null.Or.Empty, "the region client's copy, with no agent named");
            Assert.That(a.Get(id, Carol).Token, Is.Null.Or.Empty, "and for a stranger");
        }

        [Test]
        public void ARepliesCopyOfTheSessionDoesNotCarryTheCredentialToANonMember()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            string token = a.IssueToken(GroupId, Alice);

            // Members acting on the call see the credential; Carol, who is not in it, never does.
            Assert.That(service.Accept(GroupId, Bob, RegionB, "v").Session.Token, Is.EqualTo(token));
            Assert.That(service.Decline(GroupId, Carol).Session?.Token, Is.Null.Or.Empty);
            Assert.That(service.Invite(GroupId, Carol, UUID.Random()).Session?.Token, Is.Null.Or.Empty);
            Assert.That(service.CheckInviteCount(GroupId, Carol, new List<UUID> { UUID.Random() }).Session?.Token, Is.Null.Or.Empty);
        }

        [Test]
        public void DepartByViewerSession_ReleasesOnlyThatViewersSeat()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA, "viewer-one");

            Assert.That(a.DepartByViewerSession(Bob, "", DepartureReason.VoiceTeardown), Is.Empty, "an empty viewer session matches nothing");
            Assert.That(a.DepartByViewerSession(Bob, "viewer-two", DepartureReason.VoiceTeardown), Is.Empty);
            IReadOnlyList<NonSpatialVoiceSession> touched = a.DepartByViewerSession(Bob, "viewer-one", DepartureReason.VoiceTeardown);

            Assert.That(touched, Has.Count.EqualTo(1));
            Assert.That(a.Get(GroupId).Find(Bob).HoldsSeat, Is.False);
        }

        [Test]
        public void ARingNeverCreatesOrChangesAGridSession()
        {
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            string key = NonSpatialRoomKey.Derive(GridId, NonSpatialSessionType.Group, GroupId);

            SessionOutcome forged = a.AdoptRemoteRing(GroupId, key, "token", 50, Alice, new[] { Bob });
            Assert.That(forged.Ok, Is.False, "no session, so a ring for it is refused");
            Assert.That(service.SessionCount, Is.EqualTo(0), "and nothing was created from it");

            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Assert.That(a.AdoptRemoteRing(GroupId, key, "token", 50, Alice, new[] { Bob }).Decision, Is.EqualTo("ring-session-held"));
            Assert.That(a.AdoptRemoteRing(GroupId, "wrong-key", "token", 50, Alice, null).Decision, Is.EqualTo("refused-room-key-mismatch"));
            Assert.That(a.Get(GroupId).Token, Is.Null.Or.Not.EqualTo("token"), "the credential is never taken from a ring");
        }

        // ---- limits are grid-wide, with finite defaults -----------------------------------------------

        [Test]
        public void TheDefaultLimitsAreFinite()
        {
            NonSpatialLimits d = NonSpatialLimits.Default;
            Assert.That(d.MaxNonSpatialSessions, Is.EqualTo(5000));
            Assert.That(d.MaxSessionsPerAgent, Is.EqualTo(20));
            Assert.That(d.ConferenceRequestBurst, Is.EqualTo(5));
            Assert.That(d.ConferenceRequestRefillSeconds, Is.EqualTo(10));
        }

        [Test]
        public void TheSessionCapCountsEverySessionInTheGrid()
        {
            VoiceSessionService service = NewService(limits: new NonSpatialLimits { MaxNonSpatialSessions = 4, MaxSessionsPerAgent = 100 });
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);

            Assert.That(a.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);
            Assert.That(b.StartAdhoc(Bob, UUID.Random(), RegionB).Ok, Is.True);
            Assert.That(a.StartAdhoc(Carol, UUID.Random(), RegionA).Ok, Is.True);
            Assert.That(b.StartAdhoc(UUID.Random(), UUID.Random(), RegionB).Ok, Is.True);

            SessionOutcome fifth = a.StartAdhoc(UUID.Random(), UUID.Random(), RegionA);
            Assert.That(fifth.Ok, Is.False);
            Assert.That(fifth.Decision, Is.EqualTo(SessionOutcome.GlobalSessionCap));
        }

        [Test]
        public void TheAgentCapCountsSessionsStartedFromEveryRegion()
        {
            VoiceSessionService service = NewService(limits: new NonSpatialLimits { MaxNonSpatialSessions = 100, MaxSessionsPerAgent = 3 });
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            Assert.That(a.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);
            Assert.That(b.StartAdhoc(Alice, UUID.Random(), RegionB).Ok, Is.True);
            Assert.That(a.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);

            SessionOutcome fourth = b.StartAdhoc(Alice, UUID.Random(), RegionB);

            Assert.That(fourth.Decision, Is.EqualTo(SessionOutcome.AgentSessionCap));
        }

        [Test]
        public void TheRequestBucketIsSharedByEveryRegion()
        {
            VoiceSessionService service = NewService(limits: new NonSpatialLimits { ConferenceRequestBurst = 5, ConferenceRequestRefillSeconds = 10 });
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            int taken = 0;
            for (int i = 0; i < 10; i++)
                if ((i % 2 == 0 ? a : b).TryTakeConferenceRequest(Alice)) taken++;
            Assert.That(taken, Is.EqualTo(5));
        }

        [Test]
        public void ARequestTheServiceCannotReadIsRefused()
        {
            VoiceSessionService service = NewService();
            Assert.That(service.Start((int)NonSpatialSessionType.Adhoc, Alice, UUID.Zero, UUID.Zero, RegionA, 0).Ok, Is.False);
            Assert.That(service.Start(99, Alice, GroupId, GroupId, RegionA, 0).Ok, Is.False);
            Assert.That(service.Start((int)NonSpatialSessionType.Group, UUID.Zero, GroupId, GroupId, RegionA, 0).Ok, Is.False);
            Assert.That(service.Start((int)NonSpatialSessionType.Group, Alice, UUID.Zero, UUID.Zero, RegionA, 0).Ok, Is.False, "a group call needs a group");
            Assert.That(service.Depart(GroupId, Alice, 99).Ok, Is.False);
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void TheServiceChecksWhoMayJoinAConferenceItself()
        {
            VoiceSessionService service = NewService();
            SessionReply started = service.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }, 0);
            UUID id = UUID.Parse(started.Session.Id);

            Assert.That(service.Accept(id, Carol, RegionA, null).Ok, Is.False, "never invited");
            Assert.That(service.Invite(id, Carol, Bob).Ok, Is.False, "not a member, so cannot invite");
            Assert.That(service.Accept(id, Bob, RegionA, null).Ok, Is.True);
        }

        // ---- the settings ------------------------------------------------------------------------------

        [Test]
        public void TheRegionKeepsItsOwnSessionsUnlessToldOtherwise()
        {
            Assert.That(SessionStoreSettings.Read(new IniConfigSource().AddConfig("WebRtcVoice")).Grid, Is.False);
            Assert.That(SessionStoreSettings.Read(null).Grid, Is.False);

            IConfig voice = new IniConfigSource().AddConfig("WebRtcVoice");
            voice.Set("SessionStoreMode", "local");
            voice.Set("SessionStoreServerURI", "http://robust.example.org:8003");
            SessionStoreSettings local = SessionStoreSettings.Read(voice);
            Assert.That(local.Grid, Is.False, "an address alone does not switch it on");
            Assert.That(local.Problem, Is.Null);
        }

        [Test]
        public void TheGridModeNeedsAnAddress_AndAnUnknownModeStaysLocalWithAProblem()
        {
            IConfig voice = new IniConfigSource().AddConfig("WebRtcVoice");
            voice.Set("SessionStoreMode", "grid");
            SessionStoreSettings noUri = SessionStoreSettings.Read(voice);
            Assert.That(noUri.Grid, Is.False);
            Assert.That(noUri.Problem, Does.Contain("SessionStoreServerURI"));

            voice.Set("SessionStoreServerURI", "http://robust.example.org:8003");
            SessionStoreSettings grid = SessionStoreSettings.Read(voice);
            Assert.That(grid.Grid, Is.True);
            Assert.That(grid.ServerUri, Is.EqualTo("http://robust.example.org:8003"));
            Assert.That(grid.TimeoutMs, Is.EqualTo(3000));
            Assert.That(grid.RenewSeconds, Is.EqualTo(60));

            voice.Set("SessionStoreMode", "cluster");
            SessionStoreSettings unknown = SessionStoreSettings.Read(voice);
            Assert.That(unknown.Grid, Is.False);
            Assert.That(unknown.Problem, Does.Contain("cluster"));
        }

        [Test]
        public void TheServiceAndTheRegionsDeriveTheSameGridId()
        {
            string[][] cases =
            {
                new[] { "Const", "GatekeeperURI", "HTTP://Grid.Example.org:8002/" },
                new[] { "GatekeeperService", "ExternalName", "http://gk.example.org:8002" },
                new[] { "GridService", "Gatekeeper", "http://Other.example.org:8002/" },
            };
            foreach (string[] c in cases)
            {
                IniConfigSource config = new IniConfigSource();
                config.AddConfig(c[0]).Set(c[1], c[2]);
                Assert.That(VoiceSessionServerConnector.GridIdOf(config), Is.EqualTo(JanusAudioBridge.ReadGridId(config)), c[1]);
            }
            Assert.That(VoiceSessionServerConnector.GridIdOf(new IniConfigSource()), Is.EqualTo(JanusAudioBridge.ReadGridId(new IniConfigSource())));
        }
    }
}
