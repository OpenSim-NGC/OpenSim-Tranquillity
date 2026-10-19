/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A sled 40 m up that is given its vehicle type: whether it is held still for the first ReassertVehicleSeconds (0.27 s) or
// falls at once. The region's load window is the time before its first step (JoltScene.IsRegionLoading). A vehicle made
// while the region loads, by a script or restored from its record, is held: a body created then can free-fall for the
// whole load before the controller first steps. Once the region runs, llSetVehicleType gives the type's presets and
// nothing else: Second Life's page for the call describes no change to the vehicle's velocity, and Halcyon's type change
// (VehicleDynamics.VehicleTypeChanged, ResetDynamics) copies the actor's current velocity into the vehicle's state and does
// not zero it, so no hold is kept under either vehicle behaviour.
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class VehicleTypeCallHoldTests
{
    private readonly ITestOutputHelper _out;
    public VehicleTypeCallHoldTests(ITestOutputHelper output) => _out = output;

    private const double Rate = 45.0;
    private const float G = 9.81f;
    // The heartbeat whose sample is read: 0.222 s after the run's start, inside the 0.27 s hold and well past the first step.
    private const int Read = 10;

    private static readonly Vector3 Size = new(2f, 1f, 0.5f);

    private static RunResult Run(VehicleRoute route, bool warmUp, string behaviour)
    {
        var sc = new Scenario
        {
            Name = "sled-in-the-air",
            DefaultDuration = _ => 1f,
            Setup = r =>
            {
                r.AddBox(Size, new Vector3(128f, 128f, Course.Ground + 40f), Quaternion.Identity);
                r.MakeVehicle(SharedVehicle.TYPE_SLED);
            },
        };
        var o = new HarnessOptions { RateHz = Rate, PhysicsRateHz = Rate, WarmUpStep = warmUp, VehicleRoute = route };
        o.Jolt["VehicleBehaviour"] = behaviour;
        return Harness.Harness.Run(sc, o);
    }

    private float FallingSpeed(RunResult r)
    {
        float vz = r.Samples[Read - 1].Velocity.Z;
        _out.WriteLine($"vertical speed after {Read} heartbeats: {vz:0.000} m/s (free fall {-G * Read / Rate:0.000})");
        return vz;
    }

    // The script's own llSetVehicleType in a running region: the sled starts falling at once, as a body does.
    [Theory]
    [InlineData("SecondLife")]
    [InlineData("InWorldz")]
    public void A_sled_that_sets_its_type_in_a_running_region_falls_at_once(string behaviour)
    {
        float vz = FallingSpeed(Run(VehicleRoute.None, warmUp: true, behaviour));
        Assert.True(vz < -0.8f * G * Read / (float)Rate, $"{behaviour}: vertical speed {vz:0.000} m/s, held instead of falling");
    }

    // Made while the region loads (before its first step), by the script or restored from the record: held, as before.
    [Theory]
    [InlineData(VehicleRoute.None, "SecondLife")]
    [InlineData(VehicleRoute.None, "InWorldz")]
    [InlineData(VehicleRoute.RegionStart, "SecondLife")]
    [InlineData(VehicleRoute.RegionStart, "InWorldz")]
    public void A_sled_made_while_the_region_loads_is_still_held(VehicleRoute route, string behaviour)
    {
        float vz = FallingSpeed(Run(route, warmUp: false, behaviour));
        Assert.True(vz > -0.4f * G * Read / (float)Rate, $"{route} {behaviour}: vertical speed {vz:0.000} m/s, fell instead of being held");
    }
}
