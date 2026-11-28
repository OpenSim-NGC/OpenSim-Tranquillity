/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Every reply and error the AudioBridge's moderator requests (mute, unmute, kick) can return, and how the service reads
 * each. The list is that of janus_audiobridge.c in Janus 1.4.1; the table it is read against is documented in
 * JanusAudioBridge.ParticipantMute.cs with the line of the plugin that returns each.
 */

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class AudioBridgeReplyClassificationTests : IAsyncLifetime
        {
        // JANUS_AUDIOBRIDGE_ERROR_* of Janus 1.4.1 (janus_audiobridge.c lines 2289-2304).
        private static readonly int[] DefinedCodes = { 499, 480, 481, 482, 483, 484, 485, 486, 487, 488, 489, 490, 491, 492, 493, 494 };

        private static AudioBridgeResp Reply(string data)
            => new AudioBridgeResp(JanusMessageResp.FromJson(
                "{\"janus\":\"success\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":" + data + "}}"));

        private static AudioBridgeResp Error(int code, string cause = "x")
            => Reply("{\"audiobridge\":\"event\",\"error_code\":" + code + ",\"error\":\"" + cause + "\"}");

        // ---- successes ------------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ASuccessIsApplied_ForMuteUnmuteAndKick_InEveryShapeThePluginSendsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string[] shapes =
            {
                "{\"audiobridge\":\"success\"}",                                   // already in that state (mute 4348), or kicked (kick 4735)
                "{\"audiobridge\":\"success\",\"room\":5}",                         // muted or unmuted (mute 4412)
            };
            foreach (string shape in shapes)
            {
                {
                    var assertedValue = JanusAudioBridge.ClassifyMuteReply(Reply(shape));
                    TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Applied, assertedValue), shape);
                }
                {
                    var assertedValue = JanusAudioBridge.ClassifyKickReply(Reply(shape));
                    TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Applied, assertedValue), shape);
                }
            }
        }

        // ---- every error code ----------------------------------------------------------------------------------

        [InlineData(480,ParticipantMuteResult.Failed)]
        [InlineData(481,ParticipantMuteResult.Failed)]
        [InlineData(482,ParticipantMuteResult.Unsupported)]
        [InlineData(483,ParticipantMuteResult.Failed)]
        [InlineData(484,ParticipantMuteResult.Failed)]
        [InlineData(485,ParticipantMuteResult.NotInRoom)]
        [InlineData(486,ParticipantMuteResult.Failed)]
        [InlineData(487,ParticipantMuteResult.Failed)]
        [InlineData(488,ParticipantMuteResult.Failed)]
        [InlineData(489,ParticipantMuteResult.Failed)]
        [InlineData(490,ParticipantMuteResult.Failed)]
        [InlineData(491,ParticipantMuteResult.Failed)]
        [InlineData(492,ParticipantMuteResult.NotInRoom)]
        [InlineData(493,ParticipantMuteResult.Failed)]
        [InlineData(494,ParticipantMuteResult.Failed)]
        [InlineData(499,ParticipantMuteResult.Failed)]
        [Theory(Timeout = 60000)]
        public void EachErrorCodeIsReadTheSameWay_ByMuteUnmuteAndKick(int code, ParticipantMuteResult expected)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = JanusAudioBridge.ClassifyMuteReply(Error(code));
                TestAssert.WithMessage(() => Assert.Equal(expected, assertedValue), "mute and unmute");
            }
            {
                var assertedValue = JanusAudioBridge.ClassifyKickReply(Error(code));
                TestAssert.WithMessage(() => Assert.Equal(expected, assertedValue), "kick");
            }
            {
                var assertedValue = JanusAudioBridge.ModeratorErrorMeaning(code);
                TestAssert.WithMessage(() => Assert.Equal(expected, assertedValue), "the table says so");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheTableNamesEveryCodeThePluginDefines_AndNoOtherOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            foreach (int code in DefinedCodes)
                {
                    var assertedValue = JanusAudioBridge.ModeratorErrorMeaning(code);
                    TestAssert.WithMessage(() => Assert.NotNull(assertedValue), $"{code} has a row");
                }
            for (int code = 400; code < 600; code++)
                if (!DefinedCodes.Contains(code))
                    {
                        var assertedValue = JanusAudioBridge.ModeratorErrorMeaning(code);
                        TestAssert.WithMessage(() => Assert.Null(assertedValue), $"{code} is not a code the plugin defines");
                    }
        }

        [InlineData(0)]
        [InlineData(450)]
        [InlineData(458)]
        [InlineData(495)]
        [InlineData(496)]
        [InlineData(-1)]
        [Theory(Timeout = 60000)]
        public void ACodeTheTableDoesNotList_IsAFailure_NeverAnApplied(int code)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyMuteReply(Error(code)));
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyKickReply(Error(code)));
        }

        // ---- answers that carry no code or no plugin data ----------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AnAnswerWithNoPluginData_IsAFailure()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The plugin refusing while it shuts down (6118), or the gateway refusing before the plugin sees it (a session
            // or handle it does not know, a wrong API secret), answers at the top level with no plugindata.
            string[] topLevel =
            {
                "{\"janus\":\"error\",\"error\":{\"code\":450,\"reason\":\"Shutting down\"}}",
                "{\"janus\":\"error\",\"error\":{\"code\":458,\"reason\":\"No such session\"}}",
                "{\"janus\":\"error\",\"error\":{\"code\":459,\"reason\":\"No such handle\"}}",
                "{\"janus\":\"error\",\"error\":{\"code\":403,\"reason\":\"Unauthorized request (wrong or missing secret/token)\"}}",
                "{\"janus\":\"ack\"}",
            };
            foreach (string json in topLevel)
            {
                var resp = new AudioBridgeResp(JanusMessageResp.FromJson(json));
                {
                    var assertedValue = JanusAudioBridge.ClassifyMuteReply(resp);
                    TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Failed, assertedValue), json);
                }
                {
                    var assertedValue = JanusAudioBridge.ClassifyKickReply(resp);
                    TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Failed, assertedValue), json);
                }
            }
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyMuteReply(null));
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyKickReply(null));
        }

        [Fact(Timeout = 60000)]
        public void AnEventThatIsNeitherASuccessNorAnError_IsAFailure()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            AudioBridgeResp odd = Reply("{\"audiobridge\":\"event\",\"room\":5}");

            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyMuteReply(odd));
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyKickReply(odd));
        }

        [Fact(Timeout = 60000)]
        public void ASuccessThatIsNotThePluginsSuccessWord_IsNotApplied()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // "participants", "joined", "left" and so on are other requests' successes.
            foreach (string word in new[] { "participants", "joined", "left", "destroyed", "created", "exists" })
                {
                    var assertedValue = JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"" + word + "\"}"));
                    TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Failed, assertedValue), word);
                }
        }

        // ---- through the service: the room listing the sim reads first ----------------------------------------------------

        private const int Room = 7070;
        private static readonly UUID Ann = new UUID("a0000000-0000-4000-8000-00000000000a");
        private FakeJanusGateway _gw;
        private string _listAnswer;
        private string _muteAnswer;
        private string _kickAnswer;
        private void SetUp()
        {
            _gw = new FakeJanusGateway();
            _listAnswer = "{\"audiobridge\":\"participants\",\"room\":" + Room + ",\"participants\":[{\"id\":901,\"display\":\"" + Ann + "\"}]}";
            _muteAnswer = "{\"audiobridge\":\"success\"}";
            _kickAnswer = "{\"audiobridge\":\"success\"}";
            _gw.PluginAnswer = body => body["request"].AsString() switch
            {
                "listparticipants" => _listAnswer,
                "mute" or "unmute" => _muteAnswer,
                "kick" => _kickAnswer,
                _ => null,
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

        [InlineData("{\"audiobridge\":\"event\",\"error_code\":485,\"error\":\"No such room (7070)\"}",ParticipantMuteResult.NotInRoom)]
        [InlineData("{\"audiobridge\":\"event\",\"error_code\":483,\"error\":\"Missing mandatory element (room)\"}",ParticipantMuteResult.Failed)]
        [InlineData("{\"audiobridge\":\"event\",\"error_code\":484,\"error\":\"Invalid element type (room should be an integer)\"}",ParticipantMuteResult.Failed)]
        [InlineData("{\"audiobridge\":\"event\",\"error_code\":499,\"error\":\"No session associated with this handle...\"}",ParticipantMuteResult.Failed)]
        [InlineData("{\"audiobridge\":\"event\",\"room\":7070}",ParticipantMuteResult.Failed)]
        [Theory(Timeout = 60000)]
        public void TheRoomListingThePlugin_RefusesOrCannotAnswer_IsNeverReadAsTheAgentBeingThere(string answer, ParticipantMuteResult expected)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _listAnswer = answer;

            Assert.Equal(expected, NewService().SetCallMute(Ann, Room, true));
            Assert.False(NewService().HasCallParticipant(Ann, Room));
        }

        [InlineData("{\"audiobridge\":\"event\",\"error_code\":489,\"error\":\"Unauthorized (wrong secret)\"}",ParticipantMuteResult.Failed)]
        [InlineData("{\"audiobridge\":\"event\",\"error_code\":492,\"error\":\"No such user 901 in room 7070\"}",ParticipantMuteResult.NotInRoom)]
        [InlineData("{\"audiobridge\":\"event\",\"error_code\":482,\"error\":\"Unknown request 'mute'\"}",ParticipantMuteResult.Unsupported)]
        [Theory(Timeout = 60000)]
        public void AMuteTheRoomRefusesAfterTheListing_IsReadByItsCode(string answer, ParticipantMuteResult expected)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _muteAnswer = answer;

            Assert.Equal(expected, NewService().SetCallMute(Ann, Room, true));
        }

        [InlineData("{\"audiobridge\":\"event\",\"error_code\":489,\"error\":\"Unauthorized (wrong secret)\"}",0)]
        [InlineData("{\"audiobridge\":\"event\",\"error_code\":492,\"error\":\"No such user 901 in room 7070\"}",0)]
        [InlineData("{\"audiobridge\":\"success\"}",1)]
        [Theory(Timeout = 60000)]
        public void AKickIsCountedOnlyWhenThePluginSaidItWasKicked(string answer, int expected)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _kickAnswer = answer;

            Assert.Equal(expected, NewService().DropCallParticipants(Ann, Room));
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
