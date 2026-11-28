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
using Xunit;
using Nini.Config;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void Default_IsFalse()

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.False(WebRtcVoiceRegionModule.DefaultVisibilityArmingEnabled);
}
        [Fact(Timeout = 60000)]
        public void ResolvesFalse_WhenKeyAbsent_EvenWithFeederAndEmissionOn()

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.False(WebRtcVoiceRegionModule.ReadVisibilityArmingEnabled(Section()));
}
        [Fact(Timeout = 60000)]
        public void ExplicitTrue_IsHonored()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = Section();
            cfg.Set("VisibilityArmingEnabled", "true");
            Assert.True(WebRtcVoiceRegionModule.ReadVisibilityArmingEnabled(cfg));
        }
    }
}
