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
using System.Reflection;
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
