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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void TheSeatCapHoldsAcrossTwoRegions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);

            SessionOutcome started = a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Assert.True(started.Ok, started.Decision);

            int accepted = 0, refused = 0;
            UUID[] members = Agents(60);
            for (int i = 0; i < members.Length; i++)
            {
                GridNonSpatialSessions region = i % 2 == 0 ? a : b;
                SessionOutcome o = region.Accept(GroupId, members[i], i % 2 == 0 ? RegionA : RegionB);
                if (o.Ok) accepted++;
                else if (o.Decision == SessionOutcome.Capacity) refused++;
            }

            {
                var assertedValue = accepted;
                TestAssert.WithMessage(() => Assert.Equal(NonSpatialCaps.DefaultConferenceCap - 1, assertedValue), "49 more seats after the creator");
            }
            {
                var assertedValue = refused;
                TestAssert.WithMessage(() => Assert.Equal(members.Length - accepted, assertedValue), "everyone else is told the room is full");
            }
            Assert.Equal(NonSpatialCaps.DefaultConferenceCap, a.Get(GroupId).SeatsHeld);
            {
                var assertedValue = b.Get(GroupId).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(NonSpatialCaps.DefaultConferenceCap, assertedValue), "both regions see the one count");
            }
        }

        [Fact(Timeout = 60000)]
        public void WithoutASharedService_EachRegionCountsItsOwnCap()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.True((seats + 2) > NonSpatialCaps.DefaultConferenceCap, "one cap per process, not per call");
        }

        // ---- a group is rung once ------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AGroupRingsOnce_WhoeverJoinsFromWhichRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);

            SessionOutcome opened = a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            SessionOutcome joined = b.Accept(GroupId, Bob, RegionB);
            SessionOutcome joinedAgain = b.Start(NonSpatialSessionType.Group, Carol, GroupId, GroupId, RegionB);

            Assert.True(opened.StartedRinging, "the opener's region rings the group");
            Assert.True(joined.Ok, joined.Decision);
            Assert.False(joined.StartedRinging, "a join from another region does not ring it again");
            Assert.False(joinedAgain.StartedRinging);
        }

        [Fact(Timeout = 60000)]
        public void WithoutASharedService_BothRegionsRingTheGroup()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions a = NewRegion(NewService());
            GridNonSpatialSessions b = NewRegion(NewService());
            bool aRang = a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA).StartedRinging;
            bool bRang = b.Start(NonSpatialSessionType.Group, Bob, GroupId, GroupId, RegionB).StartedRinging;
            Assert.True((aRang && bRang), "the double ring the shared record prevents");
        }

        [Fact(Timeout = 60000)]
        public void TheGroupRingsAgain_AfterTheRoomEmpties()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            b.Accept(GroupId, Bob, RegionB);
            a.Depart(GroupId, Alice, DepartureReason.VoiceTeardown);
            b.Depart(GroupId, Bob, DepartureReason.VoiceTeardown);

            b.Start(NonSpatialSessionType.Group, Carol, GroupId, GroupId, RegionB);
            SessionOutcome again = b.Accept(GroupId, Carol, RegionB);

            Assert.True(again.StartedRinging, "the first seat of a later call is a new ring cycle");
        }
        public static TheoryData<int> Repetitions => new(Enumerable.Range(1, 10));

        [Theory(Timeout = 60000)]
        [MemberData(nameof(Repetitions))]
        public async Task TwoRegionsRacingToStartOneGroupCallGetOneSession(int repetition)
        {
            TestContext.Current.TestOutputHelper.WriteLine("Repetition {0}", repetition);
            VoiceSessionService service = NewService(limits: Unlimited());
            GridNonSpatialSessions[] regions = { NewRegion(service), NewRegion(service) };
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            UUID[] callers = Agents(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait(TestContext.Current.CancellationToken);
                return regions[i % 2].Start(NonSpatialSessionType.Group, callers[i], GroupId, GroupId, i % 2 == 0 ? RegionA : RegionB);
            }, TaskCreationOptions.LongRunning)).ToArray();
            SessionOutcome[] results = await Task.WhenAll(tasks);

            Assert.Equal(1, service.SessionCount);
            {
                var assertedValue = results.Count(t => t.Created);
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one start made the session");
            }
            {
                var assertedValue = results.Count(t => t.StartedRinging);
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one start rang the group");
            }
        }
        [Theory(Timeout = 60000)]
        [MemberData(nameof(Repetitions))]
        public async Task TwoRegionsRetryingOneConferenceStartGetOneSession(int repetition)
        {
            TestContext.Current.TestOutputHelper.WriteLine("Repetition {0}", repetition);
            VoiceSessionService service = NewService(limits: Unlimited());
            GridNonSpatialSessions[] regions = { NewRegion(service), NewRegion(service) };
            UUID temp = UUID.Random();
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait(TestContext.Current.CancellationToken);
                return regions[i % 2].StartAdhoc(Alice, temp, i % 2 == 0 ? RegionA : RegionB);
            }, TaskCreationOptions.LongRunning)).ToArray();
            SessionOutcome[] results = await Task.WhenAll(tasks);

            Assert.Equal(1, service.SessionCount);
            Assert.Single(results.Select(t => t.Session.SessionId).Distinct());
            Assert.Single(results, t => t.Created);
        }

        // ---- a region restarting ------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARestartedRegionReleasesItsSeatsAndNoOthers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            UUID onB = UUID.Random();
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA);
            b.Accept(GroupId, onB, RegionB);

            int released = a.DepartRegion(RegionA);

            {
                var assertedValue = released;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the creator's seat and Bob's, both taken from region A");
            }
            NonSpatialVoiceSession s = b.Get(GroupId);
            Assert.Equal(1, s.SeatsHeld);
            Assert.True(s.Find(onB).HoldsSeat, "region B's seat is untouched");
            Assert.Equal(DepartureReason.PresenceLost, s.Find(Bob).Departure);
        }

        [Fact(Timeout = 60000)]
        public void ASeatNobodyRenews_IsReleasedWhenItsLeaseRunsOut_AndARenewedSeatIsKept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.Equal(1, swept.LeasesExpired);
            Assert.True(s.Find(Alice).HoldsSeat, "renewed, so kept");
            Assert.False(s.Find(Bob).HoldsSeat, "never renewed, so released");
            Assert.Equal(DepartureReason.PresenceLost, s.Find(Bob).Departure);
        }

        [Fact(Timeout = 60000)]
        public void ACallWhoseMembersAreRenewed_IsNotCollectedAsIdle()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            {
                var assertedValue = a.Get(GroupId);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "nine hours of renewed seats is not idle");
            }
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
            public SessionReply Moderate(UUID sessionId, UUID by, int op, UUID target, bool on, string reason = null, int minutes = 0) => Down();
            public SessionReply IssueToken(UUID sessionId, UUID agent) => Down();
            public SessionReply TakeConferenceRequest(UUID agent) => Down();
            public SessionReply Get(UUID sessionId, UUID agent) => Down();
            public SessionReply GetByRoomKey(string roomKey, UUID agent) => Down();
        }

        [Fact(Timeout = 60000)]
        public void WithTheServiceUnreachable_NewCallsAreRefusedCleanly_WithNoFallback()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            DownService down = new DownService();
            GridNonSpatialSessions region = NewRegion(down);
            List<string> unavailable = new List<string>();
            region.Unavailable += unavailable.Add;

            SessionOutcome group = default, adhoc = default, p2p = default, accept = default, invite = default;
            {
                var assertedValue = () =>
                {
                    group = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
                    adhoc = region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
                    p2p = region.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);
                    accept = region.Accept(GroupId, Bob, RegionA);
                    invite = region.Invite(UUID.Random(), Alice, Bob);
                };
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }

            foreach (SessionOutcome o in new[] { group, adhoc, p2p, accept, invite })
            {
                Assert.False(o.Ok);
                Assert.Equal(SessionOutcome.StoreUnavailable, o.Decision);
            }
            {
                var assertedValue = region.Get(GroupId);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no session was made anywhere");
            }
            Assert.Null(region.GetByRoomKey("any"));
            Assert.Null(region.IssueToken(GroupId, Alice));
            Assert.False(region.TryTakeConferenceRequest(Alice));
            Assert.Empty(region.DepartAll(Alice, DepartureReason.PresenceLost));
            Assert.True(down.Calls > 0, "every request went to the service and none to a local store");
            {
                var assertedValue = unavailable;
                TestAssert.WithMessage(() =>
                {
                    Assert.NotEmpty(assertedValue);
                    Assert.True(assertedValue.Count() <= 1);
                }, "one log line a minute, not one per request");
            }
        }

        [Fact(Timeout = 60000)]
        public void ACallThatFailsAdmissionNeverReachesTheService()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            DownService down = new DownService();
            GridNonSpatialSessions region = new GridNonSpatialSessions(down, new INonSpatialAdmission[]
            {
                new P2PAdmission(), new AdhocAdmission(), new GroupAdmission((a, g) => false, (a, g) => false),
            });

            SessionOutcome o = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.False(o.Ok);
            Assert.Equal(AdmissionVerdict.NotAMember, o.Decision);
            Assert.Equal(0, down.Calls);
        }

        // ---- the engine's behaviour through the service ---------------------------------------------

        [Fact(Timeout = 60000)]
        public void ASessionReturnedToARegionIsACopy()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA);

            NonSpatialVoiceSession copy = a.Get(GroupId);
            copy.Find(Bob).State = MemberState.Departed;
            copy.Upsert(Carol, MemberState.Accepted, RegionA, DateTime.UtcNow);

            NonSpatialVoiceSession again = a.Get(GroupId);
            Assert.True(again.Find(Bob).HoldsSeat);
            Assert.Null(again.Find(Carol));
            Assert.Equal(2, again.SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void ACopyCarriesWhatTheRegionReads()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            SessionOutcome started = a.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            a.MarkInvited(started.Session.SessionId, new List<UUID> { Bob });
            string token = a.IssueToken(started.Session.SessionId, Alice);

            NonSpatialVoiceSession s = a.Get(started.Session.SessionId, Alice);

            Assert.Equal(NonSpatialSessionType.Adhoc, s.Type);
            Assert.Equal(Alice, s.Creator);
            Assert.Equal(NonSpatialRoomKey.Derive(GridId, NonSpatialSessionType.Adhoc, s.SessionId), s.RoomKey);
            Assert.Equal(s.SessionId, a.GetByRoomKey(s.RoomKey).SessionId);
            Assert.Equal(token, s.Token);
            Assert.Equal(MemberState.Invited, s.Find(Bob).State);
            Assert.True(s.WasInvited(Bob));
            Assert.False(s.WasInvited(Carol));
            Assert.True(s.Find(Alice).InChat);
            Assert.True(s.RingSent);
        }

        [Fact(Timeout = 60000)]
        public void ADepartedMemberCannotBeIssuedTheCredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            {
                var assertedValue = a.IssueToken(GroupId, Alice);
                Assert.NotNull(assertedValue);
                Assert.Equal(NonSpatialVoiceSessionEngine.TokenBytes * 2, assertedValue.Length);
            }
            {
                var assertedValue = a.IssueToken(GroupId, Carol);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "not a member");
            }
        }

        // ---- the credential goes only to the call's own people ---------------------------------------

        [Fact(Timeout = 60000)]
        public void AMemberAndAnInviteeAreGivenTheCredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            SessionOutcome started = a.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            UUID id = started.Session.SessionId;
            a.MarkInvited(id, new List<UUID> { Bob });
            string token = a.IssueToken(id, Alice);

            Assert.NotNull(token);
            {
                var assertedValue = a.Get(id, Alice).Token;
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "a member");
            }
            {
                var assertedValue = a.Get(id, Bob).Token;
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "an invitee");
            }
            {
                var assertedValue = a.GetByRoomKey(started.Session.RoomKey, Alice).Token;
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "found by room key");
            }
            {
                var assertedValue = service.Get(id, Alice).Session.Token;
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "on the service's own reply");
            }
        }

        [Fact(Timeout = 60000)]
        public void AStrangerAndARequestNamingNoAgentAreNotGivenTheCredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            SessionOutcome started = a.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            UUID id = started.Session.SessionId;
            a.MarkInvited(id, new List<UUID> { Bob });
            Assert.NotNull(a.IssueToken(id, Alice));

            foreach (SessionReply r in new[]
            {
                service.Get(id), service.Get(id, Carol), service.Get(id, UUID.Zero),
                service.GetByRoomKey(started.Session.RoomKey), service.GetByRoomKey(started.Session.RoomKey, Carol),
            })
            {
                Assert.True(r.Ok, "the session itself is still returned");
                Assert.True(string.IsNullOrEmpty(r.Session.Token), "without the credential");
                Assert.Equal(2, r.Session.Members.Count());
                Assert.Equal(started.Session.RoomKey, r.Session.RoomKey);
            }
            Assert.True(string.IsNullOrEmpty(a.Get(id).Token), "the region client's copy, with no agent named");
            Assert.True(string.IsNullOrEmpty(a.Get(id, Carol).Token), "and for a stranger");
        }

        [Fact(Timeout = 60000)]
        public void ARepliesCopyOfTheSessionDoesNotCarryTheCredentialToANonMember()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            string token = a.IssueToken(GroupId, Alice);

            // Members acting on the call see the credential; Carol, who is not in it, never does.
            Assert.Equal(token, service.Accept(GroupId, Bob, RegionB, "v").Session.Token);
            Assert.True(string.IsNullOrEmpty(service.Decline(GroupId, Carol).Session?.Token));
            Assert.True(string.IsNullOrEmpty(service.Invite(GroupId, Carol, UUID.Random()).Session?.Token));
            Assert.True(string.IsNullOrEmpty(service.CheckInviteCount(GroupId, Carol, new List<UUID> { UUID.Random() }).Session?.Token));
        }

        [Fact(Timeout = 60000)]
        public void DepartByViewerSession_ReleasesOnlyThatViewersSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            a.Accept(GroupId, Bob, RegionA, "viewer-one");

            {
                var assertedValue = a.DepartByViewerSession(Bob, "", DepartureReason.VoiceTeardown);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "an empty viewer session matches nothing");
            }
            Assert.Empty(a.DepartByViewerSession(Bob, "viewer-two", DepartureReason.VoiceTeardown));
            IReadOnlyList<NonSpatialVoiceSession> touched = a.DepartByViewerSession(Bob, "viewer-one", DepartureReason.VoiceTeardown);

            Assert.Single(touched);
            Assert.False(a.Get(GroupId).Find(Bob).HoldsSeat);
        }

        [Fact(Timeout = 60000)]
        public void ARingNeverCreatesOrChangesAGridSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            GridNonSpatialSessions a = NewRegion(service);
            string key = NonSpatialRoomKey.Derive(GridId, NonSpatialSessionType.Group, GroupId);

            SessionOutcome forged = a.AdoptRemoteRing(GroupId, key, "token", 50, Alice, new[] { Bob });
            Assert.False(forged.Ok, "no session, so a ring for it is refused");
            {
                var assertedValue = service.SessionCount;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and nothing was created from it");
            }

            a.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Assert.Equal("ring-session-held", a.AdoptRemoteRing(GroupId, key, "token", 50, Alice, new[] { Bob }).Decision);
            Assert.Equal("refused-room-key-mismatch", a.AdoptRemoteRing(GroupId, "wrong-key", "token", 50, Alice, null).Decision);
            TestAssert.WithMessage(() => Assert.NotEqual("token", a.Get(GroupId).Token), "the credential is never taken from a ring");
        }

        // ---- limits are grid-wide, with finite defaults -----------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheDefaultLimitsAreFinite()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialLimits d = NonSpatialLimits.Default;
            Assert.Equal(5000, d.MaxNonSpatialSessions);
            Assert.Equal(20, d.MaxSessionsPerAgent);
            Assert.Equal(5, d.ConferenceRequestBurst);
            Assert.Equal(10, d.ConferenceRequestRefillSeconds);
        }

        [Fact(Timeout = 60000)]
        public void TheSessionCapCountsEverySessionInTheGrid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService(limits: new NonSpatialLimits { MaxNonSpatialSessions = 4, MaxSessionsPerAgent = 100 });
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);

            Assert.True(a.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);
            Assert.True(b.StartAdhoc(Bob, UUID.Random(), RegionB).Ok);
            Assert.True(a.StartAdhoc(Carol, UUID.Random(), RegionA).Ok);
            Assert.True(b.StartAdhoc(UUID.Random(), UUID.Random(), RegionB).Ok);

            SessionOutcome fifth = a.StartAdhoc(UUID.Random(), UUID.Random(), RegionA);
            Assert.False(fifth.Ok);
            Assert.Equal(SessionOutcome.GlobalSessionCap, fifth.Decision);
        }

        [Fact(Timeout = 60000)]
        public void TheAgentCapCountsSessionsStartedFromEveryRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService(limits: new NonSpatialLimits { MaxNonSpatialSessions = 100, MaxSessionsPerAgent = 3 });
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            Assert.True(a.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);
            Assert.True(b.StartAdhoc(Alice, UUID.Random(), RegionB).Ok);
            Assert.True(a.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);

            SessionOutcome fourth = b.StartAdhoc(Alice, UUID.Random(), RegionB);

            Assert.Equal(SessionOutcome.AgentSessionCap, fourth.Decision);
        }

        [Fact(Timeout = 60000)]
        public void TheRequestBucketIsSharedByEveryRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService(limits: new NonSpatialLimits { ConferenceRequestBurst = 5, ConferenceRequestRefillSeconds = 10 });
            GridNonSpatialSessions a = NewRegion(service);
            GridNonSpatialSessions b = NewRegion(service);
            int taken = 0;
            for (int i = 0; i < 10; i++)
                if ((i % 2 == 0 ? a : b).TryTakeConferenceRequest(Alice)) taken++;
            Assert.Equal(5, taken);
        }

        [Fact(Timeout = 60000)]
        public void ARequestTheServiceCannotReadIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            Assert.False(service.Start((int)NonSpatialSessionType.Adhoc, Alice, UUID.Zero, UUID.Zero, RegionA, 0).Ok);
            Assert.False(service.Start(99, Alice, GroupId, GroupId, RegionA, 0).Ok);
            Assert.False(service.Start((int)NonSpatialSessionType.Group, UUID.Zero, GroupId, GroupId, RegionA, 0).Ok);
            Assert.False(service.Start((int)NonSpatialSessionType.Group, Alice, UUID.Zero, UUID.Zero, RegionA, 0).Ok, "a group call needs a group");
            Assert.False(service.Depart(GroupId, Alice, 99).Ok);
            Assert.Equal(0, service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void TheServiceChecksWhoMayJoinAConferenceItself()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewService();
            SessionReply started = service.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }, 0);
            UUID id = UUID.Parse(started.Session.Id);

            Assert.False(service.Accept(id, Carol, RegionA, null).Ok, "never invited");
            Assert.False(service.Invite(id, Carol, Bob).Ok, "not a member, so cannot invite");
            Assert.True(service.Accept(id, Bob, RegionA, null).Ok);
        }

        // ---- the settings ------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheRegionKeepsItsOwnSessionsUnlessToldOtherwise()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(SessionStoreSettings.Read(new IniConfigSource().AddConfig("WebRtcVoice")).Grid);
            Assert.False(SessionStoreSettings.Read(null).Grid);

            IConfig voice = new IniConfigSource().AddConfig("WebRtcVoice");
            voice.Set("SessionStoreMode", "local");
            voice.Set("SessionStoreServerURI", "http://robust.example.org:8003");
            SessionStoreSettings local = SessionStoreSettings.Read(voice);
            Assert.False(local.Grid, "an address alone does not switch it on");
            Assert.Null(local.Problem);
        }

        [Fact(Timeout = 60000)]
        public void TheGridModeNeedsAnAddress_AndAnUnknownModeStaysLocalWithAProblem()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig voice = new IniConfigSource().AddConfig("WebRtcVoice");
            voice.Set("SessionStoreMode", "grid");
            SessionStoreSettings noUri = SessionStoreSettings.Read(voice);
            Assert.False(noUri.Grid);
            Assert.Contains("SessionStoreServerURI", noUri.Problem);

            voice.Set("SessionStoreServerURI", "http://robust.example.org:8003");
            SessionStoreSettings grid = SessionStoreSettings.Read(voice);
            Assert.True(grid.Grid);
            Assert.Equal("http://robust.example.org:8003", grid.ServerUri);
            Assert.Equal(3000, grid.TimeoutMs);
            Assert.Equal(60, grid.RenewSeconds);

            voice.Set("SessionStoreMode", "cluster");
            SessionStoreSettings unknown = SessionStoreSettings.Read(voice);
            Assert.False(unknown.Grid);
            Assert.Contains("cluster", unknown.Problem);
        }

        [Fact(Timeout = 60000)]
        public void TheServiceAndTheRegionsDeriveTheSameGridId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
                {
                    var assertedValue = VoiceSessionServerConnector.GridIdOf(config);
                    TestAssert.WithMessage(() => Assert.Equal(JanusAudioBridge.ReadGridId(config), assertedValue), c[1]);
                }
            }
            Assert.Equal(JanusAudioBridge.ReadGridId(new IniConfigSource()), VoiceSessionServerConnector.GridIdOf(new IniConfigSource()));
        }
    }
}
