/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Framework;

/// <summary>
/// The text a new script gets when the viewer creates it with the Lua subtype (see <see cref="ScriptCompileTargets"/>).
/// It does what <see cref="Constants.DefaultScriptText"/> does for an LSL script: it says "Script running" on channel 0
/// when the script starts. Code at the top level of a Lua script runs when the script starts or is reset.
/// </summary>
public static class DefaultLuaScript
{
    /// <summary>The id of the asset holding <see cref="Text"/>; the region stores it the first time a Lua script needs it.</summary>
    public static readonly UUID ID = new("bab9b38e-0ac0-4c46-91ea-9cec2f9c2192");

    public const string Text =
        "--!slua\n" +
        "-- A new Lua script. The code at the top level runs when the script starts or is reset.\n" +
        "-- Handlers for events are added with LLEvents:on, for example LLEvents:on(\"timer\", function() end).\n" +
        "ll.Say(0, \"Script running\")\n";
}
