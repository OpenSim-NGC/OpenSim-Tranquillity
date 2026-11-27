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

        [InlineData("ConnectorsEnabled")]
        [InlineData("JoinCapabilityEnabled")]
        [InlineData("VisibilityFeederEnabled")]
        [InlineData("VisibilityEmitEnabled")]
        [InlineData("VisibilityArmingEnabled")]
        [InlineData("PositionFeedEnabled")]
        [Theory(Timeout = 60000)]
        public void OnTheStockPlugin_ASwitchThatIsOn_WarnsNamingTheSwitchAndThePlugin_AndIsSwitchedOff(string name)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            IConfigSource cfg = Config("janus.plugin.audiobridge", name);

            IReadOnlyList<string> off = MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.Equal(new[] { name }, off);
            Assert.False(cfg.Configs["WebRtcVoice"].GetBoolean(name, true), "switched off");
            List<string> warnings = log.Warnings();
            Assert.Single(warnings);
            {
                var assertedValue = warnings[0];
                Assert.Contains(name, assertedValue);
                Assert.Contains("janus.plugin.audiobridge", assertedValue);
            }
        }

        [InlineData("ConnectorsEnabled")]
        [InlineData("JoinCapabilityEnabled")]
        [InlineData("VisibilityFeederEnabled")]
        [InlineData("VisibilityEmitEnabled")]
        [InlineData("VisibilityArmingEnabled")]
        [InlineData("PositionFeedEnabled")]
        [Theory(Timeout = 60000)]
        public void WithNoPluginNameSet_TheDefaultIsTheStockPlugin(string name)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            IConfigSource cfg = Config(null, name);

            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.False(cfg.Configs["WebRtcVoice"].GetBoolean(name, true));
            Assert.Single(log.Warnings());
        }

        [Fact(Timeout = 60000)]
        public void WithNoJanusSection_TheDefaultIsTheStockPlugin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var src = new IniConfigSource();
            src.AddConfig("WebRtcVoice").Set("VisibilityFeederEnabled", "true");

            MixerOnlySwitches.DisableOnStockPlugin(src, new Capture());

            Assert.False(src.Configs["WebRtcVoice"].GetBoolean("VisibilityFeederEnabled", true));
        }

        [Fact(Timeout = 60000)]
        public void OnTheSlvoicePlugin_NothingChanges_AndNothingIsLogged()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            IConfigSource cfg = Config("janus.plugin.slvoice", MixerOnlySwitches.Names.ToArray());

            IReadOnlyList<string> off = MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.Empty(off);
            foreach (string name in MixerOnlySwitches.Names)
                Assert.True(cfg.Configs["WebRtcVoice"].GetBoolean(name, false), name);
            Assert.Empty(log.Warnings());
        }

        [Fact(Timeout = 60000)]
        public void ASwitchThatIsOff_IsNotMentioned()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();

            IReadOnlyList<string> off = MixerOnlySwitches.DisableOnStockPlugin(Config("janus.plugin.audiobridge"), log);

            Assert.Empty(off);
            Assert.Empty(log.Warnings());
        }

        [Fact(Timeout = 60000)]
        public void TheSwitchesThatDoWorkOnTheStockPlugin_AreLeftAlone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfigSource cfg = Config("janus.plugin.audiobridge", "VoiceModerationEnabled", "A2AVoiceEnabled", "StunServers");
            var log = new Capture();

            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.True(cfg.Configs["WebRtcVoice"].GetBoolean("VoiceModerationEnabled", false));
            Assert.True(cfg.Configs["WebRtcVoice"].GetBoolean("A2AVoiceEnabled", false));
            Assert.Empty(log.Warnings());
        }

        [Fact(Timeout = 60000)]
        public void CalledTwice_EachSwitchIsReportedOnce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            IConfigSource cfg = Config("janus.plugin.audiobridge", "JoinCapabilityEnabled", "VisibilityFeederEnabled");

            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);
            MixerOnlySwitches.DisableOnStockPlugin(cfg, log);

            Assert.Equal(2, log.Warnings().Count());
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

        [Fact(Timeout = 60000)]
        public void TheService_OnTheStockPlugin_WarnsAboutArming_AndDoesNotDeclareRooms()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            var logging = new LogCapture(log, LogLevel.Trace);
            try
            {
                IConfigSource cfg = Config("janus.plugin.audiobridge", "VisibilityArmingEnabled", "VisibilityFeederEnabled", "VisibilityEmitEnabled");

                StartService(cfg);

                Assert.True(log.Warnings().Any(w => w.Contains("VisibilityArmingEnabled") && w.Contains("janus.plugin.audiobridge")), string.Join(" | ", log.Warnings()));
                Assert.False(cfg.Configs["WebRtcVoice"].GetBoolean("VisibilityArmingEnabled", true));
                Assert.False(log.Lines.Any(l => l.Text.Contains("vis_authority=true")), "no 'rooms are declared' line");
            }
            finally
            {
                logging.Dispose();
                log.Dispose();
            }
        }

        [Fact(Timeout = 60000)]
        public void TheService_OnTheSlvoicePlugin_KeepsArming()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            var logging = new LogCapture(log, LogLevel.Trace);
            try
            {
                IConfigSource cfg = Config("janus.plugin.slvoice", "VisibilityArmingEnabled", "VisibilityFeederEnabled", "VisibilityEmitEnabled");

                StartService(cfg);

                Assert.DoesNotContain(log.Warnings(), w => w.Contains("VisibilityArmingEnabled"));
                Assert.True(cfg.Configs["WebRtcVoice"].GetBoolean("VisibilityArmingEnabled", false));
                Assert.Contains(log.Lines, l => l.Text.Contains("vis_authority=true"));
            }
            finally
            {
                logging.Dispose();
                log.Dispose();
            }
        }

        [Fact(Timeout = 60000)]
        public void TheService_OnTheStockPlugin_WarnsAboutTheJoinCapability_AndDoesNotMintOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            var logging = new LogCapture(log, LogLevel.Trace);
            try
            {
                IConfigSource cfg = Config("janus.plugin.audiobridge", "JoinCapabilityEnabled");

                StartService(cfg);

                Assert.True(log.Warnings().Any(w => w.Contains("JoinCapabilityEnabled") && w.Contains("janus.plugin.audiobridge")), string.Join(" | ", log.Warnings()));
                Assert.False(cfg.Configs["WebRtcVoice"].GetBoolean("JoinCapabilityEnabled", true));
                Assert.False(log.Lines.Any(l => l.Text.Contains("join capability enabled")), "no 'enabled' line");
            }
            finally
            {
                logging.Dispose();
                log.Dispose();
            }
        }

        [Fact(Timeout = 60000)]
        public void TheService_OnTheSlvoicePlugin_KeepsTheJoinCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var log = new Capture();
            var logging = new LogCapture(log, LogLevel.Trace);
            try
            {
                IConfigSource cfg = Config("janus.plugin.slvoice", "JoinCapabilityEnabled");

                StartService(cfg);

                Assert.DoesNotContain(log.Warnings(), w => w.Contains("JoinCapabilityEnabled"));
                Assert.True(cfg.Configs["WebRtcVoice"].GetBoolean("JoinCapabilityEnabled", false));
                Assert.Contains(log.Lines, l => l.Text.Contains("join capability enabled"));
            }
            finally
            {
                logging.Dispose();
                log.Dispose();
            }
        }
    }
}
