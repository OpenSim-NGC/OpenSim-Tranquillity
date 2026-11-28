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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialRoomKeyTests
    {
        private static readonly UUID Session1 = new UUID("11111111-2222-3333-4444-555555555555");
        private static readonly UUID Session2 = new UUID("11111111-2222-3333-4444-555555555556");

        [Fact(Timeout = 60000)]
        public void RoomKey_IsNotTheSessionId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.NotEqual(Session1.ToString(), key);
            {
                var assertedValue = key;
                TestAssert.WithMessage(() => Assert.DoesNotContain(Session1.ToString(), assertedValue), "the mapping is one-way; the key leaks no session id");
            }
        }

        [Fact(Timeout = 60000)]
        public void RoomKey_MatchesValuesComputedOutsideTheCode()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Expected values: SHA-256 of "urn:opensim:webrtc-voice:nonspatial-room:v1" 0x1F grid 0x1F tag 0x1F session,
            // first 16 bytes written as a UUID, computed with a separate SHA-256 implementation.
            Assert.Equal("nsv1:adhoc:49297b95-568c-9b3d-08d3-4da0484b01b0", NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1));
            Assert.Equal("nsv1:p2p:5b883caf-ad16-a223-2b87-5506d39e8d7b", NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.P2P, Session1));
            Assert.Equal("nsv1:group:923cab68-9cac-b91b-a34b-106d39076e22", NonSpatialRoomKey.Derive("example.org", NonSpatialSessionType.Group, Session1));
        }

        [Fact(Timeout = 60000)]
        public void Namespace_IsNeutral()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.StartsWith("urn:opensim:webrtc-voice:", NonSpatialRoomKey.Namespace);
        }

        [Fact(Timeout = 60000)]
        public void RoomKey_IsDeterministic_SoAnyInstanceDerivesTheSame()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string a = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            string b = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.Equal(b, a);
        }

        [Fact(Timeout = 60000)]
        public void RoomKey_DiffersByGrid_ByType_AndBySession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string baseline = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.NotEqual(baseline, NonSpatialRoomKey.Derive("other-grid", NonSpatialSessionType.Adhoc, Session1));
            Assert.NotEqual(baseline, NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Group, Session1));
            Assert.NotEqual(baseline, NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session2));
        }

        [Fact(Timeout = 60000)]
        public void RoomKey_OfEveryType_IsDistinctForTheSameSessionId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NonSpatialSessionType t in Enum.GetValues(typeof(NonSpatialSessionType)))
                Assert.True(seen.Add(NonSpatialRoomKey.Derive("example-grid", t, Session1)), "type " + t + " aliased another");
        }

        // ---- layer (1): structural separation from the A2A room space --------------------

        [Fact(Timeout = 60000)]
        public void RoomKey_CanNeverBeMistakenForAnA2AChannel_TheLivePredicateRefusesIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry live = new A2ARegistry();
            live.Record(NonSpatialIdentityTests.Alice, NonSpatialIdentityTests.Bob, out _);

            foreach (NonSpatialSessionType t in Enum.GetValues(typeof(NonSpatialSessionType)))
            {
                string key = NonSpatialRoomKey.Derive("example-grid", t, Session1);
                Assert.False(UUID.TryParse(key, out _), "a room key must never parse as a bare UUID: " + key);
                {
                    var assertedValue = live.TryGetByChannel(key);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "the A2A lookup must not resolve a room key");
                }
            }
        }

        [Fact(Timeout = 60000)]
        public void AnA2AChannel_IsNeverMistakenForARoomKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry live = new A2ARegistry();
            A2ASession s = live.Record(NonSpatialIdentityTests.Alice, NonSpatialIdentityTests.Bob, out _);
            Assert.False(NonSpatialRoomKey.IsRoomKey(s.ChannelUri));
        }

        [Fact(Timeout = 60000)]
        public void IsRoomKey_RecognisesOurOwnAndRejectsEverythingElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(NonSpatialRoomKey.IsRoomKey(NonSpatialRoomKey.Derive("g", NonSpatialSessionType.P2P, Session1)));
            Assert.False(NonSpatialRoomKey.IsRoomKey(Session1.ToString()));
            Assert.False(NonSpatialRoomKey.IsRoomKey(null));
            Assert.False(NonSpatialRoomKey.IsRoomKey(string.Empty));
        }

        // ---- layer (2): structural separation from SPATIAL rooms ------------------------------

        [Fact(Timeout = 60000)]
        public void SpatialRoomNumbers_IgnoreChannelId_SoARoomKeyCannotAliasAParcel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // JanusAudioBridge.cs -- the "local" arm hashes region+type+parcel and never
            // reads pChannelID. Proving it here means a room key cannot enter that arm at all.
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            int withKey = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "local", 7, key);
            int withNothing = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "local", 7, string.Empty);
            Assert.Equal(withNothing, withKey);
        }

        [Fact(Timeout = 60000)]
        public void RoomKey_AndABareSessionId_HashToDifferentMultiagentRooms()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            int asRoomKey = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "multiagent", 0, key);
            int asSessionId = JanusAudioBridge.CalcRoomNumber("example-grid", "region-1", "multiagent", 0, Session1.ToString());
            {
                var assertedValue = asRoomKey;
                TestAssert.WithMessage(() => Assert.NotEqual(asSessionId, assertedValue), "distinct channel strings must not systematically alias; birthday collisions at the int layer are layer (3) and out of scope");
            }
        }

        [Fact(Timeout = 60000)]
        public void RoomNumbers_AreAlwaysPositive_ForDerivedKeys()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The fold guard (JanusAudioBridge.cs,) must hold for our inputs too.
            for (int i = 0; i < 500; i++)
            {
                string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, UUID.Random());
                Assert.True(JanusAudioBridge.CalcRoomNumber("example-grid", "r", "multiagent", 0, key) > 0);
            }
        }

        [Fact(Timeout = 60000)]
        public void DerivedKeys_DoNotCollideAcrossManySessions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 20000; i++)
                Assert.True(keys.Add(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, UUID.Random())));
        }

        [Fact(Timeout = 60000)]
        public void ExpectedRoomDescription_MatchesTheBridgesShape()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // JanusAudioBridge.cs -- regionId + "/" + channelType + "/" + parcel + "/" + channelID.
            UUID region = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
            string key = NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, Session1);
            Assert.Equal(region + "/multiagent/0/" + key, NonSpatialRoomKey.ExpectedRoomDescription(region, "multiagent", key));
        }

        [Fact(Timeout = 60000)]
        public void Tag_RejectsAnUnknownType()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Throws<ArgumentOutOfRangeException>(() => NonSpatialRoomKey.Tag((NonSpatialSessionType)99));
        }
    }
}
