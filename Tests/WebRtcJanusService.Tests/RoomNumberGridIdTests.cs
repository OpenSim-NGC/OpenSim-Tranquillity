/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The grid id enters the multiagent room derivation. Without it,
 * CalcRoomNumber("multiagent") would hash only channel_id + channel_type, so two grids sharing one Janus mixer would land the same A2A session
 * id in the same room. The "local" arm is untouched: RoomNumberFoldTests pins its numbers to the
 * pre-existing derivation, and LocalArm_IgnoresTheGridId below shows the grid id never enters it.
 *
 * The grid id is the region's GatekeeperURI, read by JanusAudioBridge.ReadGridId through the same
 * section chain GridInfo uses (Scene.SceneGridInfo), normalised so the common spellings of one
 * grid's URI agree.
 */
using Nini.Config;
using Xunit;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class RoomNumberGridIdTests
    {
        private const string GridA = "http://grid-a.example:8002";
        private const string GridB = "http://grid-b.example:8002";
        private const string Region = "11111111-1111-1111-1111-111111111111";
        // An A2A channel is the viewer's XOR session id: symmetric, so caller and callee send the same string.
        private const string Channel = "33333333-3333-3333-3333-333333333333";

        private static int Multiagent(string gridId, string channel)
            => JanusAudioBridge.CalcRoomNumber(gridId, Region, "multiagent", JanusAudioBridge.REGION_ROOM_ID, channel);

        // ---- the derivation --------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void SameChannel_DifferentGrid_DifferentRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int a = Multiagent(GridA, Channel);
            int b = Multiagent(GridB, Channel);
            {
                var assertedValue = a;
                TestAssert.WithMessage(() => Assert.NotEqual(b, assertedValue), "two grids on a shared mixer must not share an A2A room");
            }
            Assert.True(a >= 0);
            Assert.True(b >= 0);
        }

        [Fact(Timeout = 60000)]
        public void SameGrid_SameChannel_BothDirections_SameRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The caller's and the callee's provisions carry the identical channel string; both
            // must derive the identical room or the two parties never meet.
            int caller = Multiagent(GridA, Channel);
            int callee = Multiagent(GridA, Channel);
            Assert.Equal(caller, callee);
        }

        [Fact(Timeout = 60000)]
        public void SameGrid_DifferentChannel_DifferentRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.NotEqual(Multiagent(GridA, "44444444-4444-4444-4444-444444444444"), Multiagent(GridA, Channel));
        }

        [Fact(Timeout = 60000)]
        public void Multiagent_IsIndependentOfRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // An A2A room is grid-wide, never per region: the two parties may sit in different regions.
            int r1 = JanusAudioBridge.CalcRoomNumber(GridA, Region, "multiagent", 0, Channel);
            int r2 = JanusAudioBridge.CalcRoomNumber(GridA, "22222222-2222-2222-2222-222222222222", "multiagent", 42, Channel);
            Assert.Equal(r1, r2);
        }

        [Fact(Timeout = 60000)]
        public void Multiagent_WithGridId_DiffersFromTheOldGridlessDerivation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The gridless number (channel + type only) is what an empty grid id still yields; a
            // configured grid moves every A2A room off it. Spatial rooms are unaffected (below).
            var hasher = new BHasherMdjb2();
            hasher.Add(Channel);
            hasher.Add("multiagent");
            int oldRoom = JanusAudioBridge.FoldHashToRoom(hasher.Finish().GetHashCode());
            {
                var assertedValue = Multiagent(string.Empty, Channel);
                TestAssert.WithMessage(() => Assert.Equal(oldRoom, assertedValue), "empty grid id: the old derivation, so a gridless standalone keeps working");
            }
            Assert.NotEqual(oldRoom, Multiagent(GridA, Channel));
        }

        // ---- the local arm is untouched ----------------------------------------------------------

        [InlineData(0)]
        [InlineData(7)]
        [InlineData(JanusAudioBridge.REGION_ROOM_ID)]
        [Theory(Timeout = 60000)]
        public void LocalArm_IgnoresTheGridId(int parcel)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int none = JanusAudioBridge.CalcRoomNumber(string.Empty, Region, "local", parcel, string.Empty);
            int a = JanusAudioBridge.CalcRoomNumber(GridA, Region, "local", parcel, string.Empty);
            int b = JanusAudioBridge.CalcRoomNumber(GridB, Region, "local", parcel, string.Empty);
            {
                var assertedValue = a;
                TestAssert.WithMessage(() => Assert.Equal(none, assertedValue), "a spatial room number must not move when the grid id is configured");
            }
            Assert.Equal(none, b);
        }

        [Fact(Timeout = 60000)]
        public void UnknownChannelType_StillThrows()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = () => JanusAudioBridge.CalcRoomNumber(GridA, Region, "group", 0, Channel);
                Assert.ThrowsAny<Exception>(() =>
                {
                    assertedValue();
                });
            }
        }

        // ---- ReadGridId ----------------------------------------------------------------------------

        private static IConfigSource Config(string section, string key, string value)
        {
            var src = new IniConfigSource();
            src.AddConfig(section).Set(key, value);
            return src;
        }

        [Fact(Timeout = 60000)]
        public void ReadGridId_ReadsHypergridGatekeeperURI()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("http://grid.example.org:8002", JanusAudioBridge.ReadGridId(Config("Hypergrid", "GatekeeperURI", "http://grid.example.org:8002")));
        }

        [InlineData("http://Grid.Example:8002/","http://grid.example:8002")]
        [InlineData("  http://grid.example:8002  ","http://grid.example:8002")]
        [InlineData("HTTP://GRID.EXAMPLE:8002///","http://grid.example:8002")]
        [InlineData("http://grid.example:8002","http://grid.example:8002")]
        [Theory(Timeout = 60000)]
        public void ReadGridId_NormalisesTheCommonSpellings(string configured, string expected)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Every region of one grid must derive the same id even if their ini files spell the
            // URI differently (case, whitespace, trailing slash) -- the value is a hash input.
            Assert.Equal(expected, JanusAudioBridge.ReadGridId(Config("Hypergrid", "GatekeeperURI", configured)));
        }

        [Fact(Timeout = 60000)]
        public void ReadGridId_HonoursTheGridInfoSectionChain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("http://c.example:8002", JanusAudioBridge.ReadGridId(Config("Const", "GatekeeperURI", "http://c.example:8002")));
            Assert.Equal("http://s.example:8002", JanusAudioBridge.ReadGridId(Config("Startup", "GatekeeperURI", "http://s.example:8002")));
            Assert.Equal("http://gs.example:8002", JanusAudioBridge.ReadGridId(Config("GatekeeperService", "ExternalName", "http://gs.example:8002")));
            Assert.Equal("http://g.example:8002", JanusAudioBridge.ReadGridId(Config("GridService", "Gatekeeper", "http://g.example:8002")));
        }

        [Fact(Timeout = 60000)]
        public void ReadGridId_EmptyWhenUnconfigured()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Empty(JanusAudioBridge.ReadGridId(new IniConfigSource()));
            {
                var assertedValue = JanusAudioBridge.ReadGridId(Config("Hypergrid", "HomeURI", "http://h.example:8002"));
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "HomeURI is the account home, not the grid identity");
            }
            Assert.Empty(JanusAudioBridge.ReadGridId(null));
        }
    }
}
