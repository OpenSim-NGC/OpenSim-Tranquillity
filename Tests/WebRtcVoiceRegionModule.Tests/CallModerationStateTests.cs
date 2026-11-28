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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class CallModerationStateTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;

        private static NonSpatialVoiceSession StartGroup(NonSpatialVoiceSessionEngine e)
        {
            SessionOutcome s = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            e.Accept(Group, Bob, RegionA);
            return s.Session;
        }

        [Fact(Timeout = 60000)]
        public void AVoiceMuteIsRecordedOnce_AndTheGenerationMovesOnlyWhenSomethingChanged()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);

            SessionOutcome first = e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, true);
            SessionOutcome again = e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, true);

            Assert.Equal(SessionOutcome.ModerationChanged, first.Decision);
            Assert.Equal(SessionOutcome.ModerationUnchanged, again.Decision);
            Assert.True(s.IsVoiceMuted(Bob));
            Assert.False(s.IsVoiceMuted(Alice));
            Assert.Equal(1, s.ModerationGeneration);

            Assert.Equal(SessionOutcome.ModerationChanged, e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, false).Decision);
            Assert.False(s.IsVoiceMuted(Bob));
            Assert.Equal(2, s.ModerationGeneration);
        }

        [Fact(Timeout = 60000)]
        public void TextAndVoiceAreSeparateLists()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);

            e.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            Assert.True(s.IsTextMuted(Bob));
            Assert.False(s.IsVoiceMuted(Bob));
        }

        [Fact(Timeout = 60000)]
        public void MuteAllSilencesEveryoneUntilAMemberIsLetSpeak_AndUnmuteAllClearsTheLot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);
            UUID late = UUID.Random();

            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            Assert.True(s.IsVoiceMuted(Bob));
            Assert.True(s.IsVoiceMuted(late), "someone who joins later is muted");

            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, false);
            Assert.False(s.IsVoiceMuted(Bob), "let speak by name while the call is moderated");
            Assert.True(s.IsVoiceMuted(late));

            e.Moderate(Group, Alice, ModerationOp.VoiceMute, late, true);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, false);
            Assert.False(s.VoiceModerated);
            Assert.False(s.IsVoiceMuted(late));
            Assert.False(s.IsVoiceMuted(Bob));
        }

        [Fact(Timeout = 60000)]
        public void AMuteAllEndsWithTheCall_AndTheNextCallOfTheGroupStartsUnmuted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            StartGroup(e);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            e.Depart(Group, Alice, DepartureReason.ChatLeave);
            Assert.True(e.Get(Group).VoiceModerated, "Bob is still in the call");
            e.Depart(Group, Bob, DepartureReason.ChatLeave);

            NonSpatialVoiceSession ended = e.Get(Group);
            Assert.Equal(0, ended.SeatsHeld);
            Assert.False(ended.VoiceModerated, "the last seat was released");
            Assert.False(ended.IsTextMuted(Bob));
            e.Sweep();
            {
                var assertedValue = e.Get(Group);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "an emptied group call is collected like any other");
            }

            e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            e.Accept(Group, Carol, RegionA);
            Assert.False(e.Get(Group).IsVoiceMuted(Carol), "whoever joins the next call is not muted");

            SessionOutcome conf = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            e.Moderate(conf.Session.SessionId, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Depart(conf.Session.SessionId, Alice, DepartureReason.ChatLeave);
            e.Sweep();
            {
                var assertedValue = e.Get(conf.Session.SessionId);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "an emptied conference is collected as before");
            }
        }

        [Fact(Timeout = 60000)]
        public void AGroupRecordKeptForARemovalIsCollectedWhenItGoesIdle()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialIdentityTests.FakeClock clock = new();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            StartGroup(e);
            e.Moderate(Group, Alice, ModerationOp.Remove, Carol, true, "x", TimeSpan.FromHours(20));
            e.Depart(Group, Alice, DepartureReason.ChatLeave);
            e.Depart(Group, Bob, DepartureReason.ChatLeave);

            clock.Now += TimeSpan.FromHours(7);
            e.Sweep();
            Assert.NotNull(e.Get(Group));

            clock.Now += TimeSpan.FromHours(2);
            e.Sweep();
            Assert.Null(e.Get(Group));
        }

        [Fact(Timeout = 60000)]
        public void ACallBetweenTwoPeopleIsNotModerated_AndAnUnknownSessionIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome p2p = e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);

            Assert.False(e.Moderate(p2p.Session.SessionId, Alice, ModerationOp.VoiceMute, Bob, true).Ok);
            Assert.Equal(SessionOutcome.NoSuchSession, e.Moderate(UUID.Random(), Alice, ModerationOp.VoiceMute, Bob, true).Decision);
        }

        [Fact(Timeout = 60000)]
        public void ARequestNamingNobodyOrNoOneAskingIsRefused_AndAFullListRefusesMore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);

            Assert.False(e.Moderate(Group, Alice, ModerationOp.VoiceMute, UUID.Zero, true).Ok);
            Assert.False(e.Moderate(Group, UUID.Zero, ModerationOp.VoiceMute, Bob, true).Ok);
            for (int i = 0; i < NonSpatialVoiceSession.MaxModerationEntries; i++)
                Assert.True(e.Moderate(Group, Alice, ModerationOp.TextMute, UUID.Random(), true).Ok);
            SessionOutcome over = e.Moderate(Group, Alice, ModerationOp.TextMute, UUID.Random(), true);
            Assert.Equal(SessionOutcome.ModerationRefused, over.Decision);
            Assert.Equal(NonSpatialVoiceSession.MaxModerationEntries, s.TextMuted.Count);
        }

        [Fact(Timeout = 60000)]
        public void TheCopyOfARecordCarriesTheModeration_AndAnOldCopyReadsAsNone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = StartGroup(e);
            UUID carol = UUID.Random();
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, false);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, carol, true);
            e.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            SessionData data = SessionWire.ToData(s);
            NonSpatialVoiceSession back = SessionWire.FromData(SessionReply.FromJson(new SessionReply { Session = data }.ToJson()).Session);

            Assert.True(back.VoiceModerated);
            Assert.Equal(s.ModerationGeneration, back.ModerationGeneration);
            Assert.False(back.IsVoiceMuted(Bob));
            Assert.True(back.IsVoiceMuted(carol));
            Assert.True(back.IsTextMuted(Bob));

            data.VoiceMuted = null;
            data.VoiceAllowed = null;
            data.TextMuted = null;
            data.VoiceModerated = false;
            Assert.False(SessionWire.FromData(data).HasModeration);
        }

        [Fact(Timeout = 60000)]
        public void ThroughTheGridService_TwoRegionsSeeOneModerationRecord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);

            SessionOutcome set = a.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            SessionOutcome mute = a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true);

            Assert.Equal(SessionOutcome.ModerationChanged, set.Decision);
            Assert.Equal(2, mute.Session.ModerationGeneration);
            NonSpatialVoiceSession seen = b.Get(Group, Alice);
            Assert.True(seen.VoiceModerated);
            Assert.True(seen.IsTextMuted(Bob));
            Assert.Equal(SessionOutcome.ModerationUnchanged, a.Moderate(Group, Alice, ModerationOp.TextMute, Bob, true).Decision);
            Assert.Equal(SessionOutcome.NoSuchSession, b.Moderate(UUID.Random(), Alice, ModerationOp.TextMute, Bob, true).Decision);
        }
    }
}
