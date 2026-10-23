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
    [TestFixture]
    public class VoiceModerationMuteConsoleTests
    {
        private static List<string> Words(string line) => new List<string>(line.Split(' '));

        // --- the store write (the SAME call the SpatialVoiceModerationRequest "mute" operand makes) ---

        [Test]
        public void MuteAgent_IsReflectedInTheSnapshot_AndIsModerated()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();

            s.MuteAgent(parcel, agent);

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.That(snap, Has.Count.EqualTo(1), "\"show voice moderation\" lists the parcel");
            Assert.That(snap[0].ParcelGlobalId, Is.EqualTo(parcel));
            Assert.That(snap[0].MutedAgents, Is.EqualTo(new[] { agent }));
            Assert.That(s.IsModerated(parcel, agent), Is.True, "the feeder's rule 2b reads it");
        }

        [Test]
        public void MuteAgent_Twice_IsOneEntry_AndOneUnmuteClearsIt()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();

            s.MuteAgent(parcel, agent);
            s.MuteAgent(parcel, agent);

            Assert.That(s.Snapshot()[0].MutedAgents, Has.Count.EqualTo(1));
            Assert.That(s.UnmuteAgent(parcel, agent), Is.True, "the console unmute undoes the console mute");
            Assert.That(s.Snapshot(), Is.Empty);
        }

        // --- argument parsing ---

        [Test]
        public void Parse_UuidOnly()
        {
            UUID id = UUID.Random();
            Assert.That(VoiceModerationTargets.ParseMuteArguments(Words(id.ToString()), out string target, out int? parcel), Is.True);
            Assert.That(target, Is.EqualTo(id.ToString()));
            Assert.That(parcel, Is.Null);
        }

        [Test]
        public void Parse_UuidAndParcel()
        {
            UUID id = UUID.Random();
            Assert.That(VoiceModerationTargets.ParseMuteArguments(Words(id + " 7"), out string target, out int? parcel), Is.True);
            Assert.That(target, Is.EqualTo(id.ToString()));
            Assert.That(parcel, Is.EqualTo(7));
        }

        [Test]
        public void Parse_UnquotedTwoWordName()
        {
            Assert.That(VoiceModerationTargets.ParseMuteArguments(Words("Test User"), out string target, out int? parcel), Is.True);
            Assert.That(target, Is.EqualTo("Test User"));
            Assert.That(parcel, Is.Null);
        }

        [Test]
        public void Parse_NameAndParcel()
        {
            Assert.That(VoiceModerationTargets.ParseMuteArguments(Words("Test User 12"), out string target, out int? parcel), Is.True);
            Assert.That(target, Is.EqualTo("Test User"));
            Assert.That(parcel, Is.EqualTo(12));
        }

        [Test]
        public void Parse_ALoneNumber_IsATargetNotAParcel()
        {
            Assert.That(VoiceModerationTargets.ParseMuteArguments(Words("7"), out string target, out int? parcel), Is.True);
            Assert.That(target, Is.EqualTo("7"));
            Assert.That(parcel, Is.Null);
        }

        [Test]
        public void Parse_ZeroOrNegativeTrailingNumber_IsNotAParcel()
        {
            VoiceModerationTargets.ParseMuteArguments(Words("Test User 0"), out string target, out int? parcel);
            Assert.That(parcel, Is.Null);
            Assert.That(target, Is.EqualTo("Test User 0"));
        }

        [Test]
        public void Parse_Nothing_IsRefused()
        {
            Assert.That(VoiceModerationTargets.ParseMuteArguments(new List<string>(), out _, out _), Is.False);
            Assert.That(VoiceModerationTargets.ParseMuteArguments(Words("   "), out _, out _), Is.False);
            Assert.That(VoiceModerationTargets.ParseMuteArguments(null, out _, out _), Is.False);
        }

        // --- target resolution against the region's avatars (a mute aims at someone still present) ---

        [Test]
        public void Resolve_NameOfAPresentAvatar()
        {
            UUID id = UUID.Random();
            var present = new List<VoiceModerationCandidate> { new VoiceModerationCandidate(id, "Test Resident") };

            Assert.That(VoiceModerationTargets.Resolve("test resident", present, out UUID target, out _),
                Is.EqualTo(VoiceModerationTargetMatch.Resolved));
            Assert.That(target, Is.EqualTo(id));
        }

        [Test]
        public void Resolve_NameOfNobodyPresent_IsNotFound()
        {
            var present = new List<VoiceModerationCandidate> { new VoiceModerationCandidate(UUID.Random(), "Test Resident") };
            Assert.That(VoiceModerationTargets.Resolve("Example Resident", present, out _, out _),
                Is.EqualTo(VoiceModerationTargetMatch.NotFound));
        }

        // --- parcel choice ---

        [Test]
        public void ChooseParcel_GivenIdWins()
        {
            Assert.That(VoiceModerationTargets.ChooseMuteParcel(3, 9, out int id), Is.EqualTo(VoiceModerationParcelChoice.Given));
            Assert.That(id, Is.EqualTo(3));
        }

        [Test]
        public void ChooseParcel_NoIdGiven_UsesTheParcelTheTargetStandsOn()
        {
            Assert.That(VoiceModerationTargets.ChooseMuteParcel(null, 9, out int id), Is.EqualTo(VoiceModerationParcelChoice.TargetPosition));
            Assert.That(id, Is.EqualTo(9));
        }

        [Test]
        public void ChooseParcel_NoIdAndTargetAbsent_IsRefused()
        {
            Assert.That(VoiceModerationTargets.ChooseMuteParcel(null, null, out _), Is.EqualTo(VoiceModerationParcelChoice.NoParcel));
        }
    }
}
