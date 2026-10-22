/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * When SpatialVoiceService and NonSpatialVoiceService name the same DLL, or the second
 * is left out, one service has to serve both roles. WebRtcVoiceServiceModule only loaded the
 * non-spatial service when the two names differed, so in the shared case the first
 * non-"local" provision (for example channel_type "multiagent") threw a NullReferenceException.
 */

using System.Reflection;
using Nini.Config;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class SharedServiceTests
    {
        private const string ServiceName = "WebRtcJanusService.dll:WebRtcJanusService";

        // No [JanusWebRtcVoice] section: the loaded service makes no connection to Janus.
        private static WebRtcVoiceServiceModule InitialiseModule(string spatial, string nonSpatial)
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            voice.Set("SpatialVoiceService", spatial);
            if (nonSpatial is not null)
                voice.Set("NonSpatialVoiceService", nonSpatial);
            var module = new WebRtcVoiceServiceModule();
            module.Initialise(config);
            return module;
        }

        private static object GetService(WebRtcVoiceServiceModule module, string field) =>
            typeof(WebRtcVoiceServiceModule)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(module);

        [Test]
        public void SameDllForBothRoles_NonSpatialServiceIsTheSpatialService()
        {
            var module = InitialiseModule(ServiceName, ServiceName);
            object spatial = GetService(module, "m_spatialVoiceService");
            Assert.That(spatial, Is.Not.Null, "precondition: the service loaded");
            Assert.That(GetService(module, "m_nonSpatialVoiceService"), Is.SameAs(spatial));
        }

        [Test]
        public void NonSpatialServiceNotConfigured_DefaultsToTheSpatialService()
        {
            var module = InitialiseModule(ServiceName, null);
            object spatial = GetService(module, "m_spatialVoiceService");
            Assert.That(spatial, Is.Not.Null, "precondition: the service loaded");
            Assert.That(GetService(module, "m_nonSpatialVoiceService"), Is.SameAs(spatial));
        }
    }
}
