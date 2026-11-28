/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration tests (see StockJanusRegionTests.cs): the join capability against a gateway running the slvoice
 * mixer with a join-capability secret and JS_JOIN_CAP_REQUIRED=1.
 *
 * Also needs WEBRTC_JANUS_TEST_JOIN_CAP_SECRET, the secret the mixer was started with (its JS_JOIN_CAP_SECRET). The
 * tests skip without it. They create rooms on the gateway, so point them at a scratch mixer.
 *
 *   - A join made by the region, with JoinCapabilityEnabled and that secret, is admitted.
 *   - A join made straight on the gateway with the same capability the region would mint is admitted too, so the
 *     refusals below are the capability's doing and not the harness's.
 *   - A join with no capability, with one signed with another key, and with an expired one is refused with 496.
 */

using System;
using System.Threading.Tasks;
using Nini.Config;
using OpenMetaverse;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    [NeedsPlugin(JanusPluginKind.Mixer)]
    public class StockJanusJoinCapabilityTests(RawJanus janus) : IAsyncLifetime, IClassFixture<RawJanus>
        {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
        private const int CapabilityRefused = 496;

        private readonly RawJanus _janus = janus;
        private string _secret;
        private void RequireGatewayAndSecret()
        {
            StockJanusEnvironment.IgnoreUnlessConfigured(JanusPluginKind.Mixer);
            _secret = StockJanusEnvironment.Get("WEBRTC_JANUS_TEST_JOIN_CAP_SECRET");
            if (string.IsNullOrEmpty(_secret))
                Assert.Skip("WEBRTC_JANUS_TEST_JOIN_CAP_SECRET is not set: it is the mixer's JS_JOIN_CAP_SECRET");
        }

        private void CapabilityOn(IConfig voice, IConfig janus)
        {
            voice.Set("JoinCapabilityEnabled", "true");
            janus.Set("JoinCapabilitySecret", _secret);
        }

        // A capability for a join a stranger makes straight on the gateway. The mixer binds it to the display name
        // the join carries, so the agent field is that name.
        private static string Mint(string secret, string agent, string session, int room, long issuedAtUnix)
            => JoinCapability.Mint(secret, agent, session, room, JoinCapability.NoEpoch, 0, issuedAtUnix);

        [Fact(Timeout = 240000)]
        public async Task ARegionJoinWithTheCapabilityIsAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create(CapabilityOn);
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));

            Assert.True(a.Answer.Provisioned, "the mixer admitted the join: " + a.Answer.Body);
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle));
            await region.LogoutAsync(a);
        }

        [Fact(Timeout = 240000)]
        public async Task AJoinOnTheGatewayIsAdmittedOnlyWithAValidCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create(CapabilityOn);
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            Assert.True(a.Answer.Provisioned, "" + a.Answer.Body);
            int room = a.Answer.Room;
            long now = JoinCapability.NowUnix();
            string stranger = UUID.Random().ToString();
            string session = UUID.Random().ToString();

            using (var peer = await AiortcPeer.StartAsync())
            {
                var valid = await _janus.JoinDirectWithCapabilityAsync(room, stranger, peer.Offer, Mint(_secret, stranger, session, room, now), session);
                {
                    var assertedValue = valid.AnswerSdp;
                    TestAssert.WithMessage(() => Assert.NotNull(assertedValue), $"the control join with a valid capability is admitted (error {valid.ErrorCode}, {valid.Reason})");
                }
            }
            using (var peer = await AiortcPeer.StartAsync())
            {
                var none = await _janus.JoinDirectWithCapabilityAsync(room, stranger, peer.Offer, null, null);
                {
                    var assertedValue = none.AnswerSdp;
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "a join with no capability is refused");
                }
                {
                    var assertedValue = none.ErrorCode;
                    TestAssert.WithMessage(() => Assert.Equal(CapabilityRefused, assertedValue), "no capability: " + none.Reason);
                }
            }
            using (var peer = await AiortcPeer.StartAsync())
            {
                string forged = Mint(new string('x', 48), stranger, session, room, now);
                var result = await _janus.JoinDirectWithCapabilityAsync(room, stranger, peer.Offer, forged, session);
                {
                    var assertedValue = result.AnswerSdp;
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "a capability signed with another key is refused");
                }
                {
                    var assertedValue = result.ErrorCode;
                    TestAssert.WithMessage(() => Assert.Equal(CapabilityRefused, assertedValue), "forged: " + result.Reason);
                }
            }
            using (var peer = await AiortcPeer.StartAsync())
            {
                string expired = Mint(_secret, stranger, session, room, now - 3600);
                var result = await _janus.JoinDirectWithCapabilityAsync(room, stranger, peer.Offer, expired, session);
                {
                    var assertedValue = result.AnswerSdp;
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "an expired capability is refused");
                }
                {
                    var assertedValue = result.ErrorCode;
                    TestAssert.WithMessage(() => Assert.Equal(CapabilityRefused, assertedValue), "expired: " + result.Reason);
                }
                Assert.Equal("cap_expired", result.Reason);
            }

            await region.LogoutAsync(a);
        }

        public ValueTask InitializeAsync()
        {
            RequireGatewayAndSecret();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
