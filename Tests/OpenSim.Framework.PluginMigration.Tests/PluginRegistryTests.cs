/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Framework.PluginMigration.Tests
{
    /// <summary>
    /// Test PluginRegistry functionality for Phase 2 migration.
    /// Tests config-based plugin registration without XML manifests.
    /// </summary>
    public class PluginRegistryTests
    {
        private readonly PluginRegistry m_registry;

        // Use entries in the form "relative/provider/path|/Extension/Path|Id|Full.Type.Name"
        // for intentional provider-only registrations that should not fail parity checks.
        private static readonly HashSet<string> s_allowedProviderOnlyTriples = new HashSet<string>(StringComparer.Ordinal)
        {
        };

        // Use entries in the form "relative/path/to/Resources/File.addin.xml" for manifests
        // intentionally removed from csproj EmbeddedResource includes during controlled pilots.
        private static readonly HashSet<string> s_allowedMissingEmbeddedManifests = new HashSet<string>(StringComparer.Ordinal)
        {
            "Source/OpenSim.ApplicationPlugins.LoadRegions/Resources/OpenSim.ApplicationPlugins.LoadRegions.addin.xml",
            "Source/OpenSim.ApplicationPlugins.RegionModulesController/Resources/OpenSim.ApplicationPlugins.RegionModulesController.addin.xml",
            "Source/OpenSim.ApplicationPlugins.RemoteController/Resources/OpenSim.ApplicationPlugins.RemoteController.addin.xml",
            "Source/OpenSim.Region.ClientStack.LindenUDP/Resources/OpenSim.Region.ClientStack.LindenUDP.addin.xml",
            "Source/OpenSim.Region.ClientStack.LindenCaps/Resources/OpenSim.Region.ClientStack.LindenCaps.addin.xml",
            "Source/OpenSim.Region.OptionalModules/Resources/OpenSim.Region.OptionalModules.addin.xml",
            "Source/OpenSim.Region.CoreModules/Resources/OpenSim.Region.CoreModules.addin.xml",
            "Source/OpenSim.Region.PhysicsModules.BasicPhysics/Resources/OpenSim.Region.PhysicsModules.BasicPhysics.addin.xml",
            "Source/OpenSim.Region.PhysicsModules.BulletS/Resources/OpenSim.Region.PhysicsModules.BulletS.addin.xml",
            "Source/OpenSim.Region.PhysicsModules.Meshing/Resources/OpenSim.Region.PhysicsModules.Meshing.addin.xml",
            "Source/OpenSim.Region.PhysicsModules.ubODE/Resources/OpenSim.Region.PhysicsModules.ubODE.addin.xml",
            "Source/OpenSim.Region.PhysicsModules.ubODEMeshing/Resources/OpenSim.Region.PhysicsModules.ubODEMeshing.addin.xml",
            "Source/OpenSim.Region.PhysicsModules.POS/Resources/OpenSim.Region.PhysicsModules.POS.addin.xml",
            "Addons/OpenSimSearch/Resources/OpenSimSearch.Modules.addin.xml",
            "Addons/OpenSimMutelist/Resources/OpenSimMuteList.Modules.addin.xml",
            "Addons/OpenSim.Addons.OfflineIM/Resources/OpenSim.OfflineIM.addin.xml",
            "Addons/OpenSim.Addons.Groups/Resources/OpenSim.Groups.addin.xml",
            "Addons/Gloebit.GloebitMoneyModule/Resources/Gloebit.GloebitMoneyModule.addin.xml",
            "Addons/os-webrtc-janus/WebRtcVoiceRegionModule/Resources/WebRtcVoice.WebRtcRegionModule.addin.xml",
            "Addons/os-webrtc-janus/WebRtcVoiceServiceModule/Resources/WebRtcVoice.WebRtcVoiceServiceModule.addin.xml",
            "Source/OpenSim.Region.ScriptEngine.YEngine/Resources/OpenSim.Region.ScriptEngine.YEngine.addin.xml",
            "Source/OpenSim.Data/Resources/OpenSim.Data.addin.xml",
            "Source/OpenSim.Server.RegionServer/Resources/OpenSim.Server.RegionServer.addin.xml"
        };

        public PluginRegistryTests()
        {
            m_registry = new PluginRegistry();
        }

        /// <summary>
        /// Test basic plugin registration
        /// </summary>
        [Fact]
        public void TestRegisterPlugin()
        {
            var descriptor = new PluginDescriptor("test", typeof(MockPlugin), "Test Plugin", "1.0");
            m_registry.Register("/OpenSim/Test", descriptor);

            var plugins = m_registry.GetPlugins("/OpenSim/Test");
            Assert.Single(plugins);
            Assert.Equal("test", plugins[0].Id);
        }

        /// <summary>
        /// Test registering multiple plugins for same extension point
        /// </summary>
        [Fact]
        public void TestRegisterMultiplePlugins()
        {
            var desc1 = new PluginDescriptor("plugin1", typeof(MockPlugin), "Plugin 1", "1.0");
            var desc2 = new PluginDescriptor("plugin2", typeof(MockPlugin), "Plugin 2", "1.0");

            m_registry.RegisterAll("/OpenSim/Test", desc1, desc2);

            var plugins = m_registry.GetPlugins("/OpenSim/Test");
            Assert.Equal(2, plugins.Count);
        }

        /// <summary>
        /// Test plugin priority ordering (higher priority loads first)
        /// </summary>
        [Fact]
        public void TestPluginPriority()
        {
            var lowPriority = new PluginDescriptor("low", typeof(MockPlugin), "Low", "1.0") { Priority = 0 };
            var highPriority = new PluginDescriptor("high", typeof(MockPlugin), "High", "1.0") { Priority = 10 };
            var mediumPriority = new PluginDescriptor("medium", typeof(MockPlugin), "Medium", "1.0") { Priority = 5 };

            m_registry.Register("/OpenSim/Test", lowPriority);
            m_registry.Register("/OpenSim/Test", highPriority);
            m_registry.Register("/OpenSim/Test", mediumPriority);

            var plugins = m_registry.GetPlugins("/OpenSim/Test");
            Assert.Equal("high", plugins[0].Id);
            Assert.Equal("medium", plugins[1].Id);
            Assert.Equal("low", plugins[2].Id);
        }

        /// <summary>
        /// Test disabled plugins are filtered out
        /// </summary>
        [Fact]
        public void TestDisabledPluginFiltering()
        {
            var enabled = new PluginDescriptor("enabled", typeof(MockPlugin), "Enabled", "1.0") { Enabled = true };
            var disabled = new PluginDescriptor("disabled", typeof(MockPlugin), "Disabled", "1.0") { Enabled = false };

            m_registry.Register("/OpenSim/Test", enabled);
            m_registry.Register("/OpenSim/Test", disabled);

            var plugins = m_registry.GetPlugins("/OpenSim/Test");
            Assert.Single(plugins);
            Assert.Equal("enabled", plugins[0].Id);
        }

        /// <summary>
        /// Test retrieving plugin types directly
        /// </summary>
        [Fact]
        public void TestGetPluginTypes()
        {
            var desc = new PluginDescriptor("test", typeof(MockPlugin), "Test", "1.0");
            m_registry.Register("/OpenSim/Test", desc);

            var types = m_registry.GetPluginTypes("/OpenSim/Test");
            Assert.Single(types);
            Assert.Equal(typeof(MockPlugin), types[0]);
        }

        /// <summary>
        /// Test HasPlugins query
        /// </summary>
        [Fact]
        public void TestHasPlugins()
        {
            Assert.False(m_registry.HasPlugins("/OpenSim/Test"));

            var desc = new PluginDescriptor("test", typeof(MockPlugin), "Test", "1.0");
            m_registry.Register("/OpenSim/Test", desc);

            Assert.True(m_registry.HasPlugins("/OpenSim/Test"));
        }

        /// <summary>
        /// Test GetPluginCount
        /// </summary>
        [Fact]
        public void TestGetPluginCount()
        {
            Assert.Equal(0, m_registry.GetPluginCount("/OpenSim/Test"));

            m_registry.Register("/OpenSim/Test", new PluginDescriptor("p1", typeof(MockPlugin)));
            m_registry.Register("/OpenSim/Test", new PluginDescriptor("p2", typeof(MockPlugin)));

            Assert.Equal(2, m_registry.GetPluginCount("/OpenSim/Test"));
        }

        /// <summary>
        /// Test registry merging
        /// </summary>
        [Fact]
        public void TestMergeRegistry()
        {
            var registry1 = new PluginRegistry();
            var registry2 = new PluginRegistry();

            registry1.Register("/OpenSim/Test", new PluginDescriptor("p1", typeof(MockPlugin)));
            registry2.Register("/OpenSim/Test", new PluginDescriptor("p2", typeof(MockPlugin)));

            registry1.MergeWith(registry2);

            Assert.Equal(2, registry1.GetPluginCount("/OpenSim/Test"));
        }

        /// <summary>
        /// Test getting all extension points
        /// </summary>
        [Fact]
        public void TestGetExtensionPoints()
        {
            m_registry.Register("/OpenSim/Test1", new PluginDescriptor("p1", typeof(MockPlugin)));
            m_registry.Register("/OpenSim/Test2", new PluginDescriptor("p2", typeof(MockPlugin)));

            var points = m_registry.GetExtensionPoints();
            Assert.Equal(2, points.Count);
            Assert.Contains("/OpenSim/Test1", points);
            Assert.Contains("/OpenSim/Test2", points);
        }

        /// <summary>
        /// Test registering null descriptor throws
        /// </summary>
        [Fact]
        public void TestRegisterNullThrows()
        {
            Assert.Throws<ArgumentNullException>(() => m_registry.Register("/OpenSim/Test", null));
        }

        /// <summary>
        /// Test registering descriptor without type throws
        /// </summary>
        [Fact]
        public void TestRegisterNoTypeThrows()
        {
            var descriptor = new PluginDescriptor { Id = "test" }; // No type set
            Assert.Throws<ArgumentNullException>(() => m_registry.Register("/OpenSim/Test", descriptor));
        }

        /// <summary>
        /// Test clearing registry
        /// </summary>
        [Fact]
        public void TestClear()
        {
            m_registry.Register("/OpenSim/Test", new PluginDescriptor("p1", typeof(MockPlugin)));
            Assert.Equal(1, m_registry.GetPluginCount("/OpenSim/Test"));

            m_registry.Clear();
            Assert.Equal(0, m_registry.GetPluginCount("/OpenSim/Test"));
            Assert.Empty(m_registry.GetExtensionPoints());
        }

        /// <summary>
        /// Test case-insensitive extension point names
        /// </summary>
        [Fact]
        public void TestCaseInsensitiveExtensionPoint()
        {
            m_registry.Register("/OpenSim/Test", new PluginDescriptor("p1", typeof(MockPlugin)));

            // Should find with different casing
            Assert.Equal(1, m_registry.GetPluginCount("/opensim/test"));
            Assert.Equal(1, m_registry.GetPluginCount("/OPENSIM/TEST"));
        }

        /// <summary>
        /// Test plugin version metadata
        /// </summary>
        [Fact]
        public void TestPluginVersionMetadata()
        {
            var desc = new PluginDescriptor("test", typeof(MockPlugin), "Test", "2.5.1");
            m_registry.Register("/OpenSim/Test", desc);

            var plugins = m_registry.GetPlugins("/OpenSim/Test");
            Assert.Equal("2.5.1", plugins[0].Version);
        }

        /// <summary>
        /// Test plugin description metadata
        /// </summary>
        [Fact]
        public void TestPluginDescriptionMetadata()
        {
            var desc = new PluginDescriptor 
            { 
                Id = "test",
                PluginType = typeof(MockPlugin),
                Description = "This is a test plugin"
            };
            m_registry.Register("/OpenSim/Test", desc);

            var plugins = m_registry.GetPlugins("/OpenSim/Test");
            Assert.Equal("This is a test plugin", plugins[0].Description);
        }

        /// <summary>
        /// Test loading registry entries from code providers.
        /// </summary>
        [Fact]
        public void TestRegistryFromProviders()
        {
            var registry = PluginRegistry.FromProviders(new[] { Assembly.GetExecutingAssembly() });

            var plugins = registry.GetPlugins("/OpenSim/ProviderTest");
            Assert.Single(plugins);
            Assert.Equal("provider-test", plugins[0].Id);
            Assert.Equal(typeof(MockPlugin), plugins[0].PluginType);
        }

        /// <summary>
        /// Guard rail for migration progress: any manifest with plugin extension entries
        /// must have a corresponding provider registration file in the project root.
        /// </summary>
        [Fact]
        public void TestManifestsWithExtensionsHaveProviderRegistrationFiles()
        {
            string repoRoot = FindRepoRoot();
            var manifestFiles = new List<string>();
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.addin.xml", SearchOption.AllDirectories));
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Addons"), "*.addin.xml", SearchOption.AllDirectories));

            var missingProviders = new List<string>();

            foreach (string manifest in manifestFiles)
            {
                XDocument doc = XDocument.Load(manifest);
                bool hasExtensions = doc.Root != null && doc.Root.Elements().Any(e => e.Name.LocalName == "Extension");
                if (!hasExtensions)
                    continue;

                string resourceDir = Path.GetDirectoryName(manifest);
                if (resourceDir == null)
                    continue;

                DirectoryInfo projectDir = Directory.GetParent(resourceDir);
                if (projectDir == null)
                    continue;

                string providerPath = Path.Combine(projectDir.FullName, "PluginRegistration.cs");
                if (!File.Exists(providerPath))
                    missingProviders.Add(Path.GetRelativePath(repoRoot, providerPath));
            }

            Assert.True(
                missingProviders.Count == 0,
                "Missing provider registration files: " + string.Join(", ", missingProviders));
        }

        /// <summary>
        /// Guard rail for migration progress: each manifest extension entry id/class pair
        /// should be represented in the corresponding provider registration source.
        /// </summary>
        [Fact]
        public void TestProviderRegistrationsContainManifestExtensionEntries()
        {
            string repoRoot = FindRepoRoot();
            var manifestFiles = new List<string>();
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.addin.xml", SearchOption.AllDirectories));
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Addons"), "*.addin.xml", SearchOption.AllDirectories));

            var missingEntries = new List<string>();

            foreach (string manifest in manifestFiles)
            {
                XDocument doc = XDocument.Load(manifest);
                if (doc.Root == null)
                    continue;

                var extensionElements = doc.Root.Elements().Where(e => e.Name.LocalName == "Extension").ToList();
                if (extensionElements.Count == 0)
                    continue;

                string resourceDir = Path.GetDirectoryName(manifest);
                if (resourceDir == null)
                    continue;

                DirectoryInfo projectDir = Directory.GetParent(resourceDir);
                if (projectDir == null)
                    continue;

                string providerPath = Path.Combine(projectDir.FullName, "PluginRegistration.cs");
                if (!File.Exists(providerPath))
                    continue;

                string providerSource = File.ReadAllText(providerPath);

                foreach (XElement extension in extensionElements)
                {
                    foreach (XElement entry in extension.Elements())
                    {
                        string id = entry.Attribute("id")?.Value;
                        string className = entry.Attribute("class")?.Value;
                        if (string.IsNullOrWhiteSpace(className))
                            className = entry.Attribute("type")?.Value;

                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(className))
                            continue;

                        string classShortName = className.Split('.').Last();
                        bool hasId = providerSource.Contains($"\"{id}\"", StringComparison.Ordinal);
                        bool hasClassLiteral = providerSource.Contains($"\"{className}\"", StringComparison.Ordinal);
                        bool hasTypeOf = providerSource.Contains($"typeof({classShortName})", StringComparison.Ordinal);

                        if (!hasId || (!hasClassLiteral && !hasTypeOf))
                        {
                            string relativeManifest = Path.GetRelativePath(repoRoot, manifest);
                            string relativeProvider = Path.GetRelativePath(repoRoot, providerPath);
                            missingEntries.Add($"{relativeManifest} -> {relativeProvider} missing id='{id}' class='{className}'");
                        }
                    }
                }
            }

            Assert.True(
                missingEntries.Count == 0,
                "Provider registrations missing manifest entries: " + string.Join(" | ", missingEntries));
        }

        /// <summary>
        /// Guard rail for migration progress: every manifest extension triplet
        /// (path, id, class) should be represented in provider registrations.
        /// </summary>
        [Fact]
        public void TestProviderRegistrationsMatchManifestPathIdClassTriples()
        {
            string repoRoot = FindRepoRoot();
            var manifestFiles = new List<string>();
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.addin.xml", SearchOption.AllDirectories));
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Addons"), "*.addin.xml", SearchOption.AllDirectories));

            var missingTriples = new List<string>();

            foreach (string manifest in manifestFiles)
            {
                XDocument doc = XDocument.Load(manifest);
                if (doc.Root == null)
                    continue;

                string resourceDir = Path.GetDirectoryName(manifest);
                if (resourceDir == null)
                    continue;

                DirectoryInfo projectDir = Directory.GetParent(resourceDir);
                if (projectDir == null)
                    continue;

                string providerPath = Path.Combine(projectDir.FullName, "PluginRegistration.cs");
                if (!File.Exists(providerPath))
                    continue;

                HashSet<string> providerTriples = ParseProviderTriples(File.ReadAllText(providerPath));
                IEnumerable<string> manifestTriples = ParseManifestTriples(doc);

                foreach (string triple in manifestTriples)
                {
                    if (!providerTriples.Contains(triple))
                    {
                        string relativeManifest = Path.GetRelativePath(repoRoot, manifest);
                        string relativeProvider = Path.GetRelativePath(repoRoot, providerPath);
                        missingTriples.Add($"{relativeManifest} -> {relativeProvider} missing {triple}");
                    }
                }
            }

            Assert.True(
                missingTriples.Count == 0,
                "Provider registrations missing manifest path/id/class triplets: " + string.Join(" | ", missingTriples));
        }

        /// <summary>
        /// Guard rail for migration progress: provider registrations should not include
        /// unexpected path/id/class triplets that are absent from the source manifests.
        /// </summary>
        [Fact]
        public void TestProviderRegistrationsDoNotContainUnexpectedTriples()
        {
            string repoRoot = FindRepoRoot();
            var manifestFiles = new List<string>();
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.addin.xml", SearchOption.AllDirectories));
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Addons"), "*.addin.xml", SearchOption.AllDirectories));

            var unexpectedTriples = new List<string>();

            foreach (string manifest in manifestFiles)
            {
                XDocument doc = XDocument.Load(manifest);
                if (doc.Root == null)
                    continue;

                string resourceDir = Path.GetDirectoryName(manifest);
                if (resourceDir == null)
                    continue;

                DirectoryInfo projectDir = Directory.GetParent(resourceDir);
                if (projectDir == null)
                    continue;

                string providerPath = Path.Combine(projectDir.FullName, "PluginRegistration.cs");
                if (!File.Exists(providerPath))
                    continue;

                HashSet<string> providerTriples = ParseProviderTriples(File.ReadAllText(providerPath));
                HashSet<string> manifestTriples = new HashSet<string>(ParseManifestTriples(doc), StringComparer.Ordinal);

                foreach (string triple in providerTriples)
                {
                    if (!manifestTriples.Contains(triple))
                    {
                        string relativeManifest = Path.GetRelativePath(repoRoot, manifest);
                        string relativeProvider = Path.GetRelativePath(repoRoot, providerPath);
                        string allowlistKey = BuildAllowlistKey(relativeProvider, triple);
                        if (!s_allowedProviderOnlyTriples.Contains(allowlistKey))
                            unexpectedTriples.Add($"{relativeManifest} -> {relativeProvider} unexpected {triple}");
                    }
                }
            }

            Assert.True(
                unexpectedTriples.Count == 0,
                "Provider registrations contain unexpected path/id/class triplets: " +
                string.Join(" | ", unexpectedTriples) +
                " | If intentional, add a keyed entry to s_allowedProviderOnlyTriples.");
        }

        /// <summary>
        /// Guard rail for migration progress: Mono.Addins attributes should be restricted
        /// to explicitly intentional transitional locations.
        /// </summary>
        [Fact]
        public void TestMonoAddinsAttributesAreLimitedToIntentionalLocations()
        {
            string repoRoot = FindRepoRoot();
            var sourceFiles = Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.cs", SearchOption.AllDirectories);

            var allowedAttributes = new HashSet<string>(StringComparer.Ordinal)
            {
                "Source/OpenSim.Server.Base/ServerUtils.cs|AddinRoot"
            };

            var foundAttributes = new HashSet<string>(StringComparer.Ordinal);

            foreach (string file in sourceFiles)
            {
                string relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string content = File.ReadAllText(file);

                if (Regex.IsMatch(content, "\\[\\s*assembly\\s*:\\s*AddinRoot\\s*\\(", RegexOptions.CultureInvariant))
                    foundAttributes.Add(relativePath + "|AddinRoot");

                if (Regex.IsMatch(content, "\\[\\s*TypeExtensionPoint\\s*\\(", RegexOptions.CultureInvariant))
                    foundAttributes.Add(relativePath + "|TypeExtensionPoint");
            }

            var unexpected = foundAttributes.Where(a => !allowedAttributes.Contains(a)).OrderBy(a => a).ToList();
            var missing = allowedAttributes.Where(a => !foundAttributes.Contains(a)).OrderBy(a => a).ToList();

            Assert.True(
                unexpected.Count == 0 && missing.Count == 0,
                "Mono.Addins attribute locations changed. Unexpected: " + string.Join(", ", unexpected) +
                " | Missing expected: " + string.Join(", ", missing));
        }

        /// <summary>
        /// Guard rail for migration progress: remaining Mono.Addins using directives should
        /// stay confined to known transitional implementation files.
        /// </summary>
        [Fact]
        public void TestMonoAddinsUsingDirectivesAreLimitedToIntentionalFiles()
        {
            string repoRoot = FindRepoRoot();
            var sourceFiles = Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.cs", SearchOption.AllDirectories);

            var allowedUsingFiles = new HashSet<string>(StringComparer.Ordinal)
            {
                "Source/OpenSim.Framework/IPluginDiscovery.cs",
                "Source/OpenSim.Framework/PluginManager.cs",
                "Source/OpenSim.Server.Base/CommandManager.cs",
                "Source/OpenSim.Server.Base/ServerUtils.cs"
            };

            var filesWithUsing = new HashSet<string>(StringComparer.Ordinal);

            foreach (string file in sourceFiles)
            {
                string content = File.ReadAllText(file);
                if (!Regex.IsMatch(content, "^\\s*using\\s+Mono\\.Addins\\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant))
                    continue;

                filesWithUsing.Add(Path.GetRelativePath(repoRoot, file).Replace('\\', '/'));
            }

            var unexpected = filesWithUsing.Where(f => !allowedUsingFiles.Contains(f)).OrderBy(f => f).ToList();
            var missing = allowedUsingFiles.Where(f => !filesWithUsing.Contains(f)).OrderBy(f => f).ToList();

            Assert.True(
                unexpected.Count == 0 && missing.Count == 0,
                "Mono.Addins using directive locations changed. Unexpected: " + string.Join(", ", unexpected) +
                " | Missing expected: " + string.Join(", ", missing));
        }

        /// <summary>
        /// Guard rail for transitional runtime parity: manifests with extension entries and
        /// provider registrations should remain embedded in csproj unless explicitly allowlisted
        /// for a controlled removal pilot.
        /// </summary>
        [Fact]
        public void TestProviderBackedManifestResourcesRemainEmbeddedUnlessAllowlisted()
        {
            string repoRoot = FindRepoRoot();
            var manifestFiles = new List<string>();
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Source"), "*.addin.xml", SearchOption.AllDirectories));
            manifestFiles.AddRange(Directory.GetFiles(Path.Combine(repoRoot, "Addons"), "*.addin.xml", SearchOption.AllDirectories));

            var missingEmbeddedResources = new List<string>();

            foreach (string manifest in manifestFiles)
            {
                string relativeManifest = Path.GetRelativePath(repoRoot, manifest).Replace('\\', '/');
                if (s_allowedMissingEmbeddedManifests.Contains(relativeManifest))
                    continue;

                XDocument manifestDoc = XDocument.Load(manifest);
                bool hasExtensions = manifestDoc.Root != null && manifestDoc.Root.Elements().Any(e => e.Name.LocalName == "Extension");
                if (!hasExtensions)
                    continue;

                string resourceDir = Path.GetDirectoryName(manifest);
                if (resourceDir == null)
                    continue;

                DirectoryInfo projectDir = Directory.GetParent(resourceDir);
                if (projectDir == null)
                    continue;

                string providerPath = Path.Combine(projectDir.FullName, "PluginRegistration.cs");
                if (!File.Exists(providerPath))
                    continue;

                string csprojPath = Directory.GetFiles(projectDir.FullName, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(csprojPath))
                {
                    missingEmbeddedResources.Add(relativeManifest + " (no csproj found)");
                    continue;
                }

                XDocument csprojDoc = XDocument.Load(csprojPath);
                string manifestFileName = Path.GetFileName(manifest);

                bool isEmbedded = csprojDoc
                    .Descendants()
                    .Where(e => e.Name.LocalName == "EmbeddedResource")
                    .Select(e => e.Attribute("Include")?.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Replace('\\', '/'))
                    .Any(v => string.Equals(Path.GetFileName(v), manifestFileName, StringComparison.OrdinalIgnoreCase));

                if (!isEmbedded)
                    missingEmbeddedResources.Add(relativeManifest);
            }

            Assert.True(
                missingEmbeddedResources.Count == 0,
                "Provider-backed manifests missing EmbeddedResource includes: " + string.Join(", ", missingEmbeddedResources) +
                " | If intentional for a pilot, add to s_allowedMissingEmbeddedManifests.");
        }

        private static IEnumerable<string> ParseManifestTriples(XDocument manifest)
        {
            if (manifest.Root == null)
                yield break;

            foreach (XElement extension in manifest.Root.Elements().Where(e => e.Name.LocalName == "Extension"))
            {
                string path = extension.Attribute("path")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                foreach (XElement entry in extension.Elements())
                {
                    string id = entry.Attribute("id")?.Value?.Trim();
                    string className = entry.Attribute("class")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(className))
                        className = entry.Attribute("type")?.Value?.Trim();

                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(className))
                        continue;

                    yield return BuildTriple(path, id, className);
                }
            }
        }

        private static HashSet<string> ParseProviderTriples(string source)
        {
            var triples = new HashSet<string>(StringComparer.Ordinal);
            string namespaceName = ParseProviderNamespace(source);

            var matcher = new Regex(
                "RegisterByName\\s*\\(\\s*registry\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"(?<id>[^\"]+)\"\\s*,\\s*\"(?<class>[^\"]+)\"",
                RegexOptions.CultureInvariant);

            foreach (Match match in matcher.Matches(source))
            {
                string path = match.Groups["path"].Value.Trim();
                string id = match.Groups["id"].Value.Trim();
                string className = match.Groups["class"].Value.Trim();
                triples.Add(BuildTriple(path, id, className));
            }

            var descriptorMatcher = new Regex(
                "Register\\s*\\(\\s*\"(?<path>[^\"]+)\"\\s*,\\s*new\\s+PluginDescriptor\\s*\\(\\s*\"(?<id>[^\"]+)\"\\s*,\\s*typeof\\((?<type>[^\\)]+)\\)",
                RegexOptions.CultureInvariant);

            foreach (Match match in descriptorMatcher.Matches(source))
            {
                string path = match.Groups["path"].Value.Trim();
                string id = match.Groups["id"].Value.Trim();
                string typeToken = match.Groups["type"].Value.Trim().Replace("global::", string.Empty);
                string className = ResolveTypeTokenToClassName(typeToken, namespaceName);

                if (!string.IsNullOrWhiteSpace(className))
                    triples.Add(BuildTriple(path, id, className));
            }

            return triples;
        }

        private static string ParseProviderNamespace(string source)
        {
            var namespaceMatcher = new Regex("namespace\\s+(?<ns>[A-Za-z0-9_\\.]+)", RegexOptions.CultureInvariant);
            Match match = namespaceMatcher.Match(source);
            return match.Success ? match.Groups["ns"].Value.Trim() : string.Empty;
        }

        private static string ResolveTypeTokenToClassName(string typeToken, string namespaceName)
        {
            if (string.IsNullOrWhiteSpace(typeToken))
                return string.Empty;

            if (typeToken.Contains('.'))
                return typeToken;

            if (string.IsNullOrWhiteSpace(namespaceName))
                return typeToken;

            return namespaceName + "." + typeToken;
        }

        private static string BuildTriple(string path, string id, string className)
        {
            return string.Concat(path, "|", id, "|", className);
        }

        private static string BuildAllowlistKey(string relativeProviderPath, string triple)
        {
            return string.Concat(relativeProviderPath.Replace('\\', '/'), "|", triple);
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Tranquillity.sln")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate repository root from test base directory.");
        }
    }

    /// <summary>
    /// Test DotNetCorePluginLoader functionality for Phase 2 migration.
    /// Tests plugin loading with discovery backend abstraction.
    /// </summary>
    public class DotNetCorePluginLoaderTests
    {
        private readonly MockPluginDiscovery m_discovery;
        private readonly DotNetCorePluginLoader<MockPlugin> m_loader;

        public DotNetCorePluginLoaderTests()
        {
            m_discovery = new MockPluginDiscovery();
            m_loader = new DotNetCorePluginLoader<MockPlugin>(m_discovery);
        }

        /// <summary>
        /// Test loader is properly initialized
        /// </summary>
        [Fact]
        public void TestLoaderInitialization()
        {
            Assert.NotNull(m_loader);
            Assert.Empty(m_loader.LoadedPlugins);
        }

        /// <summary>
        /// Test loader throws on null discovery
        /// </summary>
        [Fact]
        public void TestLoaderThrowsOnNullDiscovery()
        {
            Assert.Throws<ArgumentNullException>(() => 
                new DotNetCorePluginLoader<MockPlugin>(null));
        }

        /// <summary>
        /// Test load with registry
        /// </summary>
        [Fact]
        public void TestLoadFromRegistry()
        {
            var registry = new PluginRegistry();
            var desc = new PluginDescriptor("test", typeof(MockPlugin), "Test");
            registry.Register("/OpenSim/Test", desc);

            m_loader.LoadFromRegistry(registry, "/OpenSim/Test", typeof(MockPlugin));

            Assert.Single(m_loader.LoadedPlugins);
        }

        /// <summary>
        /// Test loader with null registry throws
        /// </summary>
        [Fact]
        public void TestLoadFromNullRegistryThrows()
        {
            Assert.Throws<ArgumentNullException>(() =>
                m_loader.LoadFromRegistry(null, "/OpenSim/Test", typeof(MockPlugin)));
        }

        /// <summary>
        /// Test loader without type hint doesn't load
        /// </summary>
        [Fact]
        public void TestLoadWithoutTypeHintLoadsNothing()
        {
            var registry = new PluginRegistry();
            registry.Register("/OpenSim/Test", new PluginDescriptor("test", typeof(MockPlugin)));

            m_loader.LoadFromRegistry(registry, "/OpenSim/Test", null);

            Assert.Empty(m_loader.LoadedPlugins);
        }

        /// <summary>
        /// Test dispose clears loaded plugins
        /// </summary>
        [Fact]
        public void TestDisposeClears()
        {
            var registry = new PluginRegistry();
            registry.Register("/OpenSim/Test", new PluginDescriptor("test", typeof(MockPlugin)));
            m_loader.LoadFromRegistry(registry, "/OpenSim/Test", typeof(MockPlugin));

            Assert.Single(m_loader.LoadedPlugins);

            m_loader.Dispose();
            Assert.Empty(m_loader.LoadedPlugins);
        }

        /// <summary>
        /// Test loader throws after dispose
        /// </summary>
        [Fact]
        public void TestLoaderThrowsAfterDispose()
        {
            m_loader.Dispose();

            var registry = new PluginRegistry();
            Assert.Throws<ObjectDisposedException>(() =>
                m_loader.LoadFromRegistry(registry, "/OpenSim/Test", typeof(MockPlugin)));
        }

        /// <summary>
        /// Test factory creates loader with discovery backend
        /// </summary>
        [Fact]
        public void TestFactoryCreation()
        {
            var loader = DotNetCorePluginLoaderFactory.Create<MockPlugin>();
            Assert.NotNull(loader);
            loader.Dispose();
        }

        /// <summary>
        /// Test factory with explicit discovery
        /// </summary>
        [Fact]
        public void TestFactoryCreationWithDiscovery()
        {
            var discovery = new MockPluginDiscovery();
            var loader = DotNetCorePluginLoaderFactory.Create<MockPlugin>(discovery);
            Assert.NotNull(loader);
            loader.Dispose();
        }

        /// <summary>
        /// Test loading multiple plugins
        /// </summary>
        [Fact]
        public void TestLoadMultiplePlugins()
        {
            var registry = new PluginRegistry();
            registry.Register("/OpenSim/Test", new PluginDescriptor("p1", typeof(MockPlugin)));
            registry.Register("/OpenSim/Test", new PluginDescriptor("p2", typeof(MockPlugin)));

            m_loader.LoadFromRegistry(registry, "/OpenSim/Test", typeof(MockPlugin));

            Assert.Equal(2, m_loader.LoadedPlugins.Count);
        }
    }

    /// <summary>
    /// Mock implementations for testing
    /// </summary>

    /// <summary>
    /// Mock plugin for testing
    /// </summary>

    public class MockPlugin : IPlugin
    {
        public string Version => "1.0";
        public string Name => "Mock Plugin";

        public void Initialise()
        {
            // Mock implementation
        }

        public void Dispose()
        {
            // Mock implementation
        }
    }
    /// <summary>
    /// Mock plugin discovery backend for testing
    /// </summary>
    public class MockPluginDiscovery : IPluginDiscovery
    {
        private List<PluginExtensionNode> m_nodes = new List<PluginExtensionNode>();

        public PluginDiscoveryCapabilities Capabilities => 
            new PluginDiscoveryCapabilities(supportsAddinRegistryMetadata: false);

        public void Initialize(string pluginDirectory)
        {
            // Mock implementation
        }

        public IReadOnlyList<PluginExtensionNode> GetExtensionNodes(string extensionPoint, Type requiredTypeHint = null)
        {
            return m_nodes;
        }

        public int GetExtensionNodeCount(string extensionPoint, Type requiredTypeHint = null)
        {
            return m_nodes.Count;
        }

        public void Dispose()
        {
            m_nodes.Clear();
        }

        // Helper for tests
        public void AddNode(PluginExtensionNode node)
        {
            m_nodes.Add(node);
        }
    }

    public class TestProviderRegistration : IPluginRegistryProvider
    {
        public void RegisterPlugins(PluginRegistry registry)
        {
            registry.Register(
                "/OpenSim/ProviderTest",
                new PluginDescriptor("provider-test", typeof(MockPlugin), "Provider Test", "1.0"));
        }
    }
}
