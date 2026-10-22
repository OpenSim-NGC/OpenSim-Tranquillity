/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Angular deflection turns a vehicle's nose toward its velocity. A vehicle that is free to turn and backs along its own axis
/// has its tail toward the velocity: the tail is what turns toward it, and the vehicle does not swing round to face the
/// way it is going. Pure (a body with no engine), parallel.
/// </summary>
public class ReverseAngularDeflectionTests
{
    private readonly ITestOutputHelper _out;
    public ReverseAngularDeflectionTests(ITestOutputHelper output) { _out = output; }

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    // The angle between the end of the vehicle that leads (its tail when it backs) and the way it moves.
    private static double LeadToVelocity(BehaviourRig rig, bool backing)
    {
        Vector3 tail = Vector3.UnitX * rig.Body.Orientation;
        if (backing)
            tail = -tail;
        Vector3 velocity = rig.Body.LinearVelocity;
        Vector3 v = Vector3.Normalize(in velocity);
        return Math.Acos(Math.Clamp(Vector3.Dot(tail, v), -1f, 1f));
    }

    // Efficiency and timescale: the sled preset (1 / 1 s) and the car preset (0.5 / 2 s).
    [Theory]
    [InlineData(VehicleBehaviour.SecondLife, 1.0f, 1.0f, 45.0, false, 1f)]
    [InlineData(VehicleBehaviour.SecondLife, 1.0f, 1.0f, 11.0, false, 1f)]
    [InlineData(VehicleBehaviour.SecondLife, 0.5f, 2.0f, 45.0, false, 1f)]
    [InlineData(VehicleBehaviour.SecondLife, 0.5f, 2.0f, 11.0, true, 1f)]
    [InlineData(VehicleBehaviour.InWorldz, 1.0f, 1.0f, 45.0, false, 1f)]
    [InlineData(VehicleBehaviour.InWorldz, 1.0f, 1.0f, 11.0, false, 1f)]
    [InlineData(VehicleBehaviour.InWorldz, 0.5f, 2.0f, 45.0, false, 1f)]
    [InlineData(VehicleBehaviour.InWorldz, 0.5f, 2.0f, 11.0, true, 1f)]
    [InlineData(VehicleBehaviour.SecondLife, 1.0f, 1.0f, 45.0, false, 6f)]
    [InlineData(VehicleBehaviour.SecondLife, 1.0f, 1.0f, 11.0, false, 6f)]
    [InlineData(VehicleBehaviour.SecondLife, 0.5f, 2.0f, 45.0, false, 6f)]
    [InlineData(VehicleBehaviour.SecondLife, 0.5f, 2.0f, 11.0, false, 6f)]
    [InlineData(VehicleBehaviour.InWorldz, 1.0f, 1.0f, 45.0, false, 6f)]
    [InlineData(VehicleBehaviour.InWorldz, 1.0f, 1.0f, 11.0, false, 6f)]
    [InlineData(VehicleBehaviour.InWorldz, 0.5f, 2.0f, 45.0, false, 6f)]
    [InlineData(VehicleBehaviour.InWorldz, 0.5f, 2.0f, 11.0, false, 6f)]
    public void A_vehicle_backing_with_its_tail_a_little_off_its_velocity_turns_its_tail_onto_it(VehicleBehaviour behaviour,
        float efficiency, float timescale, double rate, bool noDeflectionUp, float sideways)
    {
        (double startB, double worstB, double endB, Vector3 noseB) = Run(behaviour, efficiency, timescale, rate, noDeflectionUp, -10f, sideways);
        (double startF, _, double endF, _) = Run(behaviour, efficiency, timescale, rate, noDeflectionUp, 10f, sideways);
        _out.WriteLine($"REVERSE-ANGULAR {behaviour} {efficiency}/{timescale}s {rate:0} Hz: tail to velocity {Deg(startB):0.0} deg at the start, "
            + $"worst {Deg(worstB):0.0}, after 10 s {Deg(endB):0.0} (a vehicle going forward the same way: nose to velocity {Deg(endF):0.0}); nose now {noseB}");
        Assert.True(worstB <= startB + 0.02, $"the tail went away from the velocity: {Deg(startB):0.0} deg to {Deg(worstB):0.0} deg");
        // Backing is going forward turned round: the tail follows the velocity as the nose follows it going forward.
        Assert.True(Math.Abs(endB - endF) < 2 * Math.PI / 180, $"backing ends {Deg(endB):0.0} deg off its velocity, going forward {Deg(endF):0.0} deg");
    }

    // The angles between the leading end (the nose going forward, the tail backing) and the velocity: at the start, the worst, and after 10 s.
    private static (double start, double worst, double end, Vector3 nose) Run(VehicleBehaviour behaviour, float efficiency, float timescale,
        double rate, bool noDeflectionUp, float ahead, float sideways)
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = behaviour }, Vehicle.TYPE_SLED, gravity: false);
        rig.Flag(ExtendedVehicleFlags.NoDeflectionUp, noDeflectionUp);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, efficiency);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, timescale);
        rig.Body.LinearVelocity = new Vector3(ahead, sideways, 0f);
        double Lead() => LeadToVelocity(rig, ahead < 0f);
        double start = Lead(), worst = start;
        for (int i = 0; i < (int)(10 * rate); i++)
        {
            rig.Step(1.0 / rate);
            worst = Math.Max(worst, Lead());
        }
        return (start, worst, Lead(), Vector3.UnitX * rig.Body.Orientation);
    }
}
