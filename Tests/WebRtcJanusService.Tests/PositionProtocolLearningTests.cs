/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Where the sink learns the mixer's vis_protocol from: the reply to every part of a room's peer_ctl_batch request
 * that was cut because it was over the mixer's size limit, and the reply to a heartbeat. Also that the number of
 * entries the region puts in one peer_pos request stays under the mixer's limit.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class PositionProtocolLearningTests
    {
        private const int Room = 70001;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        // A room of 110 where everyone excludes everyone: far over the mixer's request limit, so it is sent in parts.
        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Everyone(int size)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            for (int i = 0; i < size; i++)
            {
                var others = new List<UUID>(size - 1);
                for (int j = 0; j < size; j++)
                    if (j != i)
                        others.Add(Id(1 + j));
                d[Id(1 + i)] = others;
            }
            return d;
        }

        private static string Applied(int protocol)
            => "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"op\":\"replace\",\"room\":1,\"entries\":1,\"skipped\":0," +
               "\"vis_protocol\":" + protocol + "}}";

        [Test]
        public async Task TheProtocolIsLearnedFromTheReplyToEveryPartOfASplitRequest()
        {
            var requests = new List<OSDMap>();
            // Only the last part's reply advertises protocol 3. A sink that read the first reply only would never learn it.
            int total = -1;
            using var sink = new JanusPeerCtlBatchSink("http://example.org/admin", "secret", TimeSpan.FromSeconds(5), Id(999), "Test Region", 4,
                req =>
                {
                    requests.Add(req);
                    return Task.FromResult((AdminSendResult.Ok, Applied(total >= 0 && requests.Count == total ? 3 : 2)));
                });
            sink.RoomOf = _ => Room;
            var excl = Everyone(110);

            // First pass only to count the parts the room is cut into.
            await sink.SendAsync(VisOp.Replace, excl);
            total = requests.Count;
            Assert.That(total, Is.GreaterThan(1), "the room really is sent in parts");
            Assert.That(sink.MixerVisProtocol, Is.EqualTo(2));

            requests.Clear();
            await sink.SendAsync(VisOp.Replace, excl);

            Assert.That(requests.Count, Is.EqualTo(total));
            Assert.That(sink.MixerVisProtocol, Is.EqualTo(3), "the last part's reply was read");
        }

        [Test]
        public async Task TheProtocolIsLearnedFromAHeartbeatReply()
        {
            const string heartbeat = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"heartbeat\",\"vis_protocol\":3,\"rooms\":{}}}";
            using var sink = new JanusPeerCtlBatchSink("http://example.org/admin", "secret", TimeSpan.FromSeconds(5), Id(999), "Test Region", 4,
                _ => Task.FromResult((AdminSendResult.Ok, heartbeat)));

            Assert.That(sink.MixerVisProtocol, Is.EqualTo(0));
            Assert.That(await sink.SendHeartbeatAsync(new OSDMap { ["request"] = OSD.FromString("peer_ctl_heartbeat") }), Is.True);

            Assert.That(sink.MixerVisProtocol, Is.EqualTo(3));
        }

        [Test]
        public void ARequestCarriesFewerEntriesThanTheMixerAccepts()
        {
            // The mixer refuses a peer_pos request of more than 128 entries or 65536 bytes (SLV_POS_MAX_ENTRIES and
            // SLV_POS_MAX_BYTES in the mixer's possync.h). The region's own limit is deliberately lower, and a room of
            // the mixer's largest size (110) fits in one request.
            const int mixerMaxEntries = 128;
            Assert.That(PositionBatchSerializer.MaxEntries, Is.LessThanOrEqualTo(mixerMaxEntries));
            Assert.That(PositionBatchSerializer.MaxEntries, Is.GreaterThanOrEqualTo(110));
        }
    }
}
