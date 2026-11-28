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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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
            Assert.NotNull(calls.Record(Alice, Bob, RegionA, out _));
            Assert.NotNull(calls.IssueToken(CallId, Alice));
            calls.MarkProvisioned(CallId, Alice, RegionA, "vs-alice", out bool callerBecameActive);
            Assert.False(callerBecameActive, "the caller's join never makes the call active");
        }

        [Fact(Timeout = 60000)]
        public async Task CalleeJoinsFromTwoRegionServers_ExactlyOneMakesTheCallActive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int tooMany = 0, none = 0;
            for (int round = 0; round < 100; round++)
            {
                VoiceSessionService service = VoiceSessionServiceTests.NewService();
                GridNonSpatialSessions regionA = VoiceSessionServiceTests.NewRegion(service);
                GridNonSpatialSessions regionB = VoiceSessionServiceTests.NewRegion(service);
                A2ACalls a = new A2ACalls(() => regionA);
                A2ACalls b = new A2ACalls(() => regionB);
                RingingCall(a);
                Assert.Equal(A2ASessionState.Invited, b.TryGet(CallId, Bob).State);

                int winners = 0;
                using Barrier gate = new Barrier(Joiners);
                Task[] joins = Enumerable.Range(0, Joiners).Select(n => Task.Factory.StartNew(() =>
                {
                    gate.SignalAndWait(TestContext.Current.CancellationToken);
                    bool won = n % 2 == 0
                        ? CalleeJoin(a, RegionA, "vs-bob-" + n)
                        : CalleeJoin(b, RegionB, "vs-bob-" + n);
                    if (won) Interlocked.Increment(ref winners);
                }, TaskCreationOptions.LongRunning)).ToArray();
                await Task.WhenAll(joins);
                if (winners > 1) tooMany++;
                if (winners == 0) none++;
            }
            {
                var assertedValue = tooMany;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "rounds in which more than one join was told it made the call active");
            }
            {
                var assertedValue = none;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "rounds in which no join was told it made the call active");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARejoinFromAnotherRegionServer_IsNotATransition()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions regionA = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions regionB = VoiceSessionServiceTests.NewRegion(service);
            A2ACalls a = new A2ACalls(() => regionA);
            A2ACalls b = new A2ACalls(() => regionB);
            RingingCall(a);

            Assert.True(CalleeJoin(a, RegionA, "vs-1"));
            Assert.False(CalleeJoin(b, RegionB, "vs-2"), "the callee arriving again from another process is not a new accept");
            Assert.Equal(A2ASessionState.Active, b.TryGet(CallId, Bob).State);
        }
    }
}
