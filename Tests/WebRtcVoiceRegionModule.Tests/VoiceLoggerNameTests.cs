/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Every voice class logs under the category prefix osWebRtcVoice, so one line of logging configuration quiets all of
 * them. The first test finds the voice loggers by reflection and logs through each; the second reads the line the
 * README gives from configuration, the way the region server does, and checks what it lets through.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class VoiceLoggerNameTests
    {
        public const string Prefix = "osWebRtcVoice";

        // One type from each voice assembly: the Janus service, the shared voice library, the region module and the
        // service module.
        private static readonly Assembly[] VoiceAssemblies =
        {
            typeof(JanusSession).Assembly,
            typeof(VoiceViewerSession).Assembly,
            typeof(WebRtcVoiceRegionModule).Assembly,
            typeof(WebRtcVoiceServiceModule).Assembly,
        };

        private static IEnumerable<Type> Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null); }
        }

        [Test]
        public void EveryVoiceLogger_LogsUnderTheOneNamePrefix()
        {
            using LogCapture log = new LogCapture();
            List<string> found = new();

            foreach (Assembly assembly in VoiceAssemblies.Distinct())
                foreach (Type type in Types(assembly))
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (field.FieldType != typeof(ILogger) || type.ContainsGenericParameters)
                            continue;
                        ILogger logger = (ILogger)field.GetValue(null);
                        int before = log.Lines.Count;
                        logger.LogCritical("probe {Type}.{Field}", type.FullName, field.Name);
                        List<LogCapture.Line> added = log.Lines.Skip(before).ToList();
                        Assert.That(added.Count, Is.EqualTo(1), $"{type.FullName}.{field.Name} wrote its probe");
                        found.Add($"{type.FullName}.{field.Name} -> {added[0].Category}");
                        Assert.That(added[0].Category, Does.StartWith(Prefix + "."), $"{type.FullName}.{field.Name}");
                    }

            TestContext.Out.WriteLine($"{found.Count} voice loggers, all under {Prefix}:\n" + string.Join("\n", found));
            Assert.That(found.Count, Is.GreaterThanOrEqualTo(10), "the search found the voice loggers");
        }

        [Test]
        public void TheReadmesLoggingLine_RaisesVoiceToWarning_AndLeavesOtherCategoriesAlone()
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:" + Prefix] = "Warning",
            }).Build();
            using LogCapture log = new LogCapture(LogLevel.Trace, b => b.AddConfiguration(config.GetSection("Logging")));
            ILogger voice = (ILogger)typeof(JanusSession).GetField("m_log", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            ILogger other = LoggerProvider.CreateLogger("OpenSim.Region.Framework.Scenes.Scene");

            voice.LogInformation("voice information");
            voice.LogDebug("voice debug");
            voice.LogWarning("voice warning");
            voice.LogError("voice error");
            other.LogInformation("other information");

            Assert.That(log.Lines.Select(l => l.Text), Is.EqualTo(new[] { "voice warning", "voice error", "other information" }));
        }
    }
}
