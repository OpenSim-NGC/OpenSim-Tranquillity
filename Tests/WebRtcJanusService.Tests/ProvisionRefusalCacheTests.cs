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
    public class ProvisionRefusalCacheTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-1111-1111-1111-0000000a0072");
        private static readonly UUID Other = new UUID("aaaaaaaa-2222-2222-2222-0000000a0072");
        private static readonly UUID RegionA = new UUID("bbbbbbbb-0000-0000-0000-00000a0a0072");
        private static readonly UUID RegionB = new UUID("bbbbbbbb-0000-0000-0000-00000b0b0072");

        private long _now = 1_000_000;

        private ProvisionRefusalCache New(int pSeconds = 5) => new ProvisionRefusalCache(TimeSpan.FromSeconds(pSeconds), () => _now);

        [Fact(Timeout = 60000)]
        public void RepeatInsideTheWindow_HitsTheCache_AndIsCounted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var cache = New();
            var answer = new object();
            {
                var assertedValue = cache.RecordRefusal(Agent, RegionA, answer);
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no previous window");
            }

            _now += 1000;
            Assert.True(cache.TryGetRefusal(Agent, RegionA, out object a1));
            _now += 1000;
            Assert.True(cache.TryGetRefusal(Agent, RegionA, out object a2));
            Assert.Same(answer, a1);
            Assert.Same(answer, a2);

            _now += 3500;   // past the 5 s window
            Assert.False(cache.TryGetRefusal(Agent, RegionA, out _), "re-evaluated after the window");
            {
                var assertedValue = cache.RecordRefusal(Agent, RegionA, answer);
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the previous window suppressed two retries");
            }
        }

        [Fact(Timeout = 60000)]
        public void Clear_ForgetsTheRefusal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var cache = New();
            cache.RecordRefusal(Agent, RegionA, "refused");
            cache.TryGetRefusal(Agent, RegionA, out _);

            Assert.Equal(1, cache.Clear(Agent, RegionA));
            Assert.False(cache.TryGetRefusal(Agent, RegionA, out _));
            Assert.Equal(0, cache.Count);
        }

        [Fact(Timeout = 60000)]
        public void TheKeyIsAgentAndRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var cache = New();
            cache.RecordRefusal(Agent, RegionA, "refused");

            Assert.False(cache.TryGetRefusal(Agent, RegionB, out _), "same agent, other region");
            Assert.False(cache.TryGetRefusal(Other, RegionA, out _), "other agent, same region");
        }

        [Fact(Timeout = 60000)]
        public void ZeroSeconds_DisablesTheCache()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var cache = New(0);
            cache.RecordRefusal(Agent, RegionA, "refused");
            Assert.False(cache.TryGetRefusal(Agent, RegionA, out _));
        }
    }

    public class ProvisionRefusalCacheServiceTests : IAsyncLifetime
        {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000b0072");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000b0072");
        private void SetUp()
        {
            FakeVoiceService.Mode = FakeProvisionMode.Fail;
            lock (FakeVoiceService.Created) FakeVoiceService.Created.Clear();
        }
        private void TearDown()
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

        [Fact(Timeout = 60000)]
        public void RepeatRefusalInsideTheWindow_IsAnsweredFromTheCache_WithoutANewSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcVoiceServiceModule module = NewModule(5);

            OSDMap first = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            OSDMap second = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            OSDMap third = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            {
                var assertedValue = CreatedCount();
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "only the first attempt created a session and reached the voice service");
            }
            foreach (OSDMap resp in new[] { first, second, third })
            {
                {
                    var assertedValue = resp["response"].AsString();
                    TestAssert.WithMessage(() => Assert.Equal("failed", assertedValue), "the same failure map every time");
                }
                Assert.Equal(first["error"].AsString(), resp["error"].AsString());
            }
        }

        [Fact(Timeout = 60000)]
        public void AfterTheWindow_TheProvisionIsReEvaluated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcVoiceServiceModule module = NewModule(1);

            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            Thread.Sleep(1150);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            {
                var assertedValue = CreatedCount();
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the second attempt ran again after the window");
            }
        }

        [Fact(Timeout = 60000)]
        public void Logout_ClearsTheCachedRefusal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcVoiceServiceModule module = NewModule(5);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            OSDMap closed = module.ProvisionVoiceAccountRequest(new OSDMap { ["logout"] = OSD.FromBoolean(true) }, Alice, Region);
            Assert.Equal("closed", closed["response"].AsString());

            FakeVoiceService.Mode = FakeProvisionMode.Succeed;
            OSDMap resp = module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            {
                var assertedValue = CreatedCount();
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "after the logout the provision is evaluated again");
            }
            Assert.True(resp.ContainsKey("viewer_session"), "and succeeds");
        }

        [Fact(Timeout = 60000)]
        public void RefusalCacheZero_EveryAttemptReachesTheVoiceService()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcVoiceServiceModule module = NewModule(0);

            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);
            module.ProvisionVoiceAccountRequest(LocalFirstProvision(), Alice, Region);

            Assert.Equal(2, CreatedCount());
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
