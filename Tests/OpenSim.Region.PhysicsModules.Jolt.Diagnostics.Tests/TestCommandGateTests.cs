/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests;

/// <summary>
/// With the diagnostics assembly loaded, every test subcommand and `jolt parity` is its own console command, listed by
/// help. [Startup] JoltAutoDropTest is honoured only with [Jolt] TestCommands on. (With the key off, or the assembly
/// absent, only the read-only subcommands exist: OpenSim.Region.PhysicsModules.Jolt.Tests, TestCommandGateTests.)
/// </summary>
public class TestCommandGateTests
{
    private sealed class Harness
    {
        public readonly Commands Commands = new Commands();
        public readonly List<string> Calls = new List<string>();

        public Harness()
        {
            JoltScene.RegisterConsoleCommands(Commands, (_, cmd) => Calls.Add("read " + cmd[1]));
            JoltDiagnostics.RegisterTestCommands(Commands, (_, cmd) => Calls.Add("test " + cmd[1]));
            JoltDiagnostics.RegisterParityCommand(Commands, (_, cmd) => Calls.Add("parity " + cmd[2]));
        }

        public string Run(params string[] cmd)
        {
            Calls.Clear();
            Commands.Resolve(cmd);
            return Assert.Single(Calls);
        }

        public List<string> PhysicsHelp() => Commands.GetHelp(new[] { "help", "Physics" });
    }

    private static readonly string[] TestNames = JoltDiagnostics.TestCommands.Select(t => t.Name).ToArray();

    [Fact]
    public void The_two_groups_are_disjoint_and_named_once()
    {
        Assert.Equal(TestNames.Length, TestNames.Distinct().Count());
        Assert.Equal(JoltScene.ReadOnlyCommands.Length, JoltScene.ReadOnlyCommands.Distinct().Count());
        Assert.Empty(TestNames.Intersect(JoltScene.ReadOnlyCommands));
        Assert.DoesNotContain("parity", JoltScene.ReadOnlyCommands);
    }

    [Fact]
    public void Key_on_registers_every_command()
    {
        var c = new Harness();

        foreach (string sub in JoltScene.ReadOnlyCommands)
            Assert.Equal("read " + sub, c.Run("jolt", sub, "1", "2"));
        foreach (string sub in TestNames)
            Assert.Equal("test " + sub, c.Run("jolt", sub, "4"));
        Assert.Equal("parity core", c.Run("jolt", "parity", "core"));
    }

    [Fact]
    public void Key_on_help_lists_every_command()
    {
        var help = new Harness().PhysicsHelp();

        Assert.Contains(help, l => l.StartsWith(JoltScene.ReadOnlyUsage + " - ", StringComparison.Ordinal));
        foreach (var (_, syntax, _) in JoltDiagnostics.TestCommands)
            Assert.Contains(help, l => l.StartsWith(syntax + " - ", StringComparison.Ordinal));
        Assert.Contains(help, l => l.StartsWith("jolt parity ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null, false)]        // neither key: off
    [InlineData(null, "true", false)]      // JoltAutoDropTest alone does nothing
    [InlineData("false", "true", false)]
    [InlineData("true", null, false)]      // TestCommands alone does not drop
    [InlineData("true", "false", false)]
    [InlineData("true", "true", true)]
    public void JoltAutoDropTest_needs_TestCommands(string testCommands, string autoDrop, bool expected)
    {
        var src = new IniConfigSource();
        if (testCommands != null)
            src.AddConfig("Jolt").Set("TestCommands", testCommands);
        if (autoDrop != null)
            src.AddConfig("Startup").Set("JoltAutoDropTest", autoDrop);

        Assert.Equal(testCommands == "true", JoltScene.TestCommandsEnabled(src));
        Assert.Equal(expected, JoltDiagnostics.AutoDropTestEnabled(src));
    }

    // RegionLoaded needs a live Scene, so the call site is checked by reading it: the module only hands the call to the
    // diagnostics object, which drops only behind the gate, and nothing else reads the JoltAutoDropTest key.
    [Fact]
    public void RegionLoaded_drops_only_behind_the_gate()
    {
        string source = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "Source");
        string moduleDir = Path.Combine(source, "OpenSim.Region.PhysicsModules.Jolt");
        string diagDir = Path.Combine(source, "OpenSim.Region.PhysicsModules.Jolt.Diagnostics");
        string scene = File.ReadAllText(Path.Combine(moduleDir, "JoltScene.cs"));
        string drops = File.ReadAllText(Path.Combine(diagDir, "JoltDiagnostics.Drops.cs"));
        string tests = File.ReadAllText(Path.Combine(diagDir, "JoltTestCommands.cs"));

        int at = scene.IndexOf("public void RegionLoaded(Scene scene)", StringComparison.Ordinal);
        int end = scene.IndexOf("internal static readonly string[] ReadOnlyCommands", at, StringComparison.Ordinal);
        string body = scene[at..end];
        Assert.Contains("_diagnostics?.AutoDrop(m_Config);", body);
        Assert.DoesNotContain("DropOne(", body);

        int from = drops.IndexOf("public void AutoDrop(IConfigSource source)", StringComparison.Ordinal);
        Assert.True(from > 0);
        string auto = drops[from..drops.IndexOf("// Update a tracked drop", from, StringComparison.Ordinal)];
        int gate = auto.IndexOf("if (AutoDropTestEnabled(source))", StringComparison.Ordinal);
        Assert.True(gate > 0);
        Assert.True(auto.IndexOf("DropOne(", StringComparison.Ordinal) > gate);
        Assert.Equal(3, Count(auto, "DropOne(\"box\""));

        Assert.DoesNotContain("\"JoltAutoDropTest\"", scene);
        Assert.Equal(1, Count(tests, "\"JoltAutoDropTest\""));
    }

    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string Here([CallerFilePath] string here = "") => here;
}
