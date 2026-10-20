/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * [WebRtcVoice] VisibilityArmingEnabled defaults to FALSE, so deploying it changes nothing on the wire until
 * an operator turns it on. Pins the key-absent resolution the region module's Initialise uses.
 */
using NUnit.Framework;
using Nini.Config;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityArmingDefaultsTests
    {
        private static IConfig Section()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig cfg = src.AddConfig("WebRtcVoice");
            cfg.Set("Enabled", "true");
            cfg.Set("VisibilityFeederEnabled", "true");
            cfg.Set("VisibilityEmitEnabled", "true");
            return cfg;
        }

        [Test]
        public void Default_IsFalse()
            => Assert.That(WebRtcVoiceRegionModule.DefaultVisibilityArmingEnabled, Is.False);

        [Test]
        public void ResolvesFalse_WhenKeyAbsent_EvenWithFeederAndEmissionOn()
            => Assert.That(WebRtcVoiceRegionModule.ReadVisibilityArmingEnabled(Section()), Is.False);

        [Test]
        public void ExplicitTrue_IsHonored()
        {
            IConfig cfg = Section();
            cfg.Set("VisibilityArmingEnabled", "true");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityArmingEnabled(cfg), Is.True);
        }
    }
}
