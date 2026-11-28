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
using Xunit;
using Nini.Config;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void Defaults_AreFalse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceRegionModule.DefaultVisibilityFeederEnabled);
            Assert.False(WebRtcVoiceRegionModule.DefaultVisibilityEmitEnabled);
        }

        [Fact(Timeout = 60000)]
        public void VisibilityFeederEnabled_ResolvesFalse_WhenKeyAbsent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(SectionWithoutVisibilityKeys()));
        }

        [Fact(Timeout = 60000)]
        public void VisibilityEmitEnabled_ResolvesFalse_WhenKeyAbsent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(SectionWithoutVisibilityKeys()));
        }

        // An operator turns the feeder and emission on explicitly.
        [Fact(Timeout = 60000)]
        public void ExplicitTrue_IsHonored()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = SectionWithoutVisibilityKeys();
            cfg.Set("VisibilityFeederEnabled", "true");
            cfg.Set("VisibilityEmitEnabled", "true");
            Assert.True(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(cfg));
            Assert.True(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(cfg));
        }
    }
}
