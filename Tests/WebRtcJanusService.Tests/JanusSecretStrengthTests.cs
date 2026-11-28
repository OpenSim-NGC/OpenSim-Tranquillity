/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Secret strength at startup. A join capability is an HMAC over a known structure, so anyone who sees
 * one can test candidate keys offline; a short key falls. The rule is 32 characters, the same as the
 * connector secret's:
 *   - [JanusWebRtcVoice] APIToken and AdminAPIToken already exist, so a short one only logs a WARNING
 *     that names the key (never its value or its length) and the service starts as before;
 *   - [JanusWebRtcVoice] JoinCapabilitySecret is new, so when JoinCapabilityEnabled is on a short one is
 *     refused: an ERROR names the key and the capability stays off, as it already does for an empty one.
 * An empty value is not a strength problem; it keeps its own existing handling and message.
 *
 * Each test builds a real WebRtcJanusService against the in-memory fake gateway and reads its log.
 * The ambient logger factory is process-wide, so the class runs on its own.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    public class JanusSecretStrengthTests : IAsyncLifetime
        {
        private static string Secret(int length) => new string('k', length);

        private sealed class Capture : ILoggerProvider, ILogger
        {
            public readonly List<(LogLevel Level, string Text)> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }
            public List<(LogLevel Level, string Text)> Snapshot()
            {
                lock (Lines)
                    return new List<(LogLevel, string)>(Lines);
            }
        }

        private Capture _log;
        private LogCapture _logging;
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
        }
        private void TearDown()
        {
            _logging.Dispose();
            _log.Dispose();
        }

        private void StartService(string apiToken, string adminToken, bool joinCapEnabled = false, string joinCapSecret = null)
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            if (joinCapEnabled)
                voice.Set("JoinCapabilityEnabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", apiToken);
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", adminToken);
            janus.Set("PluginName", "janus.plugin.slvoice");
            if (joinCapSecret is not null)
                janus.Set("JoinCapabilitySecret", joinCapSecret);
            _ = new WebRtcJanusService(config, () => new FakeJanusHandler(new FakeJanusGateway()));
        }

        // The strength messages all say "shorter than 32 characters", so an empty value (its own message) never matches.
        private List<(LogLevel Level, string Text)> StrengthLines(string key) =>
            _log.Snapshot().Where(l => l.Text.Contains(key) && l.Text.Contains("shorter than 32 characters")).ToList();

        private void AssertNothingSecretLogged(params string[] values)
        {
            string all = string.Join("\n", _log.Snapshot().Select(l => l.Text));
            foreach (string v in values)
                if (!string.IsNullOrEmpty(v))
                    {
                        var assertedValue = all;
                        TestAssert.WithMessage(() => Assert.DoesNotContain(v, assertedValue), "a secret's value must never be logged");
                    }
        }

        [InlineData("APIToken",31,true)]
        [InlineData("APIToken",32,false)]
        [InlineData("AdminAPIToken",31,true)]
        [InlineData("AdminAPIToken",32,false)]
        [Theory(Timeout = 60000)]
        public void AShortJanusSecretWarnsAtTheBoundary(string key, int length, bool warns)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string api = key == "APIToken" ? Secret(length) : Secret(40);
            string admin = key == "AdminAPIToken" ? Secret(length) : Secret(40);

            StartService(api, admin);

            var hits = StrengthLines(key);
            if (warns)
            {
                {
                    var assertedValue = hits;
                    TestAssert.WithMessage(() => Assert.Single(assertedValue), "one warning that names " + key);
                }
                {
                    var assertedValue = hits[0].Level;
                    TestAssert.WithMessage(() => Assert.Equal(LogLevel.Warning, assertedValue), "a warning, not a refusal: the key already exists");
                }
                {
                    var assertedValue = hits[0].Text;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(length.ToString(), assertedValue), "the secret's length is not given away");
                }
            }
            else
            {
                {
                    var assertedValue = hits;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a 32-character secret meets the rule");
                }
            }
            Assert.True(_log.Snapshot().Any(l => l.Text.EndsWith(" Enabled") && l.Level == LogLevel.Information), "a short existing secret does not stop the service starting");
            AssertNothingSecretLogged(api, admin);
        }

        [Fact(Timeout = 60000)]
        public void AnEmptyJanusSecretKeepsItsExistingMissingFieldsError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartService(string.Empty, Secret(40));

            {
                var assertedValue = StrengthLines("APIToken");
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "empty is not reported as short");
            }
            Assert.Contains(_log.Snapshot(), l => l.Level == LogLevel.Error && l.Text.Contains("missing required fields"));
        }

        [InlineData(31,false)]
        [InlineData(32,true)]
        [Theory(Timeout = 60000)]
        public void TheJoinCapabilitySecretIsRefusedUnder32Characters(int length, bool accepted)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string secret = Secret(length);

            StartService(Secret(40), Secret(40), joinCapEnabled: true, joinCapSecret: secret);

            var lines = _log.Snapshot();
            bool enabledLine = lines.Any(l => l.Text.Contains("join capability enabled"));
            var refused = lines.Where(l => l.Level == LogLevel.Error && l.Text.Contains("JoinCapabilitySecret")
                                           && l.Text.Contains("shorter than 32 characters")).ToList();
            if (accepted)
            {
                Assert.True(enabledLine, "a 32-character secret enables the capability");
                Assert.Empty(refused);
            }
            else
            {
                Assert.False(enabledLine, "a short secret must not switch the capability on");
                {
                    var assertedValue = refused;
                    TestAssert.WithMessage(() => Assert.Single(assertedValue), "one error that names JoinCapabilitySecret");
                }
                {
                    var assertedValue = refused[0].Text;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(length.ToString(), assertedValue), "the secret's length is not given away");
                }
            }
            AssertNothingSecretLogged(secret);
        }

        [Fact(Timeout = 60000)]
        public void AnEmptyJoinCapabilitySecretKeepsItsExistingError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartService(Secret(40), Secret(40), joinCapEnabled: true, joinCapSecret: string.Empty);

            {
                var assertedValue = StrengthLines("JoinCapabilitySecret");
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "empty is not reported as short");
            }
            Assert.Contains(_log.Snapshot(), l => l.Level == LogLevel.Error && l.Text.Contains("JoinCapabilitySecret is empty"));
        }

        [Fact(Timeout = 60000)]
        public void AShortJoinCapabilitySecretIsIgnoredWhileTheCapabilityIsOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartService(Secret(40), Secret(40), joinCapEnabled: false, joinCapSecret: Secret(8));

            {
                var assertedValue = StrengthLines("JoinCapabilitySecret");
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the knob is off, so the secret is not used or judged");
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
