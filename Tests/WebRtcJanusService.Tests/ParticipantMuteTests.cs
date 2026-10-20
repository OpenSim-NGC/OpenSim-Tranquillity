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
    [TestFixture]
    public class ParticipantMuteTests
    {
        private const long BigId = 6969906757968657;

        private static AudioBridgeResp Reply(string data)
            => new AudioBridgeResp(JanusMessageResp.FromJson(
                "{\"janus\":\"success\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":" + data + "}}"));

        [Test]
        public void P1_AJoinAnswersParticipantId_KeepsAll64Bits()
        {
            var join = new AudioBridgeJoinRoomResp(JanusMessageResp.FromJson(
                "{\"janus\":\"event\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":{\"audiobridge\":\"joined\",\"room\":5,\"id\":" + BigId + "}}}"));

            Assert.That(join.ParticipantId, Is.EqualTo(BigId));
        }

        [Test]
        public void P2_TheMuteRequest_NamesTheWholeParticipantId()
        {
            string json = new AudioBridgeMuteReq(5, BigId, true).ToJson();

            Assert.That(json, Does.Contain("\"request\":\"mute\"").And.Contain("\"room\":5").And.Contain("\"id\":" + BigId));
        }

        [Test]
        public void P3_TheLeaveRequest_NamesTheWholeParticipantId()
        {
            Assert.That(new AudioBridgeLeaveRoomReq(5, BigId).ToJson(), Does.Contain("\"id\":" + BigId));
        }

        [Test]
        public void P4_AnUnmuteRequest_IsAnUnmute()
        {
            Assert.That(new AudioBridgeMuteReq(5, 7, false).ToJson(), Does.Contain("\"request\":\"unmute\""));
        }

        [Test]
        public void P5_Success_IsApplied()
            => Assert.That(JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"success\"}")), Is.EqualTo(ParticipantMuteResult.Applied));

        [Test]
        public void P6_UnknownRequest_MeansTheAudioBridgeCannotMute()
            => Assert.That(JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"event\",\"error_code\":482,\"error\":\"Unknown request 'mute'\"}")),
                Is.EqualTo(ParticipantMuteResult.Unsupported));

        [TestCase(485)]
        [TestCase(492)]
        public void P7_NoSuchRoomOrUser_MeansTheParticipantIsNotThere(int code)
            => Assert.That(JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"event\",\"error_code\":" + code + ",\"error\":\"x\"}")),
                Is.EqualTo(ParticipantMuteResult.NotInRoom));

        [Test]
        public void P8_AnyOtherError_OrNoAnswer_IsAFailure()
        {
            Assert.That(JanusAudioBridge.ClassifyMuteReply(Reply("{\"audiobridge\":\"event\",\"error_code\":487,\"error\":\"Unauthorized\"}")),
                Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(JanusAudioBridge.ClassifyMuteReply(null), Is.EqualTo(ParticipantMuteResult.Failed));
        }
    }
}
