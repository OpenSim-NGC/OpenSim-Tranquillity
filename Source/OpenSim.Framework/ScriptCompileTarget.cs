/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace OpenSim.Framework;

/// <summary>
/// The script engine a viewer's script editor asked for when it saved a script, when that choice is one a region
/// has to remember. The editor's compiler list is LSL Legacy ("lsl2"), LSL on Mono ("mono"), LSL on Lua ("lsl-luau")
/// and Lua ("luau"). The first two are the same choice for this purpose (no explicit choice: the script's own first
/// line and the region's settings decide), so only the Luau pair is kept.
/// </summary>
public enum ScriptCompileTarget : byte
{
    /// <summary>"mono", "lsl2", a missing value, or a value not understood.</summary>
    Unspecified = 0,

    /// <summary>"luau": a script written in Lua. The value is the one Second Life's viewer uses as the script subtype for Lua (SST_LUA).</summary>
    Luau = 1,

    /// <summary>"lsl-luau": a script written in LSL that runs on the Luau virtual machine. (Its flag value is <see cref="ScriptCompileTargets.LslOnLuauFlag"/>, not this number.)</summary>
    LslOnLuau = 2,
}

/// <summary>
/// Reading a viewer's compile target, keeping it with a script item and naming it again for the viewer.
/// </summary>
/// <remarks>
/// "Lua" is kept in the low byte of the item's flags, where Second Life keeps a script item's subtype (its viewer reads
/// "Lua" from the value 1: llinventorydefines.h II_FLAGS_SUBTYPE_MASK, llinventorytype.h SST_LUA). "LSL on Lua" is not a
/// subtype Second Life's viewer defines, so it is a flag bit of its own and the subtype byte stays that of an LSL script.
/// Flags are already stored with task and agent inventory items, so keeping the target needs no new column.
/// </remarks>
public static class ScriptCompileTargets
{
    /// <summary>The bits of an item's flags that hold the subtype.</summary>
    public const uint SubtypeMask = 0xff;

    /// <summary>
    /// The flag bit that marks a script as "LSL on Lua" (<see cref="ScriptCompileTarget.LslOnLuau"/>). The script keeps the
    /// subtype of an LSL script (0 in the low byte). Second Life's viewer defines nothing at this bit: its item flags are the
    /// subtype byte, bits 8 and 12 (permission slam), bits 16 to 20 (permission overwrite), bit 21 (multiple items) and
    /// bit 30 (shared single reference). The region never tells a viewer this bit (see <see cref="ForViewer"/>).
    /// </summary>
    public const uint LslOnLuauFlag = 0x01000000u;

    /// <summary>
    /// The flags of an inventory item as a viewer is told them. <see cref="LslOnLuauFlag"/> and the saved engine
    /// (<see cref="ScriptEngineChoices.Mask"/>) are the region's own record of a script's target; no viewer knows
    /// those bits, so they never leave the region. Every other bit, the script subtype in the low byte among them,
    /// is sent as it is.
    /// </summary>
    public static uint ForViewer(uint flags) => flags & ~RegionOnlyFlags;

    /// <summary>
    /// The runtime a viewer is told for a script item with these flags, as Second Life's viewer reads it from an item's
    /// "script" map ("runtime": "lsl2", "mono" or "luau"; llinventory.cpp, llpreviewscript.cpp); null when none is sent.
    /// </summary>
    /// <remarks>
    /// "luau" is sent for an item that carries a Lua target: the Lua subtype, or the "LSL on Lua" flag bit, which
    /// <see cref="ForViewer"/> keeps from the viewer. Nothing is sent otherwise, so an item without a Lua target, an
    /// item with only a saved engine among them, reaches a viewer exactly as before. The caller sends it only for script items.
    /// </remarks>
    public static string ViewerRuntime(uint flags) => FromFlags(flags) == ScriptCompileTarget.Unspecified ? null : "luau";

    /// <summary>
    /// The flag bits only the region sets: <see cref="LslOnLuauFlag"/> and the saved engine. A viewer is never told them,
    /// so flags a viewer sends back are applied with these bits kept as the item has them.
    /// </summary>
    public const uint RegionOnlyFlags = LslOnLuauFlag | ScriptEngineChoices.Mask;

    /// <summary>
    /// Whether the item carries a choice that places it ahead of its first line: a Luau target or a saved engine
    /// from <see cref="ScriptEngineChoices"/>'s table. A number not in that table is not a choice.
    /// </summary>
    public static bool CarriesChoice(uint flags)
        => FromFlags(flags) != ScriptCompileTarget.Unspecified || ScriptEngineChoices.FromFlags(flags) != ScriptEngineChoices.None;

    /// <summary>
    /// The flags of a script item after a save that named <paramref name="viewerValue"/> as its target ("target" in
    /// the script upload capabilities). The rule keeps a viewer that cannot name engines from wiping an engine choice:
    /// <list type="bullet">
    /// <item>null (the save named no target): nothing changes.</item>
    /// <item>"luau" or "lsl-luau": that Lua target is kept and a saved engine is cleared.</item>
    /// <item>"engine:&lt;Name&gt;": that engine is kept and a Lua target is cleared; "engine:" with no name clears both.
    /// A name <paramref name="engineCode"/> does not know changes nothing.</item>
    /// <item>anything else, "lsl2" and "mono" among them: a Lua target is cleared, as before, and a saved engine is kept.
    /// "lsl2" and "mono" are all a viewer that cannot name engines can send.</item>
    /// </list>
    /// Every other bit of the flags is kept.
    /// </summary>
    /// <param name="engineCode">how the region turns a name into an engine number (its own aliases, or always
    /// <see cref="ScriptEngineChoices.None"/> when it keeps no choices); <see cref="ScriptEngineChoices.CodeFor"/> when null</param>
    public static uint ApplySaveTarget(uint flags, string viewerValue, Func<string, int> engineCode = null)
    {
        if (viewerValue is null)
            return flags;

        if (ScriptEngineChoices.IsEngineTarget(viewerValue))
        {
            string name = ScriptEngineChoices.EngineNameOf(viewerValue);
            if (name.Length == 0)
                return ScriptEngineChoices.WithChoice(WithTarget(flags, ScriptCompileTarget.Unspecified), ScriptEngineChoices.None);
            int code = (engineCode ?? ScriptEngineChoices.CodeFor)(name);
            if (ScriptEngineChoices.NameFor(code) is null)
                return flags;
            return ScriptEngineChoices.WithChoice(WithTarget(flags, ScriptCompileTarget.Unspecified), code);
        }

        ScriptCompileTarget target = Parse(viewerValue);
        uint withTarget = WithTarget(flags, target);
        return target == ScriptCompileTarget.Unspecified
            ? withTarget
            : ScriptEngineChoices.WithChoice(withTarget, ScriptEngineChoices.None);
    }

    /// <summary>The target a viewer named in the "target" field of a script upload; anything but "luau" and "lsl-luau" is <see cref="ScriptCompileTarget.Unspecified"/>.</summary>
    public static ScriptCompileTarget Parse(string viewerValue)
    {
        if (viewerValue is null)
            return ScriptCompileTarget.Unspecified;
        string value = viewerValue.Trim();
        if (value.Equals("luau", StringComparison.OrdinalIgnoreCase))
            return ScriptCompileTarget.Luau;
        if (value.Equals("lsl-luau", StringComparison.OrdinalIgnoreCase))
            return ScriptCompileTarget.LslOnLuau;
        return ScriptCompileTarget.Unspecified;
    }

    /// <summary>The value a viewer's compiler list uses for the target; empty for <see cref="ScriptCompileTarget.Unspecified"/>.</summary>
    public static string ToViewerValue(ScriptCompileTarget target) => target switch
    {
        ScriptCompileTarget.Luau => "luau",
        ScriptCompileTarget.LslOnLuau => "lsl-luau",
        _ => string.Empty,
    };

    /// <summary>
    /// The target kept in an item's flags: the Lua subtype (1 in the low byte) is <see cref="ScriptCompileTarget.Luau"/>, the
    /// <see cref="LslOnLuauFlag"/> bit is <see cref="ScriptCompileTarget.LslOnLuau"/>, anything else is
    /// <see cref="ScriptCompileTarget.Unspecified"/>. The Lua subtype wins if both are set.
    /// </summary>
    public static ScriptCompileTarget FromFlags(uint flags)
    {
        if ((flags & SubtypeMask) == (uint)ScriptCompileTarget.Luau)
            return ScriptCompileTarget.Luau;
        return (flags & LslOnLuauFlag) != 0 ? ScriptCompileTarget.LslOnLuau : ScriptCompileTarget.Unspecified;
    }

    /// <summary>The flags with the target replaced: the subtype byte and the <see cref="LslOnLuauFlag"/> bit are set for it alone; every other bit is kept.</summary>
    public static uint WithTarget(uint flags, ScriptCompileTarget target)
    {
        uint kept = flags & ~(SubtypeMask | LslOnLuauFlag);
        return target switch
        {
            ScriptCompileTarget.Luau => kept | (uint)ScriptCompileTarget.Luau,
            ScriptCompileTarget.LslOnLuau => kept | LslOnLuauFlag,
            _ => kept,
        };
    }
}

/// <summary>
/// The script engine a person chose for a script, kept with the script item as a number from one fixed table.
/// </summary>
/// <remarks>
/// <para>The number is kept in bits 25 to 27 of the item's flags (<see cref="Mask"/>), beside the "LSL on Lua" bit (24),
/// the way <see cref="ScriptCompileTargets"/> keeps a Lua target. Flags are already stored and copied with every task
/// and agent inventory item (the region databases, object XML for take, rez and OAR, and IAR), so the choice needs no
/// new column or element. Bit 31 is never used: a database that stores flags as a signed integer would read it back
/// as a negative number. Second Life's viewer defines nothing in bits 22 to 29 (llinventorydefines.h).</para>
/// <para>The table is fixed so that a number means the same engine on every grid an item reaches. A number is never
/// reused or renumbered; a new engine gets the next free number. A number this table does not know (from a later
/// version, for example) is ignored: the item keeps it, and the script is placed as if no engine were saved.</para>
/// <list type="table">
/// <item><term>0</term><description>none</description></item>
/// <item><term>1</term><description>YEngine</description></item>
/// <item><term>2</term><description>InWorldz.Phlox (also "Phlox")</description></item>
/// <item><term>3-7</term><description>free</description></item>
/// </list>
/// </remarks>
public static class ScriptEngineChoices
{
    /// <summary>The bits of an item's flags that hold the engine number (bits 25 to 27).</summary>
    public const uint Mask = 0x0E000000u;

    /// <summary>The position of the lowest bit of <see cref="Mask"/>.</summary>
    public const int Shift = 25;

    /// <summary>The start of a save target that names an engine: "engine:&lt;Name&gt;".</summary>
    public const string TargetPrefix = "engine:";

    /// <summary>No engine saved.</summary>
    public const int None = 0;

    /// <summary>YEngine (its ScriptEngineName "YEngine").</summary>
    public const int YEngine = 1;

    /// <summary>Phlox (its ScriptEngineName "InWorldz.Phlox").</summary>
    public const int Phlox = 2;

    // number, the engine's ScriptEngineName, further names a save may use
    private static readonly (int Code, string Name, string[] Aliases)[] s_table =
    {
        (YEngine, "YEngine", Array.Empty<string>()),
        (Phlox, "InWorldz.Phlox", new[] { "Phlox" }),
    };

    /// <summary>The ScriptEngineName of the engine with this number, or null for <see cref="None"/> and numbers not in the table.</summary>
    public static string NameFor(int code)
    {
        foreach (var entry in s_table)
        {
            if (entry.Code == code)
                return entry.Name;
        }
        return null;
    }

    /// <summary>The number of the engine with this name or alias, compared without regard to case or surrounding spaces; <see cref="None"/> when the table has no such engine.</summary>
    public static int CodeFor(string engineName)
    {
        string name = engineName?.Trim();
        if (string.IsNullOrEmpty(name))
            return None;
        foreach (var entry in s_table)
        {
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return entry.Code;
            foreach (string alias in entry.Aliases)
            {
                if (alias.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return entry.Code;
            }
        }
        return None;
    }

    /// <summary>The engine number kept in the flags; <see cref="None"/> when there is none or the number is not in the table.</summary>
    public static int FromFlags(uint flags)
    {
        int code = (int)((flags & Mask) >> Shift);
        return NameFor(code) is null ? None : code;
    }

    /// <summary>The flags with the engine number replaced; every other bit is kept.</summary>
    public static uint WithChoice(uint flags, int code)
        => (flags & ~Mask) | (((uint)code << Shift) & Mask);

    /// <summary>Whether a save target names an engine ("engine:&lt;Name&gt;", the prefix compared without regard to case).</summary>
    public static bool IsEngineTarget(string viewerValue)
        => viewerValue is not null && viewerValue.Trim().StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine name in an "engine:&lt;Name&gt;" target, trimmed (empty for "engine:"); null for any other target.</summary>
    public static string EngineNameOf(string viewerValue)
        => IsEngineTarget(viewerValue) ? viewerValue.Trim()[TargetPrefix.Length..].Trim() : null;
}
