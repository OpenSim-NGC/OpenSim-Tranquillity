/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * [WebRtcVoice] PositionFeedEnabled and its companions. The feed is off unless asked for: with the switch
 * absent or false the reader returns no settings at all, so nothing is built, computed or sent. Mode
 * "enforce" is accepted but nothing acts on it yet.
 *
 * Also the conversion from a presence's position to the global centimetres the viewer reports its own
 * position in, including a child agent in a neighbouring region.
 */

using Nini.Config;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class PositionFeedConfigTests
    {
        private static IConfig Section()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig cfg = src.AddConfig("WebRtcVoice");
            cfg.Set("Enabled", "true");
            return cfg;
        }

        [Fact(Timeout = 60000)]
        public void TheFeedIsOffByDefault()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceRegionModule.DefaultPositionFeedEnabled);
            Assert.Null(WebRtcVoiceRegionModule.ReadPositionSettings(Section()));
        }

        [Fact(Timeout = 60000)]
        public void ExplicitFalse_IsOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "false");
            cfg.Set("PositionFeedMode", "observe");
            {
                var assertedValue = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the other keys do nothing while the switch is off");
            }
        }

        [Fact(Timeout = 60000)]
        public void SwitchedOn_UsesObserveModeAndTheDocumentedDefaults()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);

            Assert.NotNull(s);
            Assert.Equal(PositionFeedMode.Observe, s.Mode);
            Assert.Equal(500, s.TickMs);
            Assert.Equal(2000, s.KeepAliveMs);
            Assert.Equal(100, s.MoveThresholdCm);
            Assert.Equal(500, s.MarginCm);
            Assert.Equal(800, s.SpeedFloorCmS);
            Assert.Equal(10000, s.FallbackSpeedMaxCmS);
        }

        [Fact(Timeout = 60000)]
        public void EveryKeyIsRead()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedTickMs", "400");
            cfg.Set("PositionFeedKeepAliveMs", "2200");
            cfg.Set("PositionFeedMoveThresholdCm", "250");
            cfg.Set("PositionFeedMarginCm", "700");
            cfg.Set("PositionFeedSpeedFloorCmS", "900");
            cfg.Set("PositionFeedSpeedMaxCmS", "20000");

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);

            Assert.Equal(400, s.TickMs);
            Assert.Equal(2200, s.KeepAliveMs);
            Assert.Equal(250, s.MoveThresholdCm);
            Assert.Equal(700, s.MarginCm);
            Assert.Equal(900, s.SpeedFloorCmS);
            Assert.Equal(20000, s.FallbackSpeedMaxCmS);
        }

        [Fact(Timeout = 60000)]
        public void AKeepAliveShorterThanTheTick_IsRaisedToTheTick()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedTickMs", "800");
            cfg.Set("PositionFeedKeepAliveMs", "200");
            Assert.Equal(800, WebRtcVoiceRegionModule.ReadPositionSettings(cfg).KeepAliveMs);
        }

        [Fact(Timeout = 60000)]
        public void EnforceIsAccepted_AndRecordedAsTheConfiguredMode()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedMode", "enforce");
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.Equal(PositionFeedMode.Enforce, s.Mode);
            {
                var assertedValue = warnings;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "enforce is a valid word, the module says separately that it is not active yet");
            }
            Assert.Equal(PositionFeedMode.Observe, PositionFeedModes.Active(s.Mode));
        }

        [Fact(Timeout = 60000)]
        public void AnUnknownModeWord_FallsBackToObserveAndSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedMode", "block");
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.Equal(PositionFeedMode.Observe, s.Mode);
            Assert.Single(warnings);
            Assert.Contains("PositionFeedMode", warnings[0]);
        }

        /// <summary>The mixer treats a point as stale 6000 ms after it received it (SLV_POS_STALE_MS in the mixer's
        /// possync.h). A full snapshot is sent at the first tick at or after the keep-alive, so the gap between two is at
        /// most the keep-alive plus one tick and its 250 ms jitter. One lost request must leave the point fresh.</summary>
        private const int MixerStaleMs = 6000;
        private const int TickJitterMs = 250;

        [Fact(Timeout = 60000)]
        public void TheDefaultKeepAlive_LeavesAPointFreshAfterOneLostRequest()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);

            Assert.Equal(2000, s.KeepAliveMs);
            Assert.True((s.KeepAliveMs + s.TickMs + TickJitterMs) * 2 < MixerStaleMs);
        }

        [Fact(Timeout = 60000)]
        public void TheLargestAcceptedKeepAlive_IsStillInsideTheMixersStaleWindow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedKeepAliveMs", "2250");
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.Equal(2250, s.KeepAliveMs);
            Assert.Empty(warnings);
            Assert.True((s.KeepAliveMs + s.TickMs + TickJitterMs) * 2 <= MixerStaleMs + 2 * TickJitterMs);
        }

        [InlineData("2251")]
        [InlineData("10000")]
        [InlineData("0")]
        [InlineData("-5")]
        [Theory(Timeout = 60000)]
        public void AKeepAliveOutsideTheRange_WarnsAndUsesTheDefault(string value)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedKeepAliveMs", value);
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.Equal(2000, s.KeepAliveMs);
            Assert.Single(warnings);
            {
                var assertedValue = warnings[0];
                Assert.Contains("PositionFeedKeepAliveMs", assertedValue);
                Assert.Contains(value, assertedValue);
                Assert.Contains("2250", assertedValue);
                Assert.Contains("2000", assertedValue);
            }
        }
    }

    public class PositionCoordinateTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        // The constant core adds to a child agent's position for each region of distance between the
        // region it came from and the one holding the child (ScenePresence.UpdateChildAgent). The test
        // assumes regions are 256 m wide; larger regions are not covered.
        private const int RegionWidth = 256;

        [Fact(Timeout = 60000)]
        public void ARootAgent_IsConvertedToGlobalCentimetres()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A region at grid (1000, 1000) starts at 256000 m; an avatar at (128, 128, 22) inside it.
            PositionView v = FeederWorldFromScene.ToPositionView(Agent, false, 1000u * RegionWidth, 1000u * RegionWidth,
                new Vector3(128f, 128f, 22f), Vector3.Zero, null);

            Assert.Equal((25612800, 25612800, 2200), (v.XCm, v.YCm, v.ZCm));
            Assert.False(v.IsChild);
            Assert.Equal(Agent, v.Id);
        }

        [Fact(Timeout = 60000)]
        public void AChildAgentInTheEasternNeighbour_LandsOnTheSamePointAsItsRootInTheWesternRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            uint western = 1000u * RegionWidth;
            uint eastern = western + RegionWidth;

            // The root avatar stands at x = 216 in the western region. The eastern region holds its child at
            // 216 - 256 = -40, the offset core applies when it receives the child's position.
            PositionView root = FeederWorldFromScene.ToPositionView(Agent, false, western, western, new Vector3(216f, 100f, 30f), Vector3.Zero, null);
            PositionView child = FeederWorldFromScene.ToPositionView(Agent, true, eastern, western, new Vector3(-40f, 100f, 30f), Vector3.Zero, null);

            Assert.Equal((root.XCm, root.YCm, root.ZCm), (child.XCm, child.YCm, child.ZCm));
            Assert.True(child.IsChild);
        }

        [Fact(Timeout = 60000)]
        public void AChildAgentInTheWesternNeighbour_LandsOnTheSamePointAsItsRootInTheEasternRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            uint western = 1000u * RegionWidth;
            uint eastern = western + RegionWidth;

            PositionView root = FeederWorldFromScene.ToPositionView(Agent, false, eastern, western, new Vector3(30f, 100f, 30f), Vector3.Zero, null);
            PositionView child = FeederWorldFromScene.ToPositionView(Agent, true, western, western, new Vector3(286f, 100f, 30f), Vector3.Zero, null);

            Assert.Equal(root.XCm, child.XCm);
            Assert.Equal((int)((eastern + 30u) * 100u), child.XCm);
        }

        [Fact(Timeout = 60000)]
        public void TheSpeed_IsTheAvatarsOwnOrThatOfWhatItSitsOn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PositionView walking = FeederWorldFromScene.ToPositionView(Agent, false, 0, 0, Vector3.Zero, new Vector3(3f, 4f, 0f), null);
            PositionView riding = FeederWorldFromScene.ToPositionView(Agent, false, 0, 0, Vector3.Zero, Vector3.Zero, new Vector3(0f, 0f, 25f));

            Assert.InRange(walking.SpeedCmS, (500) - (0.5), (500) + (0.5));
            Assert.InRange(riding.SpeedCmS, (2500) - (0.5), (2500) + (0.5));
        }

        /// <summary>A 256 m region at global metres (256000, 256000): a point at its middle is the global centimetres a viewer
        /// standing there sends as its sp (Firestorm llvoicewebrtc.cpp:1108, :1241-1244).</summary>
        [Fact(Timeout = 60000)]
        public void GlobalCentimetres_A256Region()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (int x, int y, int z) = FeederWorldFromScene.GlobalCentimetres(256000, 256000, new Vector3(128, 128, 22));
            Assert.Equal((25612800, 25612800, 2200), (x, y, z));
        }

        /// <summary>A var region (1024 x 1024) at grid (1004, 1008): the origin is still its south-west corner, and a position
        /// runs past 256.</summary>
        [Fact(Timeout = 60000)]
        public void GlobalCentimetres_AVarRegion_PositionPast256()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (int x, int y, int z) = FeederWorldFromScene.GlobalCentimetres(1004 * 256, 1008 * 256, new Vector3(574, 719, 25));
            Assert.Equal(((1004 * 256 + 574) * 100, (1008 * 256 + 719) * 100, 2500), (x, y, z));
        }
    }
}
