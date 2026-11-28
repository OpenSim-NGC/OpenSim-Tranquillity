/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Janus identifies sessions and plugin handles with 64-bit numbers, for example
 * 923631757106466. The JSON-to-OSD parser returns OSDType.Long for any number that does
 * not fit in an int32. JanusMessage.OSDToLong must read that type, otherwise the
 * session id reads as 0, every later request goes to ".../0" and Janus answers 404.
 */

using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class JanusMessageParseTests
    {
        // A Janus "create session" success response whose id exceeds Int32.MaxValue.
        private const string CreateSessionSuccess =
            "{\"janus\":\"success\",\"transaction\":\"test123\",\"data\":{\"id\":923631757106466}}";

        private static OSD ParseId(long id) =>
            JanusMessageResp.FromJson("{\"janus\":\"success\",\"data\":{\"id\":" + id + "}}").dataSection["id"];

        [Fact(Timeout = 60000)]
        public void CreateSessionResp_ReturnedId_ReadsA64BitSessionId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var createResp = new CreateSessionResp(JanusMessageResp.FromJson(CreateSessionSuccess));
            Assert.Equal("923631757106466", createResp.returnedId);
        }

        [InlineData(923631757106466L)]
        [InlineData(4294967296L)]        // 2^32
        [InlineData(2147483648L)]        // Int32.MaxValue + 1
        [InlineData(9007199254740991L)]  // 2^53 - 1
        [Theory(Timeout = 60000)]
        public void OSDToLong_ReadsValuesGreaterThanInt32(long id)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSD osd = ParseId(id);
            {
                var assertedValue = osd.Type;
                TestAssert.WithMessage(() => Assert.Equal(OSDType.Long, assertedValue), "precondition: the parser returns OSDType.Long for a number above int32");
            }
            Assert.Equal(id, JanusMessage.OSDToLong(osd));
        }

        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(12345L)]
        [InlineData(2147483647L)]        // Int32.MaxValue
        [Theory(Timeout = 60000)]
        public void OSDToLong_StillReadsInt32RangeValues(long id)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSD osd = ParseId(id);
            Assert.Equal(OSDType.Integer, osd.Type);
            Assert.Equal(id, JanusMessage.OSDToLong(osd));
        }
    }
}
