/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Response-shape tests for ProvisionVoiceAccountRequest (ProvisionResponseBuilder).
 *
 * The success map carries an additive
 * `room` field (the mixer room joined), so the response shape is asserted here. The map
 * used to be built inline inside an async method that needs a live Janus session, so the
 * construction was extracted into a pure builder; these tests pin the shape and PROVE the
 * extraction changed nothing.
 *
 * Who reads this shape (so a change here is a wire change):
 *   - WebRtcVoiceRegionModule serialises the whole map to LLSD XML for the viewer and reads
 *     `error_code` (WebRtcVoiceRegionModule.cs ProvisionVoiceAccountRequest);
 *   - the viewer reads `viewer_session` and `jsep` by name (llvoicewebrtc.cpp);
 *   - the connector topology forwards the map verbatim over JSON-RPC (WebRtcVoiceServerConnector /
 *     WebRtcVoiceServiceConnector) and reads `viewer_session`;
 *   - the region module records `room` per agent.
 *
 * The equivalence test reconstructs the PRE-extraction literals verbatim (as they stood at
 * "feat(voice): return the joined room in the provision success response", WebRtcJanusService.cs
 * :235-:238, :278-:283, :328-:336) and asserts the builder output is byte-identical on BOTH
 * serialisation paths the map travels — LLSD XML (viewer) and JSON (connector) — plus key order
 * and OSD types element by element. That is the proof of "no behaviour change".
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionResponseShapeTests
    {
        private static OSDMap FakeAnswer()
            => new OSDMap { { "type", "answer" }, { "sdp", "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\n" } };

        // ---- response shape assertions ------------------------------------------------------------

        [Test]
        public void Success_CarriesJsepViewerSessionAndRoom_InThatOrder()
        {
            OSDMap answer = FakeAnswer();
            string vs = UUID.Random().ToString();
            OSDMap m = ProvisionResponseBuilder.BuildSuccess(answer, vs, 1967062692);

            Assert.That(m.Keys, Is.EqualTo(new[] { "jsep", "viewer_session", "room" }).AsCollection,
                "success map keys, in insertion order");
            Assert.That(m["jsep"], Is.SameAs(answer), "jsep is the answer map itself, not a copy");
            Assert.That(m["viewer_session"].Type, Is.EqualTo(OSDType.String));
            Assert.That(m["viewer_session"].AsString(), Is.EqualTo(vs));
        }

        [Test]
        public void Success_RoomIsAnInteger_CarryingTheJoinedRoom()
        {
            OSDMap m = ProvisionResponseBuilder.BuildSuccess(FakeAnswer(), "s", 226001844);
            Assert.That(m["room"].Type, Is.EqualTo(OSDType.Integer), "room must be an LLSD integer (TryGetInt on the region side)");
            Assert.That(m["room"].AsInteger(), Is.EqualTo(226001844));
            Assert.That(m.TryGetInt("room", out int r), Is.True);
            Assert.That(r, Is.EqualTo(226001844));
        }

        [Test]
        public void Room_IsPresentOnlyOnTheSuccessPath()
        {
            Assert.That(ProvisionResponseBuilder.BuildSuccess(FakeAnswer(), "s", 1).ContainsKey("room"), Is.True);
            Assert.That(ProvisionResponseBuilder.BuildFailure("no jsep", 0).ContainsKey("room"), Is.False, "generic failure carries no room");
            Assert.That(ProvisionResponseBuilder.BuildFailure("room is full", 495).ContainsKey("room"), Is.False, "ROOM_FULL failure carries no room");
            Assert.That(ProvisionResponseBuilder.BuildClosed().ContainsKey("room"), Is.False, "logout ack carries no room");
        }

        [Test]
        public void Failure_CarriesErrorCodeOnlyWhenNonZero()
        {
            OSDMap plain = ProvisionResponseBuilder.BuildFailure("JoinRoom failed", 0);
            Assert.That(plain.Keys, Is.EqualTo(new[] { "response", "error" }).AsCollection);
            Assert.That(plain["response"].AsString(), Is.EqualTo("failed"));
            Assert.That(plain["error"].AsString(), Is.EqualTo("JoinRoom failed"));

            OSDMap full = ProvisionResponseBuilder.BuildFailure("room is full", 495);
            Assert.That(full.Keys, Is.EqualTo(new[] { "response", "error", "error_code" }).AsCollection);
            Assert.That(full["error_code"].Type, Is.EqualTo(OSDType.Integer));
            Assert.That(full["error_code"].AsInteger(), Is.EqualTo(495));
        }

        [Test]
        public void Closed_IsExactlyResponseClosed()
        {
            OSDMap m = ProvisionResponseBuilder.BuildClosed();
            Assert.That(m.Keys, Is.EqualTo(new[] { "response" }).AsCollection);
            Assert.That(m["response"].AsString(), Is.EqualTo("closed"));
        }

        // ---- Extraction equivalence: builder == the pre-extraction literals ------------------

        // The literals below are copied VERBATIM from WebRtcJanusService.ProvisionVoiceAccountRequestBAD
        // as it stood before the extraction (see file header). Do not "tidy" them: they are the
        // reference the builder is measured against.
        private static OSDMap OldSuccess(OSDMap answer, string viewerSessionId, int roomId)
            => new OSDMap
            {
                { "jsep", answer },
                { "viewer_session", viewerSessionId },
                { "room", roomId }
            };

        private static OSDMap OldFailure(string errorMsg, int errorCode)
        {
            OSDMap ret = new OSDMap
            {
                { "response", "failed" },
                { "error", errorMsg }
            };
            if (errorCode != 0)
                ret["error_code"] = errorCode;
            return ret;
        }

        private static OSDMap OldClosed()
            => new OSDMap
            {
                { "response", "closed" }
            };

        private static void AssertIdentical(OSDMap expected, OSDMap actual, string which)
        {
            // Both serialisation paths the map travels: LLSD XML to the viewer, JSON over the connector hop.
            Assert.That(OSDParser.SerializeLLSDXmlString(actual), Is.EqualTo(OSDParser.SerializeLLSDXmlString(expected)), which + ": LLSD XML");
            Assert.That(OSDParser.SerializeJsonString(actual), Is.EqualTo(OSDParser.SerializeJsonString(expected)), which + ": JSON");
            // And structurally: same keys in the same order, same OSD type per key.
            Assert.That(actual.Keys, Is.EqualTo(expected.Keys).AsCollection, which + ": key order");
            foreach (string k in expected.Keys)
                Assert.That(actual[k].Type, Is.EqualTo(expected[k].Type), which + ": type of " + k);
        }

        [Test]
        public void Extraction_ReproducesThePreExtractionLiterals_ByteForByte()
        {
            OSDMap answer = FakeAnswer();
            string vs = UUID.Random().ToString();

            AssertIdentical(OldSuccess(answer, vs, 1578726032), ProvisionResponseBuilder.BuildSuccess(answer, vs, 1578726032), "success");
            AssertIdentical(OldFailure("room selection failed", 0), ProvisionResponseBuilder.BuildFailure("room selection failed", 0), "failure/no code");
            AssertIdentical(OldFailure("room is full", 495), ProvisionResponseBuilder.BuildFailure("room is full", 495), "failure/ROOM_FULL");
            AssertIdentical(OldClosed(), ProvisionResponseBuilder.BuildClosed(), "closed");
        }
    }
}
