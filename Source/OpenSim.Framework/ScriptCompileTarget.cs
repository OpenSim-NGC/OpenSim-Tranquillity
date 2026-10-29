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

    /// <summary>"lsl-luau": a script written in LSL that runs on the Luau virtual machine.</summary>
    LslOnLuau = 2,
}

/// <summary>
/// Reading a viewer's compile target, keeping it with a script item and naming it again for the viewer.
/// </summary>
/// <remarks>
/// The target is kept in the low byte of the item's flags, where Second Life keeps a script item's subtype
/// (its viewer reads "Lua" from the value 1: llinventorydefines.h II_FLAGS_SUBTYPE_MASK, llinventorytype.h SST_LUA).
/// Flags are already stored with task and agent inventory items, so keeping the target needs no new column.
/// </remarks>
public static class ScriptCompileTargets
{
    /// <summary>The bits of an item's flags that hold the subtype.</summary>
    public const uint SubtypeMask = 0xff;

    /// <summary>The flag bit that marks a script as "LSL on Lua" (<see cref="ScriptCompileTarget.LslOnLuau"/>).</summary>
    public const uint LslOnLuauFlag = 0u;

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

    /// <summary>The target kept in an item's flags; a subtype this code does not know is <see cref="ScriptCompileTarget.Unspecified"/>.</summary>
    public static ScriptCompileTarget FromFlags(uint flags) => (flags & SubtypeMask) switch
    {
        (uint)ScriptCompileTarget.Luau => ScriptCompileTarget.Luau,
        (uint)ScriptCompileTarget.LslOnLuau => ScriptCompileTarget.LslOnLuau,
        _ => ScriptCompileTarget.Unspecified,
    };

    /// <summary>The flags with the subtype replaced by the target's; every other bit is kept.</summary>
    public static uint WithTarget(uint flags, ScriptCompileTarget target) => (flags & ~SubtypeMask) | (uint)target;
}
