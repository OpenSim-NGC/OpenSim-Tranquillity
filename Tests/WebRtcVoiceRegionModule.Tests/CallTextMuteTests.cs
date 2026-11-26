/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The text check on a conference and on group chat: a member whose text a moderator muted gets nothing through.
 */
using System;
using System.Collections.Generic;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class CallTextMuteTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;

        // ---- text ------------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AMutedConferenceMembersLineGoesToNobody_AndAnotherMembersStillDoes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol });
            UUID id = start.Session.SessionId;
            e.Accept(id, Bob, RegionA);
            e.Accept(id, Carol, RegionA);
            e.Moderate(id, Alice, ModerationOp.TextMute, Bob, true);

            bool mine = AdhocTextSession.TryPlan(e, true, id, Bob, "hello", out AdhocTextPlan plan, out string decision);
            Assert.True(mine);
            Assert.Null(plan);
            Assert.Equal(AdhocTextSession.DecisionMuted, decision);

            AdhocTextSession.TryPlan(e, true, id, Carol, "hello", out AdhocTextPlan ok, out string okDecision);
            Assert.NotNull(ok);
            Assert.Equal(AdhocTextSession.DecisionDelivered, okDecision);

            e.Moderate(id, Alice, ModerationOp.TextMute, Bob, false);
            AdhocTextSession.TryPlan(e, true, id, Bob, "hello", out AdhocTextPlan again, out _);
            {
                var assertedValue = again;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "unmuted");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheGroupChatGateReadsTheRecord_AndAGridCopyIsKeptBrieflyAndDroppedOnAChange()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialIdentityTests.FakeClock clock = new();
            VoiceSessionService service = VoiceSessionServiceTests.NewService(clock);
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            CallGroupChatGate gate = new CallGroupChatGate(b, cache: true, clock.Get);

            Assert.True(gate.MaySend(Group, Bob));
            a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);
            Assert.True(gate.MaySend(Group, Bob), "the copy kept for a few seconds still says yes");

            gate.Forget(Group);
            Assert.False(gate.MaySend(Group, Bob));

            a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, false);
            clock.Now += CallGroupChatGate.Kept + TimeSpan.FromSeconds(1);
            Assert.True(gate.MaySend(Group, Bob), "after the time it reads the service again");
            Assert.True(gate.MaySend(UUID.Random(), Bob), "a group with no call");
        }

        [Fact(Timeout = 60000)]
        public void TheGroupChatGateKeepsAGridCopyForTenSeconds_SoOneReadServesTheLinesInBetween()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialIdentityTests.FakeClock clock = new();
            VoiceSessionService service = VoiceSessionServiceTests.NewService(clock);
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            CallRemovalModuleTests.CountingSessions counting =
                (CallRemovalModuleTests.CountingSessions)(object)System.Reflection.DispatchProxy.Create<INonSpatialSessions, CallRemovalModuleTests.CountingSessions>();
            counting.Inner = b;
            CallGroupChatGate gate = new CallGroupChatGate((INonSpatialSessions)(object)counting, cache: true, clock.Get);

            DateTime t0 = clock.Now;
            gate.MaySend(Group, Bob);
            foreach (int second in new[] { 3, 4, 6, 9 })
            {
                clock.Now = t0 + TimeSpan.FromSeconds(second);
                gate.MaySend(Group, Bob);
            }
            {
                var assertedValue = counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.Get));
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "five lines in nine seconds: one read");
            }

            clock.Now = t0 + TimeSpan.FromSeconds(11);
            gate.MaySend(Group, Bob);
            {
                var assertedValue = counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.Get));
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "and the next one after ten seconds reads again");
            }
            Assert.Equal(TimeSpan.FromSeconds(10), CallGroupChatGate.Kept);
        }
    }
}
