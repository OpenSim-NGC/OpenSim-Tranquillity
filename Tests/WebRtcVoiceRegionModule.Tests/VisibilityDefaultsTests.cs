/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * [WebRtcVoice] VisibilityFeederEnabled and VisibilityEmitEnabled default to true, so
 * a fresh deployment pushes mix-enforced permissions to the mixer. An install can still set
 * either key to false to push nothing. These tests pin the key-absent
 * resolution the region module's Initialise uses (WebRtcVoiceRegionModule.cs, the two Read*
 * calls), independent of any ini file.
 */
using NUnit.Framework;
using Nini.Config;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityDefaultsTests
    {
        // A [WebRtcVoice] section as a minimal install writes it: enabled, no visibility keys.
        private static IConfig SectionWithoutVisibilityKeys()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig cfg = src.AddConfig("WebRtcVoice");
            cfg.Set("Enabled", "true");
            return cfg;
        }

        [Test]
        public void Defaults_AreFalse()
        {
            Assert.That(WebRtcVoiceRegionModule.DefaultVisibilityFeederEnabled, Is.False);
            Assert.That(WebRtcVoiceRegionModule.DefaultVisibilityEmitEnabled, Is.False);
        }

        [Test]
        public void VisibilityFeederEnabled_ResolvesFalse_WhenKeyAbsent()
        {
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(SectionWithoutVisibilityKeys()), Is.False);
        }

        [Test]
        public void VisibilityEmitEnabled_ResolvesFalse_WhenKeyAbsent()
        {
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(SectionWithoutVisibilityKeys()), Is.False);
        }

        // An operator turns the feeder and emission on explicitly.
        [Test]
        public void ExplicitTrue_IsHonored()
        {
            IConfig cfg = SectionWithoutVisibilityKeys();
            cfg.Set("VisibilityFeederEnabled", "true");
            cfg.Set("VisibilityEmitEnabled", "true");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(cfg), Is.True);
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(cfg), Is.True);
        }
    }
}
