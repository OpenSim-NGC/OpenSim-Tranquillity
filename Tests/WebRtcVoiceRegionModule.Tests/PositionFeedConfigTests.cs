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
    [TestFixture]
    public class PositionFeedConfigTests
    {
        private static IConfig Section()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig cfg = src.AddConfig("WebRtcVoice");
            cfg.Set("Enabled", "true");
            return cfg;
        }

        [Test]
        public void TheFeedIsOffByDefault()
        {
            Assert.That(WebRtcVoiceRegionModule.DefaultPositionFeedEnabled, Is.False);
            Assert.That(WebRtcVoiceRegionModule.ReadPositionSettings(Section()), Is.Null);
        }

        [Test]
        public void ExplicitFalse_IsOff()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "false");
            cfg.Set("PositionFeedMode", "observe");
            Assert.That(WebRtcVoiceRegionModule.ReadPositionSettings(cfg), Is.Null, "the other keys do nothing while the switch is off");
        }

        [Test]
        public void SwitchedOn_UsesObserveModeAndTheDocumentedDefaults()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);

            Assert.That(s, Is.Not.Null);
            Assert.That(s.Mode, Is.EqualTo(PositionFeedMode.Observe));
            Assert.That(s.TickMs, Is.EqualTo(500));
            Assert.That(s.KeepAliveMs, Is.EqualTo(2000));
            Assert.That(s.MoveThresholdCm, Is.EqualTo(100));
            Assert.That(s.MarginCm, Is.EqualTo(500));
            Assert.That(s.SpeedFloorCmS, Is.EqualTo(800));
            Assert.That(s.FallbackSpeedMaxCmS, Is.EqualTo(10000));
        }

        [Test]
        public void EveryKeyIsRead()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedTickMs", "400");
            cfg.Set("PositionFeedKeepAliveMs", "2200");
            cfg.Set("PositionFeedMoveThresholdCm", "250");
            cfg.Set("PositionFeedMarginCm", "700");
            cfg.Set("PositionFeedSpeedFloorCmS", "900");
            cfg.Set("PositionFeedSpeedMaxCmS", "20000");

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);

            Assert.That(s.TickMs, Is.EqualTo(400));
            Assert.That(s.KeepAliveMs, Is.EqualTo(2200));
            Assert.That(s.MoveThresholdCm, Is.EqualTo(250));
            Assert.That(s.MarginCm, Is.EqualTo(700));
            Assert.That(s.SpeedFloorCmS, Is.EqualTo(900));
            Assert.That(s.FallbackSpeedMaxCmS, Is.EqualTo(20000));
        }

        [Test]
        public void AKeepAliveShorterThanTheTick_IsRaisedToTheTick()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedTickMs", "800");
            cfg.Set("PositionFeedKeepAliveMs", "200");
            Assert.That(WebRtcVoiceRegionModule.ReadPositionSettings(cfg).KeepAliveMs, Is.EqualTo(800));
        }

        [Test]
        public void EnforceIsAccepted_AndRecordedAsTheConfiguredMode()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedMode", "enforce");
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.That(s.Mode, Is.EqualTo(PositionFeedMode.Enforce));
            Assert.That(warnings, Is.Empty, "enforce is a valid word, the module says separately that it is not active yet");
            Assert.That(PositionFeedModes.Active(s.Mode), Is.EqualTo(PositionFeedMode.Observe));
        }

        [Test]
        public void AnUnknownModeWord_FallsBackToObserveAndSaysSo()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedMode", "block");
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.That(s.Mode, Is.EqualTo(PositionFeedMode.Observe));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("PositionFeedMode"));
        }

        /// <summary>The mixer treats a point as stale 6000 ms after it received it (SLV_POS_STALE_MS in the mixer's
        /// possync.h). A full snapshot is sent at the first tick at or after the keep-alive, so the gap between two is at
        /// most the keep-alive plus one tick and its 250 ms jitter. One lost request must leave the point fresh.</summary>
        private const int MixerStaleMs = 6000;
        private const int TickJitterMs = 250;

        [Test]
        public void TheDefaultKeepAlive_LeavesAPointFreshAfterOneLostRequest()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg);

            Assert.That(s.KeepAliveMs, Is.EqualTo(2000));
            Assert.That((s.KeepAliveMs + s.TickMs + TickJitterMs) * 2, Is.LessThan(MixerStaleMs));
        }

        [Test]
        public void TheLargestAcceptedKeepAlive_IsStillInsideTheMixersStaleWindow()
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedKeepAliveMs", "2250");
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.That(s.KeepAliveMs, Is.EqualTo(2250));
            Assert.That(warnings, Is.Empty);
            Assert.That((s.KeepAliveMs + s.TickMs + TickJitterMs) * 2, Is.LessThanOrEqualTo(MixerStaleMs + 2 * TickJitterMs));
        }

        [TestCase("2251")]
        [TestCase("10000")]
        [TestCase("0")]
        [TestCase("-5")]
        public void AKeepAliveOutsideTheRange_WarnsAndUsesTheDefault(string value)
        {
            IConfig cfg = Section();
            cfg.Set("PositionFeedEnabled", "true");
            cfg.Set("PositionFeedKeepAliveMs", value);
            var warnings = new List<string>();

            PositionSettings s = WebRtcVoiceRegionModule.ReadPositionSettings(cfg, warnings.Add);

            Assert.That(s.KeepAliveMs, Is.EqualTo(2000));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("PositionFeedKeepAliveMs").And.Contain(value).And.Contain("2250").And.Contain("2000"));
        }
    }

    [TestFixture]
    public class PositionCoordinateTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        // The constant core adds to a child agent's position for each region of distance between the
        // region it came from and the one holding the child (ScenePresence.UpdateChildAgent). The test
        // assumes regions are 256 m wide; larger regions are not covered.
        private const int RegionWidth = 256;

        [Test]
        public void ARootAgent_IsConvertedToGlobalCentimetres()
        {
            // A region at grid (1000, 1000) starts at 256000 m; an avatar at (128, 128, 22) inside it.
            PositionView v = FeederWorldFromScene.ToPositionView(Agent, false, 1000u * RegionWidth, 1000u * RegionWidth,
                new Vector3(128f, 128f, 22f), Vector3.Zero, null);

            Assert.That((v.XCm, v.YCm, v.ZCm), Is.EqualTo((25612800, 25612800, 2200)));
            Assert.That(v.IsChild, Is.False);
            Assert.That(v.Id, Is.EqualTo(Agent));
        }

        [Test]
        public void AChildAgentInTheEasternNeighbour_LandsOnTheSamePointAsItsRootInTheWesternRegion()
        {
            uint western = 1000u * RegionWidth;
            uint eastern = western + RegionWidth;

            // The root avatar stands at x = 216 in the western region. The eastern region holds its child at
            // 216 - 256 = -40, the offset core applies when it receives the child's position.
            PositionView root = FeederWorldFromScene.ToPositionView(Agent, false, western, western, new Vector3(216f, 100f, 30f), Vector3.Zero, null);
            PositionView child = FeederWorldFromScene.ToPositionView(Agent, true, eastern, western, new Vector3(-40f, 100f, 30f), Vector3.Zero, null);

            Assert.That((child.XCm, child.YCm, child.ZCm), Is.EqualTo((root.XCm, root.YCm, root.ZCm)));
            Assert.That(child.IsChild, Is.True);
        }

        [Test]
        public void AChildAgentInTheWesternNeighbour_LandsOnTheSamePointAsItsRootInTheEasternRegion()
        {
            uint western = 1000u * RegionWidth;
            uint eastern = western + RegionWidth;

            PositionView root = FeederWorldFromScene.ToPositionView(Agent, false, eastern, western, new Vector3(30f, 100f, 30f), Vector3.Zero, null);
            PositionView child = FeederWorldFromScene.ToPositionView(Agent, true, western, western, new Vector3(286f, 100f, 30f), Vector3.Zero, null);

            Assert.That(child.XCm, Is.EqualTo(root.XCm));
            Assert.That(child.XCm, Is.EqualTo((int)((eastern + 30u) * 100u)));
        }

        [Test]
        public void TheSpeed_IsTheAvatarsOwnOrThatOfWhatItSitsOn()
        {
            PositionView walking = FeederWorldFromScene.ToPositionView(Agent, false, 0, 0, Vector3.Zero, new Vector3(3f, 4f, 0f), null);
            PositionView riding = FeederWorldFromScene.ToPositionView(Agent, false, 0, 0, Vector3.Zero, Vector3.Zero, new Vector3(0f, 0f, 25f));

            Assert.That(walking.SpeedCmS, Is.EqualTo(500).Within(0.5));
            Assert.That(riding.SpeedCmS, Is.EqualTo(2500).Within(0.5));
        }

        /// <summary>A 256 m region at global metres (256000, 256000): a point at its middle is the global centimetres a viewer
        /// standing there sends as its sp (Firestorm llvoicewebrtc.cpp:1108, :1241-1244).</summary>
        [Test]
        public void GlobalCentimetres_A256Region()
        {
            (int x, int y, int z) = FeederWorldFromScene.GlobalCentimetres(256000, 256000, new Vector3(128, 128, 22));
            Assert.That((x, y, z), Is.EqualTo((25612800, 25612800, 2200)));
        }

        /// <summary>A var region (1024 x 1024) at grid (1004, 1008): the origin is still its south-west corner, and a position
        /// runs past 256.</summary>
        [Test]
        public void GlobalCentimetres_AVarRegion_PositionPast256()
        {
            (int x, int y, int z) = FeederWorldFromScene.GlobalCentimetres(1004 * 256, 1008 * 256, new Vector3(574, 719, 25));
            Assert.That((x, y, z), Is.EqualTo(((1004 * 256 + 574) * 100, (1008 * 256 + 719) * 100, 2500)));
        }
    }
}
