/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenMetaverse;

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

        // LSL on Lua leaves the subtype byte as it is for an LSL script and sets its own flag bit
        uint lslOnLuau = ScriptCompileTargets.WithTarget(others | 0x7u, ScriptCompileTarget.LslOnLuau);
        Assert.Equal(others | ScriptCompileTargets.LslOnLuauFlag, lslOnLuau);
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
    [InlineData(2u)]
    [InlineData(3u)]
    public void FlagsWithAnotherSubtypeHaveNoTarget(uint flags)
    {
        Assert.Equal(ScriptCompileTarget.Unspecified, ScriptCompileTargets.FromFlags(flags));
    }

    [Fact]
    public void LslOnLuauHasAFlagBitOfItsOwnAndTheSubtypeByteStaysAsForLsl()
    {
        uint flags = ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.LslOnLuau);

        Assert.NotEqual(0u, ScriptCompileTargets.LslOnLuauFlag);
        Assert.Equal(ScriptCompileTargets.LslOnLuauFlag, flags);
        Assert.Equal(0u, flags & ScriptCompileTargets.SubtypeMask);
        Assert.Equal(ScriptCompileTarget.LslOnLuau, ScriptCompileTargets.FromFlags(flags));
    }

    [Fact]
    public void TheLslOnLuauFlagIsOneBitThatNothingElseInAnItemsFlagsUses()
    {
        uint bit = ScriptCompileTargets.LslOnLuauFlag;

        Assert.NotEqual(0u, bit);
        Assert.Equal(1, BitOperations.PopCount(bit));
        Assert.Equal(0u, bit & 0x80000000u);   // flags are stored as a signed 32-bit column in some databases

        // every flag LibreMetaverse names for an inventory item, and the ones Second Life's viewer defines
        // (llinventorydefines.h: slam perm, slam sale, the five overwrites, multiple items, shared single reference, the subtype byte)
        foreach (InventoryItemFlags named in Enum.GetValues<InventoryItemFlags>())
            Assert.Equal(0u, bit & (uint)named);
        uint[] secondLife = { 0xff, 0x100, 0x1000, 0x10000, 0x20000, 0x40000, 0x80000, 0x100000, 0x200000, 0x40000000 };
        foreach (uint used in secondLife)
            Assert.Equal(0u, bit & used);

        // the flags the region sends a viewer are masked to 0x2000ff, so the viewer never sees the bit
        Assert.Equal(0u, bit & 0x2000ffu);
    }

    [Fact]
    public void TheLuaSubtypeWinsIfTheLslOnLuauFlagIsSetAsWell()
    {
        Assert.Equal(ScriptCompileTarget.Luau, ScriptCompileTargets.FromFlags(1u | ScriptCompileTargets.LslOnLuauFlag));
    }

    [Fact]
    public void AnotherTargetReplacesTheOneBeforeInTheSubtypeAndTheFlagBit()
    {
        uint lslOnLuau = ScriptCompileTargets.WithTarget(0x40000000u, ScriptCompileTarget.LslOnLuau);

        uint luau = ScriptCompileTargets.WithTarget(lslOnLuau, ScriptCompileTarget.Luau);
        Assert.Equal(0x40000001u, luau);

        uint back = ScriptCompileTargets.WithTarget(luau, ScriptCompileTarget.LslOnLuau);
        Assert.Equal(lslOnLuau, back);

        uint none = ScriptCompileTargets.WithTarget(back, ScriptCompileTarget.Unspecified);
        Assert.Equal(0x40000000u, none);
    }
}
