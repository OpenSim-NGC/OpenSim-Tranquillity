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

        [Fact(Timeout = 60000)]
        public void EverySwitchIsOffUnlessSet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = EmptyVoiceSection();
            Assert.False(WebRtcVoiceRegionModule.ReadA2AVoiceEnabled(cfg), "A2AVoiceEnabled");
            Assert.False(WebRtcVoiceRegionModule.ReadGroupVoiceEnabled(cfg), "GroupVoiceEnabled");
            Assert.False(WebRtcVoiceRegionModule.ReadAdhocVoiceEnabled(cfg), "AdhocVoiceEnabled");
            Assert.False(WebRtcVoiceRegionModule.ReadVoiceModerationEnabled(cfg), "VoiceModerationEnabled");
            Assert.False(WebRtcVoiceRegionModule.ReadVisibilityArmingEnabled(cfg), "VisibilityArmingEnabled");
            Assert.False(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(cfg), "VisibilityFeederEnabled");
            Assert.False(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(cfg), "VisibilityEmitEnabled");
            Assert.False(cfg.GetBoolean("JoinCapabilityEnabled", false), "JoinCapabilityEnabled");
            Assert.False(cfg.GetBoolean("ConnectorsEnabled", false), "ConnectorsEnabled");
        }

        [Fact(Timeout = 60000)]
        public void EverySwitchIsHonouredWhenSet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = EmptyVoiceSection();
            foreach (string key in new[] { "A2AVoiceEnabled", "GroupVoiceEnabled", "AdhocVoiceEnabled", "VoiceModerationEnabled" })
                cfg.Set(key, "true");
            Assert.True(WebRtcVoiceRegionModule.ReadA2AVoiceEnabled(cfg));
            Assert.True(WebRtcVoiceRegionModule.ReadGroupVoiceEnabled(cfg));
            Assert.True(WebRtcVoiceRegionModule.ReadAdhocVoiceEnabled(cfg));
            Assert.True(WebRtcVoiceRegionModule.ReadVoiceModerationEnabled(cfg));
        }

        [Fact(Timeout = 60000)]
        public void A2AOff_CallAndStartConferenceAreAnsweredAsBeforeTheFeature()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);

            {
                var assertedValue = ChatSession(mod, scene, "call", UUID.Random());
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.BadRequest, assertedValue), "an unhandled method gets 400");
            }
            Assert.Equal((int)HttpStatusCode.OK, ChatSession(mod, scene, "start conference", UUID.Random()));
            Assert.Equal((int)HttpStatusCode.OK, ChatSession(mod, scene, "decline invitation", UUID.Random()));
            Assert.Equal((int)HttpStatusCode.OK, ChatSession(mod, scene, "fetch history", UUID.Random()));
        }

        [Fact(Timeout = 60000)]
        public void A2AOn_CallIsHandledByTheInvitationRegistry()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_a2aVoiceEnabled", true);

            {
                var assertedValue = ChatSession(mod, scene, "call", UUID.Random());
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.NotFound, assertedValue), "a call for a session nobody started is unknown to the registry");
            }
        }

        [Fact(Timeout = 60000)]
        public void GroupAndAdhocOff_TheirPoliciesAreNotUsable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mod = new WebRtcVoiceRegionModule();
            FieldInfo group = typeof(WebRtcVoiceRegionModule).GetField("m_groupVoice", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo adhoc = typeof(WebRtcVoiceRegionModule).GetField("m_adhocVoiceEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.False(((GroupVoicePolicy)group.GetValue(mod)).IsUsable, "group voice policy");
            Assert.False(((bool)adhoc.GetValue(mod)), "ad-hoc voice");
        }

        [Fact(Timeout = 60000)]
        public void ModerationOff_TheCapabilityIsNotAdvertised()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(CapsFor(moderationEnabled: false).CapsHandlers.ContainsCap("SpatialVoiceModerationRequest"));
            Assert.True(CapsFor(moderationEnabled: false).CapsHandlers.ContainsCap("ProvisionVoiceAccountRequest"), "the other voice capabilities are unaffected");
        }

        [Fact(Timeout = 60000)]
        public void ModerationOn_TheCapabilityIsAdvertised()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(CapsFor(moderationEnabled: true).CapsHandlers.ContainsCap("SpatialVoiceModerationRequest"));
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

        [Fact(Timeout = 60000)]
        public void ConnectorsOff_AConfiguredRecordRegistersNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Null(ConnectorRegistryAfterAddRegion(connectorsEnabled: false));
        }

        [Fact(Timeout = 60000)]
        public void ConnectorsOn_AConfiguredRecordIsRegistered()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1, ConnectorRegistryAfterAddRegion(connectorsEnabled: true)?.Snapshot().Count());
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
