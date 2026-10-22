/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the per-(agent, region) negative cache for refused provisions - the pure cache, and its use in
 * WebRtcVoiceServiceModule (a repeat refusal inside the window never reaches the voice service; the
 * counting FakeVoiceService from ProvisionFailedSessionCleanupTests proves it).
 */

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionRefusalCacheTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-1111-1111-1111-0000000a0072");
        private static readonly UUID Other = new UUID("aaaaaaaa-2222-2222-2222-0000000a0072");
        private static readonly UUID RegionA = new UUID("bbbbbbbb-0000-0000-0000-00000a0a0072");
        private static readonly UUID RegionB = new UUID("bbbbbbbb-0000-0000-0000-00000b0b0072");

        private long _now = 1_000_000;

        private ProvisionRefusalCache New(int pSeconds = 5) => new ProvisionRefusalCache(TimeSpan.FromSeconds(pSeconds), () => _now);

        [Test]
        public void RepeatInsideTheWindow_HitsTheCache_AndIsCounted()
        {
            var cache = New();
            var answer = new object();
            Assert.That(cache.RecordRefusal(Agent, RegionA, answer), Is.EqualTo(0), "no previous window");

            _now += 1000;
            Assert.That(cache.TryGetRefusal(Agent, RegionA, out object a1), Is.True);
            _now += 1000;
            Assert.That(cache.TryGetRefusal(Agent, RegionA, out object a2), Is.True);
            Assert.That(a1, Is.SameAs(answer));
            Assert.That(a2, Is.SameAs(answer));

            _now += 3500;   // past the 5 s window
            Assert.That(cache.TryGetRefusal(Agent, RegionA, out _), Is.False, "re-evaluated after the window");
            Assert.That(cache.RecordRefusal(Agent, RegionA, answer), Is.EqualTo(2), "the previous window suppressed two retries");
        }

        [Test]
        public void Clear_ForgetsTheRefusal()
        {
            var cache = New();
            cache.RecordRefusal(Agent, RegionA, "refused");
            cache.TryGetRefusal(Agent, RegionA, out _);

            Assert.That(cache.Clear(Agent, RegionA), Is.EqualTo(1));
            Assert.That(cache.TryGetRefusal(Agent, RegionA, out _), Is.False);
            Assert.That(cache.Count, Is.EqualTo(0));
        }

        [Test]
        public void TheKeyIsAgentAndRegion()
        {
            var cache = New();
            cache.RecordRefusal(Agent, RegionA, "refused");

            Assert.That(cache.TryGetRefusal(Agent, RegionB, out _), Is.False, "same agent, other region");
            Assert.That(cache.TryGetRefusal(Other, RegionA, out _), Is.False, "other agent, same region");
        }

        [Test]
        public void ZeroSeconds_DisablesTheCache()
        {
            var cache = New(0);
            cache.RecordRefusal(Agent, RegionA, "refused");
            Assert.That(cache.TryGetRefusal(Agent, RegionA, out _), Is.False);
        }
    }

    [TestFixture]
    public class ProvisionRefusalCacheServiceTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000b0072");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000b0072");

        [SetUp]
        public void SetUp()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Fail;
            lock (FakeVoiceService.Created) FakeVoiceService.Created.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(Region, Alice, UUID.Zero))
                VoiceViewerSession.CloseCompleted(s);
            FakeVoiceService.Mode = FakeProvisionMode.Fail;
        }

        private static WebRtcVoiceServiceModule NewModule(int pRefusalSeconds)
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            voice.Set("SpatialVoiceService", "WebRtcJanusService.Tests.dll:FakeVoiceService");
            voice.Set("NonSpatialVoiceService", "WebRtcJanusService.Tests.dll:FakeVoiceService");
            voice.Set("RefusalCacheSeconds", pRefusalSeconds.ToString());
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

        private static int CreatedCount()
        {
            lock (FakeVoiceService.Created) return FakeVoiceService.Created.Count;
        }

        [Test]
        public void RepeatRefusalInsideTheWindow_IsAnsweredFromTheCache_WithoutANewSession()
        {
            WebRtcVoiceServiceModule module = NewModule(5);

            OSDMap first = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            OSDMap second = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            OSDMap third = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(CreatedCount(), Is.EqualTo(1), "only the first attempt created a session and reached the voice service");
            foreach (OSDMap resp in new[] { first, second, third })
            {
                Assert.That(resp["response"].AsString(), Is.EqualTo("failed"), "the same failure map every time");
                Assert.That(resp["error"].AsString(), Is.EqualTo(first["error"].AsString()));
            }
        }

        [Test]
        public void AfterTheWindow_TheProvisionIsReEvaluated()
        {
            WebRtcVoiceServiceModule module = NewModule(1);

            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            Thread.Sleep(1150);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(CreatedCount(), Is.EqualTo(2), "the second attempt ran again after the window");
        }

        [Test]
        public void Logout_ClearsTheCachedRefusal()
        {
            WebRtcVoiceServiceModule module = NewModule(5);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            OSDMap closed = module.ProvisionVoiceAccountRequest(new OSDMap { ["logout"] = OSD.FromBoolean(true) }, Alice, Region);
            Assert.That(closed["response"].AsString(), Is.EqualTo("closed"));

            FakeVoiceService.Mode = FakeProvisionMode.Succeed;
            OSDMap resp = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(CreatedCount(), Is.EqualTo(2), "after the logout the provision is evaluated again");
            Assert.That(resp.ContainsKey("viewer_session"), Is.True, "and succeeds");
        }

        [Test]
        public void RefusalCacheZero_EveryAttemptReachesTheVoiceService()
        {
            WebRtcVoiceServiceModule module = NewModule(0);

            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.That(CreatedCount(), Is.EqualTo(2));
        }
    }
}
