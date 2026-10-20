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
    [TestFixture]
    public class AudioBridgeReplyClassificationTests
    {
        // JANUS_AUDIOBRIDGE_ERROR_* of Janus 1.4.1 (janus_audiobridge.c lines 2289-2304).
        private static readonly int[] DefinedCodes = { 499, 480, 481, 482, 483, 484, 485, 486, 487, 488, 489, 490, 491, 492, 493, 494 };

        private static AudioBridgeResp Reply(string data)
            => new AudioBridgeResp(JanusMessageResp.FromJson(
                "{\"janus\":\"success\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":" + data + "}}"));

        private static AudioBridgeResp Error(int code, string cause = "x")
            => Reply("{\"audiobridge\":\"event\",\"error_code\":" + code + ",\"error\":\"" + cause + "\"}");

        // ---- successes ------------------------------------------------------------------------------------------

        [Test]
        public void ASuccessIsApplied_ForMuteUnmuteAndKick_InEveryShapeThePluginSendsIt()
        {
            string[] shapes =
            {
                "{\"audiobridge\":\"success\"}",                                   // already in that state (mute 4348), or kicked (kick 4735)
                "{\"audiobridge\":\"success\",\"room\":5}",                         // muted or unmuted (mute 4412)
            };
            foreach (string shape in shapes)
            {
                Assert.That(JanusAudioBridge.ClassifyMuteReply(Reply(shape)), Is.EqualTo(ParticipantMuteResult.Applied), shape);
                Assert.That(JanusAudioBridge.ClassifyKickReply(Reply(shape)), Is.EqualTo(ParticipantMuteResult.Applied), shape);
            }
        }

        // ---- every error code ----------------------------------------------------------------------------------

        [TestCase(480, ParticipantMuteResult.Failed)]
        [TestCase(481, ParticipantMuteResult.Failed)]
        [TestCase(482, ParticipantMuteResult.Unsupported)]
        [TestCase(483, ParticipantMuteResult.Failed)]
        [TestCase(484, ParticipantMuteResult.Failed)]
        [TestCase(485, ParticipantMuteResult.NotInRoom)]
        [TestCase(486, ParticipantMuteResult.Failed)]
        [TestCase(487, ParticipantMuteResult.Failed)]
        [TestCase(488, ParticipantMuteResult.Failed)]
        [TestCase(489, ParticipantMuteResult.Failed)]
        [TestCase(490, ParticipantMuteResult.Failed)]
        [TestCase(491, ParticipantMuteResult.Failed)]
        [TestCase(492, ParticipantMuteResult.NotInRoom)]
        [TestCase(493, ParticipantMuteResult.Failed)]
        [TestCase(494, ParticipantMuteResult.Failed)]
        [TestCase(499, ParticipantMuteResult.Failed)]
        public void EachErrorCodeIsReadTheSameWay_ByMuteUnmuteAndKick(int code, ParticipantMuteResult expected)
        {
            Assert.That(JanusAudioBridge.ClassifyMuteReply(Error(code)), Is.EqualTo(expected), "mute and unmute");
            Assert.That(JanusAudioBridge.ClassifyKickReply(Error(code)), Is.EqualTo(expected), "kick");
            Assert.That(JanusAudioBridge.ModeratorErrorMeaning(code), Is.EqualTo(expected), "the table says so");
        }

        [Test]
        public void TheTableNamesEveryCodeThePluginDefines_AndNoOtherOne()
        {
            foreach (int code in DefinedCodes)
                Assert.That(JanusAudioBridge.ModeratorErrorMeaning(code), Is.Not.Null, $"{code} has a row");
            for (int code = 400; code < 600; code++)
                if (!DefinedCodes.Contains(code))
                    Assert.That(JanusAudioBridge.ModeratorErrorMeaning(code), Is.Null, $"{code} is not a code the plugin defines");
        }

        [TestCase(0)]
        [TestCase(450)]
        [TestCase(458)]
        [TestCase(495)]
        [TestCase(496)]
        [TestCase(-1)]
        public void ACodeTheTableDoesNotList_IsAFailure_NeverAnApplied(int code)
        {
            Assert.That(JanusAudioBridge.ClassifyMuteReply(Error(code)), Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(JanusAudioBridge.ClassifyKickReply(Error(code)), Is.EqualTo(ParticipantMuteResult.Failed));
        }

        // ---- answers that carry no code or no plugin data ----------------------------------------------------------

        [Test]
        public void AnAnswerWithNoPluginData_IsAFailure()
        {
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
                Assert.That(JanusAudioBridge.ClassifyMuteReply(resp), Is.EqualTo(ParticipantMuteResult.Failed), json);
                Assert.That(JanusAudioBridge.ClassifyKickReply(resp), Is.EqualTo(ParticipantMuteResult.Failed), json);
            }
            Assert.That(JanusAudioBridge.ClassifyMuteReply(null), Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(JanusAudioBridge.ClassifyKickReply(null), Is.EqualTo(ParticipantMuteResult.Failed));
        }

        [Test]
        public void AnEventThatIsNeitherASuccessNorAnError_IsAFailure()
        {
            AudioBridgeResp odd = Reply("{\"audiobridge\":\"event\",\"room\":5}");

            Assert.That(JanusAudioBridge.ClassifyMuteReply(odd), Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(JanusAudioBridge.ClassifyKickReply(odd), Is.EqualTo(ParticipantMuteResult.Failed));
        }

        [Test]
        public void ASuccessThatIsNotThePluginsSuccessWord_IsNotApplied()
        {
            // "participants", "joined", "left" and so on are other requests' successes.
            foreach (string word in new[] { "participants", "joined", "left", "destroyed", "created", "exists" })
                Assert.That(JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"" + word + "\"}")), Is.EqualTo(ParticipantMuteResult.Failed), word);
        }

        // ---- through the service: the room listing the sim reads first ----------------------------------------------------

        private const int Room = 7070;
        private static readonly UUID Ann = new UUID("a0000000-0000-4000-8000-00000000000a");
        private FakeJanusGateway _gw;
        private string _listAnswer;
        private string _muteAnswer;
        private string _kickAnswer;

        [SetUp]
        public void SetUp()
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

        [TestCase("{\"audiobridge\":\"event\",\"error_code\":485,\"error\":\"No such room (7070)\"}", ParticipantMuteResult.NotInRoom)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":483,\"error\":\"Missing mandatory element (room)\"}", ParticipantMuteResult.Failed)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":484,\"error\":\"Invalid element type (room should be an integer)\"}", ParticipantMuteResult.Failed)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":499,\"error\":\"No session associated with this handle...\"}", ParticipantMuteResult.Failed)]
        [TestCase("{\"audiobridge\":\"event\",\"room\":7070}", ParticipantMuteResult.Failed)]
        public void TheRoomListingThePlugin_RefusesOrCannotAnswer_IsNeverReadAsTheAgentBeingThere(string answer, ParticipantMuteResult expected)
        {
            _listAnswer = answer;

            Assert.That(NewService().SetCallMute(Ann, Room, true), Is.EqualTo(expected));
            Assert.That(NewService().HasCallParticipant(Ann, Room), Is.False);
        }

        [TestCase("{\"audiobridge\":\"event\",\"error_code\":489,\"error\":\"Unauthorized (wrong secret)\"}", ParticipantMuteResult.Failed)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":492,\"error\":\"No such user 901 in room 7070\"}", ParticipantMuteResult.NotInRoom)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":482,\"error\":\"Unknown request 'mute'\"}", ParticipantMuteResult.Unsupported)]
        public void AMuteTheRoomRefusesAfterTheListing_IsReadByItsCode(string answer, ParticipantMuteResult expected)
        {
            _muteAnswer = answer;

            Assert.That(NewService().SetCallMute(Ann, Room, true), Is.EqualTo(expected));
        }

        [TestCase("{\"audiobridge\":\"event\",\"error_code\":489,\"error\":\"Unauthorized (wrong secret)\"}", 0)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":492,\"error\":\"No such user 901 in room 7070\"}", 0)]
        [TestCase("{\"audiobridge\":\"success\"}", 1)]
        public void AKickIsCountedOnlyWhenThePluginSaidItWasKicked(string answer, int expected)
        {
            _kickAnswer = answer;

            Assert.That(NewService().DropCallParticipants(Ann, Room), Is.EqualTo(expected));
        }
    }
}
