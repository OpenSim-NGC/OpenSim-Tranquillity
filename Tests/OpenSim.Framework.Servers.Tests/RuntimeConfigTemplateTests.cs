/*
 * Copyright (c) 2026, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Text.Json;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Framework.Servers.Tests
{
    /// <summary>
    /// The SDK writes a project's runtimeconfig.template.json inside "runtimeOptions" of the published
    /// &lt;server&gt;.runtimeconfig.json. A template that wraps its settings in its own "runtimeOptions" ends up nested
    /// one level too deep, where the runtime never reads it: the servers then ran workstation GC. Each server's
    /// template must hold its settings at the top level, and leaves the thread pool at the .NET defaults.
    /// </summary>
    public class RuntimeConfigTemplateTests
    {
        private static string RepositoryRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Tranquillity.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.True(dir != null, "Tranquillity.sln not found above " + AppContext.BaseDirectory);
            return dir!;
        }

        [Xunit.Theory]
        [InlineData("OpenSim.Server.RegionServer")]
        [InlineData("OpenSim.Server.GridServer")]
        [InlineData("OpenSim.Server.MoneyServer")]
        public void TemplateSettingsAreWhereTheRuntimeReadsThem(string server)
        {
            string path = Path.Combine(RepositoryRoot(), "Source", server, "runtimeconfig.template.json");
            using JsonDocument template = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = template.RootElement;

            Assert.False(root.TryGetProperty("runtimeOptions", out _),
                path + " wraps its settings in \"runtimeOptions\"; the SDK adds that level itself");
            Assert.True(root.TryGetProperty("configProperties", out JsonElement properties),
                path + " has no top-level \"configProperties\"");
            Assert.True(properties.GetProperty("System.GC.Server").GetBoolean());
            Assert.True(properties.GetProperty("System.GC.Concurrent").GetBoolean());
            // The thread pool keeps the .NET defaults (a minimum of one worker thread per processor). A minimum fixed
            // here would apply to every server and stop the program from changing it (Docs/BUILDING.md).
            Assert.False(properties.TryGetProperty("System.Threading.ThreadPool.MinThreads", out _),
                path + " fixes the thread pool minimum");
            Assert.False(properties.TryGetProperty("System.Threading.ThreadPool.MaxThreads", out _),
                path + " fixes the thread pool maximum");
            Assert.True(properties.GetProperty("System.Runtime.TieredCompilation").GetBoolean());
        }
    }
}
