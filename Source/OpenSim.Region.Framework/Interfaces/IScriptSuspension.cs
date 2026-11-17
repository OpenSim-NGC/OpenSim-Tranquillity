/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// Optional for a script engine: says whether one of its scripts is suspended now (IScriptModule.SuspendScript, for
/// example while its object crosses to another region). The region's "disable scripts" switch uses it to leave such a
/// script alone, so that allowing scripts again resumes only the scripts the switch itself suspended. An engine that
/// does not implement it has every script it holds suspended and resumed by the switch.
/// </summary>
public interface IScriptSuspension
{
    /// <summary>True when this engine runs the script and the script is suspended, or a suspend of it is pending.</summary>
    bool IsSuspended(UUID itemID);
}
