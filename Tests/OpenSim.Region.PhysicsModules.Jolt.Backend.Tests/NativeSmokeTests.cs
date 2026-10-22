/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>Can a test host load the joltc the build ships and stand a PhysicsSystem up at all?</summary>
public class NativeSmokeTests
{
    [Fact]
    public void A_backend_initialises_and_steps()
    {
        var b = new JoltPhysicsBackend();
        b.Initialize(new PhysicsBackendSettings());
        b.Dispose();
    }
}
