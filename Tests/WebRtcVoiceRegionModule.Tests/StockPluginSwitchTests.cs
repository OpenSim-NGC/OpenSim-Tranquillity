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
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class StockPluginSwitchTests
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

        [Test]
        public void TheRegion_OnTheStockPlugin_RunsWithThePositionFeedOff_AndSaysSo()
        {
            var module = new WebRtcVoiceRegionModule();

            module.Initialise(Config("janus.plugin.audiobridge", "VisibilityFeederEnabled", "VisibilityEmitEnabled", "PositionFeedEnabled"));

            object settings = typeof(WebRtcVoiceRegionModule).GetField("m_PositionSettings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module);
            Assert.That(settings, Is.Null, "the position feed stays off");
            List<string> warnings = _log.Warnings();
            Assert.That(warnings.Any(w => w.Contains("PositionFeedEnabled") && w.Contains("janus.plugin.audiobridge")), Is.True, string.Join(" | ", warnings));
        }

        [TestCase("VisibilityFeederEnabled", "m_VisibilityFeederEnabled")]
        [TestCase("VisibilityEmitEnabled", "m_VisibilityEmitEnabled")]
        [TestCase("VisibilityArmingEnabled", "m_VisibilityArmingEnabled")]
        public void TheRegion_OnTheStockPlugin_RunsWithTheSwitchOff_AndSaysSo(string key, string field)
        {
            var module = new WebRtcVoiceRegionModule();

            module.Initialise(Config("janus.plugin.audiobridge", key));

            Assert.That(Field(module, field), Is.False);
            List<string> warnings = _log.Warnings();
            Assert.That(warnings.Any(w => w.Contains(key) && w.Contains("janus.plugin.audiobridge")), Is.True, string.Join(" | ", warnings));
        }

        [Test]
        public void TheRegion_WithNoPluginNameSet_IsOnTheStockPlugin()
        {
            var module = new WebRtcVoiceRegionModule();

            module.Initialise(Config(null, "VisibilityFeederEnabled"));

            Assert.That(Field(module, "m_VisibilityFeederEnabled"), Is.False);
            Assert.That(_log.Warnings().Any(w => w.Contains("VisibilityFeederEnabled")), Is.True);
        }

        [Test]
        public void TheRegion_OnTheStockPlugin_KnowsItIsOnTheStockPlugin()
        {
            var stock = new WebRtcVoiceRegionModule();
            stock.Initialise(Config("janus.plugin.audiobridge"));

            Assert.That(Field(stock, "m_stockPlugin"), Is.True);
        }

        [Test]
        public void TheConnectorModule_OnTheStockPlugin_RegistersNothing_AndSaysSo()
        {
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

            Assert.That(scene.RequestModuleInterface<IVoiceConnectorRegistry>(), Is.Null);
            Assert.That(_log.Warnings().Any(w => w.Contains("ConnectorsEnabled") && w.Contains("janus.plugin.audiobridge")), Is.True,
                string.Join(" | ", _log.Warnings()));
        }
    }
}
