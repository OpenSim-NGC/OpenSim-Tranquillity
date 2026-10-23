/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration tests (see StockJanusRegionTests.cs): the features whose switches say they need the
 * slvoice mixer, switched on against a gateway running the stock AudioBridge. The ini says the stock plugin
 * "ignores them"; these tests record what a region does when an operator turns them on anyway: whether voice
 * still works, and whether the region says anything about the feature having no effect.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(240000)]
#pragma warning restore CS0618
    [NeedsPlugin(JanusPluginKind.Stock)]
    public class StockJanusMixerOnlyFeatureTests
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        private readonly RawJanus _janus = new();

        [OneTimeSetUp]
        public void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        /// <summary>Every WARN and ERROR line the sim writes while a test runs.</summary>
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

        private static void MixerOnlySwitchesOn(IConfig voice, IConfig janus)
        {
            voice.Set("VisibilityFeederEnabled", "true");
            voice.Set("VisibilityEmitEnabled", "true");
            voice.Set("VisibilityArmingEnabled", "true");
            voice.Set("JoinCapabilityEnabled", "true");
            voice.Set("VoiceModerationEnabled", "true");
            janus.Set("JoinCapabilitySecret", "0123456789abcdef0123456789abcdef0123456789abcdef");
        }

        [Test]
        public async Task StockPlugin_PlainJoinAndLeave_WritesNoWarningsOrErrors()
        {
            var problems = new ProblemCollector();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(problems).SetMinimumLevel(LogLevel.Warning));
            try
            {
                using var region = StockRegion.Create();
                region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
                region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
                using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
                using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
                Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
                await region.LogoutAsync(a);
                await region.LogoutAsync(b);
                await Task.Delay(1500);
                string[] lines;
                lock (problems.Lines) lines = problems.Lines.ToArray();
                foreach (string l in lines) TestContext.Out.WriteLine(l.Length > 400 ? l.Substring(0, 400) : l);
                // One line is expected here: the missing-gatekeeper note of a test grid. The room broadcasts the stock
                // plugin sends to every participant (another participant joined, left or finished setting up) are
                // logged at Debug and so do not appear at this level.
                string[] unexpected = lines.Where(l => !l.Contains("no GatekeeperURI")).ToArray();
                Assert.That(unexpected, Is.Empty, "a normal join and leave on the stock plugin should log no other warning or error");
            }
            finally
            {
                LoggerProvider.LoggerFactory = previous;
            }
        }

        [Test]
        public async Task EverySlvoiceSwitchOn_VoiceStillWorksOnTheStockPlugin_AndEachSwitchReportsItCannotWork()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            var problems = new ProblemCollector();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(problems).SetMinimumLevel(LogLevel.Warning));
            try
            {
                using var region = StockRegion.Create(MixerOnlySwitchesOn);
                region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
                region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);

                using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), 440);
                using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), 880);
                Assert.That(a.Answer.Provisioned, Is.True, "provision with the switches on: " + a.Answer.Body);
                Assert.That(b.Answer.Provisioned, Is.True, "second provision: " + b.Answer.Body);
                Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
                await Task.Delay(4000);   // several feeder ticks and a heartbeat

                List<Measurement> heard = await a.Peer.MeasureWindowsAsync(4, 0.5, 440, 880);
                TestContext.Out.WriteLine("A heard: " + Measurement.Describe(heard, 440, 880));

                string[] lines;
                lock (problems.Lines) lines = problems.Lines.ToArray();
                TestContext.Out.WriteLine($"{lines.Length} WARN/ERROR lines while the switches were on:");
                foreach (var g in lines.GroupBy(l => l.Length > 160 ? l.Substring(0, 160) : l).OrderByDescending(g => g.Count()).Take(12))
                    TestContext.Out.WriteLine($"  x{g.Count()} {g.Key}");

                Assert.That(Measurement.MedianShare(heard, 880), Is.GreaterThan(0.8), "audio is unaffected by the switches");
                // Each switch the stock plugin cannot honour says so at start, naming the switch and the plugin, and stays off
                // (so the sender never meets a plugin that rejects its batches).
                foreach (string name in new[] { "JoinCapabilityEnabled", "VisibilityFeederEnabled", "VisibilityEmitEnabled", "VisibilityArmingEnabled" })
                    Assert.That(lines.Any(l => l.StartsWith("Warning") && l.Contains(name) && l.Contains("janus.plugin.audiobridge")), Is.True,
                        $"a warning names {name} and the plugin");
                Assert.That(lines.Any(l => l.Contains("rejected as ProtocolError")), Is.False,
                    "no visibility batch is sent, so none is rejected");
                await region.LogoutAsync(a);
                await region.LogoutAsync(b);
            }
            finally
            {
                LoggerProvider.LoggerFactory = previous;
            }
        }

        // ---- what the stock plugin silently ignores --------------------------------------------

        [Test]
        public async Task ModerationMute_OnTheStockPlugin_UsesTheAudioBridgesOwnMute()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            StockJanusEnvironment.IgnoreUnlessAudioBridge();
            using var region = StockRegion.Create((voice, janus) => voice.Set("VoiceModerationEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);   // Alice owns the parcel
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), 440);
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), 880);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
            await Task.Delay(1500);   // let the first frames flow
            List<Measurement> before = await a.Peer.MeasureWindowsAsync(4, 0.5, 440, 880);
            Assert.That(Measurement.MedianShare(before, 880), Is.GreaterThan(0.8), "precondition: Alice hears Bob");

            CapAnswer muted = await region.ModerateAsync(Alice, new OpenMetaverse.StructuredData.OSDMap
            {
                ["operand"] = OpenMetaverse.StructuredData.OSD.FromString("mute"),
                ["agent_id"] = OpenMetaverse.StructuredData.OSD.FromUUID(Bob),
            });
            await Task.Delay(1000);
            List<Measurement> heard = await a.Peer.MeasureWindowsAsync(4, 0.5, 440, 880);
            TestContext.Out.WriteLine($"moderation answered HTTP {muted.Status}; Alice then heard: " + Measurement.Describe(heard, 440, 880));

            Assert.That(muted.Status, Is.EqualTo(200));
            Assert.That(Measurement.MedianRms(heard), Is.LessThan(0.005), "the muted avatar's tone is no longer heard");

            CapAnswer unmuted = await region.ModerateAsync(Alice, new OpenMetaverse.StructuredData.OSDMap
            {
                ["operand"] = OpenMetaverse.StructuredData.OSD.FromString("unmute"),
                ["agent_id"] = OpenMetaverse.StructuredData.OSD.FromUUID(Bob),
            });
            await Task.Delay(1000);
            List<Measurement> after = await a.Peer.MeasureWindowsAsync(4, 0.5, 440, 880);
            Assert.That(unmuted.Status, Is.EqualTo(200));
            Assert.That(Measurement.MedianShare(after, 880), Is.GreaterThan(0.8), "and after the unmute Bob is heard again");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [Test]
        public async Task ModerationMuteAll_OnTheStockPlugin_AlsoMutesSomeoneWhoJoinsAfterwards()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            StockJanusEnvironment.IgnoreUnlessAudioBridge();
            using var region = StockRegion.Create((voice, janus) => voice.Set("VoiceModerationEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), 440);

            CapAnswer muted = await region.ModerateAsync(Alice, new OpenMetaverse.StructuredData.OSDMap
            {
                ["operand"] = OpenMetaverse.StructuredData.OSD.FromString("mute_all"),
            });
            Assert.That(muted.Status, Is.EqualTo(200));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), 880);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
            await Task.Delay(1500);
            List<Measurement> heard = await a.Peer.MeasureWindowsAsync(4, 0.5, 440, 880);
            TestContext.Out.WriteLine("Alice heard: " + Measurement.Describe(heard, 440, 880));

            Assert.That(Measurement.MedianRms(heard), Is.LessThan(0.005), "Bob joined after mute_all and is not heard");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [Test]
        public async Task JoinCapability_OnTheStockPlugin_IsNotEnforced()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create(MixerOnlySwitchesOn);
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            Assert.That(a.Answer.Provisioned, Is.True, "the join is accepted: " + a.Answer.Body);

            // The stock plugin has nothing that checks a capability, so the switch is reported and stays off (the
            // unit tests show no capability is minted). Anything holding the gateway's API secret can join the same
            // room with none; the secret is the only protection the stock gateway has.
            using var stranger = await AiortcPeer.StartAsync();
            string answer = await _janus.JoinDirectAsync(a.Answer.Room, "stranger", stranger.Offer);

            Assert.That(answer, Is.Not.Null, "a join with no capability is accepted by the stock plugin");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
            await region.LogoutAsync(a);
        }
    }
}
