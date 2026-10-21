/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Every optional voice feature has its own switch in [WebRtcVoice], and each is off unless the operator
 * turns it on. These tests pin the default of each switch and show that the feature is inert while its
 * switch is off:
 *
 *   A2AVoiceEnabled        - a chat session "call" is answered as it was before the feature existed
 *   GroupVoiceEnabled      - the group policy is not usable
 *   AdhocVoiceEnabled      - "start conference" is not acted on
 *   VisibilityArmingEnabled, VisibilityFeederEnabled, VisibilityEmitEnabled - read as false
 *   JoinCapabilityEnabled  - read as false (the Janus service tests cover the room payloads)
 *   VoiceModerationEnabled - the SpatialVoiceModerationRequest capability is not advertised
 *   ConnectorsEnabled      - the connector module registers no registry for a configured record
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceFeatureSwitchTests
    {
        private static readonly UUID Alice = new UUID("3b8f2c71-5d04-4e96-a1b7-60c9e48d2f13");

        private sealed class PostRequest : IOSHttpRequest
        {
            public PostRequest(OSDMap body)
            {
                InputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(body)));
            }
            public string HttpMethod => "POST";
            public Uri Url => new Uri("http://sim.example.org/chat");
            public string RawUrl => "/chat";
            public string UriPath => "/chat";
            public Stream InputStream { get; set; }
            public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
            public bool HasEntityBody => true;
            public long ContentLength => InputStream.Length;
            public long ContentLength64 => InputStream.Length;
            public string ContentType => "application/llsd+xml";
            public string[] AcceptTypes => Array.Empty<string>();
            public System.Text.Encoding ContentEncoding => System.Text.Encoding.UTF8;
            public bool IsSecured => false;
            public bool KeepAlive => false;
            public System.Collections.Specialized.NameValueCollection QueryString => throw new NotImplementedException();
            public System.Collections.Hashtable Query => throw new NotImplementedException();
            public HashSet<string> QueryFlags => throw new NotImplementedException();
            public Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
            public IPEndPoint RemoteIPEndPoint => new(IPAddress.Parse("192.0.2.1"), 1);
            public IPEndPoint LocalIPEndPoint => new(IPAddress.Parse("192.0.2.2"), 2);
            public string UserAgent => "test";
            public double ArrivalTS => 0;
        }

        private static void SetPrivate(object target, string field, object value)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? t.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            f.SetValue(f.IsStatic ? null : target, value);
        }

        private static IConfig EmptyVoiceSection()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig cfg = src.AddConfig("WebRtcVoice");
            cfg.Set("Enabled", "true");
            return cfg;
        }

        private static int ChatSession(WebRtcVoiceRegionModule mod, Scene scene, string method, UUID sessionId)
        {
            OSDMap body = new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(sessionId),
            };
            TestOSHttpResponse resp = new TestOSHttpResponse();
            mod.ChatSessionRequest(new PostRequest(body), resp, Alice, scene);
            return resp.StatusCode;
        }

        [Test]
        public void EverySwitchIsOffUnlessSet()
        {
            IConfig cfg = EmptyVoiceSection();
            Assert.That(WebRtcVoiceRegionModule.ReadA2AVoiceEnabled(cfg), Is.False, "A2AVoiceEnabled");
            Assert.That(WebRtcVoiceRegionModule.ReadGroupVoiceEnabled(cfg), Is.False, "GroupVoiceEnabled");
            Assert.That(WebRtcVoiceRegionModule.ReadAdhocVoiceEnabled(cfg), Is.False, "AdhocVoiceEnabled");
            Assert.That(WebRtcVoiceRegionModule.ReadVoiceModerationEnabled(cfg), Is.False, "VoiceModerationEnabled");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityArmingEnabled(cfg), Is.False, "VisibilityArmingEnabled");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(cfg), Is.False, "VisibilityFeederEnabled");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(cfg), Is.False, "VisibilityEmitEnabled");
            Assert.That(cfg.GetBoolean("JoinCapabilityEnabled", false), Is.False, "JoinCapabilityEnabled");
            Assert.That(cfg.GetBoolean("ConnectorsEnabled", false), Is.False, "ConnectorsEnabled");
        }

        [Test]
        public void EverySwitchIsHonouredWhenSet()
        {
            IConfig cfg = EmptyVoiceSection();
            foreach (string key in new[] { "A2AVoiceEnabled", "GroupVoiceEnabled", "AdhocVoiceEnabled", "VoiceModerationEnabled" })
                cfg.Set(key, "true");
            Assert.That(WebRtcVoiceRegionModule.ReadA2AVoiceEnabled(cfg), Is.True);
            Assert.That(WebRtcVoiceRegionModule.ReadGroupVoiceEnabled(cfg), Is.True);
            Assert.That(WebRtcVoiceRegionModule.ReadAdhocVoiceEnabled(cfg), Is.True);
            Assert.That(WebRtcVoiceRegionModule.ReadVoiceModerationEnabled(cfg), Is.True);
        }

        [Test]
        public void A2AOff_CallAndStartConferenceAreAnsweredAsBeforeTheFeature()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);

            Assert.That(ChatSession(mod, scene, "call", UUID.Random()), Is.EqualTo((int)HttpStatusCode.BadRequest),
                "an unhandled method gets 400");
            Assert.That(ChatSession(mod, scene, "start conference", UUID.Random()), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(ChatSession(mod, scene, "decline invitation", UUID.Random()), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(ChatSession(mod, scene, "fetch history", UUID.Random()), Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void A2AOn_CallIsHandledByTheInvitationRegistry()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_a2aVoiceEnabled", true);

            Assert.That(ChatSession(mod, scene, "call", UUID.Random()), Is.EqualTo((int)HttpStatusCode.NotFound),
                "a call for a session nobody started is unknown to the registry");
        }

        [Test]
        public void GroupAndAdhocOff_TheirPoliciesAreNotUsable()
        {
            var mod = new WebRtcVoiceRegionModule();
            FieldInfo group = typeof(WebRtcVoiceRegionModule).GetField("m_groupVoice", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo adhoc = typeof(WebRtcVoiceRegionModule).GetField("m_adhocVoiceEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(((GroupVoicePolicy)group.GetValue(mod)).IsUsable, Is.False, "group voice policy");
            Assert.That((bool)adhoc.GetValue(mod), Is.False, "ad-hoc voice");
        }

        [Test]
        public void ModerationOff_TheCapabilityIsNotAdvertised()
        {
            Assert.That(CapsFor(moderationEnabled: false).CapsHandlers.ContainsCap("SpatialVoiceModerationRequest"), Is.False);
            Assert.That(CapsFor(moderationEnabled: false).CapsHandlers.ContainsCap("ProvisionVoiceAccountRequest"), Is.True,
                "the other voice capabilities are unaffected");
        }

        [Test]
        public void ModerationOn_TheCapabilityIsAdvertised()
        {
            Assert.That(CapsFor(moderationEnabled: true).CapsHandlers.ContainsCap("SpatialVoiceModerationRequest"), Is.True);
        }

        private static OpenSim.Framework.Capabilities.Caps CapsFor(bool moderationEnabled)
        {
            Scene scene = new SceneHelpers().SetupScene();
            var server = new BaseHttpServer(9);   // never started: only its handler table is used
            var caps = new OpenSim.Framework.Capabilities.Caps(server, "sim.example.org", 9000, "/CAPS/" + UUID.Random(), Alice, "Test Region");
            var mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_moderationEnabled", moderationEnabled);
            mod.OnRegisterCaps(scene, Alice, caps);
            return caps;
        }

        [Test]
        public void ConnectorsOff_AConfiguredRecordRegistersNothing()
        {
            Assert.That(ConnectorRegistryAfterAddRegion(connectorsEnabled: false), Is.Null);
        }

        [Test]
        public void ConnectorsOn_AConfiguredRecordIsRegistered()
        {
            Assert.That(ConnectorRegistryAfterAddRegion(connectorsEnabled: true)?.Snapshot(), Has.Count.EqualTo(1));
        }

        private static VoiceConnectorRegistry ConnectorRegistryAfterAddRegion(bool connectorsEnabled)
        {
            var cfg = new IniConfigSource();
            IConfig voice = cfg.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            if (connectorsEnabled)
                voice.Set("ConnectorsEnabled", "true");
            cfg.AddConfig("JanusWebRtcVoice").Set("PluginName", "janus.plugin.slvoice");   // connectors need the slvoice mixer
            IConfig rec = cfg.AddConfig("VoiceConnector.Recorder");
            rec.Set("Enabled", "true");
            rec.Set("NpcFirstName", "Recorder");
            rec.Set("NpcLastName", "NPC");
            rec.Set("Position", "<128,128,25>");

            Scene scene = new SceneHelpers().SetupScene();
            var module = new VoiceConnectorModule();
            module.Initialise(cfg);
            module.AddRegion(scene);
            return scene.RequestModuleInterface<IVoiceConnectorRegistry>() as VoiceConnectorRegistry;
        }
    }
}
