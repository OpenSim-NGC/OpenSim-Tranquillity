/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit.Sdk;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Marks a test class whose tests check a controller's analytic law (a motor's ramp, a friction timescale's decay, a
/// hover or attractor spring). Their runs take the earlier material keys (see <see cref="MaterialPins"/>), so the vehicle
/// is acted on by its controller alone. Tests without it run on the module's shipped defaults. <see cref="BasicForces"/>
/// also sets <c>[Jolt] BasicForcesBehaviour</c> for a class that tests one behaviour's laws; a subclass that runs the same
/// tests under the other behaviour sets <see cref="MaterialPins.BasicForces"/> in its constructor, which wins.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PinnedMaterialsAttribute : BeforeAfterTestAttribute
{
    /// <summary>The <c>BasicForcesBehaviour</c> the class's runs take (SecondLife or InWorldz); null leaves the shipped default.</summary>
    public string BasicForces { get; set; }

    public override void Before(MethodInfo methodUnderTest)
    {
        MaterialPins.Enabled = true;
        MaterialPins.BasicForces ??= BasicForces;
    }

    public override void After(MethodInfo methodUnderTest)
    {
        MaterialPins.Enabled = false;
        MaterialPins.BasicForces = null;
    }
}
