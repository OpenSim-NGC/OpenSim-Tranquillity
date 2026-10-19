/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the configurable Janus plugin name ([JanusWebRtcVoice] PluginName).
 *
 * JanusAudioBridge used to hardcode "janus.plugin.audiobridge", so the region could not attach to an
 * alternative mixer (for example janus.plugin.slvoice) without a code change. The name is now read from
 * the PluginName key and passed through the name-agnostic JanusPlugin base. An absent key keeps the
 * stock AudioBridge.
 */

using Nini.Config;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusPluginNameTests
    {
        // JanusSession's ctor stores fields only (no network), so this is safe in a unit test.
        private static JanusSession NewSession() =>
            new JanusSession("http://127.0.0.1:24223/voice", "tok",
                             "http://127.0.0.1:24225/voiceAdmin", "tok");

        // The fix: a configured plugin name flows through JanusAudioBridge to the
        // name-agnostic JanusPlugin base (it is no longer hardcoded to audiobridge).
        [Test]
        public void JanusAudioBridge_HonorsConfiguredPluginName()
        {
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.slvoice");
            Assert.That(ab.PluginName, Is.EqualTo("janus.plugin.slvoice"));
        }

        [Test]
        public void JanusAudioBridge_AudiobridgeNameFlowsThrough()
        {
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.audiobridge");
            Assert.That(ab.PluginName, Is.EqualTo("janus.plugin.audiobridge"));
        }

        [Test]
        public void DefaultPluginName_IsTheStockAudioBridge()
        {
            Assert.That(WebRtcJanusService.DefaultPluginName, Is.EqualTo("janus.plugin.audiobridge"));
        }

        // The key-absent path WebRtcJanusService takes: section present, PluginName absent.
        [Test]
        public void PluginName_DefaultsToStockAudioBridge_WhenKeyAbsent()
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("JanusGatewayURI", "http://janus.test/voice");
            Assert.That(WebRtcJanusService.ReadPluginName(cfg), Is.EqualTo("janus.plugin.audiobridge"));
        }

        // An operator who wants the spatial mixer sets the key explicitly.
        [Test]
        public void PluginName_IsHonored_WhenKeySet()
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("PluginName", "janus.plugin.slvoice");
            Assert.That(WebRtcJanusService.ReadPluginName(cfg), Is.EqualTo("janus.plugin.slvoice"));
        }
    }
}
