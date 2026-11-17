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
    /// The flags of an inventory item as a viewer is told them. <see cref="LslOnLuauFlag"/> is the region's own
    /// record of a script's target; no viewer knows it, so it never leaves the region. Every other bit, the script
    /// subtype in the low byte among them, is sent as it is.
    /// </summary>
    public static uint ForViewer(uint flags) => flags & ~LslOnLuauFlag;

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
