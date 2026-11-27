/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The AudioBridge's own moderator mute, and the participant id it needs.
 *
 * Janus gives each participant a random 64-bit id. The join answer's id used to be narrowed to an int, so a
 * request that names the participant (mute, unmute) named a different, nonexistent one. P1-P3 pin the id; P4-P8
 * read the plugin's answers to a mute request.
 */

using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class ParticipantMuteTests
    {
        private const long BigId = 6969906757968657;

        private static AudioBridgeResp Reply(string data)
            => new AudioBridgeResp(JanusMessageResp.FromJson(
                "{\"janus\":\"success\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":" + data + "}}"));

        [Fact(Timeout = 60000)]
        public void P1_AJoinAnswersParticipantId_KeepsAll64Bits()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var join = new AudioBridgeJoinRoomResp(JanusMessageResp.FromJson(
                "{\"janus\":\"event\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":{\"audiobridge\":\"joined\",\"room\":5,\"id\":" + BigId + "}}}"));

            Assert.Equal(BigId, join.ParticipantId);
        }

        [Fact(Timeout = 60000)]
        public void P2_TheMuteRequest_NamesTheWholeParticipantId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string json = new AudioBridgeMuteReq(5, BigId, true).ToJson();

            {
                var assertedValue = json;
                Assert.Contains("\"request\":\"mute\"", assertedValue);
                Assert.Contains("\"room\":5", assertedValue);
                Assert.Contains("\"id\":" + BigId, assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void P3_TheLeaveRequest_NamesTheWholeParticipantId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("\"id\":" + BigId, new AudioBridgeLeaveRoomReq(5, BigId).ToJson());
        }

        [Fact(Timeout = 60000)]
        public void P4_AnUnmuteRequest_IsAnUnmute()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("\"request\":\"unmute\"", new AudioBridgeMuteReq(5, 7, false).ToJson());
        }

        [Fact(Timeout = 60000)]
        public void P5_Success_IsApplied()

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.Equal(ParticipantMuteResult.Applied, JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"success\"}")));
}
        [Fact(Timeout = 60000)]
        public void P6_UnknownRequest_MeansTheAudioBridgeCannotMute()

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.Equal(ParticipantMuteResult.Unsupported, JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"event\",\"error_code\":482,\"error\":\"Unknown request 'mute'\"}")));
}
        [InlineData(485)]
        [InlineData(492)]
        [Theory(Timeout = 60000)]
        public void P7_NoSuchRoomOrUser_MeansTheParticipantIsNotThere(int code)

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.Equal(ParticipantMuteResult.NotInRoom, JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"event\",\"error_code\":" + code + ",\"error\":\"x\"}")));
}
        [Fact(Timeout = 60000)]
        public void P8_AnyOtherError_OrNoAnswer_IsAFailure()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"event\",\"error_code\":487,\"error\":\"Not joined\"}")));
            Assert.Equal(ParticipantMuteResult.Failed, JanusAudioBridge.ClassifyMuteReply(null));
        }
    }
}
