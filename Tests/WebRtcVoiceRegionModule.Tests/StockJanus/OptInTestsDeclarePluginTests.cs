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
using Xunit;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    public class OptInTestsDeclarePluginTests
    {
        private static IEnumerable<MethodInfo> OptInTests()
            => typeof(OptInTestsDeclarePluginTests).Assembly.GetTypes()
                .Where(t => t.Name.StartsWith("StockJanus", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Where(m => m.GetCustomAttributes<FactAttribute>().Any());

        [Fact(Timeout = 60000)]
        public void EveryOptInTestDeclaresThePluginItNeeds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<string> bare = OptInTests()
                .Where(m => m.GetCustomAttribute<NeedsPluginAttribute>() is null && m.DeclaringType.GetCustomAttribute<NeedsPluginAttribute>() is null)
                .Select(m => m.DeclaringType.Name + "." + m.Name).ToList();

            Assert.True(OptInTests().Count() > 20, "the scan found the opt-in tests");
            {
                var assertedValue = bare;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "declare [NeedsPlugin(...)] on these opt-in tests");
            }
        }

        private static string SkipReason(JanusPluginKind needs, string url, string plugin)
        {
            string reason = null;
            WithGateway(url, plugin, () => reason = new NeedsPluginAttribute(needs).GetSkipReason());
            return reason;
        }

        private static void WithGateway(string url, string plugin, Action action)
        {
            string oldUrl = Environment.GetEnvironmentVariable(StockJanusEnvironment.UrlVariable);
            string oldPlugin = Environment.GetEnvironmentVariable("WEBRTC_JANUS_TEST_PLUGIN");
            try
            {
                Environment.SetEnvironmentVariable(StockJanusEnvironment.UrlVariable, url);
                Environment.SetEnvironmentVariable("WEBRTC_JANUS_TEST_PLUGIN", plugin);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(StockJanusEnvironment.UrlVariable, oldUrl);
                Environment.SetEnvironmentVariable("WEBRTC_JANUS_TEST_PLUGIN", oldPlugin);
            }
        }

        [Theory(Timeout = 60000)]
        [InlineData(JanusPluginKind.Stock, "janus.plugin.slvoice")]
        [InlineData(JanusPluginKind.Mixer, "janus.plugin.audiobridge")]
        public void GatewaySkipReason_WithAnotherPlugin_RefusesFixtureSetup(JanusPluginKind needs, string plugin)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WithGateway("http://localhost:1/janus", plugin, () =>
            {
                string reason = StockJanusEnvironment.GetConfigurationSkipReason(needs);
                Assert.NotNull(reason);
                Assert.Contains(plugin, reason);
            });
        }

        [InlineData(JanusPluginKind.Stock,"janus.plugin.audiobridge",false)]
        [InlineData(JanusPluginKind.Stock,"janus.plugin.slvoice",true)]
        [InlineData(JanusPluginKind.Mixer,"janus.plugin.slvoice",false)]
        [InlineData(JanusPluginKind.Mixer,"janus.plugin.audiobridge",true)]
        [InlineData(JanusPluginKind.Any,"janus.plugin.audiobridge",false)]
        [InlineData(JanusPluginKind.Any,"janus.plugin.slvoice",false)]
        [Theory(Timeout = 60000)]
        public void AStockOnlyTestSkipsOnTheMixer_AMixerOnlyTestSkipsOnTheStockPlugin_AndTheReasonSaysWhy(JanusPluginKind needs, string plugin, bool skipped)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string reason = SkipReason(needs, "http://localhost:1/janus", plugin);

            Assert.Equal(skipped, reason is not null);
            if (skipped)
            {
                Assert.Contains("needs the", reason);
                Assert.Contains(plugin, reason);
            }
        }

        [Fact(Timeout = 60000)]
        public void WithNoGatewayNamed_TheAttributeDoesNothing_AndTheFixturesOwnOptInSkipApplies()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string reason = SkipReason(JanusPluginKind.Mixer, null, "janus.plugin.audiobridge");

            Assert.Null(reason);
        }
    }
}
