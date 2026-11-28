/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The scenarios that need more than one region, run two ways from one set of assertions:
 *
 *  - InProcessStoreScenarios: every region talks to one VoiceSessionService in this process, time is a test clock.
 *  - GridStoreProcessTests: every region talks over HTTP to a grid server that carries the call session service,
 *    a separate process. Opt-in, like the stock Janus tests: it runs only when WEBRTC_VOICE_STORE_TEST_URL names the
 *    service's address and WEBRTC_VOICE_STORE_TEST_PASSWORD holds its HTTP password, and is skipped otherwise.
 *
 * Variables for the process run:
 *   WEBRTC_VOICE_STORE_TEST_URL                 base address of the service, e.g. http://192.0.2.10:8924
 *   WEBRTC_VOICE_STORE_TEST_USER                HTTP user name (default "voice-store")
 *   WEBRTC_VOICE_STORE_TEST_PASSWORD            HTTP password
 *   WEBRTC_VOICE_STORE_TEST_SEAT_LEASE_SECONDS  the service's SeatLeaseSeconds; the seat lease test is skipped without it
 *
 * The service is shared and keeps what it is given, so every scenario uses new ids and never relies on an empty store.
 *
 * GridStoreRestartProof is run three times by whoever stops and starts the service in between.
 * WEBRTC_VOICE_STORE_TEST_PHASE selects the run and WEBRTC_VOICE_STORE_TEST_STATE names a file that carries the
 * ids from one run to the next:
 *   seed   the service is up: a group call with three seats and an avatar-to-avatar call are made, the ids are saved
 *   down   the service is stopped: new work is refused cleanly (no exception, the viewer is answered 503)
 *   after  the service was started again: it is empty, and the regions can seat their calls again
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nini.Config;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    public abstract class StoreScenarios
    {
        /// <summary>A region's connection to the service. Each call makes a new client, as each region process has its own.</summary>
        protected abstract IVoiceSessionService NewRegionClient();

        /// <summary>Runs before a scenario; a fixture that cannot run says so here.</summary>
        protected virtual void Require(string need) { }

        /// <summary>
        /// Lets the seat lease run out. <paramref name="renewKept"/> renews the seat that must survive; it is called
        /// at least once in each lease period. Returns when <paramref name="gone"/> is true or fails the test.
        /// </summary>
        protected abstract void LetSeatLeaseRun(Action renewKept, Func<bool> gone);

        private GridNonSpatialSessions NewRegion() => VoiceSessionServiceTests.NewRegion(NewRegionClient());

        private static UUID[] Agents(int n) => Enumerable.Range(0, n).Select(_ => UUID.Random()).ToArray();

        // ---- the seat cap is counted once for every region ------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheSeatCapHoldsAcrossTwoRegions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions a = NewRegion(), b = NewRegion();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), group = UUID.Random();

            SessionOutcome started = a.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, regionA);
            Assert.True(started.Ok, started.Decision);

            int accepted = 0, refused = 0;
            UUID[] members = Agents(60);
            for (int i = 0; i < members.Length; i++)
            {
                SessionOutcome o = (i % 2 == 0 ? a : b).Accept(group, members[i], i % 2 == 0 ? regionA : regionB);
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
            Assert.Equal(NonSpatialCaps.DefaultConferenceCap, a.Get(group).SeatsHeld);
            {
                var assertedValue = b.Get(group).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(NonSpatialCaps.DefaultConferenceCap, assertedValue), "both regions see the one count");
            }
        }

        // ---- a group is rung once ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AGroupRingsOnce_WhoeverJoinsFromWhichRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions a = NewRegion(), b = NewRegion();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), group = UUID.Random();

            SessionOutcome opened = a.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, regionA);
            SessionOutcome joined = b.Accept(group, UUID.Random(), regionB);
            SessionOutcome joinedAgain = b.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, regionB);

            Assert.True(opened.StartedRinging, "the opener's region rings the group");
            Assert.True(joined.Ok, joined.Decision);
            Assert.False(joined.StartedRinging, "a join from another region does not ring it again");
            Assert.False(joinedAgain.StartedRinging);
        }

        // ---- two regions racing -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public async Task TwoRegionsRacingToStartOneGroupCallGetOneSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions[] regions = { NewRegion(), NewRegion() };
            UUID[] origin = { UUID.Random(), UUID.Random() };
            UUID group = UUID.Random();
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            UUID[] callers = Agents(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait(TestContext.Current.CancellationToken);
                return regions[i % 2].Start(NonSpatialSessionType.Group, callers[i], group, group, origin[i % 2]);
            }, TaskCreationOptions.LongRunning)).ToArray();
            SessionOutcome[] results = await Task.WhenAll(tasks);

            Assert.True(results.All(t => t.Ok), string.Join(",", results.Select(t => t.Decision).Distinct()));
            {
                var assertedValue = results.Count(t => t.Created);
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one start made the session");
            }
            {
                var assertedValue = results.Count(t => t.StartedRinging);
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one start rang the group");
            }
            {
                var assertedValue = regions[0].Get(group).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "a start seats only the one that made the session");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task TwoRegionsRetryingOneConferenceStartGetOneSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions[] regions = { NewRegion(), NewRegion() };
            UUID[] origin = { UUID.Random(), UUID.Random() };
            UUID creator = UUID.Random(), temp = UUID.Random();
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait(TestContext.Current.CancellationToken);
                return regions[i % 2].StartAdhoc(creator, temp, origin[i % 2]);
            }, TaskCreationOptions.LongRunning)).ToArray();
            SessionOutcome[] results = await Task.WhenAll(tasks);

            Assert.True(results.All(t => t.Ok), string.Join(",", results.Select(t => t.Decision).Distinct()));
            Assert.Single(results.Select(t => t.Session.SessionId).Distinct());
            Assert.Single(results, t => t.Created);
        }

        // ---- a region restarting, and a seat nobody renews ----------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARestartedRegionReleasesItsSeatsAndNoOthers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions a = NewRegion(), b = NewRegion();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), group = UUID.Random();
            UUID alice = UUID.Random(), bob = UUID.Random(), onB = UUID.Random();
            a.Start(NonSpatialSessionType.Group, alice, group, group, regionA);
            a.Accept(group, bob, regionA);
            b.Accept(group, onB, regionB);

            int released = a.DepartRegion(regionA);

            {
                var assertedValue = released;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the creator's seat and Bob's, both taken from region A");
            }
            NonSpatialVoiceSession s = b.Get(group);
            Assert.Equal(1, s.SeatsHeld);
            Assert.True(s.Find(onB).HoldsSeat, "region B's seat is untouched");
            Assert.Equal(DepartureReason.PresenceLost, s.Find(bob).Departure);
            {
                var assertedValue = b.DepartRegion(regionA);
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a second release finds nothing left");
            }
        }

        [Fact(Timeout = 60000)]
        public void ASeatNobodyRenews_IsReleasedWhenItsLeaseRunsOut_AndARenewedSeatIsKept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Require("lease");
            GridNonSpatialSessions a = NewRegion();
            UUID regionA = UUID.Random(), group = UUID.Random(), alice = UUID.Random(), bob = UUID.Random();
            a.Start(NonSpatialSessionType.Group, alice, group, group, regionA);
            a.Accept(group, bob, regionA);

            LetSeatLeaseRun(
                () => a.RenewSeats(new List<SeatKey> { new SeatKey(group, alice) }),
                () => !a.Get(group).Find(bob).HoldsSeat);

            NonSpatialVoiceSession s = a.Get(group);
            Assert.True(s.Find(alice).HoldsSeat, "renewed, so kept");
            Assert.False(s.Find(bob).HoldsSeat, "never renewed, so released");
            Assert.Equal(DepartureReason.PresenceLost, s.Find(bob).Departure);
        }

        // ---- a ring is judged by the service ------------------------------------------------------------

        private sealed class Conference
        {
            public GridNonSpatialSessions Sender, Receiver;
            public UUID SessionId, Alice, Bob, Carol, Dave, SenderRegion, ReceiverRegion;
            public string Token, RoomKey;
        }

        /// <summary>Alice starts a conference on the sender region inviting Bob and Carol; the sender marks them invited, as it does before it rings.</summary>
        private Conference NewConference(bool markInvited)
        {
            Conference c = new Conference
            {
                Sender = NewRegion(), Receiver = NewRegion(),
                Alice = UUID.Random(), Bob = UUID.Random(), Carol = UUID.Random(), Dave = UUID.Random(),
                SenderRegion = UUID.Random(), ReceiverRegion = UUID.Random(),
            };
            SessionOutcome started = c.Sender.StartAdhoc(c.Alice, UUID.Random(), c.SenderRegion, new List<UUID> { c.Bob, c.Carol });
            Assert.True(started.Ok, started.Decision);
            c.SessionId = started.Session.SessionId;
            c.RoomKey = started.Session.RoomKey;
            c.Token = c.Sender.IssueToken(c.SessionId, c.Alice);
            if (markInvited) c.Sender.MarkInvited(c.SessionId, new[] { c.Bob, c.Carol });
            return c;
        }

        [Fact(Timeout = 60000)]
        public void ARingVerifiesOnceAndOnlyForWhatWasSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Conference c = NewConference(markInvited: true);
            const NonSpatialSessionType adhoc = NonSpatialSessionType.Adhoc;

            SessionOutcome genuine = c.Receiver.VerifyRing(c.SessionId, c.Bob, c.Alice, adhoc);
            Assert.True(genuine.Ok, genuine.Decision);
            {
                var assertedValue = genuine.Session.Token;
                TestAssert.WithMessage(() => Assert.Equal(c.Token, assertedValue), "an invited target is given the credential");
            }

            SessionOutcome replay = c.Receiver.VerifyRing(c.SessionId, c.Bob, c.Alice, adhoc);
            Assert.False(replay.Ok);
            Assert.Equal("refused-ring-used", replay.Decision);

            Assert.Equal("refused-target-not-invited", c.Receiver.VerifyRing(c.SessionId, c.Dave, c.Alice, adhoc).Decision);
            Assert.Equal("refused-caller-not-seated", c.Receiver.VerifyRing(c.SessionId, c.Carol, c.Dave, adhoc).Decision);
            Assert.Equal("refused-session-type-mismatch", c.Receiver.VerifyRing(c.SessionId, c.Carol, c.Alice, NonSpatialSessionType.Group).Decision);
            Assert.True(c.Receiver.VerifyRing(c.SessionId, c.Carol, c.Alice, adhoc).Ok, "the refusals did not use Carol's ring up");
        }

        [Fact(Timeout = 60000)]
        public void ARingForASessionThatDoesNotExist_IsRefused_AndNothingIsCreated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Conference c = NewConference(markInvited: true);
            UUID nothing = UUID.Random();

            SessionOutcome o = c.Receiver.VerifyRing(nothing, c.Bob, c.Alice, NonSpatialSessionType.Adhoc);

            Assert.False(o.Ok);
            Assert.Equal(SessionOutcome.NoSuchSession, o.Decision);
            {
                var assertedValue = c.Receiver.Get(nothing);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the forged ring made no session");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARingBeforeTheSenderMarkedTheTarget_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Conference c = NewConference(markInvited: false);

            Assert.Equal("refused-target-not-invited", c.Receiver.VerifyRing(c.SessionId, c.Bob, c.Alice, NonSpatialSessionType.Adhoc).Decision);
        }

        // ---- the conference text key goes only to seated members ----------------------------------------

        [Fact(Timeout = 60000)]
        public void TheConferenceTextKeyGoesOnlyToSeatedMembers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Conference c = NewConference(markInvited: true);
            Assert.True(c.Receiver.Accept(c.SessionId, c.Bob, c.ReceiverRegion).Ok);

            string first = c.Sender.CarrierKey(c.SessionId, c.Alice);
            string second = c.Receiver.CarrierKey(c.SessionId, c.Bob);

            Assert.True(first.Length >= CarrierAuth.MinSecretLength);
            {
                var assertedValue = second;
                TestAssert.WithMessage(() => Assert.Equal(first, assertedValue), "every seated member has the same key, from whichever region");
            }
            {
                var assertedValue = c.Receiver.CarrierKey(c.SessionId, c.Carol);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "invited, not seated");
            }
            {
                var assertedValue = c.Receiver.CarrierKey(c.SessionId, c.Dave);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "not in the conference");
            }
            {
                var assertedValue = c.Receiver.CarrierKey(UUID.Random(), c.Alice);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no such conference");
            }

            SessionOutcome other = c.Sender.StartAdhoc(c.Alice, UUID.Random(), c.SenderRegion);
            {
                var assertedValue = c.Sender.CarrierKey(other.Session.SessionId, c.Alice);
                TestAssert.WithMessage(() => Assert.NotEqual(first, assertedValue), "each conference has its own key");
            }

            c.Receiver.Depart(c.SessionId, c.Bob, DepartureReason.VoiceTeardown);
            {
                var assertedValue = NewRegion().CarrierKey(c.SessionId, c.Bob);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a member who left no longer has it (asked of a region that has not kept it)");
            }
        }

        // ---- the time one provision step takes ----------------------------------------------------------

        /// <summary>The numbers are reported, not held to a tight bound: where the service runs changes them.</summary>
        [Fact(Timeout = 60000)]
        public void TheTimeOfAJoinAndAReadIsReported()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridNonSpatialSessions region = NewRegion();
            UUID origin = UUID.Random();
            const int groups = 6, perGroup = 40;
            List<double> joins = new List<double>(), reads = new List<double>();
            for (int g = 0; g < groups; g++)
            {
                UUID group = UUID.Random();
                Assert.True(region.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, origin).Ok);
                for (int i = 0; i < perGroup; i++)
                {
                    UUID agent = UUID.Random();
                    Stopwatch w = Stopwatch.StartNew();
                    SessionOutcome o = region.Accept(group, agent, origin);
                    joins.Add(w.Elapsed.TotalMilliseconds);
                    Assert.True(o.Ok, o.Decision);
                    w.Restart();
                    Assert.NotNull(region.Get(group, agent));
                    reads.Add(w.Elapsed.TotalMilliseconds);
                }
            }

            string joinLine = Describe("join", joins), readLine = Describe("read", reads);
            TestContext.Current.TestOutputHelper.WriteLine(joinLine);
            TestContext.Current.TestOutputHelper.WriteLine(readLine);
            Assert.True(Percentile(joins, 0.95) < 2000, "a sanity bound only: " + joinLine);
        }

        private static double Percentile(List<double> values, double p)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
        }

        private static string Describe(string what, List<double> ms)
            => $"[STORE TIMING] {what}: n={ms.Count} median={Percentile(ms, 0.5):F2} ms p95={Percentile(ms, 0.95):F2} ms max={ms.Max():F2} ms";
    }

    /// <summary>The scenarios with every region sharing one service object in this process.</summary>
    public class InProcessStoreScenarios : StoreScenarios, IAsyncLifetime
        {
        private readonly NonSpatialIdentityTests.FakeClock _clock = new NonSpatialIdentityTests.FakeClock();
        private const int LeaseSeconds = 10;
        private VoiceSessionService _service;
        public void NewStore()
            => _service = new VoiceSessionService("example-grid", clock: _clock.Get, seatLease: TimeSpan.FromSeconds(LeaseSeconds));

        protected override IVoiceSessionService NewRegionClient() => _service;

        protected override void LetSeatLeaseRun(Action renewKept, Func<bool> gone)
        {
            _clock.Now += TimeSpan.FromSeconds(LeaseSeconds / 2 + 1);
            renewKept();
            _clock.Now += TimeSpan.FromSeconds(LeaseSeconds / 2 + 1);
            _service.Sweep();
            Assert.True(gone());
        }

        public ValueTask InitializeAsync()
        {
            NewStore();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The scenarios against a call session service in another process, over HTTP. Opt-in; see the file header.</summary>
    // The assembly-wide 10 s backstop is too short for a seat lease that has to run out in real time.
    public class GridStoreProcessTests : StoreScenarios, IAsyncLifetime
        {
        public const string UrlVariable = "WEBRTC_VOICE_STORE_TEST_URL";
        public const string UserVariable = "WEBRTC_VOICE_STORE_TEST_USER";
        public const string PasswordVariable = "WEBRTC_VOICE_STORE_TEST_PASSWORD";
        public const string LeaseVariable = "WEBRTC_VOICE_STORE_TEST_SEAT_LEASE_SECONDS";

        private static string Url => Environment.GetEnvironmentVariable(UrlVariable);
        private static string Password => Environment.GetEnvironmentVariable(PasswordVariable);
        private static string User => Environment.GetEnvironmentVariable(UserVariable) is { Length: > 0 } u ? u : "voice-store";

        /// <summary>True when the variables that name the service are set.</summary>
        public static bool Configured => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(Password);
        public void RequireService()
        {
            if (!Configured)
                Assert.Skip($"{UrlVariable} and {PasswordVariable} are not both set: the tests against a call session service process are opt-in");
        }

        protected override void Require(string need)
        {
            if (need == "lease" && !int.TryParse(Environment.GetEnvironmentVariable(LeaseVariable), out _))
                Assert.Skip($"{LeaseVariable} is not set: the seat lease test needs the service's SeatLeaseSeconds");
        }

        protected override IVoiceSessionService NewRegionClient()
        {
            IniConfigSource config = new IniConfigSource();
            IConfig c = config.AddConfig("WebRtcVoice");
            c.Set("AuthType", "BasicHttpAuthentication");
            c.Set("HttpAuthUsername", User);
            c.Set("HttpAuthPassword", Password);
            return new VoiceSessionServicesConnector(config, "WebRtcVoice", Url, 5000);
        }

        protected override void LetSeatLeaseRun(Action renewKept, Func<bool> gone)
        {
            int lease = int.Parse(Environment.GetEnvironmentVariable(LeaseVariable));
            Stopwatch clock = Stopwatch.StartNew();
            // The sweep that collects an expired seat runs every few seconds, so allow a generous margin.
            TimeSpan limit = TimeSpan.FromSeconds(lease + 60);
            int renewEvery = Math.Max(1, lease / 3);
            while (clock.Elapsed < limit)
            {
                renewKept();
                if (gone()) return;
                Thread.Sleep(TimeSpan.FromSeconds(renewEvery));
            }
            Assert.Fail($"the unrenewed seat was still held {limit.TotalSeconds:F0} s after it was taken");
        }

        public ValueTask InitializeAsync()
        {
            RequireService();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>What a region does while the service is stopped, and after it is started again. Opt-in; see the file header.</summary>
    public class GridStoreRestartProof : IAsyncLifetime
        {
        private const string PhaseVariable = "WEBRTC_VOICE_STORE_TEST_PHASE";
        private const string StateVariable = "WEBRTC_VOICE_STORE_TEST_STATE";

        private static string Phase => Environment.GetEnvironmentVariable(PhaseVariable);
        private static string StateFile => Environment.GetEnvironmentVariable(StateVariable);

        private sealed class Ids
        {
            public UUID Group, Alice, Bob, Carol, Dave, Origin;
        }

        private IVoiceSessionService _client;
        private GridNonSpatialSessions _region;
        private A2ACalls _calls;
        private void Connect()
        {
            if (!GridStoreProcessTests.Configured || string.IsNullOrEmpty(Phase) || string.IsNullOrEmpty(StateFile))
                Assert.Skip($"{PhaseVariable} and {StateVariable} are not set (with the service address and password): the restart proof is opt-in");
            IniConfigSource config = new IniConfigSource();
            IConfig c = config.AddConfig("WebRtcVoice");
            c.Set("AuthType", "BasicHttpAuthentication");
            c.Set("HttpAuthUsername", Environment.GetEnvironmentVariable(GridStoreProcessTests.UserVariable) is { Length: > 0 } u ? u : "voice-store");
            c.Set("HttpAuthPassword", Environment.GetEnvironmentVariable(GridStoreProcessTests.PasswordVariable));
            // The same wrapper a region puts around the client, with the breaker's usual settings.
            _client = new GuardedVoiceSessionService(
                new VoiceSessionServicesConnector(config, "WebRtcVoice", Environment.GetEnvironmentVariable(GridStoreProcessTests.UrlVariable), 3000),
                new SessionCircuitBreaker(5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
            _region = VoiceSessionServiceTests.NewRegion(_client);
            _calls = new A2ACalls(() => _region);
        }

        private static Ids Load()
        {
            UUID[] v = File.ReadAllLines(StateFile).Select(l => new UUID(l.Trim())).ToArray();
            return new Ids { Group = v[0], Alice = v[1], Bob = v[2], Carol = v[3], Dave = v[4], Origin = v[5] };
        }

        private static OSDMap Request(string method, UUID session, UUID? other = null)
        {
            OSDMap m = new OSDMap { ["method"] = OSD.FromString(method), ["session-id"] = OSD.FromUUID(session) };
            if (other.HasValue) m["params"] = OSD.FromUUID(other.Value);
            return m;
        }

        [Fact(Timeout = 120000)]
        public void Run()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            switch (Phase)
            {
                case "seed": Seed(); break;
                case "down": Down(); break;
                case "after": After(); break;
                default: Assert.Fail($"{PhaseVariable} must be seed, down or after"); break;
            }
        }

        private void Seed()
        {
            Ids i = new Ids { Group = UUID.Random(), Alice = UUID.Random(), Bob = UUID.Random(), Carol = UUID.Random(), Dave = UUID.Random(), Origin = UUID.Random() };
            Assert.True(_region.Start(NonSpatialSessionType.Group, i.Alice, i.Group, i.Group, i.Origin).Ok);
            Assert.True(_region.Accept(i.Group, i.Bob, i.Origin).Ok);
            Assert.True(_region.Accept(i.Group, i.Carol, i.Origin).Ok);
            Assert.Equal(3, _region.Get(i.Group).SeatsHeld);
            {
                var assertedValue = _calls.Record(i.Alice, i.Dave, i.Origin, out _);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the avatar-to-avatar call is taken");
            }
            File.WriteAllLines(StateFile, new[] { i.Group, i.Alice, i.Bob, i.Carol, i.Dave, i.Origin }.Select(u => u.ToString()));
        }

        private void Down()
        {
            Ids i = Load();
            SessionOutcome start = default;
            {
                var assertedValue = () => start = _region.Start(NonSpatialSessionType.Group, UUID.Random(), i.Group, i.Group, i.Origin);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            Assert.False(start.Ok);
            Assert.Equal(SessionOutcome.StoreUnavailable, start.Decision);

            SessionOutcome join = default;
            {
                var assertedValue = () => join = _region.Accept(i.Group, UUID.Random(), i.Origin);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            {
                var assertedValue = join.Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.StoreUnavailable, assertedValue), "a new join is refused");
            }

            ChatSessionOutcome p2p = default;
            {
                var assertedValue = () => p2p = ChatSessionRequestLogic.Decide(Request(ChatSessionRequestLogic.MethodStartP2PVoice, UUID.Random(), UUID.Random()), UUID.Random(), _calls);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            {
                var assertedValue = p2p.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.ServiceUnavailable, assertedValue), "starting an avatar-to-avatar call is answered 503");
            }

            ChatSessionOutcome call = default;
            {
                var assertedValue = () => call = ChatSessionRequestLogic.Decide(Request(ChatSessionRequestLogic.MethodCall, A2ACalls.ComputeSessionId(i.Alice, i.Dave)), i.Alice, _calls);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            {
                var assertedValue = call.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.ServiceUnavailable, assertedValue), "joining one is answered 503, not 404: the store has not said the call is gone");
            }
        }

        private void After()
        {
            Ids i = Load();
            {
                var assertedValue = _region.Get(i.Group);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the restarted service starts empty: the group call from before is gone");
            }
            ChatSessionOutcome gone = ChatSessionRequestLogic.Decide(Request(ChatSessionRequestLogic.MethodCall, A2ACalls.ComputeSessionId(i.Alice, i.Dave)), i.Alice, _calls);
            {
                var assertedValue = gone.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.NotFound, assertedValue), "and now the store answers that the call is gone");
            }

            SessionOutcome start = _region.Start(NonSpatialSessionType.Group, i.Alice, i.Group, i.Group, i.Origin);
            Assert.True(start.Ok, start.Decision);
            Assert.True(start.Created, "a region starts the call again, as a new session");
            Assert.True(start.StartedRinging);
            Assert.True(_region.Accept(i.Group, i.Bob, i.Origin).Ok, "and its members take their seats again");
            Assert.Equal(2, _region.Get(i.Group).SeatsHeld);
            Assert.NotNull(_calls.Record(i.Alice, i.Dave, i.Origin, out bool created));
            Assert.True(created, "the avatar-to-avatar call is taken again");
            Assert.True(_region.DepartRegion(i.Origin) >= 3, "the region's seats are released again");
        }

        public ValueTask InitializeAsync()
        {
            Connect();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
