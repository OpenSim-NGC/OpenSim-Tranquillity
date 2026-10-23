/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Every opt-in Janus test says which plugin it needs (NeedsPlugin), so a run against the stock AudioBridge and a run
 * against the slvoice mixer each end with every test passed or skipped with its reason. These tests run in every
 * ordinary run (they need no gateway): one reads the attribute off every opt-in test, the others check how it decides.
 */
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Internal;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class OptInTestsDeclarePluginTests
    {
        private static IEnumerable<MethodInfo> OptInTests()
            => typeof(OptInTestsDeclarePluginTests).Assembly.GetTypes()
                .Where(t => t.Name.StartsWith("StockJanus", StringComparison.Ordinal) && t.GetCustomAttributes<TestFixtureAttribute>().Any())
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Where(m => m.GetCustomAttributes<TestAttribute>().Any());

        [Test]
        public void EveryOptInTestDeclaresThePluginItNeeds()
        {
            List<string> bare = OptInTests()
                .Where(m => m.GetCustomAttribute<NeedsPluginAttribute>() is null && m.DeclaringType.GetCustomAttribute<NeedsPluginAttribute>() is null)
                .Select(m => m.DeclaringType.Name + "." + m.Name).ToList();

            Assert.That(OptInTests().Count(), Is.GreaterThan(20), "the scan found the opt-in tests");
            Assert.That(bare, Is.Empty, "declare [NeedsPlugin(...)] on these opt-in tests");
        }

        private static Test Skipped(JanusPluginKind needs, string url, string plugin)
        {
            string oldUrl = Environment.GetEnvironmentVariable(StockJanusEnvironment.UrlVariable);
            string oldPlugin = Environment.GetEnvironmentVariable("WEBRTC_JANUS_TEST_PLUGIN");
            try
            {
                Environment.SetEnvironmentVariable(StockJanusEnvironment.UrlVariable, url);
                Environment.SetEnvironmentVariable("WEBRTC_JANUS_TEST_PLUGIN", plugin);
                Test test = new TestMethod(new MethodWrapperFor(typeof(OptInTestsDeclarePluginTests), nameof(EveryOptInTestDeclaresThePluginItNeeds)));
                new NeedsPluginAttribute(needs).ApplyToTest(test);
                return test;
            }
            finally
            {
                Environment.SetEnvironmentVariable(StockJanusEnvironment.UrlVariable, oldUrl);
                Environment.SetEnvironmentVariable("WEBRTC_JANUS_TEST_PLUGIN", oldPlugin);
            }
        }

        // A MethodInfo wrapper NUnit's TestMethod accepts, for building a Test to apply the attribute to.
        private sealed class MethodWrapperFor : NUnit.Framework.Internal.MethodWrapper
        {
            public MethodWrapperFor(Type type, string name) : base(type, type.GetMethod(name)) { }
        }

        [TestCase(JanusPluginKind.Stock, "janus.plugin.audiobridge", false)]
        [TestCase(JanusPluginKind.Stock, "janus.plugin.slvoice", true)]
        [TestCase(JanusPluginKind.Mixer, "janus.plugin.slvoice", false)]
        [TestCase(JanusPluginKind.Mixer, "janus.plugin.audiobridge", true)]
        [TestCase(JanusPluginKind.Any, "janus.plugin.audiobridge", false)]
        [TestCase(JanusPluginKind.Any, "janus.plugin.slvoice", false)]
        public void AStockOnlyTestSkipsOnTheMixer_AMixerOnlyTestSkipsOnTheStockPlugin_AndTheReasonSaysWhy(JanusPluginKind needs, string plugin, bool skipped)
        {
            Test t = Skipped(needs, "http://localhost:1/janus", plugin);

            Assert.That(t.RunState == NUnit.Framework.Interfaces.RunState.Ignored, Is.EqualTo(skipped));
            if (skipped)
                Assert.That((string)t.Properties.Get(PropertyNames.SkipReason), Does.Contain("needs the").And.Contain(plugin));
        }

        [Test]
        public void WithNoGatewayNamed_TheAttributeDoesNothing_AndTheFixturesOwnOptInSkipApplies()
        {
            Test t = Skipped(JanusPluginKind.Mixer, null, "janus.plugin.audiobridge");

            Assert.That(t.RunState, Is.Not.EqualTo(NUnit.Framework.Interfaces.RunState.Ignored));
        }
    }
}
