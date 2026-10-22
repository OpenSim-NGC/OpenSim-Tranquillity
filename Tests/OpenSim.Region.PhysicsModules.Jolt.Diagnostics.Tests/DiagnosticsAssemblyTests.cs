/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests;

/// <summary>
/// The module loads the diagnostics assembly when [Jolt] TestCommands is true and the assembly is beside it (this test
/// project references it, so it is in the test output). With the key on the test commands register and run; with the
/// key off nothing of them exists. Also: the region server's plugin discovery finds no region module in the assembly,
/// and the assembly loaded for the load context the plugin discovery gives the module binds to that copy of the module.
///
/// Serial: it swaps MainConsole.Instance, and its scenes step a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class DiagnosticsAssemblyTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;

    private sealed class CapturingConsole : MockConsole, ICommandConsole
    {
        public readonly List<string> Lines = new();
        public new void Output(string format) { lock (Lines) Lines.Add(format); }
        public new void Output(string format, params object[] components) { lock (Lines) Lines.Add(string.Format(format, components)); }
    }

    private static JoltScene NewScene(bool testCommands)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt").Set("TestCommands", testCommands ? "true" : "false");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, 25f);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    [Fact]
    public void With_the_assembly_present_and_the_key_on_the_test_commands_register_and_run()
    {
        ICommandConsole saved = MainConsole.Instance;
        var console = new CapturingConsole();
        MainConsole.Instance = console;
        JoltScene scene = NewScene(testCommands: true);
        try
        {
            Assert.IsType<JoltDiagnostics>(scene.Diagnostics);
            var commands = new Commands();
            scene.AttachConsole(commands);

            var help = commands.GetHelp(new[] { "help", "Physics" });
            Assert.Contains(help, l => l.StartsWith(JoltScene.ReadOnlyUsage + " - ", StringComparison.Ordinal));
            foreach (var (_, syntax, _) in JoltDiagnostics.TestCommands)
                Assert.Contains(help, l => l.StartsWith(syntax + " - ", StringComparison.Ordinal));

            // One of them runs: with no drop tracked, `jolt dropstatus` says so, through the region's backend.
            commands.Resolve(new[] { "jolt", "dropstatus" });
            Assert.Contains(console.Lines, l => l.Contains("no active drops"));

            // An operator command still runs beside them.
            console.Lines.Clear();
            commands.Resolve(new[] { "jolt", "capacity" });
            Assert.Contains(console.Lines, l => l.Contains("'Test Region'"));
        }
        finally
        {
            scene.Dispose();
            MainConsole.Instance = saved;
        }
    }

    [Fact]
    public void With_the_assembly_present_and_the_key_off_nothing_registers()
    {
        ICommandConsole saved = MainConsole.Instance;
        MainConsole.Instance = new CapturingConsole();
        JoltScene scene = NewScene(testCommands: false);
        try
        {
            Assert.Null(scene.Diagnostics);
            var commands = new Commands();
            scene.AttachConsole(commands);

            var help = commands.GetHelp(new[] { "help", "Physics" }).Where(l => l.StartsWith("jolt", StringComparison.Ordinal)).ToList();
            Assert.Single(help);
            foreach (var (name, _, _) in JoltDiagnostics.TestCommands)
                Assert.DoesNotContain(help, l => l.StartsWith("jolt " + name, StringComparison.Ordinal));
        }
        finally
        {
            scene.Dispose();
            MainConsole.Instance = saved;
        }
    }

    [Fact]
    public void RemoveRegion_cuts_the_test_commands_route_to_the_scene()
    {
        ICommandConsole saved = MainConsole.Instance;
        var console = new CapturingConsole();
        MainConsole.Instance = console;
        JoltScene scene = NewScene(testCommands: true);
        try
        {
            var commands = new Commands();
            scene.AttachConsole(commands);
            commands.Resolve(new[] { "jolt", "dropstatus" });
            Assert.Contains(console.Lines, l => l.Contains("no active drops"));
            console.Lines.Clear();

            scene.RemoveRegion(null);
            commands.Resolve(new[] { "jolt", "dropstatus" });

            Assert.Empty(console.Lines);   // the console still holds the handler, which now does nothing
        }
        finally
        {
            scene.Dispose();
            MainConsole.Instance = saved;
        }
    }

    private static List<PluginExtensionNode> RegionModules(string dir, out DotNetCorePluginsDiscovery discovery)
    {
        discovery = new DotNetCorePluginsDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        discovery.Initialize(dir);
        return discovery.GetExtensionNodes("/OpenSim/RegionModules", typeof(IRegionModuleBase)).ToList();
    }

    [Fact]
    public void The_hosts_discovery_finds_no_region_module_in_the_diagnostics_assembly()
    {
        // The region module controller's own discovery, over the folder with both assemblies in it. A region module in
        // the diagnostics assembly would be made once per region, in a load context that cannot use the module's types.
        string dir = Path.GetDirectoryName(typeof(JoltScene).Assembly.Location);
        Assert.True(File.Exists(Path.Combine(dir, JoltDiagnosticsLoader.AssemblyFileName + ".dll")));

        var nodes = RegionModules(dir, out DotNetCorePluginsDiscovery discovery);
        using (discovery)
        {
            Assert.Single(nodes, n => n.Type.Name == "JoltModule");
            Assert.DoesNotContain(nodes, n => n.Type.Assembly.GetName().Name == JoltDiagnosticsLoader.AssemblyFileName);
        }
    }

    [Fact]
    public void Loaded_for_the_module_the_plugin_discovery_made_the_assembly_binds_to_that_copy_of_the_module()
    {
        // The discovery loads the module into a load context of its own, apart from the default one this test runs in.
        // The loader, run from that copy, must load the diagnostics assembly into the same context, or the
        // diagnostics object would not be an IJoltDiagnostics of the copy that asks for it.
        string dir = Path.GetDirectoryName(typeof(JoltScene).Assembly.Location);
        var nodes = RegionModules(dir, out DotNetCorePluginsDiscovery discovery);
        using (discovery)
        {
            Assembly pluginCopy = nodes.Single(n => n.Type.Name == "JoltModule").Type.Assembly;
            Assert.NotSame(typeof(JoltScene).Assembly, pluginCopy);

            Type loader = pluginCopy.GetType("OpenSim.Region.PhysicsModules.Jolt.JoltDiagnosticsLoader", throwOnError: true);
            MethodInfo load = loader.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
            object diagnostics = load.Invoke(null, new object[] { dir, null });

            Assert.NotNull(diagnostics);
            Assert.Same(AssemblyLoadContext.GetLoadContext(pluginCopy), AssemblyLoadContext.GetLoadContext(diagnostics.GetType().Assembly));
            Assert.Contains("IJoltDiagnostics",
                diagnostics.GetType().GetInterfaces().Select(i => i.Name));
        }
    }
}
