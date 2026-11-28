/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A moderator's mute of one member of a call reaches the participants of that agent and no one else.
 *
 * The service sets the AudioBridge display of every participant it joins to the agent id (JanusRoom.JoinRoom), so the
 * agent id is what names a participant in the room, whichever region server joined it. These tests give the fake
 * gateway a room with several participants and read which "mute" and "unmute" requests the service sends.
 */

using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class CallMuteIdentityTests : IAsyncLifetime
        {
        private const int Room = 4242;
        private static readonly UUID Ann = new UUID("a0000000-0000-4000-8000-00000000000a");
        private static readonly UUID Bea = new UUID("b0000000-0000-4000-8000-00000000000b");
        private static readonly UUID Cal = new UUID("c0000000-0000-4000-8000-00000000000c");

        private FakeJanusGateway _gw;
        private ConcurrentQueue<(string Request, long Id)> _sent;
        private List<(long Id, string Display)> _participants;
        private int _failOnNth;
        private int _muteCalls;
        private void SetUp()
        {
            _participants = new List<(long, string)>();
            _sent = new ConcurrentQueue<(string Request, long Id)>();
            _failOnNth = 0;
            _muteCalls = 0;
            _gw = new FakeJanusGateway();
            _gw.PluginAnswer = body =>
            {
                string request = body["request"].AsString();
                if (request == "listparticipants")
                {
                    var items = _participants.Select(p =>
                        "{\"id\":" + p.Id + ",\"display\":\"" + p.Display + "\",\"setup\":true,\"muted\":false}");
                    return "{\"audiobridge\":\"participants\",\"room\":" + body["room"].AsInteger() + ",\"participants\":[" + string.Join(",", items) + "]}";
                }
                if (request == "mute" || request == "unmute")
                {
                    _sent.Enqueue((request, body["id"].AsLong()));
                    if (_failOnNth != 0 && Interlocked.Increment(ref _muteCalls) == _failOnNth)
                        return "{\"audiobridge\":\"event\",\"error_code\":499,\"error\":\"x\"}";
                    return "{\"audiobridge\":\"success\",\"room\":" + body["room"].AsInteger() + "}";
                }
                return null;
            };
        }

        private WebRtcJanusService NewService()
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", "tok");
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", "tok");
            janus.Set("PluginName", "janus.plugin.audiobridge");
            janus.Set("RequestTimeoutMs", "2000");
            return new WebRtcJanusService(config, () => new FakeJanusHandler(_gw));
        }

        private long[] MutedIds() => _sent.Where(s => s.Request == "mute").Select(s => s.Id).OrderBy(i => i).ToArray();

        [Fact(Timeout = 60000)]
        public void TwoParticipantsOfOneAgent_AreBothMuted_AndNobodyElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The agent has two viewer sessions in the room, so two participants carry its id. Others share the room,
            // among them a participant whose display is an avatar name rather than an id.
            _participants.Add((101, Ann.ToString()));
            _participants.Add((102, Bea.ToString()));
            _participants.Add((103, Ann.ToString()));
            _participants.Add((104, "Test User"));
            _participants.Add((105, Cal.ToString()));
            WebRtcJanusService svc = NewService();

            ParticipantMuteResult r = svc.SetCallMute(Ann, Room, true);

            Assert.Equal(ParticipantMuteResult.Applied, r);
            {
                var assertedValue = MutedIds();
                TestAssert.WithMessage(() => Assert.Equal(new long[] { 101, 103 }, assertedValue), "every participant of the agent, and no other");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheOtherSameNamedParticipant_IsLeftHeard()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Two different agents who share an avatar name join under their own ids, so the names never meet.
            _participants.Add((201, Ann.ToString()));
            _participants.Add((202, Bea.ToString()));
            WebRtcJanusService svc = NewService();

            Assert.Equal(ParticipantMuteResult.Applied, svc.SetCallMute(Bea, Room, true));

            Assert.Equal(new long[] { 202 }, MutedIds());
        }

        [Fact(Timeout = 60000)]
        public void AParticipantJoinedThroughAnotherRegionServer_IsFoundByItsAgentId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Nothing in this process joined anyone: the room is all this service has to go on.
            _participants.Add((301, Cal.ToString().ToUpperInvariant()));   // the same id, written in capitals
            _participants.Add((302, Bea.ToString()));
            _participants.Add((303, Ann.ToString()));
            WebRtcJanusService svc = NewService();

            Assert.Equal(ParticipantMuteResult.Applied, svc.SetCallMute(Cal, Room, true));

            Assert.Equal(new long[] { 301 }, MutedIds());
        }

        [Fact(Timeout = 60000)]
        public void AnAgentWithNoParticipant_IsNotInTheRoom_AndNothingIsSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((401, Ann.ToString()));
            _participants.Add((402, "not-an-id"));
            WebRtcJanusService svc = NewService();

            Assert.Equal(ParticipantMuteResult.NotInRoom, svc.SetCallMute(Bea, Room, true));
            {
                var assertedValue = _sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no request names a participant that is not the agent's");
            }
        }

        [Fact(Timeout = 60000)]
        public void ADisplayThatOnlyContainsTheId_IsNotTheAgent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((501, Ann + " (guest)"));
            _participants.Add((502, "x" + Ann));
            WebRtcJanusService svc = NewService();

            Assert.Equal(ParticipantMuteResult.NotInRoom, svc.SetCallMute(Ann, Room, true));
            Assert.Empty(_sent);
        }

        [Fact(Timeout = 60000)]
        public void TheAgentsOtherParticipant_IsNotLeftMuted_WhenOneCannotBeMuted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((601, Ann.ToString()));
            _participants.Add((602, Ann.ToString()));
            _failOnNth = 2;
            WebRtcJanusService svc = NewService();

            ParticipantMuteResult r = svc.SetCallMute(Ann, Room, true);

            {
                var assertedValue = r;
                TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Failed, assertedValue), "a partial mute is not reported as done");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnUnmute_ReachesEveryParticipantOfTheAgent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((701, Ann.ToString()));
            _participants.Add((702, Ann.ToString()));
            _participants.Add((703, Bea.ToString()));
            WebRtcJanusService svc = NewService();

            Assert.Equal(ParticipantMuteResult.Applied, svc.SetCallMute(Ann, Room, false));

            Assert.Equal(new[] { "unmute" }, _sent.Select(s => s.Request).Distinct());
            Assert.Equal(new long[] { 701, 702 }, _sent.Select(s => s.Id).OrderBy(i => i));
        }

        [Fact(Timeout = 60000)]
        public void HasCallParticipant_IsTrueOnlyForAnAgentThatHasOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _participants.Add((801, Ann.ToString()));
            _participants.Add((802, "Test User"));
            WebRtcJanusService svc = NewService();

            Assert.True(svc.HasCallParticipant(Ann, Room));
            Assert.False(svc.HasCallParticipant(Bea, Room));
            {
                var assertedValue = _sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "asking does not mute anyone");
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
