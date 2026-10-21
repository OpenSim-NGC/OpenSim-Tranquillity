/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Switches that need the slvoice mixer, turned on against Janus's stock AudioBridge, which cannot do what they
 * ask. Each one that is on logs a warning naming the switch and the plugin, and is switched off. With the slvoice
 * plugin nothing changes, and a switch that is off is not mentioned.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // the service tests swap the process-wide logger factory
    public class MixerOnlySwitchesTests
    {
        internal sealed class Capture : ILoggerProvider, ILogger
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
            public List<string> Warnings()
            {
                lock (Lines)
                    return Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
            }
        }

        private static IConfigSource Config(string plugin, params string[] switchesOn)
        {
            var src = new IniConfigSource();
            IConfig voice = src.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            foreach (string s in switchesOn)
                voice.Set(s, "true");
            IConfig janus = src.AddConfig("JanusWebRtcVoice");
            if (plugin is not null)
                janus.Set("PluginName", plugin);
            return src;
        }

        [TestCase("ConnectorsEnabled")]
        [TestCase("JoinCapabilityEnabled")]
        [TestCase("VisibilityFeederEnabled")]
        [TestCase("VisibilityEmitEnabled")]
        [TestCase("VisibilityArmingEnabled")]
        [TestCase("PositionFeedEnabled")]
        public void OnTheStockPlugin_ASwitchThatIsOn_WarnsNamingTheSwitchAndThePlugin_AndIsSwitchedOff(string name)
        {
            var log = new Capture();
            IConfigSource cfg = Config("janus.plugin.audiobridge", name);

            IReadOnlyList<string> off = MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.That(off, Is.EqualTo(new[] { name }));
            Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean(name, true), Is.False, "switched off");
            List<string> warnings = log.Warnings();
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain(name).And.Contain("janus.plugin.audiobridge"));
        }

        [TestCase("ConnectorsEnabled")]
        [TestCase("JoinCapabilityEnabled")]
        [TestCase("VisibilityFeederEnabled")]
        [TestCase("VisibilityEmitEnabled")]
        [TestCase("VisibilityArmingEnabled")]
        [TestCase("PositionFeedEnabled")]
        public void WithNoPluginNameSet_TheDefaultIsTheStockPlugin(string name)
        {
            var log = new Capture();
            IConfigSource cfg = Config(null, name);

            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean(name, true), Is.False);
            Assert.That(log.Warnings(), Has.Count.EqualTo(1));
        }

        [Test]
        public void WithNoJanusSection_TheDefaultIsTheStockPlugin()
        {
            var src = new IniConfigSource();
            src.AddConfig("WebRtcVoice").Set("VisibilityFeederEnabled", "true");

            MixerOnlySwitches.DisableOnStockPlugin(src, new Capture());

            Assert.That(src.Configs["WebRtcVoice"].GetBoolean("VisibilityFeederEnabled", true), Is.False);
        }

        [Test]
        public void OnTheSlvoicePlugin_NothingChanges_AndNothingIsLogged()
        {
            var log = new Capture();
            IConfigSource cfg = Config("janus.plugin.slvoice", MixerOnlySwitches.Names.ToArray());

            IReadOnlyList<string> off = MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.That(off, Is.Empty);
            foreach (string name in MixerOnlySwitches.Names)
                Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean(name, false), Is.True, name);
            Assert.That(log.Warnings(), Is.Empty);
        }

        [Test]
        public void ASwitchThatIsOff_IsNotMentioned()
        {
            var log = new Capture();

            IReadOnlyList<string> off = MixerOnlySwitches.DisableOnStockPlugin(Config("janus.plugin.audiobridge"), log);

            Assert.That(off, Is.Empty);
            Assert.That(log.Warnings(), Is.Empty);
        }

        [Test]
        public void TheSwitchesThatDoWorkOnTheStockPlugin_AreLeftAlone()
        {
            IConfigSource cfg = Config("janus.plugin.audiobridge", "VoiceModerationEnabled", "A2AVoiceEnabled", "StunServers");
            var log = new Capture();

            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean("VoiceModerationEnabled", false), Is.True);
            Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean("A2AVoiceEnabled", false), Is.True);
            Assert.That(log.Warnings(), Is.Empty);
        }

        [Test]
        public void CalledTwice_EachSwitchIsReportedOnce()
        {
            var log = new Capture();
            IConfigSource cfg = Config("janus.plugin.audiobridge", "JoinCapabilityEnabled", "VisibilityFeederEnabled");

            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);
            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.That(log.Warnings(), Has.Count.EqualTo(2));
        }

        // ---- the Janus service applies it to the room declaration ------------------------------------------

        private static WebRtcJanusService StartService(IConfigSource cfg)
        {
            IConfig janus = cfg.Configs["JanusWebRtcVoice"];
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", "tok");
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", "tok");
            janus.Set("JoinCapabilitySecret", "0123456789abcdef0123456789abcdef0123456789abcdef");
            return new WebRtcJanusService(cfg, () => new FakeJanusHandler(new FakeJanusGateway()));
        }

        [Test]
        public void TheService_OnTheStockPlugin_WarnsAboutArming_AndDoesNotDeclareRooms()
        {
            var log = new Capture();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));
            try
            {
                IConfigSource cfg = Config("janus.plugin.audiobridge", "VisibilityArmingEnabled", "VisibilityFeederEnabled", "VisibilityEmitEnabled");

                StartService(cfg);

                Assert.That(log.Warnings().Any(w => w.Contains("VisibilityArmingEnabled") && w.Contains("janus.plugin.audiobridge")), Is.True,
                    string.Join(" | ", log.Warnings()));
                Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean("VisibilityArmingEnabled", true), Is.False);
                Assert.That(log.Lines.Any(l => l.Text.Contains("vis_authority=true")), Is.False, "no 'rooms are declared' line");
            }
            finally
            {
                ILoggerFactory ours = LoggerProvider.LoggerFactory;
                LoggerProvider.LoggerFactory = previous;
                ours.Dispose();
                log.Dispose();
            }
        }

        [Test]
        public void TheService_OnTheSlvoicePlugin_KeepsArming()
        {
            var log = new Capture();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));
            try
            {
                IConfigSource cfg = Config("janus.plugin.slvoice", "VisibilityArmingEnabled", "VisibilityFeederEnabled", "VisibilityEmitEnabled");

                StartService(cfg);

                Assert.That(log.Warnings().Any(w => w.Contains("VisibilityArmingEnabled")), Is.False);
                Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean("VisibilityArmingEnabled", false), Is.True);
                Assert.That(log.Lines.Any(l => l.Text.Contains("vis_authority=true")), Is.True);
            }
            finally
            {
                ILoggerFactory ours = LoggerProvider.LoggerFactory;
                LoggerProvider.LoggerFactory = previous;
                ours.Dispose();
                log.Dispose();
            }
        }

        [Test]
        public void TheService_OnTheStockPlugin_WarnsAboutTheJoinCapability_AndDoesNotMintOne()
        {
            var log = new Capture();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));
            try
            {
                IConfigSource cfg = Config("janus.plugin.audiobridge", "JoinCapabilityEnabled");

                StartService(cfg);

                Assert.That(log.Warnings().Any(w => w.Contains("JoinCapabilityEnabled") && w.Contains("janus.plugin.audiobridge")), Is.True,
                    string.Join(" | ", log.Warnings()));
                Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean("JoinCapabilityEnabled", true), Is.False);
                Assert.That(log.Lines.Any(l => l.Text.Contains("join capability enabled")), Is.False, "no 'enabled' line");
            }
            finally
            {
                ILoggerFactory ours = LoggerProvider.LoggerFactory;
                LoggerProvider.LoggerFactory = previous;
                ours.Dispose();
                log.Dispose();
            }
        }

        [Test]
        public void TheService_OnTheSlvoicePlugin_KeepsTheJoinCapability()
        {
            var log = new Capture();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));
            try
            {
                IConfigSource cfg = Config("janus.plugin.slvoice", "JoinCapabilityEnabled");

                StartService(cfg);

                Assert.That(log.Warnings().Any(w => w.Contains("JoinCapabilityEnabled")), Is.False);
                Assert.That(cfg.Configs["WebRtcVoice"].GetBoolean("JoinCapabilityEnabled", false), Is.True);
                Assert.That(log.Lines.Any(l => l.Text.Contains("join capability enabled")), Is.True);
            }
            finally
            {
                ILoggerFactory ours = LoggerProvider.LoggerFactory;
                LoggerProvider.LoggerFactory = previous;
                ours.Dispose();
                log.Dispose();
            }
        }
    }
}
