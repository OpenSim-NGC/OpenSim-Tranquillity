/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The module registers only the read-only `jolt` subcommands. The console test commands are in the diagnostics
/// assembly, which this test project does not reference (its tests are in OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests),
/// so a test subcommand typed here reaches the read-only handler, which answers with the usage line.
/// </summary>
public class TestCommandGateTests
{
    // A sample of the test subcommands the diagnostics assembly registers, and `parity`.
    private static readonly string[] TestNames = { "linktest", "boattest", "terrainhill", "rezprims", "droptest", "sittest", "clearprims", "parity" };

    private sealed class Harness
    {
        public readonly Commands Commands = new Commands();
        public readonly List<string> Calls = new List<string>();

        public Harness()
        {
            JoltScene.RegisterConsoleCommands(Commands, (_, cmd) => Calls.Add("read " + cmd[1]));
        }

        public string Run(params string[] cmd)
        {
            Calls.Clear();
            Commands.Resolve(cmd);
            return Assert.Single(Calls);
        }

        public List<string> PhysicsHelp() => Commands.GetHelp(new[] { "help", "Physics" });
    }

    [Fact]
    public void The_two_groups_are_disjoint_and_named_once()
    {
        Assert.Equal(JoltScene.ReadOnlyCommands.Length, JoltScene.ReadOnlyCommands.Distinct().Count());
        Assert.Empty(TestNames.Intersect(JoltScene.ReadOnlyCommands));
    }

    [Fact]
    public void Key_off_registers_the_read_only_commands_only()
    {
        var c = new Harness();

        foreach (string sub in JoltScene.ReadOnlyCommands)
            Assert.Equal("read " + sub, c.Run("jolt", sub, "1", "2"));

        // Not registered: each falls through to the read-only handler, which prints the usage line.
        foreach (string sub in TestNames)
            Assert.Equal("read " + sub, c.Run("jolt", sub, "core"));
    }

    [Fact]
    public void Key_off_help_lists_no_test_command()
    {
        var help = new Harness().PhysicsHelp();

        Assert.Contains(help, l => l.StartsWith(JoltScene.ReadOnlyUsage + " - ", StringComparison.Ordinal));
        foreach (string sub in TestNames)
            Assert.DoesNotContain(help, l => l.StartsWith("jolt " + sub, StringComparison.Ordinal));
        foreach (string sub in TestNames)
            Assert.DoesNotContain(" " + sub, JoltScene.ReadOnlyUsage + " ");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void TestCommandsEnabled_reads_the_Jolt_key(string value, bool expected)
    {
        var src = new IniConfigSource();
        if (value != null)
            src.AddConfig("Jolt").Set("TestCommands", value);

        Assert.Equal(expected, JoltScene.TestCommandsEnabled(src));
    }

    [Fact]
    public void TestCommands_defaults_to_off()
    {
        Assert.False(JoltScene.TestCommandsEnabled(new IniConfigSource()));
        Assert.False(JoltScene.TestCommandsEnabled(null));
    }

    // The module's own source names no test command and holds no test code: the diagnostics assembly has them all.
    [Fact]
    public void The_module_assembly_holds_no_test_command_code()
    {
        string dir = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "Source", "OpenSim.Region.PhysicsModules.Jolt");
        Assert.False(File.Exists(Path.Combine(dir, "JoltTestCommands.cs")));
        foreach (string file in Directory.GetFiles(dir, "*.cs"))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("\"JoltAutoDropTest\"", text);
            Assert.DoesNotContain("RegisterTestCommands", text);
            Assert.DoesNotContain("HandleJoltTestConsole", text);
        }
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;
}
