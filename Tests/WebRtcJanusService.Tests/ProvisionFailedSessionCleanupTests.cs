/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * WebRtcVoiceServiceModule.ProvisionVoiceAccountRequest creates and registers a
 * viewer session BEFORE the service provisions it. On a failure return (no viewer_session in the map)
 * that viewer session must not stay registered; otherwise its Janus session and plugin handle stay alive,
 * IsAgentInRegion reports the agent as voiced, and the viewer — never told the session id — cannot
 * log it out. The stock viewer retries a failed provision, so every retry would leak another one.
 *
 * The module here loads FakeVoiceService from this assembly through the same ServerUtils.LoadPlugin
 * path production uses, so the test controls exactly what the service answers.
 */
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public enum FakeProvisionMode { Succeed, Fail, Null }

    // Loaded by name ("WebRtcJanusService.Tests.dll:FakeVoiceService"); LoadPlugin needs a public top-level type.
    public class FakeVoiceService : IWebRtcVoiceService
    {
        // The room seam; this fake owns no rooms.
        public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;

        public static FakeProvisionMode Mode = FakeProvisionMode.Fail;
        public static readonly List<FakeViewerSession> Created = new List<FakeViewerSession>();

        public FakeVoiceService(IConfigSource config)
        {
        }

        public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene)
        {
            var s = new FakeViewerSession { VoiceService = this, AgentId = pUserID, RegionId = pScene };
            lock (Created) Created.Add(s);
            return s;
        }

        public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene)
        {
            switch (Mode)
            {
                case FakeProvisionMode.Succeed:
                    return ProvisionResponseBuilder.BuildSuccess(new OSDMap { ["type"] = OSD.FromString("answer") }, pVSession.ViewerSessionID, 1);
                case FakeProvisionMode.Null:
                    return null;
                default:
                    return ProvisionResponseBuilder.BuildFailure("JoinRoom failed", 0);
            }
        }

        public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => throw new NotSupportedException();
        public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => throw new NotSupportedException();
        public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => throw new NotSupportedException();
    }

    public class FakeViewerSession : IVoiceViewerSession
    {
        private int _shutdownCalls;
        public int ShutdownCalls => Volatile.Read(ref _shutdownCalls);

        public string ViewerSessionID { get; set; } = UUID.Random().ToString();
        public IWebRtcVoiceService VoiceService { get; set; }
        public string VoiceServiceSessionId { get; set; } = string.Empty;
        public UUID RegionId { get; set; }
        public UUID AgentId { get; set; }
        public UUID ClientSessionId { get; set; }

        public Task Shutdown()
        {
            Interlocked.Increment(ref _shutdownCalls);
            return Task.CompletedTask;
        }
    }

    [TestFixture]
    public class ProvisionFailedSessionCleanupTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-00000000a053");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-00000000a053");

        private WebRtcVoiceServiceModule _module;

        [SetUp]
        public void SetUp()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Fail;
            lock (FakeVoiceService.Created) FakeVoiceService.Created.Clear();

            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            voice.Set("SpatialVoiceService", "WebRtcJanusService.Tests.dll:FakeVoiceService");
            voice.Set("NonSpatialVoiceService", "WebRtcJanusService.Tests.dll:FakeVoiceService");
            _module = new WebRtcVoiceServiceModule();
            _module.Initialise(config);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(Region, Alice, UUID.Zero))
                VoiceViewerSession.CloseCompleted(s);
        }

        private static OSDMap LocalFirstProvision() => new OSDMap
        {
            ["channel_type"] = OSD.FromString("local"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
        };

        // How many registered sessions the agent holds here. Capture removes them, so this is the
        // last registry assertion a test makes (TearDown then finds nothing).
        private static int RegisteredSessionCount()
        {
            List<IVoiceViewerSession> captured = VoiceViewerSession.CaptureSessionsForClose(Region, Alice, UUID.Zero);
            foreach (IVoiceViewerSession s in captured)
                VoiceViewerSession.CloseCompleted(s);
            return captured.Count;
        }

        private static FakeViewerSession OnlyCreated()
        {
            lock (FakeVoiceService.Created)
            {
                Assert.That(FakeVoiceService.Created, Has.Count.EqualTo(1), "the module created exactly one session");
                return FakeVoiceService.Created[0];
            }
        }

        private static void WaitForShutdown(FakeViewerSession s)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (s.ShutdownCalls == 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
        }

        [Test]
        public void FailedFirstProvision_RemovesTheNewSession_AndShutsItDown()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Fail;

            OSDMap resp = _module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(resp["response"].AsString(), Is.EqualTo("failed"), "the failure map is still returned to the viewer");
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, Alice), Is.False, "a failed provision leaves the agent unvoiced");
            FakeViewerSession created = OnlyCreated();
            WaitForShutdown(created);
            Assert.That(created.ShutdownCalls, Is.EqualTo(1), "its voice-service resources are shut down");
            Assert.That(RegisteredSessionCount(), Is.EqualTo(0), "no registry entry left for the agent");
        }

        [Test]
        public void NullResponseFirstProvision_RemovesTheNewSession()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Null;

            Assert.That(_module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region), Is.Null);

            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, Alice), Is.False);
            Assert.That(RegisteredSessionCount(), Is.EqualTo(0));
        }

        [Test]
        public void SuccessfulFirstProvision_KeepsExactlyOneSession()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Succeed;

            OSDMap resp = _module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(resp.ContainsKey("viewer_session"), Is.True, "success map");
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, Alice), Is.True);
            Assert.That(OnlyCreated().ShutdownCalls, Is.EqualTo(0), "a successful session is not shut down");
            Assert.That(RegisteredSessionCount(), Is.EqualTo(1), "exactly one registry entry");
        }

        [Test]
        public void FailedReprovisionOfAnExistingSession_LeavesThatSessionAlone()
        {
            // The viewer already owns this viewer session (it holds its id); a failed re-provision must not
            // tear it down behind the viewer's back.
            var existing = new FakeViewerSession { VoiceService = new FakeVoiceService(null), AgentId = Alice, RegionId = Region };
            VoiceViewerSession.AddViewerSession(existing);
            FakeVoiceService.Mode = FakeProvisionMode.Fail;

            OSDMap req = LocalFirstProvision();
            req["viewer_session"] = OSD.FromString(existing.ViewerSessionID);
            OSDMap resp = _module.ProvisionVoiceAccountRequest(req, Alice, Region);

            Assert.That(resp["response"].AsString(), Is.EqualTo("failed"));
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, Alice), Is.True, "the viewer's existing session stays registered");
            Thread.Sleep(50);
            Assert.That(existing.ShutdownCalls, Is.EqualTo(0), "and is not shut down");
            Assert.That(RegisteredSessionCount(), Is.EqualTo(1));
        }
    }
}
