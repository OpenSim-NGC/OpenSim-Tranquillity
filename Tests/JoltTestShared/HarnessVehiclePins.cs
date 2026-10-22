/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The shipped [Jolt] section chooses <c>VehicleBehaviour = InWorldz</c>, and a harness run starts from it
/// (<see cref="HarnessOptions.ShippedConfig"/>). A test of the behaviour Second Life documents (the documented presets and
/// laws, which the step-rate, gravity, attractor and restore tests check against closed-form answers) pins the choice with
/// <see cref="SecondLife"/>; a test of InWorldz's behaviour sets <c>Jolt["VehicleBehaviour"]</c> to InWorldz itself.
/// </summary>
internal static class HarnessVehiclePins
{
    /// <summary>Runs with <c>[Jolt] VehicleBehaviour = SecondLife</c>, whatever the shipped default is.</summary>
    internal static HarnessOptions SecondLife(this HarnessOptions options)
    {
        options.Jolt["VehicleBehaviour"] = "SecondLife";
        return options;
    }
}
