/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Admitted multiagent provisions were admitted by the region
 * module but died before the voice service ran. Root cause: WebRtcVoiceServiceModule.Initialise
 * loaded m_nonSpatialVoiceService ONLY when the two configured DLL names differed; a config
 * that sets both to the identical "WebRtcJanusService.dll:WebRtcJanusService" left the field null
 * and the first non-"local" provision threw NullReferenceException at the CreateViewerSession
 * dispatch -- swallowed unlogged by the caps wrapper's bare catch (SimpleStreamHandler.cs:91-101),
 * leaving the viewer an empty 500 (no viewer_session/jsep) and VOICE_STATE_SESSION_RETRY.
 *
 * These tests run the REAL module + REAL WebRtcJanusService (loaded via the same ServerUtils
 * .LoadPlugin path production uses -- the DLL sits in the test bin via project reference) with no
 * [JanusWebRtcVoice] section, so no Janus connection is attempted at construction and the provision
 * path's connect attempt fails fast and caught (JanusSession.CreateSession catches, :116-118).
 * Reaching the service is proven by its OWN failure map ({response:"failed", error:"janus unavailable"},
 * built by ProvisionResponseBuilder.BuildFailure when the connect fails) -- which only the Janus service produces, past the seam that used to throw.
 */
using System;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionDispatchTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000a2a31");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000a2a31");

        private WebRtcVoiceServiceModule _module;

        [SetUp]
        public void SetUp()
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            // The live shape that broke: BOTH roles name the SAME service.
            voice.Set("SpatialVoiceService", "WebRtcJanusService.dll:WebRtcJanusService");
            voice.Set("NonSpatialVoiceService", "WebRtcJanusService.dll:WebRtcJanusService");
            // no [JanusWebRtcVoice]: the loaded service attempts no Janus connection at construction.

            _module = new WebRtcVoiceServiceModule();
            _module.Initialise(config);
        }

        [TearDown]
        public void TearDown()
        {
            // The multiagent test may register a viewer session in the static table; sweep it out so other
            // fixtures see a clean table. Generation UUID.Zero captures any session of the agent.
            foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(Region, Alice, UUID.Zero))
                VoiceViewerSession.CloseCompleted(s);
        }

        private static OSDMap MultiagentFirstProvision() => new OSDMap
        {
            ["channel_type"] = OSD.FromString("multiagent"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["channel"] = OSD.FromString("33333333-3333-3333-3333-333333333333"),
            ["credentials"] = OSD.FromString("token"),
            // No viewer_session: the first provision from a viewer. No Janus is configured, so the
            // REAL service answers its own failure map -- the proof the dispatch reached it.
        };

        [Test]
        public void SameServiceName_MultiagentFirstProvision_CreatesASessionAndReachesTheService()
        {
            OSDMap resp = _module.ProvisionVoiceAccountRequest(MultiagentFirstProvision(), Alice, Region);

            Assert.That(resp, Is.Not.Null, "pre-fix this was a swallowed NullReferenceException (m_nonSpatialVoiceService null)");
            Assert.That(resp["response"].AsString(), Is.EqualTo("failed"), "the Janus service's own BuildFailure map: the dispatch reached the service");
            // The service's connect fails (no Janus configured) and it answers that with its own
            // failure map rather than carrying on to the jsep check.
            Assert.That(resp["error"].AsString(), Is.EqualTo("janus unavailable"), "died INSIDE the service, at its connect check");
            // The dispatch created and registered a viewer session (as local does), and removes it again
            // because the provision failed, so nothing leaks.
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, Alice), Is.False,
                "a failed first provision leaves no registered session behind");
        }

        [Test]
        public void ZeroUuidLogout_IsANoOpClosedReply()
        {
            // Observed live: retry-era teardowns POST logout with viewer_session zero and no
            // channel_type; this used to ERROR "no channel_type in request" in the create branch.
            var logout = new OSDMap
            {
                ["logout"] = OSD.FromBoolean(true),
                ["viewer_session"] = OSD.FromUUID(UUID.Zero),
                ["voice_server_type"] = OSD.FromString("webrtc"),
            };
            OSDMap resp = _module.ProvisionVoiceAccountRequest(logout, Alice, Region);
            Assert.That(resp, Is.Not.Null);
            Assert.That(resp["response"].AsString(), Is.EqualTo("closed"), "the same shape a successful logout answers (BuildClosed)");
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, Alice), Is.False, "a no-op logout must not create a session");
        }

        [Test]
        public void UnknownViewerSessionLogout_IsANoOpClosedReply()
        {
            var logout = new OSDMap
            {
                ["logout"] = OSD.FromBoolean(true),
                ["viewer_session"] = OSD.FromString(UUID.Random().ToString()),
                ["voice_server_type"] = OSD.FromString("webrtc"),
            };
            OSDMap resp = _module.ProvisionVoiceAccountRequest(logout, Alice, Region);
            Assert.That(resp, Is.Not.Null);
            Assert.That(resp["response"].AsString(), Is.EqualTo("closed"));
        }

        [Test]
        public void MissingViewerSessionNonLogoutNonChannelType_StillErrorsToNull()
        {
            // The pre-existing shape for a malformed initial request is unchanged: null response
            // (the region module answers "got null response" / empty 200).
            var bad = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") };
            Assert.That(_module.ProvisionVoiceAccountRequest(bad, Alice, Region), Is.Null);
        }
    }
}
