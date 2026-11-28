/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A connector's program fetches a join capability before every join, and the endpoint answers each fetch. The line
 * that says a capability was minted is therefore written once for every fetch of every connector, which is a steady
 * stream at Information level for no change anyone needs to act on. It is a Debug line; nothing at Information or
 * above is written for a successful fetch.
 */

using System.Text;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class ConnectorMintLogTests : IAsyncLifetime
        {
        private const string Secret = "connector-bearer-secret-0123456789abcdef";
        private const string MintKey = "mixer-join-cap-hmac-key-fedcba9876543210";

        private sealed class Capture : ILogger
        {
            public readonly List<(LogLevel Level, string Text)> Lines = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add((logLevel, formatter(state, exception)));
            }
        }
        private void SetUp() => JoinCapabilityAuthority.Clear();
        private void TearDown() => JoinCapabilityAuthority.Clear();

        private static VoiceConnectorRecord ActiveRecord()
        {
            var r = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, false, "Operator", null, Secret);
            r.NpcId = new UUID("0c0c0c0c-0000-4000-8000-00000000c0c0");
            r.ViewerSessionId = "5e551011-0000-4000-8000-000000005e55";
            r.Room = 226001844;
            return r;
        }

        private static (VoiceConnectorJoinCapEndpoint Endpoint, Capture Log) Attached()
        {
            var log = new Capture();
            var endpoint = new VoiceConnectorJoinCapEndpoint(new BaseHttpServer(9), log);
            VoiceConnectorRecord record = ActiveRecord();
            endpoint.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { record }, true, MintKey));
            lock (log.Lines)
                log.Lines.Clear();   // the attach writes its own start-up line; only the fetches are of interest
            return (endpoint, log);
        }

        private static VoiceConnectorJoinCapEndpoint.Response Fetch(VoiceConnectorJoinCapEndpoint endpoint)
            => endpoint.Handle("POST", "/voice/connector/Recorder/join-cap", "Bearer " + Secret);

        [Fact(Timeout = 60000)]
        public void ASuccessfulFetch_WritesNothingAtInformationOrAbove()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (VoiceConnectorJoinCapEndpoint endpoint, Capture log) = Attached();

            for (int i = 0; i < 5; i++)
                Assert.Equal(200, Fetch(endpoint).Status);

            lock (log.Lines)
                {
                    var assertedValue = log.Lines.Where(l => l.Level >= LogLevel.Information).Select(l => l.Text);
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a fetch every minute for every connector must not write an Information line");
                }
        }

        [Fact(Timeout = 60000)]
        public void ASuccessfulFetch_WritesTheMintedLineAtDebug_WithoutTheCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (VoiceConnectorJoinCapEndpoint endpoint, Capture log) = Attached();

            VoiceConnectorJoinCapEndpoint.Response response = Fetch(endpoint);

            Assert.Equal(200, response.Status);
            string capability = ((OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(response.Body)))["join_cap"].AsString();
            lock (log.Lines)
            {
                var minted = log.Lines.Where(l => l.Text.Contains("join capability minted")).ToList();
                Assert.Single(minted);
                Assert.Equal(LogLevel.Debug, minted[0].Level);
                {
                    var assertedValue = minted[0].Text;
                    Assert.Contains("Recorder", assertedValue);
                    Assert.Contains("226001844", assertedValue);
                }
                {
                    var assertedValue = log.Lines.Select(l => l.Text);
                    Assert.DoesNotContain(assertedValue, item => item.Contains(Secret));
                    Assert.DoesNotContain(assertedValue, item => item.Contains(MintKey));
                }
                {
                    var assertedValue = log.Lines.Select(l => l.Text);
                    TestAssert.WithMessage(() => Assert.DoesNotContain(assertedValue, item => item.Contains(capability)), "the capability itself is never logged");
                }
            }
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
