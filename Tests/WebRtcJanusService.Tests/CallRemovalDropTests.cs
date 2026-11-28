/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Taking a removed member out of a call's room. A participant is named by the agent id the service set at join. On the
 * stock AudioBridge a participant that another region server joined is kicked; the slvoice mixer has no kick request,
 * so there the service sends none and the server that joined the participant drops it.
 */

using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class CallRemovalDropTests : IAsyncLifetime
        {
        private const int Room = 5151;
        private static readonly UUID Ann = new UUID("a0000000-0000-4000-8000-00000000000a");
        private static readonly UUID Bea = new UUID("b0000000-0000-4000-8000-00000000000b");

        private FakeJanusGateway _gw;
        private ConcurrentQueue<string> _requests;
        private ConcurrentQueue<long> _kicked;
        private List<(long Id, string Display)> _participants;
        private bool _kickFails;
        private void SetUp()
        {
            _participants = new List<(long, string)>();
            _requests = new ConcurrentQueue<string>();
            _kicked = new ConcurrentQueue<long>();
            _kickFails = false;
            _gw = new FakeJanusGateway();
            _gw.PluginAnswer = body =>
            {
                string request = body["request"].AsString();
                if (request == "listparticipants")
                {
                    _requests.Enqueue(request);
                    var items = _participants.Select(p => "{\"id\":" + p.Id + ",\"display\":\"" + p.Display + "\",\"setup\":true,\"muted\":false}");
                    return "{\"audiobridge\":\"participants\",\"room\":" + Room + ",\"participants\":[" + string.Join(",", items) + "]}";
                }
                if (request == "kick")
                {
                    _requests.Enqueue(request);
                    _kicked.Enqueue(body["id"].AsLong());
                    return _kickFails ? "{\"audiobridge\":\"event\",\"error_code\":489,\"error\":\"Unauthorized\"}"
                                      : "{\"audiobridge\":\"success\"}";
                }
                return null;
            };
        }

        private WebRtcJanusService NewService(string plugin)
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", "tok");
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", "tok");
            janus.Set("PluginName", plugin);
            janus.Set("RequestTimeoutMs", "2000");
            return new WebRtcJanusService(config, () => new FakeJanusHandler(_gw));
        }

        [Fact(Timeout = 60000)]
        public void OnTheStockPlugin_EveryParticipantOfTheAgentIsKicked_AndNobodyElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((11, Ann.ToString()));
            _participants.Add((12, Bea.ToString()));
            _participants.Add((13, Ann.ToString()));
            _participants.Add((14, "Test User"));

            int dropped = NewService("janus.plugin.audiobridge").DropCallParticipants(Ann, Room);

            Assert.Equal(2, dropped);
            Assert.Equal(new long[] { 11, 13 }, _kicked.OrderBy(i => i));
        }

        [Fact(Timeout = 60000)]
        public void OnTheStockPlugin_AnAgentWithNoParticipant_DropsNothing_AndKicksNobody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((21, Bea.ToString()));

            Assert.Equal(0, NewService("janus.plugin.audiobridge").DropCallParticipants(Ann, Room));
            Assert.Empty(_kicked);
        }

        [Fact(Timeout = 60000)]
        public void AKickTheRoomRefuses_IsNotCountedAsDropped()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((31, Ann.ToString()));
            _kickFails = true;

            Assert.Equal(0, NewService("janus.plugin.audiobridge").DropCallParticipants(Ann, Room));
            {
                var assertedValue = _kicked.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "it was tried");
            }
        }

        [Fact(Timeout = 60000)]
        public void OnTheMixer_NoKickIsSent_BecauseThePluginHasNone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((41, Ann.ToString()));

            int dropped = NewService("janus.plugin.slvoice").DropCallParticipants(Ann, Room);

            {
                var assertedValue = dropped;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the server that joined the participant drops it");
            }
            {
                var assertedValue = _requests;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the room is not even listed");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheKickNamesTheWholeParticipantId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string json = new AudioBridgeKickReq(5, 6969906757968657).ToJson();

            {
                var assertedValue = json;
                Assert.Contains("\"request\":\"kick\"", assertedValue);
                Assert.Contains("\"room\":5", assertedValue);
                Assert.Contains("\"id\":6969906757968657", assertedValue);
            }
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
