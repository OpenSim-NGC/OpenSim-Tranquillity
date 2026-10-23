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
    [TestFixture]
    [NonParallelizable]
    public class JanusSecretStrengthTests
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
        private ILoggerFactory _previous;

        [SetUp]
        public void SetUp()
        {
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
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
                    Assert.That(all, Does.Not.Contain(v), "a secret's value must never be logged");
        }

        [TestCase("APIToken", 31, true)]
        [TestCase("APIToken", 32, false)]
        [TestCase("AdminAPIToken", 31, true)]
        [TestCase("AdminAPIToken", 32, false)]
        public void AShortJanusSecretWarnsAtTheBoundary(string key, int length, bool warns)
        {
            string api = key == "APIToken" ? Secret(length) : Secret(40);
            string admin = key == "AdminAPIToken" ? Secret(length) : Secret(40);

            StartService(api, admin);

            var hits = StrengthLines(key);
            if (warns)
            {
                Assert.That(hits, Has.Count.EqualTo(1), "one warning that names " + key);
                Assert.That(hits[0].Level, Is.EqualTo(LogLevel.Warning), "a warning, not a refusal: the key already exists");
                Assert.That(hits[0].Text, Does.Not.Contain(length.ToString()), "the secret's length is not given away");
            }
            else
            {
                Assert.That(hits, Is.Empty, "a 32-character secret meets the rule");
            }
            Assert.That(_log.Snapshot().Any(l => l.Text.EndsWith(" Enabled") && l.Level == LogLevel.Information), Is.True,
                "a short existing secret does not stop the service starting");
            AssertNothingSecretLogged(api, admin);
        }

        [Test]
        public void AnEmptyJanusSecretKeepsItsExistingMissingFieldsError()
        {
            StartService(string.Empty, Secret(40));

            Assert.That(StrengthLines("APIToken"), Is.Empty, "empty is not reported as short");
            Assert.That(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains("missing required fields")), Is.True);
        }

        [TestCase(31, false)]
        [TestCase(32, true)]
        public void TheJoinCapabilitySecretIsRefusedUnder32Characters(int length, bool accepted)
        {
            string secret = Secret(length);

            StartService(Secret(40), Secret(40), joinCapEnabled: true, joinCapSecret: secret);

            var lines = _log.Snapshot();
            bool enabledLine = lines.Any(l => l.Text.Contains("join capability enabled"));
            var refused = lines.Where(l => l.Level == LogLevel.Error && l.Text.Contains("JoinCapabilitySecret")
                                           && l.Text.Contains("shorter than 32 characters")).ToList();
            if (accepted)
            {
                Assert.That(enabledLine, Is.True, "a 32-character secret enables the capability");
                Assert.That(refused, Is.Empty);
            }
            else
            {
                Assert.That(enabledLine, Is.False, "a short secret must not switch the capability on");
                Assert.That(refused, Has.Count.EqualTo(1), "one error that names JoinCapabilitySecret");
                Assert.That(refused[0].Text, Does.Not.Contain(length.ToString()), "the secret's length is not given away");
            }
            AssertNothingSecretLogged(secret);
        }

        [Test]
        public void AnEmptyJoinCapabilitySecretKeepsItsExistingError()
        {
            StartService(Secret(40), Secret(40), joinCapEnabled: true, joinCapSecret: string.Empty);

            Assert.That(StrengthLines("JoinCapabilitySecret"), Is.Empty, "empty is not reported as short");
            Assert.That(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains("JoinCapabilitySecret is empty")), Is.True);
        }

        [Test]
        public void AShortJoinCapabilitySecretIsIgnoredWhileTheCapabilityIsOff()
        {
            StartService(Secret(40), Secret(40), joinCapEnabled: false, joinCapSecret: Secret(8));

            Assert.That(StrengthLines("JoinCapabilitySecret"), Is.Empty, "the knob is off, so the secret is not used or judged");
        }
    }
}
