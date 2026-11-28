/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration test (see StockJanusRegionTests.cs) for the position feed, against a scratch slvoice mixer that
 * speaks vis_protocol 3 (the slvoice mixer, release 1.2.0 or later). A region with the feed on and two avatars in a room sends
 * peer_pos requests; the mixer's own counters (visibility.positions in the admin API's handle_info for a participant in
 * the room) are the proof that it received and applied them, and that it refused none.
 *
 * Needs, besides the variables StockJanusRegionTests.cs lists, WEBRTC_JANUS_TEST_ADMIN_URL and
 * WEBRTC_JANUS_TEST_ADMIN_SECRET for the mixer's Admin API. Use a scratch mixer: the test creates rooms on it.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    [NeedsPlugin(JanusPluginKind.Mixer)]
    public class StockJanusPositionFeedTests : IClassFixture<StockJanusPositionFeedTests.GatewayResources>
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        public sealed class GatewayResources : IDisposable
        {
            public RawJanus Janus { get; } = new();
            public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };
            public void Dispose() => Http.Dispose();
        }

        private readonly RawJanus _janus;
        private readonly HttpClient _http;

        public StockJanusPositionFeedTests(GatewayResources resources)
        {
            StockJanusEnvironment.IgnoreUnlessConfigured(JanusPluginKind.Mixer);
            _janus = resources.Janus;
            _http = resources.Http;
        }

        /// <summary>Every WARN and ERROR line the sim writes while the test runs.</summary>
        private sealed class ProblemCollector : ILoggerProvider
        {
            public readonly List<string> Lines = new();
            public ILogger CreateLogger(string categoryName) => new Sink(this);
            public void Dispose() { }

            private sealed class Sink : ILogger
            {
                private readonly ProblemCollector _owner;
                public Sink(ProblemCollector owner) { _owner = owner; }
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    if (!IsEnabled(logLevel)) return;
                    lock (_owner.Lines) _owner.Lines.Add($"{logLevel}: {formatter(state, exception)}");
                }
            }
        }

        private static void PositionFeedOn(IConfig voice, IConfig janus)
        {
            voice.Set("VisibilityFeederEnabled", "true");
            voice.Set("VisibilityEmitEnabled", "true");
            // Arming is left off, and nobody is excluded or muted: the region learns the mixer's version by asking.
            voice.Set("PositionFeedEnabled", "true");
            voice.Set("PositionFeedMode", "observe");
        }

        private async Task<JsonObject> AdminAsync(string path, JsonObject body)
        {
            body["transaction"] = Guid.NewGuid().ToString("N");
            body["admin_secret"] = StockJanusEnvironment.AdminSecret;
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage r = await _http.PostAsync(StockJanusEnvironment.AdminUrl.TrimEnd('/') + path, content);
            return JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
        }

        /// <summary>The mixer's visibility.positions block for the room, read from the handles of its participants, or null
        /// when no handle reports one (no participant yet, or the room has accepted no peer_pos).</summary>
        private async Task<JsonObject> PositionsBlockAsync() => (await VisibilityBlockAsync())?["positions"] as JsonObject;

        /// <summary>The mixer's whole visibility block for the room (authority, heartbeats, join cap and positions), read from
        /// the handles of its participants, or null when there is no participant.</summary>
        private async Task<JsonObject> VisibilityBlockAsync()
        {
            JsonObject sessions = await AdminAsync(string.Empty, new JsonObject { ["janus"] = "list_sessions" });
            foreach (JsonNode sid in sessions?["sessions"] as JsonArray ?? new JsonArray())
            {
                JsonObject handles = await AdminAsync($"/{sid}", new JsonObject { ["janus"] = "list_handles" });
                foreach (JsonNode hid in handles?["handles"] as JsonArray ?? new JsonArray())
                {
                    JsonObject info = await AdminAsync($"/{sid}/{hid}", new JsonObject { ["janus"] = "handle_info" });
                    if (info?["info"]?["plugin_specific"]?["visibility"] is JsonObject block)
                        return block;
                }
            }
            return null;
        }

        // The counters that move with time alone, not with anything a request does.
        private static string WithoutAges(JsonObject visibility)
        {
            var copy = (JsonObject)visibility.DeepClone();
            copy.Remove("authority_age_ms");
            if (copy["positions"] is JsonObject positions)
                positions.Remove("last_batch_age_ms");
            return copy.ToJsonString();
        }

        [Fact(Timeout = 240000)]
        public async Task TheVersionCheck_ChangesNothingInTheRoomsVisibility()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            Assert.SkipWhen(string.IsNullOrEmpty(StockJanusEnvironment.AdminUrl), "WEBRTC_JANUS_TEST_ADMIN_URL is not set");
            using var region = StockRegion.Create((voice, janus) => { });   // the position feed is off: the check is the only request
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel + new Vector3(3, 0, 0));
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));

            JsonObject before = await VisibilityBlockAsync();
            {
                var assertedValue = before;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the mixer reported a visibility block for the room");
            }

            using var sink = new JanusPeerCtlBatchSink(StockJanusEnvironment.AdminUrl, StockJanusEnvironment.AdminSecret,
                TimeSpan.FromSeconds(10), UUID.Random(), "Test Region");
            Assert.True(await sink.CheckMixerProtocolAsync());
            Assert.True(sink.MixerProtocolKnown);
            Assert.True(sink.MixerVisProtocol >= 3, "the mixer answered with its version");

            JsonObject after = await VisibilityBlockAsync();
            TestContext.Current.TestOutputHelper.WriteLine("before: " + WithoutAges(before));
            TestContext.Current.TestOutputHelper.WriteLine("after:  " + WithoutAges(after));
            {
                var assertedValue = WithoutAges(after);
                TestAssert.WithMessage(() => Assert.Equal(WithoutAges(before), assertedValue), "no epoch, generation, record, heartbeat or position counter moved");
            }
        }

        private static long Counter(JsonObject block, string name) => block?[name] is JsonNode n ? n.GetValue<long>() : 0;

        private async Task<JsonObject> WaitForBatchesAsync(long atLeast, TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;
            JsonObject block = null;
            while (DateTime.UtcNow < until)
            {
                block = await PositionsBlockAsync();
                if (Counter(block, "batches") >= atLeast)
                    return block;
                await Task.Delay(500);
            }
            return block;
        }

        [Fact(Timeout = 240000)]
        public async Task PositionFeed_IsReceivedAndAppliedByTheMixer_AndItsCountersMove()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            Assert.SkipWhen(string.IsNullOrEmpty(StockJanusEnvironment.AdminUrl), "WEBRTC_JANUS_TEST_ADMIN_URL is not set");
            var problems = new ProblemCollector();
            var logging = new LogCapture(problems, LogLevel.Warning);
            try
            {
                using var region = StockRegion.Create(PositionFeedOn);
                ScenePresence alice = region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
                region.AddAvatar(Bob, StockRegion.InEstateChannelParcel + new Vector3(3, 0, 0));
                using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
                using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
                Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));

                // The region asks the mixer which vis_protocol it speaks, then sends the room's first full snapshot at the
                // next position tick.
                JsonObject first = await WaitForBatchesAsync(1, TimeSpan.FromSeconds(30));
                {
                    var assertedValue = first;
                    TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the mixer reported no visibility.positions block for the room: it accepted no peer_pos");
                }
                long before = Counter(first, "batches");
                TestContext.Current.TestOutputHelper.WriteLine("after the first snapshot: " + first.ToJsonString());
                Assert.True(before >= 1);

                // A participant who walks away is sent as a change; one who stands still is repeated at the keep-alive.
                alice.AbsolutePosition = StockRegion.InEstateChannelParcel + new Vector3(-6, 0, 0);
                JsonObject later = await WaitForBatchesAsync(before + 2, TimeSpan.FromSeconds(30));
                TestContext.Current.TestOutputHelper.WriteLine("after the move and a keep-alive: " + later?.ToJsonString());
                Assert.True(Counter(later, "batches") >= before + 2, "the mixer's batch counter moved");

                {
                    var assertedValue = Counter(later, "rejected_stale_seq");
                    TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the mixer refused none for a stale sequence number");
                }
                Assert.Equal(0, Counter(later, "rejected_stale_epoch"));
                {
                    var assertedValue = Counter(later, "entries_skipped");
                    TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the mixer skipped no entry");
                }

                string[] lines;
                lock (problems.Lines) lines = problems.Lines.ToArray();
                foreach (string l in lines) TestContext.Current.TestOutputHelper.WriteLine(l.Length > 400 ? l.Substring(0, 400) : l);
                {
                    var assertedValue = lines.Where(l => l.Contains("refused a peer_pos"));
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the region saw no refusal");
                }
            }
            finally
            {
                logging.Dispose();
            }
        }

    }
}
