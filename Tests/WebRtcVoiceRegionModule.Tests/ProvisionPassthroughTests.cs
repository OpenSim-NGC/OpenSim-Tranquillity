/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * With no avatar-to-avatar, group or conference feature switched on, a provision whose channel_type is "multiagent" or
 * absent is answered as it was before those features existed: it goes to the voice service unexamined (a room for the
 * multiagent body, an empty answer for the body with no channel_type). Any other channel type is refused 403, where
 * the voice service threw and the request answered 500. With any of the features on, the registry decides and an unknown
 * channel, or any other channel type, is refused 403.
 */

using System;
using System.Net;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionPassthroughTests
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");

        private sealed class RecordingService : IWebRtcVoiceService
        {
            public int Calls;
            public bool AnswerNull;
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
            {
                Calls++;
                return AnswerNull ? null : new OSDMap { ["viewer_session"] = OSD.FromString("vs-1"), ["room"] = OSD.FromInteger(77) };
            }
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene)
                => ProvisionVoiceAccountRequest(pRequest, pUserID, pScene);
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        private static (WebRtcVoiceRegionModule Module, Scene Scene, RecordingService Service) Harness(Action<WebRtcVoiceRegionModule> tweak = null)
        {
            Scene scene = new SceneHelpers().SetupScene();
            var svc = new RecordingService();
            scene.RegisterModuleInterface<IWebRtcVoiceService>(svc);
            var mod = new WebRtcVoiceRegionModule();
            typeof(WebRtcVoiceRegionModule).GetField("m_Enabled", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true);
            tweak?.Invoke(mod);
            return (mod, scene, svc);
        }

        private static void Set(WebRtcVoiceRegionModule mod, string field, object value)
            => typeof(WebRtcVoiceRegionModule).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mod, value);

        private static (int Status, OSDMap Body) Provision(WebRtcVoiceRegionModule mod, Scene scene, OSDMap body)
        {
            var resp = new TestOSHttpResponse();
            mod.ProvisionVoiceAccountRequest(new LlsdPost(body), resp, Alice, scene);
            OSDMap parsed = null;
            if (resp.RawBuffer is { Length: > 0 })
            {
                try { parsed = OSDParser.DeserializeLLSDXml(resp.RawBuffer) as OSDMap; } catch (Exception) { }
            }
            return (resp.StatusCode, parsed);
        }

        private static OSDMap Body(string channelType)
        {
            var m = new OSDMap
            {
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["channel"] = OSD.FromString(UUID.Random().ToString()),
                ["credentials"] = OSD.FromString("credentials"),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            if (channelType is not null)
                m["channel_type"] = OSD.FromString(channelType);
            return m;
        }

        [Test]
        public void X1_EverySwitchOff_AMultiagentProvision_GoesToTheVoiceService()
        {
            var (mod, scene, svc) = Harness();

            (int status, OSDMap body) = Provision(mod, scene, Body("multiagent"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(svc.Calls, Is.EqualTo(1));
            Assert.That(body?["viewer_session"].AsString(), Is.EqualTo("vs-1"), "and its answer comes back");
        }

        [Test]
        public void X2_EverySwitchOff_AProvisionWithNoChannelType_GoesToTheVoiceService_AndAnEmptyAnswerIsA200()
        {
            var (mod, scene, svc) = Harness();
            svc.AnswerNull = true;   // the service has nothing to give a body with no channel_type

            (int status, OSDMap body) = Provision(mod, scene, Body(null));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(svc.Calls, Is.EqualTo(1));
            Assert.That(body, Is.Null);
        }

        [Test]
        public void X3_EverySwitchOff_AnotherChannelType_IsRefused()
        {
            var (mod, scene, svc) = Harness();

            (int status, _) = Provision(mod, scene, Body("group"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.Zero);
        }

        [TestCase("m_a2aVoiceEnabled")]
        [TestCase("m_adhocVoiceEnabled")]
        public void X3b_WithAFeatureOn_AnotherChannelType_IsRefused(string field)
        {
            var (mod, scene, svc) = Harness(m => Set(m, field, true));

            (int status, _) = Provision(mod, scene, Body("group"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.Zero);
        }

        [TestCase("m_a2aVoiceEnabled")]
        [TestCase("m_adhocVoiceEnabled")]
        public void X4_WithAFeatureOn_AMultiagentProvisionForAnUnknownChannel_IsRefused(string field)
        {
            var (mod, scene, svc) = Harness(m => Set(m, field, true));

            (int status, _) = Provision(mod, scene, Body("multiagent"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.Zero);
        }

        [Test]
        public void X5_WithAFeatureOn_AProvisionWithNoChannelType_IsRefused()
        {
            var (mod, scene, svc) = Harness(m => Set(m, "m_a2aVoiceEnabled", true));

            (int status, _) = Provision(mod, scene, Body(null));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.Zero);
        }

        [Test]
        public void X6_EverySwitchOff_ALogoutIsStillRoutedToTheVoiceService()
        {
            var (mod, scene, svc) = Harness();
            var body = new OSDMap
            {
                ["logout"] = OSD.FromBoolean(true),
                ["viewer_session"] = OSD.FromString(UUID.Random().ToString()),
                ["voice_server_type"] = OSD.FromString("webrtc"),
            };

            (int status, _) = Provision(mod, scene, body);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(svc.Calls, Is.EqualTo(1));
        }
    }
}
