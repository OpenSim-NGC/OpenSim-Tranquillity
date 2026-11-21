/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Framework.Tests;

/// <summary>
/// A script engine a person chose for a script ("engine:&lt;Name&gt;" as a save target), kept as a number from one fixed
/// table in spare bits of the script item's flags, and the rule for which saves change it.
/// </summary>
public class ScriptEngineChoiceTests
{
    [Fact]
    public void TheTableGivesEachEngineAFixedNumber()
    {
        // These numbers are stored with items and travel between grids: they must never change.
        Assert.Equal(1, ScriptEngineChoices.YEngine);
        Assert.Equal(2, ScriptEngineChoices.Phlox);
        Assert.Equal("YEngine", ScriptEngineChoices.NameFor(1));
        Assert.Equal("InWorldz.Phlox", ScriptEngineChoices.NameFor(2));
        Assert.Null(ScriptEngineChoices.NameFor(0));
        Assert.Null(ScriptEngineChoices.NameFor(3));
        Assert.Null(ScriptEngineChoices.NameFor(7));
    }

    [Theory]
    [InlineData("YEngine", 1)]
    [InlineData("yengine", 1)]
    [InlineData("InWorldz.Phlox", 2)]
    [InlineData("Phlox", 2)]
    [InlineData(" phlox ", 2)]
    [InlineData("SLua", 0)]
    [InlineData("XEngine", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void AnEngineIsFoundByItsNameOrAlias(string name, int expected)
    {
        Assert.Equal(expected, ScriptEngineChoices.CodeFor(name));
    }

    [Fact]
    public void TheChoiceUsesBits25To27AndNeverBit31()
    {
        Assert.Equal(0x0E000000u, ScriptEngineChoices.Mask);
        Assert.Equal(0u, ScriptEngineChoices.Mask & 0x80000000u);
        Assert.Equal(0u, ScriptEngineChoices.Mask & ScriptCompileTargets.LslOnLuauFlag);
        Assert.Equal(0u, ScriptEngineChoices.Mask & ScriptCompileTargets.SubtypeMask);
        Assert.Equal(0x02000000u, ScriptEngineChoices.WithChoice(0u, ScriptEngineChoices.YEngine));
        Assert.Equal(0x04000000u, ScriptEngineChoices.WithChoice(0u, ScriptEngineChoices.Phlox));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40000100u)]
    [InlineData(0x01000001u)]
    public void TheChoiceSurvivesBeingKeptInFlagsAndKeepsEveryOtherBit(uint other)
    {
        uint flags = ScriptEngineChoices.WithChoice(other, ScriptEngineChoices.Phlox);
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(flags));
        Assert.Equal(other, flags & ~ScriptEngineChoices.Mask);

        flags = ScriptEngineChoices.WithChoice(flags, ScriptEngineChoices.None);
        Assert.Equal(ScriptEngineChoices.None, ScriptEngineChoices.FromFlags(flags));
        Assert.Equal(other, flags);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(7u)]
    public void ANumberNotInTheTableIsIgnored(uint code)
    {
        uint flags = code << ScriptEngineChoices.Shift;
        Assert.Equal(ScriptEngineChoices.None, ScriptEngineChoices.FromFlags(flags));
        Assert.False(ScriptCompileTargets.CarriesChoice(flags));
    }

    [Fact]
    public void TheChoiceIsNeverSentToAViewer()
    {
        uint flags = ScriptEngineChoices.WithChoice(0x40000101u, ScriptEngineChoices.Phlox);
        Assert.Equal(0x40000101u, ScriptCompileTargets.ForViewer(flags));
        Assert.Equal(0u, ScriptCompileTargets.ForViewer(7u << ScriptEngineChoices.Shift));
    }

    [Theory]
    [InlineData("engine:Phlox", true, "Phlox")]
    [InlineData("ENGINE:YEngine", true, "YEngine")]
    [InlineData(" engine: InWorldz.Phlox ", true, "InWorldz.Phlox")]
    [InlineData("engine:", true, "")]
    [InlineData("mono", false, null)]
    [InlineData("luau", false, null)]
    [InlineData(null, false, null)]
    public void AnEngineTargetIsReadFromTheSave(string value, bool isEngine, string name)
    {
        Assert.Equal(isEngine, ScriptEngineChoices.IsEngineTarget(value));
        Assert.Equal(name, ScriptEngineChoices.EngineNameOf(value));
    }

    private static uint Engine(int code) => ScriptEngineChoices.WithChoice(0u, code);

    // The safety rule. lsl2 and mono are what every viewer that cannot name engines sends: they keep an engine choice and
    // clear a Lua target, as they did before. lsl-luau and luau choose Lua, so they replace an engine choice.
    // engine:<Name> chooses the engine and replaces a Lua target.
    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    [InlineData("MONO")]
    [InlineData("auto")]
    public void ASaveWithMonoOrLsl2KeepsASavedEngine(string target)
    {
        foreach (int code in new[] { ScriptEngineChoices.YEngine, ScriptEngineChoices.Phlox })
        {
            uint flags = ScriptCompileTargets.ApplySaveTarget(Engine(code) | 0x40000000u, target);
            Assert.Equal(code, ScriptEngineChoices.FromFlags(flags));
            Assert.Equal(Engine(code) | 0x40000000u, flags);
        }
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    public void ASaveWithMonoOrLsl2StillClearsALuaTarget(string target)
    {
        uint luau = ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.Luau);
        uint lslOnLuau = ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.LslOnLuau);
        Assert.Equal(0u, ScriptCompileTargets.ApplySaveTarget(luau, target));
        Assert.Equal(0u, ScriptCompileTargets.ApplySaveTarget(lslOnLuau, target));
    }

    [Theory]
    [InlineData("luau", ScriptCompileTarget.Luau)]
    [InlineData("lsl-luau", ScriptCompileTarget.LslOnLuau)]
    public void ASaveWithALuaTargetSetsItAndReplacesAnEngineChoice(string target, ScriptCompileTarget expected)
    {
        uint flags = ScriptCompileTargets.ApplySaveTarget(Engine(ScriptEngineChoices.Phlox), target);
        Assert.Equal(expected, ScriptCompileTargets.FromFlags(flags));
        Assert.Equal(ScriptEngineChoices.None, ScriptEngineChoices.FromFlags(flags));
    }

    [Theory]
    [InlineData("engine:YEngine", ScriptEngineChoices.YEngine)]
    [InlineData("engine:Phlox", ScriptEngineChoices.Phlox)]
    [InlineData("engine:InWorldz.Phlox", ScriptEngineChoices.Phlox)]
    public void ASaveWithAnEngineTargetSetsTheEngineAndReplacesALuaTarget(string target, int expected)
    {
        uint luau = ScriptCompileTargets.WithTarget(0x40000000u, ScriptCompileTarget.Luau);
        uint flags = ScriptCompileTargets.ApplySaveTarget(luau, target);
        Assert.Equal(expected, ScriptEngineChoices.FromFlags(flags));
        Assert.Equal(ScriptCompileTarget.Unspecified, ScriptCompileTargets.FromFlags(flags));
        Assert.Equal(0x40000000u, flags & ~ScriptEngineChoices.Mask);

        uint replaced = ScriptCompileTargets.ApplySaveTarget(Engine(ScriptEngineChoices.YEngine), target);
        Assert.Equal(expected, ScriptEngineChoices.FromFlags(replaced));
    }

    [Fact]
    public void AnEmptyEngineTargetClearsEveryChoice()
    {
        Assert.Equal(0x40000000u, ScriptCompileTargets.ApplySaveTarget(Engine(ScriptEngineChoices.Phlox) | 0x40000000u, "engine:"));
        Assert.Equal(0u, ScriptCompileTargets.ApplySaveTarget(ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.Luau), "engine:"));
    }

    [Theory]
    [InlineData("engine:NoSuchEngine")]
    [InlineData("engine:SLua")]
    public void AnEngineTargetNamingNoEngineInTheTableChangesNothing(string target)
    {
        uint before = Engine(ScriptEngineChoices.Phlox) | 0x40000000u;
        Assert.Equal(before, ScriptCompileTargets.ApplySaveTarget(before, target));
        uint luau = ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.Luau);
        Assert.Equal(luau, ScriptCompileTargets.ApplySaveTarget(luau, target));
    }

    [Fact]
    public void ASaveWithNoTargetChangesNothing()
    {
        uint before = Engine(ScriptEngineChoices.Phlox) | 0x40000000u;
        Assert.Equal(before, ScriptCompileTargets.ApplySaveTarget(before, null));
    }

    [Fact]
    public void TheRegionMayResolveEngineNamesItself()
    {
        // The region passes its own lookup: a loaded engine's header alias, or 0 when engine choice is turned off.
        Func<string, int> off = _ => ScriptEngineChoices.None;
        uint before = Engine(ScriptEngineChoices.YEngine);
        Assert.Equal(before, ScriptCompileTargets.ApplySaveTarget(before, "engine:Phlox", off));

        Func<string, int> aliases = n => n == "Ph" ? ScriptEngineChoices.Phlox : ScriptEngineChoices.CodeFor(n);
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(ScriptCompileTargets.ApplySaveTarget(0u, "engine:Ph", aliases)));
    }

    [Fact]
    public void AnEngineChoiceCountsAsAChoiceTheRegionMade()
    {
        Assert.False(ScriptCompileTargets.CarriesChoice(0x40000100u));
        Assert.True(ScriptCompileTargets.CarriesChoice(Engine(ScriptEngineChoices.YEngine)));
        Assert.True(ScriptCompileTargets.CarriesChoice(ScriptCompileTargets.WithTarget(0u, ScriptCompileTarget.LslOnLuau)));
    }
}
