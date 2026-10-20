/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Which callee join made an avatar-to-avatar call active, with the call kept in the grid session service.
 *
 * The callee's join sends both parties the active-pair update, so exactly one join may claim the change however
 * many arrive together, from one region server or several. A region reads copies of the call, so comparing the
 * call's state before and after its own join cannot decide it: joins that arrive together all read Invited first.
 * The engine in the service decides inside the store's mutation (the first seating of the callee), the reply
 * carries that decision back, and A2ACalls reports it. A2ASessionConcurrencyTests covers the same property for
 * a call kept in this process.
 */
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2AActivationRaceTests
    {
        private const int Joiners = 8;
        private static readonly UUID CallId = A2ACalls.ComputeSessionId(Alice, Bob);

        private static bool CalleeJoin(A2ACalls calls, UUID region, string viewerSession)
        {
            calls.MarkProvisioned(CallId, Bob, region, viewerSession, out bool becameActive);
            return becameActive;
        }

        private static void RingingCall(A2ACalls calls)
        {
            Assert.That(calls.Record(Alice, Bob, RegionA, out _), Is.Not.Null);
            Assert.That(calls.IssueToken(CallId, Alice), Is.Not.Null);
            calls.MarkProvisioned(CallId, Alice, RegionA, "vs-alice", out bool callerBecameActive);
            Assert.That(callerBecameActive, Is.False, "the caller's join never makes the call active");
        }

        [Test]
        public void CalleeJoinsFromTwoRegionServers_ExactlyOneMakesTheCallActive()
        {
            int tooMany = 0, none = 0;
            for (int round = 0; round < 100; round++)
            {
                VoiceSessionService service = VoiceSessionServiceTests.NewService();
                GridNonSpatialSessions regionA = VoiceSessionServiceTests.NewRegion(service);
                GridNonSpatialSessions regionB = VoiceSessionServiceTests.NewRegion(service);
                A2ACalls a = new A2ACalls(() => regionA);
                A2ACalls b = new A2ACalls(() => regionB);
                RingingCall(a);
                Assert.That(b.TryGet(CallId, Bob).State, Is.EqualTo(A2ASessionState.Invited));

                int winners = 0;
                using Barrier gate = new Barrier(Joiners);
                Task[] joins = Enumerable.Range(0, Joiners).Select(n => Task.Factory.StartNew(() =>
                {
                    gate.SignalAndWait();
                    bool won = n % 2 == 0
                        ? CalleeJoin(a, RegionA, "vs-bob-" + n)
                        : CalleeJoin(b, RegionB, "vs-bob-" + n);
                    if (won) Interlocked.Increment(ref winners);
                }, TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(joins);
                if (winners > 1) tooMany++;
                if (winners == 0) none++;
            }
            Assert.That(tooMany, Is.EqualTo(0), "rounds in which more than one join was told it made the call active");
            Assert.That(none, Is.EqualTo(0), "rounds in which no join was told it made the call active");
        }

        [Test]
        public void ARejoinFromAnotherRegionServer_IsNotATransition()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions regionA = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions regionB = VoiceSessionServiceTests.NewRegion(service);
            A2ACalls a = new A2ACalls(() => regionA);
            A2ACalls b = new A2ACalls(() => regionB);
            RingingCall(a);

            Assert.That(CalleeJoin(a, RegionA, "vs-1"), Is.True);
            Assert.That(CalleeJoin(b, RegionB, "vs-2"), Is.False, "the callee arriving again from another process is not a new accept");
            Assert.That(b.TryGet(CallId, Bob).State, Is.EqualTo(A2ASessionState.Active));
        }
    }
}
