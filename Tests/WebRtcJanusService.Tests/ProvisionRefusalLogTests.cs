/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What a viewer that keeps re-provisioning after a refusal writes to the log, through WebRtcVoiceServiceModule:
 * one line for the first refusal of the episode, nothing for the retries, one line with the count when the
 * episode ends. The log is counted with a test appender.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory and sets FakeVoiceService.Mode
    public class ProvisionRefusalLogTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000c0072");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000c0072");

        private LogCapture _log;

        [SetUp]
        public void SetUp()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Fail;
            lock (FakeVoiceService.Created) FakeVoiceService.Created.Clear();
            _log = new LogCapture();
        }

        [TearDown]
        public void TearDown()
        {
            _log.Dispose();
            foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(Region, Alice, UUID.Zero))
                VoiceViewerSession.CloseCompleted(s);
            FakeVoiceService.Mode = FakeProvisionMode.Fail;
        }

        private static WebRtcVoiceServiceModule NewModule(int refusalSeconds)
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            voice.Set("SpatialVoiceService", "WebRtcJanusService.Tests.dll:FakeVoiceService");
            voice.Set("NonSpatialVoiceService", "WebRtcJanusService.Tests.dll:FakeVoiceService");
            voice.Set("RefusalCacheSeconds", refusalSeconds.ToString());
            var module = new WebRtcVoiceServiceModule();
            module.Initialise(config);
            return module;
        }

        private static OSDMap LocalFirstProvision() => new OSDMap
        {
            ["channel_type"] = OSD.FromString("local"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
        };

        // Only this fixture's viewer: lines from other tests running before or after must not count.
        private List<LogCapture.Line> RefusalLines() => _log.AtInfoOrAbove("refused").Where(l => l.Text.Contains(Alice.ToString())).ToList();

        [Test]
        public void TheFirstRefusalOfAnEpisode_IsLoggedOnce_AndTheRetriesInTheWindowAddNoLine()
        {
            WebRtcVoiceServiceModule module = NewModule(5);

            for (int i = 0; i < 20; i++)
                module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(RefusalLines().Count, Is.EqualTo(1), "the first refusal, and none of the 19 retries");
            Assert.That(RefusalLines()[0].Text, Does.Contain(Alice.ToString()));
        }

        [Test]
        public void AnEpisodeThatSpansSeveralWindows_StillWritesOneLineUntilItEnds()
        {
            WebRtcVoiceServiceModule module = NewModule(1);
            int fresh = 0;

            // Retry until the one-second window has passed twice, so the refusal has been evaluated afresh at least three times.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 2300)
            {
                module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
                lock (FakeVoiceService.Created) fresh = FakeVoiceService.Created.Count;
                Thread.Sleep(20);
            }

            Assert.That(fresh, Is.GreaterThanOrEqualTo(3), "precondition: the refusal was re-evaluated in later windows");
            Assert.That(RefusalLines().Count, Is.EqualTo(1));
        }

        [Test]
        public void ALogoutEndsTheEpisode_WithOneLineThatCarriesTheNumberOfRefusals()
        {
            WebRtcVoiceServiceModule module = NewModule(5);
            for (int i = 0; i < 20; i++)
                module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            module.ProvisionVoiceAccountRequest(new OSDMap { ["logout"] = OSD.FromBoolean(true) }, Alice, Region);

            List<LogCapture.Line> lines = RefusalLines();
            Assert.That(lines.Count, Is.EqualTo(2), "the first refusal and the end");
            Assert.That(lines[1].Text, Does.Contain("20 times"));
        }

        [Test]
        public void AProvisionThatIsAdmitted_EndsTheEpisode_AndANewRefusalLogsAgain()
        {
            WebRtcVoiceServiceModule module = NewModule(1);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            FakeVoiceService.Mode = FakeProvisionMode.Succeed;

            // The cached refusal is replayed until its window has passed; after it the provision is evaluated again and admitted.
            // (A wait for the window to pass: the next call must be the first one after it, so it cannot poll.)
            Thread.Sleep(1150);
            OSDMap resp = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            Assert.That(resp.ContainsKey("viewer_session"), Is.True, "precondition: admitted");

            Assert.That(RefusalLines().Count, Is.EqualTo(2), "the first refusal, and the end with its count");
            Assert.That(RefusalLines()[1].Text, Does.Contain("3 times"));

            FakeVoiceService.Mode = FakeProvisionMode.Fail;
            module.ProvisionVoiceAccountRequest(new OSDMap { ["logout"] = OSD.FromBoolean(true) }, Alice, Region);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            Assert.That(RefusalLines().Count, Is.EqualTo(3), "a refusal after the end starts a new episode");
        }

        [Test]
        public void ARefusalOnItsOwn_IsOneLine_AndTheEndHasNothingToAdd()
        {
            WebRtcVoiceServiceModule module = NewModule(5);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            module.ProvisionVoiceAccountRequest(new OSDMap { ["logout"] = OSD.FromBoolean(true) }, Alice, Region);

            Assert.That(RefusalLines().Count, Is.EqualTo(1));
        }
    }
}
