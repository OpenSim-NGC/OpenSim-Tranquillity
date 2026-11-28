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
    public class JanusPluginNameTests
    {
        // JanusSession's ctor stores fields only (no network), so this is safe in a unit test.
        private static JanusSession NewSession() =>
            new JanusSession("http://127.0.0.1:24223/voice", "tok",
                             "http://127.0.0.1:24225/voiceAdmin", "tok");

        // The fix: a configured plugin name flows through JanusAudioBridge to the
        // name-agnostic JanusPlugin base (it is no longer hardcoded to audiobridge).
        [Fact(Timeout = 60000)]
        public void JanusAudioBridge_HonorsConfiguredPluginName()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.slvoice");
            Assert.Equal("janus.plugin.slvoice", ab.PluginName);
        }

        [Fact(Timeout = 60000)]
        public void JanusAudioBridge_AudiobridgeNameFlowsThrough()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.audiobridge");
            Assert.Equal("janus.plugin.audiobridge", ab.PluginName);
        }

        [Fact(Timeout = 60000)]
        public void DefaultPluginName_IsTheStockAudioBridge()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("janus.plugin.audiobridge", WebRtcJanusService.DefaultPluginName);
        }

        // The key-absent path WebRtcJanusService takes: section present, PluginName absent.
        [Fact(Timeout = 60000)]
        public void PluginName_DefaultsToStockAudioBridge_WhenKeyAbsent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("JanusGatewayURI", "http://janus.test/voice");
            Assert.Equal("janus.plugin.audiobridge", WebRtcJanusService.ReadPluginName(cfg));
        }

        // An operator who wants the spatial mixer sets the key explicitly.
        [Fact(Timeout = 60000)]
        public void PluginName_IsHonored_WhenKeySet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("PluginName", "janus.plugin.slvoice");
            Assert.Equal("janus.plugin.slvoice", WebRtcJanusService.ReadPluginName(cfg));
        }
    }
}
