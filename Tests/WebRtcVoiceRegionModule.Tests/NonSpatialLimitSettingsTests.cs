/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The [WebRtcVoice] keys that set the non-spatial session ceilings: their defaults, what an operator
 * can change, and what an unusable value falls back to.
 */
using Nini.Config;
using Xunit;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialLimitSettingsTests
    {
        private static IConfig Section(params (string Key, string Value)[] values)
        {
            IniConfigSource source = new IniConfigSource();
            IConfig section = source.AddConfig("WebRtcVoice");
            foreach ((string key, string value) in values)
                section.Set(key, value);
            return section;
        }

        [Fact(Timeout = 60000)]
        public void AnEmptySectionReadsAsTheFiniteDefaults()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialLimits limits = WebRtcVoiceRegionModule.ReadNonSpatialLimits(Section());

            Assert.Equal(5000, limits.MaxNonSpatialSessions);
            Assert.Equal(20, limits.MaxSessionsPerAgent);
            Assert.Equal(5, limits.ConferenceRequestBurst);
            Assert.Equal(10, limits.ConferenceRequestRefillSeconds);
        }

        [Fact(Timeout = 60000)]
        public void TheKeysSetTheCeilings()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialLimits limits = WebRtcVoiceRegionModule.ReadNonSpatialLimits(
                Section(("MaxNonSpatialSessions", "250"), ("MaxSessionsPerAgent", "3"),
                        ("ConferenceRequestBurst", "8"), ("ConferenceRequestRefillSeconds", "2.5")));

            Assert.Equal(250, limits.MaxNonSpatialSessions);
            Assert.Equal(3, limits.MaxSessionsPerAgent);
            Assert.Equal(8, limits.ConferenceRequestBurst);
            Assert.Equal(2.5, limits.ConferenceRequestRefillSeconds);
        }

        [InlineData("0")]
        [InlineData("-4")]
        [Theory(Timeout = 60000)]
        public void AValueBelowOneReadsAsTheDefault(string value)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialLimits limits = WebRtcVoiceRegionModule.ReadNonSpatialLimits(
                Section(("MaxNonSpatialSessions", value), ("MaxSessionsPerAgent", value),
                        ("ConferenceRequestBurst", value), ("ConferenceRequestRefillSeconds", value)));

            Assert.Equal(5000, limits.MaxNonSpatialSessions);
            Assert.Equal(20, limits.MaxSessionsPerAgent);
            Assert.Equal(5, limits.ConferenceRequestBurst);
            Assert.Equal(10, limits.ConferenceRequestRefillSeconds);
        }
    }
}
