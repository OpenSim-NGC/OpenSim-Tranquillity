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
 * trees). A request listed here must keep its answer; the ones that differ are named in the comment on their rows.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    public class DefaultsParityTests : IAsyncLifetime
        {
        private void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        // name -> (HTTP status, provisioned, response word, rooms the request created on the gateway; -1: see its row)
        private static readonly Dictionary<string, (int Status, bool Provisioned, string Response, int NewRooms)> Expected = new()
        {
            ["chat session: call"] = (400, false, "-", 0),
            ["chat session: start conference"] = (200, false, "-", 0),
            ["chat session: decline invitation"] = (200, false, "-", 0),
            ["chat session: fetch history"] = (200, false, "-", 0),
            ["chat session: unknown method"] = (400, false, "-", 0),
            // A difference in the rooms only: the recorded build read channel_id, which viewers do not send, so every
            // multiagent body joined one room. The room now comes from `channel`, so each channel has a room of its own
            // and a new one is created. The status and the answer are the same.
            ["provision multiagent, unknown session"] = (200, true, "-", 1),
            ["provision multiagent, group-style channel"] = (200, true, "-", 1),
            ["provision with no channel_type"] = (200, false, "-", 0),
            // A difference from the recorded build: a channel type the module issues no room for ("group" while
            // group calls are off) is refused 403 before it reaches the voice service. That build passed it on, the
            // service threw, and the request answered 500.
            ["provision channel_type=group"] = (403, false, "-", 0),
            // The other difference from the build the answers were recorded on: a logout for a viewer session that
            // does not exist is acknowledged with {response: closed}; that build answered an empty 200.
            ["logout, unknown viewer session"] = (200, false, "closed", 0),
        };

        [Fact(Timeout = 240000)]
        public async Task EveryOptionalSettingAtItsDefault_AnswersAsTheRecordedBuildDid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var janus = new RawJanus();
            using var region = StockRegion.Create();
            UUID agent = new UUID("a11ce000-0000-4000-8000-000000000001");
            region.AddAvatar(agent, StockRegion.InEstateChannelParcel);

            List<ProbeRow> rows = await DefaultsProbe.RunAsync(region, agent, janus);

            foreach (ProbeRow row in rows)
                TestContext.Current.TestOutputHelper.WriteLine("ROW " + row);
            {
                var assertedValue = rows.Select(r => r.Name);
                TestAssert.WithMessage(() => Assert.Equivalent(Expected.Keys, assertedValue, strict: true), "the same set of requests");
            }
            foreach (ProbeRow row in rows)
            {
                var want = Expected[row.Name];
                Assert.Multiple(() =>
                {
                    {
                        var assertedValue = row.Status;
                        TestAssert.WithMessage(() => Assert.Equal(want.Status, assertedValue), row.Name + ": HTTP status");
                    }
                    {
                        var assertedValue = row.Provisioned;
                        TestAssert.WithMessage(() => Assert.Equal(want.Provisioned, assertedValue), row.Name + ": provisioned");
                    }
                    {
                        var assertedValue = row.Response;
                        TestAssert.WithMessage(() => Assert.Equal(want.Response, assertedValue), row.Name + ": response");
                    }
                    if (want.NewRooms < 0)
                        {
                            var assertedValue = row.NewRooms;
                            TestAssert.WithMessage(() => Assert.InRange(assertedValue, 0, 1), row.Name + ": rooms created");
                        }
                    else
                        {
                            var assertedValue = row.NewRooms;
                            TestAssert.WithMessage(() => Assert.Equal(want.NewRooms, assertedValue), row.Name + ": rooms created");
                        }
                });
            }
        }

        public ValueTask InitializeAsync()
        {
            RequireGateway();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
