/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * On Janus's stock AudioBridge the visibility feeder, its emit and arming switches, and the voice connectors cannot
 * work. A region started with one of them on logs a warning naming the switch and the plugin, and runs with it off.
 * (The join capability switch is read by the Janus service and is covered in WebRtcJanusService.Tests.)
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace osWebRtcVoice.Tests
{
    public class StockPluginSwitchTests : IAsyncLifetime
        {
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
            public List<string> Warnings()
            {
                lock (Lines)
                    return Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
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

        private static IConfigSource Config(string plugin, params string[] on)
        {
            var src = new IniConfigSource();
            IConfig voice = src.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            foreach (string name in on)
                voice.Set(name, "true");
            IConfig janus = src.AddConfig("JanusWebRtcVoice");
            if (plugin is not null)
                janus.Set("PluginName", plugin);
            return src;
        }

        private static bool Field(WebRtcVoiceRegionModule module, string name)
            => (bool)typeof(WebRtcVoiceRegionModule).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module);

        [Fact(Timeout = 60000)]
        public void TheRegion_OnTheStockPlugin_RunsWithThePositionFeedOff_AndSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var module = new WebRtcVoiceRegionModule();

            module.Initialise(Config("janus.plugin.audiobridge", "VisibilityFeederEnabled", "VisibilityEmitEnabled", "PositionFeedEnabled"));

            object settings = typeof(WebRtcVoiceRegionModule).GetField("m_PositionSettings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module);
            {
                var assertedValue = settings;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the position feed stays off");
            }
            List<string> warnings = _log.Warnings();
            Assert.True(warnings.Any(w => w.Contains("PositionFeedEnabled") && w.Contains("janus.plugin.audiobridge")), string.Join(" | ", warnings));
        }

        [InlineData("VisibilityFeederEnabled","m_VisibilityFeederEnabled")]
        [InlineData("VisibilityEmitEnabled","m_VisibilityEmitEnabled")]
        [InlineData("VisibilityArmingEnabled","m_VisibilityArmingEnabled")]
        [Theory(Timeout = 60000)]
        public void TheRegion_OnTheStockPlugin_RunsWithTheSwitchOff_AndSaysSo(string key, string field)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var module = new WebRtcVoiceRegionModule();

            module.Initialise(Config("janus.plugin.audiobridge", key));

            Assert.False(Field(module, field));
            List<string> warnings = _log.Warnings();
            Assert.True(warnings.Any(w => w.Contains(key) && w.Contains("janus.plugin.audiobridge")), string.Join(" | ", warnings));
        }

        [Fact(Timeout = 60000)]
        public void TheRegion_WithNoPluginNameSet_IsOnTheStockPlugin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var module = new WebRtcVoiceRegionModule();

            module.Initialise(Config(null, "VisibilityFeederEnabled"));

            Assert.False(Field(module, "m_VisibilityFeederEnabled"));
            Assert.Contains(_log.Warnings(), w => w.Contains("VisibilityFeederEnabled"));
        }

        [Fact(Timeout = 60000)]
        public void TheRegion_OnTheStockPlugin_KnowsItIsOnTheStockPlugin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var stock = new WebRtcVoiceRegionModule();
            stock.Initialise(Config("janus.plugin.audiobridge"));

            Assert.True(Field(stock, "m_stockPlugin"));
        }

        [Fact(Timeout = 60000)]
        public void TheConnectorModule_OnTheStockPlugin_RegistersNothing_AndSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var cfg = Config("janus.plugin.audiobridge", "ConnectorsEnabled");
            IConfig rec = cfg.AddConfig("VoiceConnector.Recorder");
            rec.Set("Enabled", "true");
            rec.Set("NpcFirstName", "Recorder");
            rec.Set("NpcLastName", "NPC");
            rec.Set("Position", "<128,128,25>");
            Scene scene = new SceneHelpers().SetupScene();
            var module = new VoiceConnectorModule();

            module.Initialise(cfg);
            module.AddRegion(scene);

            Assert.Null(scene.RequestModuleInterface<IVoiceConnectorRegistry>());
            Assert.True(_log.Warnings().Any(w => w.Contains("ConnectorsEnabled") && w.Contains("janus.plugin.audiobridge")), string.Join(" | ", _log.Warnings()));
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
