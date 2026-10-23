/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in test against a real Janus gateway with the AudioBridge plugin. It is skipped
 * unless WEBRTC_JANUS_TEST_URL is set.
 *
 *   WEBRTC_JANUS_TEST_URL         Janus HTTP API base, for example http://127.0.0.1:8088/janus
 *   WEBRTC_JANUS_TEST_API_SECRET  the gateway's API secret (optional if the gateway has none)
 *   WEBRTC_JANUS_TEST_ADMIN_URL   admin API URL (optional; defaults to the API URL; the
 *                                 service only needs a non-empty value to start)
 *
 * The test starts the real WebRtcJanusService, which creates a Janus session and attaches
 * the AudioBridge plugin, then creates and destroys an audio room. Tested against
 * Janus 1.4.1 with AudioBridge plugin 2.0.10.
 */

using System.Reflection;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusGatewayTests
    {
        private static string Env(string name) => Environment.GetEnvironmentVariable(name);

        private static object Field(object o, string name) =>
            o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);

        [Test]
        [NeedsPlugin(JanusPluginKind.Stock)]   // it attaches Janus's stock AudioBridge plugin, not the slvoice mixer
        public async Task ServiceStarts_CreatesASessionAttachesAudioBridgeAndCreatesARoom()
        {
            string url = Env("WEBRTC_JANUS_TEST_URL");
            if (string.IsNullOrEmpty(url))
                Assert.Ignore("Set WEBRTC_JANUS_TEST_URL to run against a Janus gateway");

            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", url);
            janus.Set("APIToken", Env("WEBRTC_JANUS_TEST_API_SECRET") ?? "unused");
            janus.Set("JanusGatewayAdminURI", Env("WEBRTC_JANUS_TEST_ADMIN_URL") ?? url);
            janus.Set("AdminAPIToken", "unused");

            // The service registers console commands when it starts.
            ICommandConsole savedConsole = MainConsole.Instance;
            MainConsole.Instance = new MockConsole();
            WebRtcJanusService service;
            try
            {
                service = new WebRtcJanusService(config);
            }
            finally
            {
                MainConsole.Instance = savedConsole;
            }

            Assert.That(Field(service, "_Enabled"), Is.EqualTo(true),
                "the service disables itself when it cannot create a session or attach the plugin");
            var viewerSession = (JanusViewerSession)Field(service, "_ViewerSession");
            Assert.That(long.Parse(viewerSession.Session.SessionId), Is.Not.Zero, "Janus session id");
            TestContext.Out.WriteLine($"Janus session id {viewerSession.Session.SessionId}, AudioBridge handle {viewerSession.AudioBridge.PluginId}");
            Assert.That(viewerSession.AudioBridge.PluginId, Is.Not.Null.And.Not.Empty, "AudioBridge handle");

            int roomId = Random.Shared.Next(100000, 2000000000);
            JanusRoom room = await viewerSession.AudioBridge.CreateRoom(roomId, true, "unit test room");
            try
            {
                Assert.That(room, Is.Not.Null, "AudioBridge created the room");
                Assert.That(room.RoomId, Is.EqualTo(roomId));
            }
            finally
            {
                if (room is not null)
                    await viewerSession.AudioBridge.DestroyRoom(room);
                await viewerSession.Session.DestroySession();
            }
        }
    }
}
