/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Room identity is derived independently of session identity, and the collision
 * argument in NonSpatialRoomKey's header is asserted rather than asserted-about.
 *
 * Layer (1) -- structural, against A2A rooms -- is the one that matters most, because A2A
 * admission resolves a room with UUID.TryParse (A2ACalls.TryGetByChannel). The test below
 * runs the ACTUAL live predicate against a room key, so if anyone ever drops the prefix the test
 * fails rather than the grid.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialRoomKeyTests
    {
        private static readonly UUID Session1 = new UUID("11111111-2222-3333-4444-555555555555");
        private static readonly UUID Session2 = new UUID("11111111-2222-3333-4444-555555555556");

        [Test]
        public void RoomKey_IsNotTheSessionId()
        {
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.That(key, Is.Not.EqualTo(Session1.ToString()));
            Assert.That(key, Does.Not.Contain(Session1.ToString()), "the mapping is one-way; the key leaks no session id");
        }

        [Test]
        public void RoomKey_MatchesValuesComputedOutsideTheCode()
        {
            // Expected values: SHA-256 of "urn:opensim:webrtc-voice:nonspatial-room:v1" 0x1F grid 0x1F tag 0x1F session,
            // first 16 bytes written as a UUID, computed with a separate SHA-256 implementation.
            Assert.That(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1),
                        Is.EqualTo("nsv1:adhoc:49297b95-568c-9b3d-08d3-4da0484b01b0"));
            Assert.That(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.P2P, Session1),
                        Is.EqualTo("nsv1:p2p:5b883caf-ad16-a223-2b87-5506d39e8d7b"));
            Assert.That(NonSpatialRoomKey.Derive("example.org", NonSpatialSessionType.Group, Session1),
                        Is.EqualTo("nsv1:group:923cab68-9cac-b91b-a34b-106d39076e22"));
        }

        [Test]
        public void Namespace_IsNeutral()
        {
            Assert.That(NonSpatialRoomKey.Namespace, Does.StartWith("urn:opensim:webrtc-voice:"));
        }

        [Test]
        public void RoomKey_IsDeterministic_SoAnyInstanceDerivesTheSame()
        {
            string a = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            string b = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void RoomKey_DiffersByGrid_ByType_AndBySession()
        {
            string baseline = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.That(NonSpatialRoomKey.Derive("other-grid", NonSpatialSessionType.Adhoc, Session1), Is.Not.EqualTo(baseline));
            Assert.That(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Group, Session1), Is.Not.EqualTo(baseline));
            Assert.That(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session2), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void RoomKey_OfEveryType_IsDistinctForTheSameSessionId()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NonSpatialSessionType t in Enum.GetValues(typeof(NonSpatialSessionType)))
                Assert.That(seen.Add(NonSpatialRoomKey.Derive("example-grid", t, Session1)), Is.True, "type " + t + " aliased another");
        }

        // ---- layer (1): structural separation from the A2A room space --------------------

        [Test]
        public void RoomKey_CanNeverBeMistakenForAnA2AChannel_TheLivePredicateRefusesIt()
        {
            A2ARegistry live = new A2ARegistry();
            live.Record(NonSpatialIdentityTests.Alice, NonSpatialIdentityTests.Bob, out _);

            foreach (NonSpatialSessionType t in Enum.GetValues(typeof(NonSpatialSessionType)))
            {
                string key = NonSpatialRoomKey.Derive("example-grid", t, Session1);
                Assert.That(UUID.TryParse(key, out _), Is.False, "a room key must never parse as a bare UUID: " + key);
                Assert.That(live.TryGetByChannel(key), Is.Null, "the A2A lookup must not resolve a room key");
            }
        }

        [Test]
        public void AnA2AChannel_IsNeverMistakenForARoomKey()
        {
            A2ARegistry live = new A2ARegistry();
            A2ASession s = live.Record(NonSpatialIdentityTests.Alice, NonSpatialIdentityTests.Bob, out _);
            Assert.That(NonSpatialRoomKey.IsRoomKey(s.ChannelUri), Is.False);
        }

        [Test]
        public void IsRoomKey_RecognisesOurOwnAndRejectsEverythingElse()
        {
            Assert.That(NonSpatialRoomKey.IsRoomKey(NonSpatialRoomKey.Derive("g", NonSpatialSessionType.P2P, Session1)), Is.True);
            Assert.That(NonSpatialRoomKey.IsRoomKey(Session1.ToString()), Is.False);
            Assert.That(NonSpatialRoomKey.IsRoomKey(null), Is.False);
            Assert.That(NonSpatialRoomKey.IsRoomKey(string.Empty), Is.False);
        }

        // ---- layer (2): structural separation from SPATIAL rooms ------------------------------

        [Test]
        public void SpatialRoomNumbers_IgnoreChannelId_SoARoomKeyCannotAliasAParcel()
        {
            // JanusAudioBridge.cs -- the "local" arm hashes region+type+parcel and never
            // reads pChannelID. Proving it here means a room key cannot enter that arm at all.
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            int withKey = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "local", 7, key);
            int withNothing = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "local", 7, string.Empty);
            Assert.That(withKey, Is.EqualTo(withNothing));
        }

        [Test]
        public void RoomKey_AndABareSessionId_HashToDifferentMultiagentRooms()
        {
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            int asRoomKey = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "multiagent", 0, key);
            int asSessionId = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "multiagent", 0, Session1.ToString());
            Assert.That(asRoomKey, Is.Not.EqualTo(asSessionId),
                "distinct channel strings must not systematically alias; birthday collisions at the int layer are layer (3) and out of scope");
        }

        [Test]
        public void RoomNumbers_AreAlwaysPositive_ForDerivedKeys()
        {
            // The fold guard (JanusAudioBridge.cs,) must hold for our inputs too.
            for (int i = 0; i < 500; i++)
            {
                string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, UUID.Random());
                Assert.That(JanusAudioBridge.CalcRoomNumber("example-grid", "r", "multiagent", 0, key), Is.GreaterThan(0));
            }
        }

        [Test]
        public void DerivedKeys_DoNotCollideAcrossManySessions()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 20000; i++)
                Assert.That(keys.Add(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, UUID.Random())), Is.True);
        }

        [Test]
        public void ExpectedRoomDescription_MatchesTheBridgesShape()
        {
            // JanusAudioBridge.cs -- regionId + "/" + channelType + "/" + parcel + "/" + channelID.
            UUID region = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.That(NonSpatialRoomKey.ExpectedRoomDescription(region, "multiagent", key),
                        Is.EqualTo(region + "/multiagent/0/" + key));
        }

        [Test]
        public void Tag_RejectsAnUnknownType()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NonSpatialRoomKey.Tag((NonSpatialSessionType)99));
        }
    }
}
