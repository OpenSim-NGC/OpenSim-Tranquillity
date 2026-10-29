/*
 * Copyright (c) Legion Builds
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

using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.CoreModules.World.Objects.BlockedOwners;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.BlockedOwners.Tests;

/// <summary>
/// The console commands of the blocked-owner module, run through the console's real command dispatcher
/// with two regions loaded.
/// </summary>
/// <remarks>
/// MainConsole.Instance is process-wide. This project runs its test classes one at a time
/// (AssemblyInfo.cs), and each test puts the previous console back.
/// </remarks>
public class BlockedOwnerConsoleTests : OpenSimTestCase
{
    private static readonly UUID OwnerId = new("3c7d1e95-62a0-4b8f-9d14-e8a5f2c7b360");
    private static readonly UUID SecondId = new("b84f0a27-c519-4d6e-8f3a-17d9e2b6c405");
    private static readonly Vector3 RezPos = new(10, 10, 25);

    private ICommandConsole m_savedConsole = null!;
    private RecordingConsole m_console = null!;
    private TestScene m_sceneA = null!;
    private TestScene m_sceneB = null!;
    private BlockedOwnerModule m_moduleA = null!;
    private BlockedOwnerModule m_moduleB = null!;

    public override void SetUp()
    {
        base.SetUp();

        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("Region A", UUID.Random(), 1000, 1000);
        m_sceneB = sh.SetupScene("Region B", UUID.Random(), 1001, 1000);

        // SetupScene installs a console that drops commands; this one keeps them.
        m_savedConsole = MainConsole.Instance;
        m_console = new RecordingConsole();
        MainConsole.Instance = m_console;

        IConfigSource config = new IniConfigSource();
        m_moduleA = new BlockedOwnerModule();
        m_moduleB = new BlockedOwnerModule();
        SceneHelpers.SetupSceneModules(m_sceneA, config, m_moduleA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, m_moduleB);
    }

    public override void Dispose()
    {
        MainConsole.Instance = m_savedConsole;
        base.Dispose();
    }

    [Fact]
    public void BlockOwnerActsOnEveryRegionWhenNoneIsSelected()
    {
        m_console.Run($"block owner {OwnerId}");

        Assert.True(m_moduleA.IsBlocked(OwnerId));
        Assert.True(m_moduleB.IsBlocked(OwnerId));
        Assert.False(m_sceneA.Permissions.CanRezObject(1, OwnerId, RezPos));
        Assert.False(m_sceneB.Permissions.CanRezObject(1, OwnerId, RezPos));
        Assert.Contains($"Owner {OwnerId} is now blocked from rezzing in Region A.", m_console.Lines);
        Assert.Contains($"Owner {OwnerId} is now blocked from rezzing in Region B.", m_console.Lines);
    }

    [Fact]
    public void BlockOwnerActsOnlyOnTheConsolesCurrentRegion()
    {
        m_console.ConsoleScene = m_sceneB;

        m_console.Run($"block owner {OwnerId}");

        Assert.False(m_moduleA.IsBlocked(OwnerId));
        Assert.True(m_moduleB.IsBlocked(OwnerId));
        Assert.True(m_sceneA.Permissions.CanRezObject(1, OwnerId, RezPos));
        Assert.False(m_sceneB.Permissions.CanRezObject(1, OwnerId, RezPos));
        Assert.Equal(new[] { $"Owner {OwnerId} is now blocked from rezzing in Region B." }, m_console.Lines);
    }

    [Fact]
    public void BlockOwnerTwiceSaysTheOwnerWasAlreadyBlocked()
    {
        m_console.ConsoleScene = m_sceneA;
        m_console.Run($"block owner {OwnerId}");

        m_console.Run($"block owner {OwnerId}");

        Assert.Equal(new[] { $"Owner {OwnerId} was already blocked in Region A." }, m_console.Lines);
        Assert.Equal(new[] { OwnerId }, m_moduleA.GetBlockedOwners());
    }

    [Fact]
    public void UnblockOwnerActsOnEveryRegionWhenNoneIsSelected()
    {
        m_moduleA.Block(OwnerId);
        m_moduleB.Block(OwnerId);

        m_console.Run($"unblock owner {OwnerId}");

        Assert.False(m_moduleA.IsBlocked(OwnerId));
        Assert.False(m_moduleB.IsBlocked(OwnerId));
        Assert.True(m_sceneA.Permissions.CanRezObject(1, OwnerId, RezPos));
        Assert.Contains($"Owner {OwnerId} may rez again in Region A.", m_console.Lines);
        Assert.Contains($"Owner {OwnerId} may rez again in Region B.", m_console.Lines);
    }

    [Fact]
    public void UnblockOwnerActsOnlyOnTheConsolesCurrentRegion()
    {
        m_moduleA.Block(OwnerId);
        m_moduleB.Block(OwnerId);
        m_console.ConsoleScene = m_sceneA;

        m_console.Run($"unblock owner {OwnerId}");

        Assert.False(m_moduleA.IsBlocked(OwnerId));
        Assert.True(m_moduleB.IsBlocked(OwnerId));
        Assert.Equal(new[] { $"Owner {OwnerId} may rez again in Region A." }, m_console.Lines);
    }

    [Fact]
    public void UnblockOwnerOfAnOwnerNotBlockedSaysSo()
    {
        m_console.ConsoleScene = m_sceneA;

        m_console.Run($"unblock owner {OwnerId}");

        Assert.Equal(new[] { $"Owner {OwnerId} was not blocked in Region A." }, m_console.Lines);
    }

    [Fact]
    public void ShowBlockedOwnersListsEachRegionsOwnersWhenNoneIsSelected()
    {
        m_moduleA.Block(OwnerId);
        m_moduleA.Block(SecondId);
        m_moduleB.Block(SecondId);

        m_console.Run("show blocked owners");

        Assert.Equal(
            new[]
            {
                "Blocked owners in Region A: 2", $"  {OwnerId}", $"  {SecondId}",
                "Blocked owners in Region B: 1", $"  {SecondId}"
            },
            SortOwnerLines(m_console.Lines));
    }

    [Fact]
    public void ShowBlockedOwnersListsOnlyTheConsolesCurrentRegion()
    {
        m_moduleA.Block(OwnerId);
        m_console.ConsoleScene = m_sceneB;

        m_console.Run("show blocked owners");

        Assert.Equal(new[] { "Blocked owners in Region B: 0" }, m_console.Lines);
    }

    [Theory]
    [InlineData("block owner not-a-uuid")]
    [InlineData("unblock owner not-a-uuid")]
    public void ABadUuidChangesNothingAndSaysSo(string command)
    {
        m_moduleA.Block(OwnerId);
        m_console.ConsoleScene = m_sceneA;

        m_console.Run(command);

        Assert.Equal(new[] { "ERROR: not-a-uuid is not a valid uuid" }, m_console.Lines);
        Assert.Equal(new[] { OwnerId }, m_moduleA.GetBlockedOwners());
        Assert.Empty(m_moduleB.GetBlockedOwners());
    }

    [Fact]
    public void TheZeroUuidIsRefusedWithAMessage()
    {
        m_console.ConsoleScene = m_sceneA;

        m_console.Run($"block owner {UUID.Zero}");

        Assert.Equal(new[] { $"ERROR: {UUID.Zero} is not an owner and cannot be blocked." }, m_console.Lines);
        Assert.Empty(m_moduleA.GetBlockedOwners());
    }

    [Theory]
    [InlineData("block owner")]
    [InlineData("unblock owner")]
    public void AMissingUuidPrintsTheUsage(string command)
    {
        m_console.ConsoleScene = m_sceneA;

        m_console.Run(command);

        Assert.Equal(new[] { $"Usage: {command} <UUID>" }, m_console.Lines);
        Assert.Empty(m_moduleA.GetBlockedOwners());
    }

    // Each region writes a heading line and then its owners. The owners come from a set, so they are
    // compared sorted within their region.
    private static string[] SortOwnerLines(List<string> lines)
    {
        List<string> result = new();
        List<string> owners = new();
        foreach (string line in lines)
        {
            if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                owners.Add(line);
                continue;
            }
            owners.Sort(StringComparer.Ordinal);
            result.AddRange(owners);
            owners.Clear();
            result.Add(line);
        }
        owners.Sort(StringComparer.Ordinal);
        result.AddRange(owners);
        return result.ToArray();
    }

    /// <summary>
    /// A console that dispatches through the real <see cref="Commands"/> and records what is written.
    /// </summary>
    private sealed class RecordingConsole : ICommandConsole
    {
#pragma warning disable 0067
        public event OnOutputDelegate OnOutput = delegate { };
#pragma warning restore 0067

        public List<string> Lines { get; } = new();

        public ICommands Commands { get; } = new Commands();

        public string DefaultPrompt { get; set; } = string.Empty;

        public IScene ConsoleScene { get; set; } = null!;

        public void Run(string commandLine)
        {
            Lines.Clear();
            Commands.Resolve(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public void Output(string format) => Lines.Add(format);
        public void Output(string format, params object[] components) => Lines.Add(string.Format(format, components));

        public void Prompt() { }
        public void RunCommand(string cmd) { }
        public string ReadLine(string p, bool isCommand, bool e) => "";
        public void WriteLine(string s) => Lines.Add(s);
        public void ReadConfig(IConfigSource configSource) { }
        public void SetCntrCHandler(OnCntrCCelegate handler) { }
        public string Prompt(string p) => "";
        public string Prompt(string p, string def) => "";
        public string Prompt(string p, List<char> excludedCharacters) => "";
        public string Prompt(string p, string def, List<char> excludedCharacters, bool echo = true) => "";
        public string Prompt(string prompt, string defaultresponse, List<string> options) => "";
    }
}
