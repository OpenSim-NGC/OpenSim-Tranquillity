/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Framework.Tests;

/// <summary>
/// The compile target a viewer's script editor sends ("target" in the script upload capabilities) and how a region
/// keeps it in the low byte of the script item's flags, where Second Life keeps the script subtype.
/// </summary>
public class ScriptCompileTargetTests
{
    [Theory]
    [InlineData("luau", ScriptCompileTarget.Luau)]
    [InlineData("lsl-luau", ScriptCompileTarget.LslOnLuau)]
    [InlineData("mono", ScriptCompileTarget.Unspecified)]
    [InlineData("lsl2", ScriptCompileTarget.Unspecified)]
    [InlineData("", ScriptCompileTarget.Unspecified)]
    [InlineData(null, ScriptCompileTarget.Unspecified)]
    [InlineData("lsl-lua", ScriptCompileTarget.Unspecified)]
    [InlineData("lua", ScriptCompileTarget.Unspecified)]
    [InlineData("slua", ScriptCompileTarget.Unspecified)]
    public void TheViewersValueIsReadAsTheTargetItNames(string value, ScriptCompileTarget expected)
    {
        Assert.Equal(expected, ScriptCompileTargets.Parse(value));
    }

    [Theory]
    [InlineData("LUAU", ScriptCompileTarget.Luau)]
    [InlineData(" luau ", ScriptCompileTarget.Luau)]
    [InlineData("Lsl-Luau", ScriptCompileTarget.LslOnLuau)]
    public void TheViewersValueIsReadWithoutRegardToCaseOrSurroundingSpaces(string value, ScriptCompileTarget expected)
    {
        Assert.Equal(expected, ScriptCompileTargets.Parse(value));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau, "luau")]
    [InlineData(ScriptCompileTarget.LslOnLuau, "lsl-luau")]
    [InlineData(ScriptCompileTarget.Unspecified, "")]
    public void TheTargetIsNamedTheWayTheViewersCompilerListNamesIt(ScriptCompileTarget target, string expected)
    {
        Assert.Equal(expected, ScriptCompileTargets.ToViewerValue(target));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    [InlineData(ScriptCompileTarget.Unspecified)]
    public void TheTargetSurvivesBeingKeptInFlagsAndReadBack(ScriptCompileTarget target)
    {
        Assert.Equal(target, ScriptCompileTargets.FromFlags(ScriptCompileTargets.WithTarget(0u, target)));
        Assert.Equal(target, ScriptCompileTargets.FromFlags(ScriptCompileTargets.WithTarget(0x40000100u, target)));
    }

    [Fact]
    public void LuauIsTheValueSecondLifeUsesForTheLuaScriptSubtype()
    {
        Assert.Equal(1u, ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.Luau));
    }

    [Fact]
    public void OnlyTheSubtypeByteChangesAndEveryOtherFlagIsKept()
    {
        const uint others = 0x40000000u | 0x00000100u | 0x00200000u;

        uint luau = ScriptCompileTargets.WithTarget(others | 0x7u, ScriptCompileTarget.Luau);
        Assert.Equal(others | 1u, luau);

        uint lslOnLuau = ScriptCompileTargets.WithTarget(others, ScriptCompileTarget.LslOnLuau);
        Assert.Equal(others, lslOnLuau & ~ScriptCompileTargets.SubtypeMask);
        Assert.NotEqual(0u, lslOnLuau & ScriptCompileTargets.SubtypeMask);
        Assert.NotEqual(luau, lslOnLuau);
    }

    [Fact]
    public void NoTargetClearsTheSubtypeAndKeepsEveryOtherFlag()
    {
        const uint others = 0x40000000u | 0x00000100u;
        uint kept = ScriptCompileTargets.WithTarget(others | 1u, ScriptCompileTarget.Unspecified);
        Assert.Equal(others, kept);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40000000u)]
    [InlineData(0x7fu)]
    [InlineData(0xffu)]
    public void FlagsWithAnotherSubtypeHaveNoTarget(uint flags)
    {
        Assert.Equal(ScriptCompileTarget.Unspecified, ScriptCompileTargets.FromFlags(flags));
    }
}
