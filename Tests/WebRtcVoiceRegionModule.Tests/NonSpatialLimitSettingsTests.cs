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
using NUnit.Framework;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void AnEmptySectionReadsAsTheFiniteDefaults()
        {
            NonSpatialLimits limits = WebRtcVoiceRegionModule.ReadNonSpatialLimits(Section());

            Assert.That(limits.MaxNonSpatialSessions, Is.EqualTo(5000));
            Assert.That(limits.MaxSessionsPerAgent, Is.EqualTo(20));
            Assert.That(limits.ConferenceRequestBurst, Is.EqualTo(5));
            Assert.That(limits.ConferenceRequestRefillSeconds, Is.EqualTo(10));
        }

        [Test]
        public void TheKeysSetTheCeilings()
        {
            NonSpatialLimits limits = WebRtcVoiceRegionModule.ReadNonSpatialLimits(
                Section(("MaxNonSpatialSessions", "250"), ("MaxSessionsPerAgent", "3"),
                        ("ConferenceRequestBurst", "8"), ("ConferenceRequestRefillSeconds", "2.5")));

            Assert.That(limits.MaxNonSpatialSessions, Is.EqualTo(250));
            Assert.That(limits.MaxSessionsPerAgent, Is.EqualTo(3));
            Assert.That(limits.ConferenceRequestBurst, Is.EqualTo(8));
            Assert.That(limits.ConferenceRequestRefillSeconds, Is.EqualTo(2.5));
        }

        [TestCase("0")]
        [TestCase("-4")]
        public void AValueBelowOneReadsAsTheDefault(string value)
        {
            NonSpatialLimits limits = WebRtcVoiceRegionModule.ReadNonSpatialLimits(
                Section(("MaxNonSpatialSessions", value), ("MaxSessionsPerAgent", value),
                        ("ConferenceRequestBurst", value), ("ConferenceRequestRefillSeconds", value)));

            Assert.That(limits.MaxNonSpatialSessions, Is.EqualTo(5000));
            Assert.That(limits.MaxSessionsPerAgent, Is.EqualTo(20));
            Assert.That(limits.ConferenceRequestBurst, Is.EqualTo(5));
            Assert.That(limits.ConferenceRequestRefillSeconds, Is.EqualTo(10));
        }
    }
}
