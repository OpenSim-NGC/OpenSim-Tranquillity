/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What a call's moderators decided -- voice mutes, text mutes and "mute all" -- lives in the session record:
 * in the engine of this process, and through the grid session service. These tests cover the record and its copy.
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
    public class CallModerationStateTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;

        private static NonSpatialVoiceSession StartGroup(NonSpatialVoiceSessionEngine e)
        {
            SessionOutcome s = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            e.Accept(Group, Bob, RegionA);
            return s.Session;
        }

        [Test]
        public void AVoiceMuteIsRecordedOnce_AndTheGenerationMovesOnlyWhenSomethingChanged()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);

            SessionOutcome first = e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, true);
            SessionOutcome again = e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, true);

            Assert.That(first.Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            Assert.That(again.Decision, Is.EqualTo(SessionOutcome.ModerationUnchanged));
            Assert.That(s.IsVoiceMuted(Bob), Is.True);
            Assert.That(s.IsVoiceMuted(Alice), Is.False);
            Assert.That(s.ModerationGeneration, Is.EqualTo(1));

            Assert.That(e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, false).Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            Assert.That(s.IsVoiceMuted(Bob), Is.False);
            Assert.That(s.ModerationGeneration, Is.EqualTo(2));
        }

        [Test]
        public void TextAndVoiceAreSeparateLists()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);

            e.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            Assert.That(s.IsTextMuted(Bob), Is.True);
            Assert.That(s.IsVoiceMuted(Bob), Is.False);
        }

        [Test]
        public void MuteAllSilencesEveryoneUntilAMemberIsLetSpeak_AndUnmuteAllClearsTheLot()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);
            UUID late = UUID.Random();

            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            Assert.That(s.IsVoiceMuted(Bob), Is.True);
            Assert.That(s.IsVoiceMuted(late), Is.True, "someone who joins later is muted");

            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, false);
            Assert.That(s.IsVoiceMuted(Bob), Is.False, "let speak by name while the call is moderated");
            Assert.That(s.IsVoiceMuted(late), Is.True);

            e.Moderate(Group, Alice, ModerationOp.VoiceMute, late, true);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, false);
            Assert.That(s.VoiceModerated, Is.False);
            Assert.That(s.IsVoiceMuted(late), Is.False);
            Assert.That(s.IsVoiceMuted(Bob), Is.False);
        }

        [Test]
        public void AMuteAllEndsWithTheCall_AndTheNextCallOfTheGroupStartsUnmuted()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            StartGroup(e);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            e.Depart(Group, Alice, DepartureReason.ChatLeave);
            Assert.That(e.Get(Group).VoiceModerated, Is.True, "Bob is still in the call");
            e.Depart(Group, Bob, DepartureReason.ChatLeave);

            NonSpatialVoiceSession ended = e.Get(Group);
            Assert.That(ended.SeatsHeld, Is.EqualTo(0));
            Assert.That(ended.VoiceModerated, Is.False, "the last seat was released");
            Assert.That(ended.IsTextMuted(Bob), Is.False);
            e.Sweep();
            Assert.That(e.Get(Group), Is.Null, "an emptied group call is collected like any other");

            e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            e.Accept(Group, Carol, RegionA);
            Assert.That(e.Get(Group).IsVoiceMuted(Carol), Is.False, "whoever joins the next call is not muted");

            SessionOutcome conf = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            e.Moderate(conf.Session.SessionId, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Depart(conf.Session.SessionId, Alice, DepartureReason.ChatLeave);
            e.Sweep();
            Assert.That(e.Get(conf.Session.SessionId), Is.Null, "an emptied conference is collected as before");
        }

        [Test]
        public void AGroupRecordKeptForARemovalIsCollectedWhenItGoesIdle()
        {
            NonSpatialIdentityTests.FakeClock clock = new();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            StartGroup(e);
            e.Moderate(Group, Alice, ModerationOp.Remove, Carol, true, "x", TimeSpan.FromHours(20));
            e.Depart(Group, Alice, DepartureReason.ChatLeave);
            e.Depart(Group, Bob, DepartureReason.ChatLeave);

            clock.Now += TimeSpan.FromHours(7);
            e.Sweep();
            Assert.That(e.Get(Group), Is.Not.Null);

            clock.Now += TimeSpan.FromHours(2);
            e.Sweep();
            Assert.That(e.Get(Group), Is.Null);
        }

        [Test]
        public void ACallBetweenTwoPeopleIsNotModerated_AndAnUnknownSessionIsRefused()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome p2p = e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);

            Assert.That(e.Moderate(p2p.Session.SessionId, Alice, ModerationOp.VoiceMute, Bob, true).Ok, Is.False);
            Assert.That(e.Moderate(UUID.Random(), Alice, ModerationOp.VoiceMute, Bob, true).Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
        }

        [Test]
        public void ARequestNamingNobodyOrNoOneAskingIsRefused_AndAFullListRefusesMore()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);

            Assert.That(e.Moderate(Group, Alice, ModerationOp.VoiceMute, UUID.Zero, true).Ok, Is.False);
            Assert.That(e.Moderate(Group, UUID.Zero, ModerationOp.VoiceMute, Bob, true).Ok, Is.False);
            for (int i = 0; i < NonSpatialVoiceSession.MaxModerationEntries; i++)
                Assert.That(e.Moderate(Group, Alice, ModerationOp.TextMute, UUID.Random(), true).Ok, Is.True);
            SessionOutcome over = e.Moderate(Group, Alice, ModerationOp.TextMute, UUID.Random(), true);
            Assert.That(over.Decision, Is.EqualTo(SessionOutcome.ModerationRefused));
            Assert.That(s.TextMuted.Count, Is.EqualTo(NonSpatialVoiceSession.MaxModerationEntries));
        }

        [Test]
        public void TheCopyOfARecordCarriesTheModeration_AndAnOldCopyReadsAsNone()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);
            UUID carol = UUID.Random();
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, false);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, carol, true);
            e.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            SessionData data = SessionWire.ToData(s);
            NonSpatialVoiceSession back = SessionWire.FromData(SessionReply.FromJson(new SessionReply { Session = data }.ToJson()).Session);

            Assert.That(back.VoiceModerated, Is.True);
            Assert.That(back.ModerationGeneration, Is.EqualTo(s.ModerationGeneration));
            Assert.That(back.IsVoiceMuted(Bob), Is.False);
            Assert.That(back.IsVoiceMuted(carol), Is.True);
            Assert.That(back.IsTextMuted(Bob), Is.True);

            data.VoiceMuted = null;
            data.VoiceAllowed = null;
            data.TextMuted = null;
            data.VoiceModerated = false;
            Assert.That(SessionWire.FromData(data).HasModeration, Is.False);
        }

        [Test]
        public void ThroughTheGridService_TwoRegionsSeeOneModerationRecord()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);

            SessionOutcome set = a.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            SessionOutcome mute = a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            Assert.That(set.Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            Assert.That(mute.Session.ModerationGeneration, Is.EqualTo(2));
            NonSpatialVoiceSession seen = b.Get(Group, Alice);
            Assert.That(seen.VoiceModerated, Is.True);
            Assert.That(seen.IsTextMuted(Bob), Is.True);
            Assert.That(a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true).Decision, Is.EqualTo(SessionOutcome.ModerationUnchanged));
            Assert.That(b.Moderate(UUID.Random(), Alice, ModerationOp.TextMute, Bob, true).Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
        }
    }
}
