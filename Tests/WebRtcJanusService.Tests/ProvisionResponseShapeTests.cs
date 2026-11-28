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
    public class ProvisionResponseShapeTests
    {
        private static OSDMap FakeAnswer()
            => new OSDMap { { "type", "answer" }, { "sdp", "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\n" } };

        // ---- response shape assertions ------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Success_CarriesJsepViewerSessionAndRoom_InThatOrder()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap answer = FakeAnswer();
            string vs = UUID.Random().ToString();
            OSDMap m = ProvisionResponseBuilder.BuildSuccess(answer, vs, 1967062692);

            {
                var assertedValue = m.Keys;
                TestAssert.WithMessage(() => Assert.Equal(new[] { "jsep", "viewer_session", "room" }, assertedValue), "success map keys, in insertion order");
            }
            {
                var assertedValue = m["jsep"];
                TestAssert.WithMessage(() => Assert.Same(answer, assertedValue), "jsep is the answer map itself, not a copy");
            }
            Assert.Equal(OSDType.String, m["viewer_session"].Type);
            Assert.Equal(vs, m["viewer_session"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void Success_RoomIsAnInteger_CarryingTheJoinedRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap m = ProvisionResponseBuilder.BuildSuccess(FakeAnswer(), "s", 226001844);
            {
                var assertedValue = m["room"].Type;
                TestAssert.WithMessage(() => Assert.Equal(OSDType.Integer, assertedValue), "room must be an LLSD integer (TryGetInt on the region side)");
            }
            Assert.Equal(226001844, m["room"].AsInteger());
            Assert.True(m.TryGetInt("room", out int r));
            Assert.Equal(226001844, r);
        }

        [Fact(Timeout = 60000)]
        public void Room_IsPresentOnlyOnTheSuccessPath()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(ProvisionResponseBuilder.BuildSuccess(FakeAnswer(), "s", 1).ContainsKey("room"));
            Assert.False(ProvisionResponseBuilder.BuildFailure("no jsep", 0).ContainsKey("room"), "generic failure carries no room");
            Assert.False(ProvisionResponseBuilder.BuildFailure("room is full", 495).ContainsKey("room"), "ROOM_FULL failure carries no room");
            Assert.False(ProvisionResponseBuilder.BuildClosed().ContainsKey("room"), "logout ack carries no room");
        }

        [Fact(Timeout = 60000)]
        public void Failure_CarriesErrorCodeOnlyWhenNonZero()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap plain = ProvisionResponseBuilder.BuildFailure("JoinRoom failed", 0);
            Assert.Equal(new[] { "response", "error" }, plain.Keys);
            Assert.Equal("failed", plain["response"].AsString());
            Assert.Equal("JoinRoom failed", plain["error"].AsString());

            OSDMap full = ProvisionResponseBuilder.BuildFailure("room is full", 495);
            Assert.Equal(new[] { "response", "error", "error_code" }, full.Keys);
            Assert.Equal(OSDType.Integer, full["error_code"].Type);
            Assert.Equal(495, full["error_code"].AsInteger());
        }

        [Fact(Timeout = 60000)]
        public void Closed_IsExactlyResponseClosed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap m = ProvisionResponseBuilder.BuildClosed();
            Assert.Equal(new[] { "response" }, m.Keys);
            Assert.Equal("closed", m["response"].AsString());
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
            {
                var assertedValue = OSDParser.SerializeLLSDXmlString(actual);
                TestAssert.WithMessage(() => Assert.Equal(OSDParser.SerializeLLSDXmlString(expected), assertedValue), which + ": LLSD XML");
            }
            {
                var assertedValue = OSDParser.SerializeJsonString(actual);
                TestAssert.WithMessage(() => Assert.Equal(OSDParser.SerializeJsonString(expected), assertedValue), which + ": JSON");
            }
            // And structurally: same keys in the same order, same OSD type per key.
            {
                var assertedValue = actual.Keys;
                TestAssert.WithMessage(() => Assert.Equal(expected.Keys, assertedValue), which + ": key order");
            }
            foreach (string k in expected.Keys)
                {
                    var assertedValue = actual[k].Type;
                    TestAssert.WithMessage(() => Assert.Equal(expected[k].Type, assertedValue), which + ": type of " + k);
                }
        }

        [Fact(Timeout = 60000)]
        public void Extraction_ReproducesThePreExtractionLiterals_ByteForByte()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap answer = FakeAnswer();
            string vs = UUID.Random().ToString();

            AssertIdentical(OldSuccess(answer, vs, 1578726032), ProvisionResponseBuilder.BuildSuccess(answer, vs, 1578726032), "success");
            AssertIdentical(OldFailure("room selection failed", 0), ProvisionResponseBuilder.BuildFailure("room selection failed", 0), "failure/no code");
            AssertIdentical(OldFailure("room is full", 495), ProvisionResponseBuilder.BuildFailure("room is full", 495), "failure/ROOM_FULL");
            AssertIdentical(OldClosed(), ProvisionResponseBuilder.BuildClosed(), "closed");
        }
    }
}
