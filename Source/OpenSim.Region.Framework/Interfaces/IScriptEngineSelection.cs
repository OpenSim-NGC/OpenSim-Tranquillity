/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// Optional hooks that let a script engine take part in <c>Scene.ResolveScriptEngine</c> beyond the exact engine name
/// on a script's first line. An engine that does not implement it is selected as before: by its name in the header,
/// by saved state, or as the default.
/// </summary>
public interface IScriptEngineSelection
{
    /// <summary>
    /// Further names a first line may use for this engine, compared as written (case matters) like
    /// <see cref="IScriptModule.ScriptEngineName"/>. A header naming one of them gives the script to this engine
    /// exactly as the engine's own name does.
    /// </summary>
    IReadOnlyList<string> HeaderAliases { get; }

    /// <summary>
    /// Whether this engine takes a script whose first line names no loaded engine and which no engine holds saved state
    /// for. It is asked only when <paramref name="scriptSource"/> is not empty, after the header and saved-state steps,
    /// and the first engine that says yes gets the script; when none does, the default engine runs it.
    /// </summary>
    bool ClaimsScript(string scriptSource);

    /// <summary>
    /// Whether this engine runs scripts a viewer saved with the given compile target (the viewer's compiler list:
    /// "luau" or "lsl-luau"). A script item that carries such a target goes to the first engine that says yes, ahead
    /// of the script's first line, unless another engine holds saved state for the item and asset. Never asked for
    /// <see cref="ScriptCompileTarget.Unspecified"/>. The default is no, so an engine written before this member
    /// existed is selected as before.
    /// </summary>
    bool AcceptsCompileTarget(ScriptCompileTarget target) => false;
}
