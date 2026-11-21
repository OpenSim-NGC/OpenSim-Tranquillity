/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.Framework.Scenes;

/// <summary>What <see cref="Scene.SetScriptEngine"/> did with a request to choose a script's engine.</summary>
public enum ScriptEngineChoiceResult
{
    /// <summary>The choice was stored and the script restarted in the engine it now places the script in.</summary>
    Changed,

    /// <summary>The script already had that choice; nothing was stored or restarted.</summary>
    Unchanged,

    /// <summary>No such prim, or no script of that id in it.</summary>
    NotFound,

    /// <summary>The person asking may not choose this script's engine.</summary>
    NotAllowed,

    /// <summary>The region keeps no choices ([Startup] ScriptEngineChoice = false).</summary>
    Disabled,

    /// <summary>The target names no engine in the fixed table.</summary>
    UnknownEngine,

    /// <summary>A script may not change its own engine: the change restarts the script it would be running in.</summary>
    OwnScript,
}
