/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// With JOLT_TEST_ACCEPT_UNRECORDED_NATIVE=1 the suite loads whatever joltc sits under the test output's
/// runtimes/&lt;rid&gt;/native/, recorded or not, so it can be run against another build of the native (put there by
/// hand). The tests that check the file in the output against the module's record still fail then, as they should.
/// </summary>
internal static class TestNativeOverride
{
    public const string Variable = "JOLT_TEST_ACCEPT_UNRECORDED_NATIVE";

    [ModuleInitializer]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable(Variable) == "1")
            JoltNative.AcceptUnrecordedForTest = true;
    }
}
