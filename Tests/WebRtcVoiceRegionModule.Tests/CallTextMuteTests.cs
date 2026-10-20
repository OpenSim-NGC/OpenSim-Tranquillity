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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CallTextMuteTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;

        // ---- text ------------------------------------------------------------------------------------------

        [Test]
        public void AMutedConferenceMembersLineGoesToNobody_AndAnotherMembersStillDoes()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol });
            UUID id = start.Session.SessionId;
            e.Accept(id, Bob, RegionA);
            e.Accept(id, Carol, RegionA);
            e.Moderate(id, Alice, ModerationOp.TextMute, Bob, true);

            bool mine = AdhocTextSession.TryPlan(e, true, id, Bob, "hello", out AdhocTextPlan plan, out string decision);
            Assert.That(mine, Is.True);
            Assert.That(plan, Is.Null);
            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionMuted));

            AdhocTextSession.TryPlan(e, true, id, Carol, "hello", out AdhocTextPlan ok, out string okDecision);
            Assert.That(ok, Is.Not.Null);
            Assert.That(okDecision, Is.EqualTo(AdhocTextSession.DecisionDelivered));

            e.Moderate(id, Alice, ModerationOp.TextMute, Bob, false);
            AdhocTextSession.TryPlan(e, true, id, Bob, "hello", out AdhocTextPlan again, out _);
            Assert.That(again, Is.Not.Null, "unmuted");
        }

        [Test]
        public void TheGroupChatGateReadsTheRecord_AndAGridCopyIsKeptBrieflyAndDroppedOnAChange()
        {
            NonSpatialIdentityTests.FakeClock clock = new();
            VoiceSessionService service = VoiceSessionServiceTests.NewService(clock);
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            CallGroupChatGate gate = new CallGroupChatGate(b, cache: true, clock.Get);

            Assert.That(gate.MaySend(Group, Bob), Is.True);
            a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);
            Assert.That(gate.MaySend(Group, Bob), Is.True, "the copy kept for a few seconds still says yes");

            gate.Forget(Group);
            Assert.That(gate.MaySend(Group, Bob), Is.False);

            a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, false);
            clock.Now += CallGroupChatGate.Kept + TimeSpan.FromSeconds(1);
            Assert.That(gate.MaySend(Group, Bob), Is.True, "after the time it reads the service again");
            Assert.That(gate.MaySend(UUID.Random(), Bob), Is.True, "a group with no call");
        }
    }
}
