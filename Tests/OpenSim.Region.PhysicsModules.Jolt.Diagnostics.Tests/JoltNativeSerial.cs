/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests;

/// <summary>
/// Tests that step a real Jolt backend, or swap the process-wide console or logger, run one at a time: the backends
/// share one native job pool, and two Update calls on it at once can spin inside the native.
/// </summary>
[Xunit.CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JoltNativeSerial
{
    public const string Name = "Jolt native (serial)";
}
