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
    [TestFixture]
    public class JanusMessageParseTests
    {
        // A Janus "create session" success response whose id exceeds Int32.MaxValue.
        private const string CreateSessionSuccess =
            "{\"janus\":\"success\",\"transaction\":\"test123\",\"data\":{\"id\":923631757106466}}";

        private static OSD ParseId(long id) =>
            JanusMessageResp.FromJson("{\"janus\":\"success\",\"data\":{\"id\":" + id + "}}").dataSection["id"];

        [Test]
        public void CreateSessionResp_ReturnedId_ReadsA64BitSessionId()
        {
            var createResp = new CreateSessionResp(JanusMessageResp.FromJson(CreateSessionSuccess));
            Assert.That(createResp.returnedId, Is.EqualTo("923631757106466"));
        }

        [TestCase(923631757106466L)]
        [TestCase(4294967296L)]        // 2^32
        [TestCase(2147483648L)]        // Int32.MaxValue + 1
        [TestCase(9007199254740991L)]  // 2^53 - 1
        public void OSDToLong_ReadsValuesGreaterThanInt32(long id)
        {
            OSD osd = ParseId(id);
            Assert.That(osd.Type, Is.EqualTo(OSDType.Long),
                "precondition: the parser returns OSDType.Long for a number above int32");
            Assert.That(JanusMessage.OSDToLong(osd), Is.EqualTo(id));
        }

        [TestCase(0L)]
        [TestCase(1L)]
        [TestCase(12345L)]
        [TestCase(2147483647L)]        // Int32.MaxValue
        public void OSDToLong_StillReadsInt32RangeValues(long id)
        {
            OSD osd = ParseId(id);
            Assert.That(osd.Type, Is.EqualTo(OSDType.Integer));
            Assert.That(JanusMessage.OSDToLong(osd), Is.EqualTo(id));
        }
    }
}
