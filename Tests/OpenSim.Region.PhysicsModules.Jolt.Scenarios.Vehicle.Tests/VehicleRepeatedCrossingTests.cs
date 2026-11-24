/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A scripted vehicle that crosses a region border more than once keeps its motor, and a script's later motor or
// rotation change on a vehicle that has crossed takes effect, exactly as on a vehicle that has not crossed. The crossing is
// replayed as core does it: the engine writes the motors' running state into the record (SceneObjectGroup.CrossAsync), the
// record goes through the object's XML2, and the next region builds the vehicle from it (SOPVehicle.SetVehicle).
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleRepeatedCrossingTests
{
    private readonly ITestOutputHelper _out;
    public VehicleRepeatedCrossingTests(ITestOutputHelper output) => _out = output;

    private const double Rate = 45.0;
    private const float Speed = 30f;
    private static readonly Vector3 Size = new(4f, 2f, 1f);
    private static readonly Vector3 At = new(128f, 128f, 60f);

    // The test vehicle's script setup: the type, then the settings the aircraft and the boat scripts use.
    private static void ScriptSetup(Run r, SharedVehicle type)
    {
        r.MakeVehicle(type);
        // The aircraft script's settings. The boat is held up the same way (a floating boat has no ground under it here).
        r.SetFloat(SharedVehicle.BUOYANCY, 1f);
        if (type == SharedVehicle.TYPE_AIRPLANE)
            r.SetFloat(SharedVehicle.LINEAR_FRICTION_TIMESCALE, 1f);
        r.SetFloat(SharedVehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        r.SetFloat(SharedVehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
    }

    // A region crossing at the physics surface, as VehicleRestoreRouteTests.RunRoute does it.
    private static void Cross(Run r, Vector3? arrivalAt = null)
    {
        PhysicsActor old = r.Actor;
        old.CrossingStart();
        old.CaptureVehicleRunningState(ref r.VehiclePart.VehicleParams.vd);
        SOPVehicle saved = VehicleRestore.Saved(r.VehiclePart, VehicleRoute.Crossing);
        Vector3 pos = arrivalAt ?? old.Position;   // the neighbour's frame: the object lands at the far edge of the next region
        Quaternion rot = old.Orientation;
        Vector3 vel = old.Velocity;
        r.PhysicsScene.RemovePrim(old);
        r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, saved, pos, Size, rot, true, false, true, vel, Vector3.Zero,
                                              Run.ActorLocalId, a => { r.AsSimulatorAdds(a); a.Density = 1000f; });
        // The destination's part carries the record that arrived, as core's does.
        r.VehiclePart.VehicleParams = saved;
        r.VehiclePart.PhysActor = r.Actor;
    }

    private static float Along(Vector3 v) => v.X;

    /// <summary>Runs <paramref name="crossings"/> crossings, one second apart; returns the speed along x just before each
    /// and 0.8 s after each.</summary>
    private static (float before, float after)[] RunCrossings(SharedVehicle type, int crossings)
    {
        var result = new (float, float)[crossings];
        const int Every = 90;       // 2 s between crossings
        var sc = new Scenario
        {
            Name = "vehicle-repeated-crossing-" + type,
            DefaultDuration = _ => (Every * (crossings + 1) + 45) / (float)Rate,
            Setup = r =>
            {
                r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, null, At, Size, Quaternion.Identity, true, false, false, Vector3.Zero,
                                                      Vector3.Zero, Run.ActorLocalId, a => { r.AsSimulatorAdds(a); a.Density = 1000f; });
                r.ActorSize = Size;
                ScriptSetup(r, type);
                r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(Speed, 0f, 0f));
            },
            Input = r =>
            {
                int k = (int)Math.Round(r.Now * Rate);
                if (k < Every || (k % Every) != 0 && (k % Every) != (int)(0.8 * Rate))
                    return;
                int n = k / Every - 1;
                if (k % Every == 0)
                {
                    if (n < crossings)
                    {
                        result[n].Item1 = Along(r.Actor.Velocity);
                        Cross(r);
                    }
                }
                else
                {
                    n = k / Every - 1;
                    if (n < crossings)
                        result[n].Item2 = Along(r.Actor.Velocity);
                }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = Rate, SimulatorDefaults = true }.SecondLife());
        return result;
    }

    [Theory]
    [InlineData("Airplane")]
    public void A_vehicle_keeps_its_motor_through_three_crossings_with_no_script_push(string kind)
    {
        SharedVehicle type = kind == "Airplane" ? SharedVehicle.TYPE_AIRPLANE : SharedVehicle.TYPE_BOAT;
        var r = RunCrossings(type, 3);
        for (int i = 0; i < r.Length; i++)
            _out.WriteLine($"{kind} crossing {i + 1}: {r[i].before:F1} m/s before, {r[i].after:F1} m/s 0.8 s after");
        for (int i = 0; i < r.Length; i++)
            Assert.True(r[i].after > 0.7f * r[i].before && r[i].after > 5f,
                $"{kind}: after crossing {i + 1} the motor still drives ({r[i].before:F1} before, {r[i].after:F1} after)");
    }

    // What a script does after a crossing, 2 s after it: the action runs once at the first heartbeat of that second, and the
    // vehicle's velocity along x is read 1.5 s later. With crossings = 0 the same action is the control: no crossing.
    private static (float before, float after) RunAfterCrossing(SharedVehicle type, int crossings, Action<Run> action)
    {
        const int ActAt = 90 * 3;
        float before = 0f, after = 0f;
        var sc = new Scenario
        {
            Name = "vehicle-after-crossing-" + type,
            DefaultDuration = _ => (ActAt + 90) / (float)Rate,
            Setup = r =>
            {
                r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, null, At, Size, Quaternion.Identity, true, false, false, Vector3.Zero,
                                                      Vector3.Zero, Run.ActorLocalId, a => { r.AsSimulatorAdds(a); a.Density = 1000f; });
                r.ActorSize = Size;
                ScriptSetup(r, type);
                r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(Speed, 0f, 0f));
            },
            Input = r =>
            {
                int k = (int)Math.Round(r.Now * Rate);
                if (k > 0 && k % 90 == 0 && k / 90 <= crossings)
                    Cross(r);
                else if (k == ActAt)
                {
                    before = Along(r.Actor.Velocity);
                    action(r);
                }
                else if (k == ActAt + 60)
                    after = Along(r.Actor.Velocity);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = Rate, SimulatorDefaults = true }.SecondLife());
        return (before, after);
    }

    // llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, <-30,0,0>): the vehicle turns round and drives the other way.
    [Theory]
    [InlineData("Airplane", 0)]
    [InlineData("Airplane", 1)]
    public void A_motor_change_after_a_crossing_takes_effect(string kind, int crossings)
    {
        SharedVehicle type = kind == "Airplane" ? SharedVehicle.TYPE_AIRPLANE : SharedVehicle.TYPE_BOAT;
        var (before, after) = RunAfterCrossing(type, crossings, r => r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(-Speed, 0f, 0f)));
        _out.WriteLine($"{kind}, {crossings} crossings: {before:F1} m/s along x before the reversal, {after:F1} 1.5 s after");
        Assert.True(before > 5f, $"it was driving east ({before:F1})");
        Assert.True(after < 0f, $"the reversed motor drives it west ({after:F1})");
    }

    // llSetRot(llGetRot() * 180 degrees about z) on the moving vehicle: its motor, along its own x axis, now drives it west.
    [Theory]
    [InlineData("Airplane", 0)]
    [InlineData("Airplane", 1)]
    public void A_rotation_after_a_crossing_takes_effect(string kind, int crossings)
    {
        SharedVehicle type = kind == "Airplane" ? SharedVehicle.TYPE_AIRPLANE : SharedVehicle.TYPE_BOAT;
        var (before, after) = RunAfterCrossing(type, crossings, r =>
            r.Actor.Orientation = r.Actor.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));
        _out.WriteLine($"{kind}, {crossings} crossings: {before:F1} m/s along x before the turn, {after:F1} 1.5 s after");
        Assert.True(before > 5f, $"it was driving east ({before:F1})");
        Assert.True(after < 0f, $"turned round, the motor drives it west ({after:F1})");
    }

    // The test vehicle's whole sequence: it crosses, 0.3 s later turns round the way its script does (physics off, a half turn,
    // physics on, the type and its settings again, the motor), runs on, and crosses again, with no motor push after a crossing.
    // Returns the speed just before each crossing and 0.8 s after it.
    private static (float before, float after)[] RunWithTurnarounds(SharedVehicle type, int crossings)
    {
        var result = new (float, float)[crossings];
        const int Every = 90;
        var sc = new Scenario
        {
            Name = "vehicle-turnaround-crossing-" + type,
            DefaultDuration = _ => (Every * (crossings + 1) + 45) / (float)Rate,
            Setup = r =>
            {
                r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, null, At, Size, Quaternion.Identity, true, false, false, Vector3.Zero,
                                                      Vector3.Zero, Run.ActorLocalId, a => { r.AsSimulatorAdds(a); a.Density = 1000f; });
                r.ActorSize = Size;
                ScriptSetup(r, type);
                r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(Speed, 0f, 0f));
            },
            Input = r =>
            {
                int k = (int)Math.Round(r.Now * Rate);
                int leg = k / Every, at = k % Every;
                if (leg < 1 || leg > crossings)
                {
                    return;
                }
                if (at == 0)
                {
                    result[leg - 1].Item1 = r.Actor.Velocity.Length();
                    Cross(r);
                }
                else if (at == 36)
                    result[leg - 1].Item2 = r.Actor.Velocity.Length();
                else if (at == 40)
                    VehicleRestore.PhysicsOff(r.Actor);
                else if (at == 44)
                    r.Actor.Orientation = r.Actor.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
                else if (at == 48)
                    VehicleRestore.PhysicsOn(r.Actor);
                else if (at == 52)
                {
                    ScriptSetup(r, type);
                    r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(Speed, 0f, 0f));
                }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = Rate, SimulatorDefaults = true }.SecondLife());
        return result;
    }

    [Fact]
    public void An_aircraft_that_turns_round_between_crossings_keeps_its_motor_through_each()
    {
        var r = RunWithTurnarounds(SharedVehicle.TYPE_AIRPLANE, 4);
        for (int i = 0; i < r.Length; i++)
            _out.WriteLine($"crossing {i + 1}: {r[i].before:F1} m/s before, {r[i].after:F1} m/s 0.8 s after");
        for (int i = 0; i < r.Length; i++)
            Assert.True(r[i].after > 0.7f * r[i].before && r[i].after > 5f,
                $"after crossing {i + 1} the motor still drives ({r[i].before:F1} before, {r[i].after:F1} after)");
    }

    // A crossing puts the object down in the next region's own frame: after an east-going crossing it lands at the west edge,
    // after a west-going one at the east edge, wherever it was across the region (SceneObjectGroup.CrossAsync hands the
    // position over as the destination's coordinates). The vehicle's controller is new there. Its stall check reads the
    // rate of change of the position, so the rate must start from the arrival position, not from the origin: starting from
    // the origin, an arrival far out in x and near the border in y, heading back toward the origin, put a swing through
    // zero into the rate, which the check read as "stalled" and put the linear motor's clock far past its decay.
    private static (float before, float after) RunFarArrival(SharedVehicle type, Vector3 arrivalAt, string behaviour)
    {
        float before = 0f, after = 0f;
        const int CrossAt = 180;
        var sc = new Scenario
        {
            Name = "vehicle-far-arrival-" + type,
            DefaultDuration = _ => (CrossAt + 90) / (float)Rate,
            Setup = r =>
            {
                r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, null, At, Size,
                                                      Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),   // heading west
                                                      true, false, false, Vector3.Zero, Vector3.Zero, Run.ActorLocalId,
                                                      a => { r.AsSimulatorAdds(a); a.Density = 1000f; });
                r.ActorSize = Size;
                ScriptSetup(r, type);
                r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(Speed, 0f, 0f));   // along its own x: west
            },
            Input = r =>
            {
                int k = (int)Math.Round(r.Now * Rate);
                if (k == CrossAt)
                {
                    before = r.Actor.Velocity.Length();
                    Cross(r, arrivalAt);
                }
                else if (k == CrossAt + 72)
                    after = r.Actor.Velocity.Length();
            },
        };
        var options = new HarnessOptions { RateHz = Rate, SimulatorDefaults = true }.SecondLife();
        options.Jolt["VehicleBehaviour"] = behaviour;
        Harness.Harness.Run(sc, options);
        return (before, after);
    }

    // Arrival points: far out in x with y small (the swing's worst case), and far out in x with y about half the region
    // (the swing stays clear of the stall threshold, so this is the control).
    [Theory]
    [InlineData("InWorldz", 250f, 12f)]
    [InlineData("InWorldz", 250f, 136f)]
    [InlineData("SecondLife", 250f, 12f)]
    public void A_vehicle_that_lands_far_from_the_origin_keeps_its_motor(string behaviour, float x, float y)
    {
        var (before, after) = RunFarArrival(SharedVehicle.TYPE_AIRPLANE, new Vector3(x, y, 60f), behaviour);
        _out.WriteLine($"{behaviour}, lands at ({x}, {y}): {before:F1} m/s before, {after:F1} m/s 1.6 s after");
        Assert.True(before > 5f, $"it was flying ({before:F1})");
        Assert.True(after > 0.7f * before, $"the motor still drives it after landing ({before:F1} before, {after:F1} after)");
    }
}
