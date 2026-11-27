/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The console "voice moderation mute" command's Scene-free pieces, mirroring what
 * VoiceModerationConsoleTests covers for the unmute: the store write the command makes and its
 * visibility in "show voice moderation" (Snapshot), and the operator-token handling - argument
 * parsing, target resolution against present avatars, and the parcel choice. The handler itself needs
 * MainConsole, a live Scene and a land channel, the same boundary the unmute tests stop at.
 */

using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class VoiceModerationMuteConsoleTests
    {
        private static List<string> Words(string line) => new List<string>(line.Split(' '));

        // --- the store write (the SAME call the SpatialVoiceModerationRequest "mute" operand makes) ---

        [Fact(Timeout = 60000)]
        public void MuteAgent_IsReflectedInTheSnapshot_AndIsModerated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();

            s.MuteAgent(parcel, agent);

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            {
                var assertedValue = snap;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "\"show voice moderation\" lists the parcel");
            }
            Assert.Equal(parcel, snap[0].ParcelGlobalId);
            Assert.Equal(new[] { agent }, snap[0].MutedAgents);
            Assert.True(s.IsModerated(parcel, agent), "the feeder's rule 2b reads it");
        }

        [Fact(Timeout = 60000)]
        public void MuteAgent_Twice_IsOneEntry_AndOneUnmuteClearsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();

            s.MuteAgent(parcel, agent);
            s.MuteAgent(parcel, agent);

            Assert.Single(s.Snapshot()[0].MutedAgents);
            Assert.True(s.UnmuteAgent(parcel, agent), "the console unmute undoes the console mute");
            Assert.Empty(s.Snapshot());
        }

        // --- argument parsing ---

        [Fact(Timeout = 60000)]
        public void Parse_UuidOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            Assert.True(VoiceModerationTargets.ParseMuteArguments(Words(id.ToString()), out string target, out int? parcel));
            Assert.Equal(id.ToString(), target);
            Assert.Null(parcel);
        }

        [Fact(Timeout = 60000)]
        public void Parse_UuidAndParcel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            Assert.True(VoiceModerationTargets.ParseMuteArguments(Words(id + " 7"), out string target, out int? parcel));
            Assert.Equal(id.ToString(), target);
            Assert.Equal(7, parcel);
        }

        [Fact(Timeout = 60000)]
        public void Parse_UnquotedTwoWordName()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(VoiceModerationTargets.ParseMuteArguments(Words("Test User"), out string target, out int? parcel));
            Assert.Equal("Test User", target);
            Assert.Null(parcel);
        }

        [Fact(Timeout = 60000)]
        public void Parse_NameAndParcel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(VoiceModerationTargets.ParseMuteArguments(Words("Test User 12"), out string target, out int? parcel));
            Assert.Equal("Test User", target);
            Assert.Equal(12, parcel);
        }

        [Fact(Timeout = 60000)]
        public void Parse_ALoneNumber_IsATargetNotAParcel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(VoiceModerationTargets.ParseMuteArguments(Words("7"), out string target, out int? parcel));
            Assert.Equal("7", target);
            Assert.Null(parcel);
        }

        [Fact(Timeout = 60000)]
        public void Parse_ZeroOrNegativeTrailingNumber_IsNotAParcel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceModerationTargets.ParseMuteArguments(Words("Test User 0"), out string target, out int? parcel);
            Assert.Null(parcel);
            Assert.Equal("Test User 0", target);
        }

        [Fact(Timeout = 60000)]
        public void Parse_Nothing_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(VoiceModerationTargets.ParseMuteArguments(new List<string>(), out _, out _));
            Assert.False(VoiceModerationTargets.ParseMuteArguments(Words("   "), out _, out _));
            Assert.False(VoiceModerationTargets.ParseMuteArguments(null, out _, out _));
        }

        // --- target resolution against the region's avatars (a mute aims at someone still present) ---

        [Fact(Timeout = 60000)]
        public void Resolve_NameOfAPresentAvatar()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            var present = new List<VoiceModerationCandidate> { new VoiceModerationCandidate(id, "Test Resident") };

            Assert.Equal(VoiceModerationTargetMatch.Resolved, VoiceModerationTargets.Resolve("test resident", present, out UUID target, out _));
            Assert.Equal(id, target);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_NameOfNobodyPresent_IsNotFound()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var present = new List<VoiceModerationCandidate> { new VoiceModerationCandidate(UUID.Random(), "Test Resident") };
            Assert.Equal(VoiceModerationTargetMatch.NotFound, VoiceModerationTargets.Resolve("Example Resident", present, out _, out _));
        }

        // --- parcel choice ---

        [Fact(Timeout = 60000)]
        public void ChooseParcel_GivenIdWins()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(VoiceModerationParcelChoice.Given, VoiceModerationTargets.ChooseMuteParcel(3, 9, out int id));
            Assert.Equal(3, id);
        }

        [Fact(Timeout = 60000)]
        public void ChooseParcel_NoIdGiven_UsesTheParcelTheTargetStandsOn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(VoiceModerationParcelChoice.TargetPosition, VoiceModerationTargets.ChooseMuteParcel(null, 9, out int id));
            Assert.Equal(9, id);
        }

        [Fact(Timeout = 60000)]
        public void ChooseParcel_NoIdAndTargetAbsent_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(VoiceModerationParcelChoice.NoParcel, VoiceModerationTargets.ChooseMuteParcel(null, null, out _));
        }
    }
}
