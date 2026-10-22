/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The balloon preset's wind reaction. Under VehicleBehaviour = InWorldz the balloon reacts to wind: its linear and angular
/// wind efficiencies (0.1 on each axis, 0.01 on x and y) act, since the preset sets REACT_TO_WIND as the InWorldz Advanced
/// Vehicle Tutorial's table of defaults does (the balloon row, flag column) and as Halcyon's balloon preset does
/// (VehicleDynamics.cs:2001). Under Second Life's behaviour the balloon is as it was: Second Life's balloon page has no
/// wind flag and the efficiencies are an InWorldz extension. Pure (a body with no engine, a stand-in wind module) and
/// parallel.
/// </summary>
public class VehicleBalloonWindTests
{
    private const double Step = 1.0 / 45;

    private static BehaviourRig Balloon(VehicleBehaviour behaviour, float windX)
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = behaviour }, Vehicle.TYPE_BALLOON, gravity: false);
        rig.Body.WindAt = _ => new Vector3(windX, 0f, 0f);
        return rig;
    }

    [Fact]
    public void An_InWorldz_balloon_reacts_to_wind_and_a_Second_Life_balloon_does_not()
    {
        Assert.True(Balloon(VehicleBehaviour.InWorldz, 0f).Car.Flags.HasFlag(ExtendedVehicleFlags.ReactToWind));
        Assert.False(Balloon(VehicleBehaviour.SecondLife, 0f).Car.Flags.HasFlag(ExtendedVehicleFlags.ReactToWind));
    }

    [Fact]
    public void An_InWorldz_balloon_drifts_with_the_wind()
    {
        BehaviourRig balloon = Balloon(VehicleBehaviour.InWorldz, 10f);
        balloon.Run(Step, 45 * 4);
        // 10 m/s of wind at 0.1 is 1 m/s every second, less the 7 steps before the first read and the balloon's own
        // friction; it is still well under the wind's speed.
        Assert.InRange(balloon.Body.LinearVelocity.X, 0.5f, 10f);
        Assert.True(balloon.Body.Position.X > 128f + 1f, $"drifted to x {balloon.Body.Position.X:0.00}");
    }

    [Fact]
    public void A_Second_Life_balloon_does_not_drift_in_the_wind()
    {
        BehaviourRig balloon = Balloon(VehicleBehaviour.SecondLife, 10f);
        balloon.Run(Step, 45 * 4);
        Assert.Equal(0f, balloon.Body.LinearVelocity.X);
        Assert.Equal(128f, balloon.Body.Position.X);
    }

    // A script's llSetVehicleType gives the preset again, flag and all; a script that sets the flag itself keeps it.
    [Fact]
    public void An_InWorldz_balloon_script_can_still_take_the_flag_off()
    {
        BehaviourRig balloon = Balloon(VehicleBehaviour.InWorldz, 10f);
        balloon.Car.ProcessVehicleFlags(0x20000, remove: true);   // REACT_TO_WIND
        balloon.Run(Step, 45 * 2);
        Assert.Equal(0f, balloon.Body.LinearVelocity.X);
    }
}
