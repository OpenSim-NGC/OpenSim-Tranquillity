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
using NUnit.Framework;
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

        [Test]
        public void TheSeatCapHoldsAcrossTwoRegions()
        {
            GridNonSpatialSessions a = NewRegion(), b = NewRegion();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), group = UUID.Random();

            SessionOutcome started = a.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, regionA);
            Assert.That(started.Ok, Is.True, started.Decision);

            int accepted = 0, refused = 0;
            UUID[] members = Agents(60);
            for (int i = 0; i < members.Length; i++)
            {
                SessionOutcome o = (i % 2 == 0 ? a : b).Accept(group, members[i], i % 2 == 0 ? regionA : regionB);
                if (o.Ok) accepted++;
                else if (o.Decision == SessionOutcome.Capacity) refused++;
            }

            Assert.That(accepted, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap - 1), "49 more seats after the creator");
            Assert.That(refused, Is.EqualTo(members.Length - accepted), "everyone else is told the room is full");
            Assert.That(a.Get(group).SeatsHeld, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap));
            Assert.That(b.Get(group).SeatsHeld, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap), "both regions see the one count");
        }

        // ---- a group is rung once ---------------------------------------------------------------------

        [Test]
        public void AGroupRingsOnce_WhoeverJoinsFromWhichRegion()
        {
            GridNonSpatialSessions a = NewRegion(), b = NewRegion();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), group = UUID.Random();

            SessionOutcome opened = a.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, regionA);
            SessionOutcome joined = b.Accept(group, UUID.Random(), regionB);
            SessionOutcome joinedAgain = b.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, regionB);

            Assert.That(opened.StartedRinging, Is.True, "the opener's region rings the group");
            Assert.That(joined.Ok, Is.True, joined.Decision);
            Assert.That(joined.StartedRinging, Is.False, "a join from another region does not ring it again");
            Assert.That(joinedAgain.StartedRinging, Is.False);
        }

        // ---- two regions racing -----------------------------------------------------------------------

        [Test]
        public void TwoRegionsRacingToStartOneGroupCallGetOneSession()
        {
            GridNonSpatialSessions[] regions = { NewRegion(), NewRegion() };
            UUID[] origin = { UUID.Random(), UUID.Random() };
            UUID group = UUID.Random();
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            UUID[] callers = Agents(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                return regions[i % 2].Start(NonSpatialSessionType.Group, callers[i], group, group, origin[i % 2]);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(tasks);

            Assert.That(tasks.All(t => t.Result.Ok), Is.True, string.Join(",", tasks.Select(t => t.Result.Decision).Distinct()));
            Assert.That(tasks.Count(t => t.Result.Created), Is.EqualTo(1), "one start made the session");
            Assert.That(tasks.Count(t => t.Result.StartedRinging), Is.EqualTo(1), "one start rang the group");
            Assert.That(regions[0].Get(group).SeatsHeld, Is.EqualTo(1), "a start seats only the one that made the session");
        }

        [Test]
        public void TwoRegionsRetryingOneConferenceStartGetOneSession()
        {
            GridNonSpatialSessions[] regions = { NewRegion(), NewRegion() };
            UUID[] origin = { UUID.Random(), UUID.Random() };
            UUID creator = UUID.Random(), temp = UUID.Random();
            const int attempts = 32;
            using Barrier gate = new Barrier(attempts);
            Task<SessionOutcome>[] tasks = Enumerable.Range(0, attempts).Select(i => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                return regions[i % 2].StartAdhoc(creator, temp, origin[i % 2]);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(tasks);

            Assert.That(tasks.All(t => t.Result.Ok), Is.True, string.Join(",", tasks.Select(t => t.Result.Decision).Distinct()));
            Assert.That(tasks.Select(t => t.Result.Session.SessionId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(tasks.Count(t => t.Result.Created), Is.EqualTo(1));
        }

        // ---- a region restarting, and a seat nobody renews ----------------------------------------------

        [Test]
        public void ARestartedRegionReleasesItsSeatsAndNoOthers()
        {
            GridNonSpatialSessions a = NewRegion(), b = NewRegion();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), group = UUID.Random();
            UUID alice = UUID.Random(), bob = UUID.Random(), onB = UUID.Random();
            a.Start(NonSpatialSessionType.Group, alice, group, group, regionA);
            a.Accept(group, bob, regionA);
            b.Accept(group, onB, regionB);

            int released = a.DepartRegion(regionA);

            Assert.That(released, Is.EqualTo(2), "the creator's seat and Bob's, both taken from region A");
            NonSpatialVoiceSession s = b.Get(group);
            Assert.That(s.SeatsHeld, Is.EqualTo(1));
            Assert.That(s.Find(onB).HoldsSeat, Is.True, "region B's seat is untouched");
            Assert.That(s.Find(bob).Departure, Is.EqualTo(DepartureReason.PresenceLost));
            Assert.That(b.DepartRegion(regionA), Is.EqualTo(0), "a second release finds nothing left");
        }

        [Test]
        public void ASeatNobodyRenews_IsReleasedWhenItsLeaseRunsOut_AndARenewedSeatIsKept()
        {
            Require("lease");
            GridNonSpatialSessions a = NewRegion();
            UUID regionA = UUID.Random(), group = UUID.Random(), alice = UUID.Random(), bob = UUID.Random();
            a.Start(NonSpatialSessionType.Group, alice, group, group, regionA);
            a.Accept(group, bob, regionA);

            LetSeatLeaseRun(
                () => a.RenewSeats(new List<SeatKey> { new SeatKey(group, alice) }),
                () => !a.Get(group).Find(bob).HoldsSeat);

            NonSpatialVoiceSession s = a.Get(group);
            Assert.That(s.Find(alice).HoldsSeat, Is.True, "renewed, so kept");
            Assert.That(s.Find(bob).HoldsSeat, Is.False, "never renewed, so released");
            Assert.That(s.Find(bob).Departure, Is.EqualTo(DepartureReason.PresenceLost));
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
            Assert.That(started.Ok, Is.True, started.Decision);
            c.SessionId = started.Session.SessionId;
            c.RoomKey = started.Session.RoomKey;
            c.Token = c.Sender.IssueToken(c.SessionId, c.Alice);
            if (markInvited) c.Sender.MarkInvited(c.SessionId, new[] { c.Bob, c.Carol });
            return c;
        }

        [Test]
        public void ARingVerifiesOnceAndOnlyForWhatWasSent()
        {
            Conference c = NewConference(markInvited: true);
            const NonSpatialSessionType adhoc = NonSpatialSessionType.Adhoc;

            SessionOutcome genuine = c.Receiver.VerifyRing(c.SessionId, c.Bob, c.Alice, adhoc);
            Assert.That(genuine.Ok, Is.True, genuine.Decision);
            Assert.That(genuine.Session.Token, Is.EqualTo(c.Token), "an invited target is given the credential");

            SessionOutcome replay = c.Receiver.VerifyRing(c.SessionId, c.Bob, c.Alice, adhoc);
            Assert.That(replay.Ok, Is.False);
            Assert.That(replay.Decision, Is.EqualTo("refused-ring-used"));

            Assert.That(c.Receiver.VerifyRing(c.SessionId, c.Dave, c.Alice, adhoc).Decision, Is.EqualTo("refused-target-not-invited"));
            Assert.That(c.Receiver.VerifyRing(c.SessionId, c.Carol, c.Dave, adhoc).Decision, Is.EqualTo("refused-caller-not-seated"));
            Assert.That(c.Receiver.VerifyRing(c.SessionId, c.Carol, c.Alice, NonSpatialSessionType.Group).Decision, Is.EqualTo("refused-session-type-mismatch"));
            Assert.That(c.Receiver.VerifyRing(c.SessionId, c.Carol, c.Alice, adhoc).Ok, Is.True, "the refusals did not use Carol's ring up");
        }

        [Test]
        public void ARingForASessionThatDoesNotExist_IsRefused_AndNothingIsCreated()
        {
            Conference c = NewConference(markInvited: true);
            UUID nothing = UUID.Random();

            SessionOutcome o = c.Receiver.VerifyRing(nothing, c.Bob, c.Alice, NonSpatialSessionType.Adhoc);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
            Assert.That(c.Receiver.Get(nothing), Is.Null, "the forged ring made no session");
        }

        [Test]
        public void ARingBeforeTheSenderMarkedTheTarget_IsRefused()
        {
            Conference c = NewConference(markInvited: false);

            Assert.That(c.Receiver.VerifyRing(c.SessionId, c.Bob, c.Alice, NonSpatialSessionType.Adhoc).Decision, Is.EqualTo("refused-target-not-invited"));
        }

        // ---- the conference text key goes only to seated members ----------------------------------------

        [Test]
        public void TheConferenceTextKeyGoesOnlyToSeatedMembers()
        {
            Conference c = NewConference(markInvited: true);
            Assert.That(c.Receiver.Accept(c.SessionId, c.Bob, c.ReceiverRegion).Ok, Is.True);

            string first = c.Sender.CarrierKey(c.SessionId, c.Alice);
            string second = c.Receiver.CarrierKey(c.SessionId, c.Bob);

            Assert.That(first, Has.Length.GreaterThanOrEqualTo(CarrierAuth.MinSecretLength));
            Assert.That(second, Is.EqualTo(first), "every seated member has the same key, from whichever region");
            Assert.That(c.Receiver.CarrierKey(c.SessionId, c.Carol), Is.Null, "invited, not seated");
            Assert.That(c.Receiver.CarrierKey(c.SessionId, c.Dave), Is.Null, "not in the conference");
            Assert.That(c.Receiver.CarrierKey(UUID.Random(), c.Alice), Is.Null, "no such conference");

            SessionOutcome other = c.Sender.StartAdhoc(c.Alice, UUID.Random(), c.SenderRegion);
            Assert.That(c.Sender.CarrierKey(other.Session.SessionId, c.Alice), Is.Not.EqualTo(first), "each conference has its own key");

            c.Receiver.Depart(c.SessionId, c.Bob, DepartureReason.VoiceTeardown);
            Assert.That(NewRegion().CarrierKey(c.SessionId, c.Bob), Is.Null, "a member who left no longer has it (asked of a region that has not kept it)");
        }

        // ---- the time one provision step takes ----------------------------------------------------------

        /// <summary>The numbers are reported, not held to a tight bound: where the service runs changes them.</summary>
        [Test]
        public void TheTimeOfAJoinAndAReadIsReported()
        {
            GridNonSpatialSessions region = NewRegion();
            UUID origin = UUID.Random();
            const int groups = 6, perGroup = 40;
            List<double> joins = new List<double>(), reads = new List<double>();
            for (int g = 0; g < groups; g++)
            {
                UUID group = UUID.Random();
                Assert.That(region.Start(NonSpatialSessionType.Group, UUID.Random(), group, group, origin).Ok, Is.True);
                for (int i = 0; i < perGroup; i++)
                {
                    UUID agent = UUID.Random();
                    Stopwatch w = Stopwatch.StartNew();
                    SessionOutcome o = region.Accept(group, agent, origin);
                    joins.Add(w.Elapsed.TotalMilliseconds);
                    Assert.That(o.Ok, Is.True, o.Decision);
                    w.Restart();
                    Assert.That(region.Get(group, agent), Is.Not.Null);
                    reads.Add(w.Elapsed.TotalMilliseconds);
                }
            }

            string joinLine = Describe("join", joins), readLine = Describe("read", reads);
            TestContext.Progress.WriteLine(joinLine);
            TestContext.Progress.WriteLine(readLine);
            Assert.That(Percentile(joins, 0.95), Is.LessThan(2000), "a sanity bound only: " + joinLine);
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
    [TestFixture]
    public class InProcessStoreScenarios : StoreScenarios
    {
        private readonly NonSpatialIdentityTests.FakeClock _clock = new NonSpatialIdentityTests.FakeClock();
        private const int LeaseSeconds = 10;
        private VoiceSessionService _service;

        [SetUp]
        public void NewStore()
            => _service = new VoiceSessionService("example-grid", clock: _clock.Get, seatLease: TimeSpan.FromSeconds(LeaseSeconds));

        protected override IVoiceSessionService NewRegionClient() => _service;

        protected override void LetSeatLeaseRun(Action renewKept, Func<bool> gone)
        {
            _clock.Now += TimeSpan.FromSeconds(LeaseSeconds / 2 + 1);
            renewKept();
            _clock.Now += TimeSpan.FromSeconds(LeaseSeconds / 2 + 1);
            _service.Sweep();
            Assert.That(gone(), Is.True);
        }
    }

    /// <summary>The scenarios against a call session service in another process, over HTTP. Opt-in; see the file header.</summary>
    // The assembly-wide 10 s backstop is too short for a seat lease that has to run out in real time.
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(240000)]
#pragma warning restore CS0618
    public class GridStoreProcessTests : StoreScenarios
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

        [SetUp]
        public void RequireService()
        {
            if (!Configured)
                Assert.Ignore($"{UrlVariable} and {PasswordVariable} are not both set: the tests against a call session service process are opt-in");
        }

        protected override void Require(string need)
        {
            if (need == "lease" && !int.TryParse(Environment.GetEnvironmentVariable(LeaseVariable), out _))
                Assert.Ignore($"{LeaseVariable} is not set: the seat lease test needs the service's SeatLeaseSeconds");
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
    }

    /// <summary>What a region does while the service is stopped, and after it is started again. Opt-in; see the file header.</summary>
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(120000)]
#pragma warning restore CS0618
    public class GridStoreRestartProof
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

        [SetUp]
        public void Connect()
        {
            if (!GridStoreProcessTests.Configured || string.IsNullOrEmpty(Phase) || string.IsNullOrEmpty(StateFile))
                Assert.Ignore($"{PhaseVariable} and {StateVariable} are not set (with the service address and password): the restart proof is opt-in");
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

        [Test]
        public void Run()
        {
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
            Assert.That(_region.Start(NonSpatialSessionType.Group, i.Alice, i.Group, i.Group, i.Origin).Ok, Is.True);
            Assert.That(_region.Accept(i.Group, i.Bob, i.Origin).Ok, Is.True);
            Assert.That(_region.Accept(i.Group, i.Carol, i.Origin).Ok, Is.True);
            Assert.That(_region.Get(i.Group).SeatsHeld, Is.EqualTo(3));
            Assert.That(_calls.Record(i.Alice, i.Dave, i.Origin, out _), Is.Not.Null, "the avatar-to-avatar call is taken");
            File.WriteAllLines(StateFile, new[] { i.Group, i.Alice, i.Bob, i.Carol, i.Dave, i.Origin }.Select(u => u.ToString()));
        }

        private void Down()
        {
            Ids i = Load();
            SessionOutcome start = default;
            Assert.DoesNotThrow(() => start = _region.Start(NonSpatialSessionType.Group, UUID.Random(), i.Group, i.Group, i.Origin));
            Assert.That(start.Ok, Is.False);
            Assert.That(start.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));

            SessionOutcome join = default;
            Assert.DoesNotThrow(() => join = _region.Accept(i.Group, UUID.Random(), i.Origin));
            Assert.That(join.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable), "a new join is refused");

            ChatSessionOutcome p2p = default;
            Assert.DoesNotThrow(() => p2p = ChatSessionRequestLogic.Decide(Request(ChatSessionRequestLogic.MethodStartP2PVoice, UUID.Random(), UUID.Random()), UUID.Random(), _calls));
            Assert.That(p2p.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "starting an avatar-to-avatar call is answered 503");

            ChatSessionOutcome call = default;
            Assert.DoesNotThrow(() => call = ChatSessionRequestLogic.Decide(Request(ChatSessionRequestLogic.MethodCall, A2ACalls.ComputeSessionId(i.Alice, i.Dave)), i.Alice, _calls));
            Assert.That(call.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "joining one is answered 503, not 404: the store has not said the call is gone");
        }

        private void After()
        {
            Ids i = Load();
            Assert.That(_region.Get(i.Group), Is.Null, "the restarted service starts empty: the group call from before is gone");
            ChatSessionOutcome gone = ChatSessionRequestLogic.Decide(Request(ChatSessionRequestLogic.MethodCall, A2ACalls.ComputeSessionId(i.Alice, i.Dave)), i.Alice, _calls);
            Assert.That(gone.Status, Is.EqualTo(HttpStatusCode.NotFound), "and now the store answers that the call is gone");

            SessionOutcome start = _region.Start(NonSpatialSessionType.Group, i.Alice, i.Group, i.Group, i.Origin);
            Assert.That(start.Ok, Is.True, start.Decision);
            Assert.That(start.Created, Is.True, "a region starts the call again, as a new session");
            Assert.That(start.StartedRinging, Is.True);
            Assert.That(_region.Accept(i.Group, i.Bob, i.Origin).Ok, Is.True, "and its members take their seats again");
            Assert.That(_region.Get(i.Group).SeatsHeld, Is.EqualTo(2));
            Assert.That(_calls.Record(i.Alice, i.Dave, i.Origin, out bool created), Is.Not.Null);
            Assert.That(created, Is.True, "the avatar-to-avatar call is taken again");
            Assert.That(_region.DepartRegion(i.Origin), Is.GreaterThanOrEqualTo(3), "the region's seats are released again");
        }
    }
}
