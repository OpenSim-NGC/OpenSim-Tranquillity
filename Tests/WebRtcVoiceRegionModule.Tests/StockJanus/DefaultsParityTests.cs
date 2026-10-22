/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in parity test against a real Janus gateway running the stock AudioBridge plugin.
 *
 * DefaultsProbe sends one fixed set of requests to a region with every optional voice setting at its default.
 * The expected answers below were recorded by sending the same requests to the voice addon of upstream
 * develop with the session-start and shared-service fixes and nothing else (the probe compiles against both
 * trees). A request listed here must keep its answer; the one that differs is named in the comment on its row.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(240000)]
#pragma warning restore CS0618
    public class DefaultsParityTests
    {
        [OneTimeSetUp]
        public void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        // name -> (HTTP status, provisioned, response word, rooms the request created on the gateway; -1: see its row)
        private static readonly Dictionary<string, (int Status, bool Provisioned, string Response, int NewRooms)> Expected = new()
        {
            ["chat session: call"] = (400, false, "-", 0),
            ["chat session: start conference"] = (200, false, "-", 0),
            ["chat session: decline invitation"] = (200, false, "-", 0),
            ["chat session: fetch history"] = (200, false, "-", 0),
            ["chat session: unknown method"] = (400, false, "-", 0),
            // A multiagent body with no channel_id always lands in the same room, which a gateway that has served one
            // before already holds: -1 means "one room on a fresh gateway, none on one that has the room".
            ["provision multiagent, unknown session"] = (200, true, "-", -1),
            ["provision multiagent, group-style channel"] = (200, true, "-", 0),
            ["provision with no channel_type"] = (200, false, "-", 0),
            ["provision channel_type=group"] = (500, false, "Exception", 0),
            // The one difference from the build the answers were recorded on: a logout for a viewer session that
            // does not exist is acknowledged with {response: closed}; that build answered an empty 200.
            ["logout, unknown viewer session"] = (200, false, "closed", 0),
        };

        [Test]
        public async Task EveryOptionalSettingAtItsDefault_AnswersAsTheRecordedBuildDid()
        {
            var janus = new RawJanus();
            using var region = StockRegion.Create();
            UUID agent = new UUID("a11ce000-0000-4000-8000-000000000001");
            region.AddAvatar(agent, StockRegion.InEstateChannelParcel);

            List<ProbeRow> rows = await DefaultsProbe.RunAsync(region, agent, janus);

            foreach (ProbeRow row in rows)
                TestContext.Out.WriteLine("ROW " + row);
            Assert.That(rows.Select(r => r.Name), Is.EquivalentTo(Expected.Keys), "the same set of requests");
            foreach (ProbeRow row in rows)
            {
                var want = Expected[row.Name];
                Assert.Multiple(() =>
                {
                    Assert.That(row.Status, Is.EqualTo(want.Status), row.Name + ": HTTP status");
                    Assert.That(row.Provisioned, Is.EqualTo(want.Provisioned), row.Name + ": provisioned");
                    Assert.That(row.Response, Is.EqualTo(want.Response), row.Name + ": response");
                    if (want.NewRooms < 0)
                        Assert.That(row.NewRooms, Is.InRange(0, 1), row.Name + ": rooms created");
                    else
                        Assert.That(row.NewRooms, Is.EqualTo(want.NewRooms), row.Name + ": rooms created");
                });
            }
        }
    }
}
