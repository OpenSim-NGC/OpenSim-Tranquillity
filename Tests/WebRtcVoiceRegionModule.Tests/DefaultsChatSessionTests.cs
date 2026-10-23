/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * With every optional voice setting at its default, the chat session capability answers each method as the
 * addon in upstream develop does. The expected statuses are the ones recorded for the opt-in DefaultsParityTests
 * (which needs a Janus gateway); these do not, because none of these requests reaches the voice service.
 */

using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Nini.Config;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class DefaultsChatSessionTests
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");

        [TestCase("call", HttpStatusCode.BadRequest)]
        [TestCase("start conference", HttpStatusCode.OK)]
        [TestCase("decline invitation", HttpStatusCode.OK)]
        [TestCase("decline p2p voice", HttpStatusCode.OK)]
        [TestCase("fetch history", HttpStatusCode.OK)]
        [TestCase("no such method", HttpStatusCode.BadRequest)]
        public void EverySettingAtItsDefault_ChatSessionRequestAnswersAsBefore(string method, HttpStatusCode expected)
        {
            IniConfigSource ini = new IniConfigSource();
            ini.AddConfig("WebRtcVoice").Set("Enabled", "true");
            WebRtcVoiceRegionModule module = new WebRtcVoiceRegionModule();
            module.Initialise(ini);
            try
            {
                Scene scene = new SceneHelpers().SetupScene();
                SceneHelpers.AddScenePresence(scene, Alice);
                OSDMap body = new OSDMap
                {
                    ["method"] = OSD.FromString(method),
                    ["session-id"] = OSD.FromUUID(UUID.Random()),
                };
                TestOSHttpResponse response = new TestOSHttpResponse();

                module.ChatSessionRequest(new LlsdPost(body), response, Alice, scene);

                Assert.That(response.StatusCode, Is.EqualTo((int)expected), method);
            }
            finally
            {
                module.Close();
            }
        }
    }
}
