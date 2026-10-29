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
/// </summary>
public static class DefaultLuaScript
{
    public static readonly UUID ID = UUID.Zero;

    public const string Text = "";
}
