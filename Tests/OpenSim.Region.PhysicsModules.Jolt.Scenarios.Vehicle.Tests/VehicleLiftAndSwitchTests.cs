/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Two vehicle behaviours, run through the harness as the simulator builds a region, under both [Jolt] VehicleBehaviour
// values and at one step per heartbeat and at 45 Hz steps:
// - what holds an airplane up. Its preset has no buoyancy; its lift is linear deflection (the type's documented
//   definition: "uses linear deflection for lift"), which turns the velocity toward the nose. A level airplane therefore
//   sinks, and the higher the nose, the less it sinks, until the nose is high enough to climb;
// - a vehicle stopped, switched non-physical, moved and switched physical again with its velocity zeroed, as
//   SceneObjectPart.Stop does when physics goes off, leaves at rest.
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class VehicleLiftAndSwitchTests
{
    private readonly ITestOutputHelper _out;
    public VehicleLiftAndSwitchTests(ITestOutputHelper output) => _out = output;

    private static RunResult FlyAirplane(float pitchDegrees, double physicsRate, string behaviour, float seconds = 8f)
    {
        var sc = new Scenario
        {
            Name = "airplane-pitched",
            DefaultDuration = _ => seconds,
            Setup = r =>
            {
                // Nose up about the world's y, as the harness's banking scenarios tilt theirs.
                r.AddBox(new Vector3(3f, 3f, 0.5f), new Vector3(30f, 128f, Course.Ground + 60f),
                         Quaternion.CreateFromAxisAngle(Vector3.UnitY, -pitchDegrees * MathF.PI / 180f));
                r.MakeVehicle(SharedVehicle.TYPE_AIRPLANE);
                r.Actor.Velocity = new Vector3(15f * MathF.Cos(pitchDegrees * MathF.PI / 180f), 0f, 15f * MathF.Sin(pitchDegrees * MathF.PI / 180f));
            },
            Input = r => r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(15f, 0f, 0f)),
        };
        var o = new HarnessOptions { RateHz = 11, PhysicsRateHz = physicsRate }.SecondLife();
        o.Jolt["VehicleBehaviour"] = behaviour;
        return Harness.Harness.Run(sc, o);
    }

    [Theory]
    [InlineData("SecondLife", 0.0)]
    [InlineData("InWorldz", 0.0)]
    [InlineData("SecondLife", 45.0)]
    [InlineData("InWorldz", 45.0)]
    public void A_level_airplane_sinks_and_a_nose_up_one_holds_or_gains_height(string behaviour, double physicsRate)
    {
        float lastEnd = float.MinValue;
        // Under InWorldz an airplane held 10 degrees nose up holds its nose: the attractor goes dormant once the tilt has stopped
        // changing (VehicleDynamics.cs:1255-1264), and it flies on at its height (ends 88.7 m at one step per heartbeat, 67.6 m at
        // 45 Hz, from 85 m), where at 15 degrees and up the tilt keeps changing, the attractor stays awake and the nose comes
        // down. The controller run once per step had the tilt jitter by more than the dormancy limit at 10 degrees and so was
        // never dormant there (25.5 m and 29.8 m). The ends rise with the pitch from 15 degrees on, so that is where InWorldz is read.
        float[] pitches = behaviour == "InWorldz" ? new[] { 0f, 15f, 20f, 30f, 40f } : new[] { 0f, 10f, 20f, 30f, 40f };
        foreach (float pitch in pitches)
        {
            RunResult run = FlyAirplane(pitch, physicsRate, behaviour);
            Sample at2 = run.Samples.First(s => s.T >= 2.0), at4 = run.Samples.First(s => s.T >= 4.0), end = run.Samples[^1];
            _out.WriteLine($"{behaviour} p{physicsRate} pitch {pitch,2}: z {run.Samples[0].Position.Z:0.0} -> t2 {at2.Position.Z:0.0} (vz {at2.Velocity.Z:0.00}, tilt {at2.Tilt:0.0}) t4 {at4.Position.Z:0.0} (vz {at4.Velocity.Z:0.00}, tilt {at4.Tilt:0.0}) end t{end.T:0.0} z {end.Position.Z:0.0} speed {end.Speed:0.0}");
            // The higher the nose, the higher it ends.
            Assert.True(end.Position.Z >= lastEnd - 0.5f, $"{behaviour} p{physicsRate} pitch {pitch}: ends at {end.Position.Z:0.0}, lower than a lower nose ({lastEnd:0.0})");
            lastEnd = end.Position.Z;
            float start = run.Samples[0].Position.Z;
            if (pitch == 0f)
                Assert.True(at4.Velocity.Z < -6f && end.Position.Z < Course.Ground + 5f, $"a level airplane sinks to the ground: vz {at4.Velocity.Z:0.00} at 4 s, ends at {end.Position.Z:0.0}");
            if (pitch == 40f)
                Assert.True(end.Position.Z > start - 1f, $"an airplane with its nose 40 degrees up holds its height: {start:0.0} -> {end.Position.Z:0.0}");
        }
    }

    // A weightless sled with a motor 2 m/s down gets it, then is stopped (velocity zeroed while physical, as the part does),
    // its vehicle cleared, physics switched off, moved home, and switched on again 0.3 s later with the vehicle set once more
    // and no motor. Does it keep the old velocity?
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_stopped_vehicle_switched_off_moved_and_on_again_leaves_at_rest(double physicsRate)
    {
        Vector3 home = new(128f, 128f, Course.Ground + 60f);
        var sc = new Scenario
        {
            Name = "off-on",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                r.AddBox(new Vector3(1f, 1f, 1f), home, Quaternion.Identity);
                r.MakeVehicle(SharedVehicle.TYPE_SLED);
                r.SetFloat(SharedVehicle.BUOYANCY, 1f);
                r.SetFloat(SharedVehicle.LINEAR_MOTOR_TIMESCALE, 0.5f);
                r.SetFloat(SharedVehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 100f);
            },
            Input = r =>
            {
                PhysicsActor a = r.Actor;
                if (r.Now < 2.0)
                    r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, new Vector3(0f, 0f, -2f));
                else if (Math.Abs(r.Now - 2.0) < 0.01)
                {
                    a.Velocity = Vector3.Zero;
                    a.RotationalVelocity = Vector3.Zero;
                    a.IsPhysical = false;
                    a.VehicleType = 0;
                    a.Position = home;
                }
                else if (r.Now >= 2.3 && !_on)
                {
                    _on = true;
                    a.IsPhysical = true;
                    a.VehicleType = (int)SharedVehicle.TYPE_SLED;
                    a.VehicleFloatParam((int)SharedVehicle.BUOYANCY, 1f);
                }
            },
        };
        _on = false;
        RunResult run = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11, PhysicsRateHz = physicsRate }.SecondLife());
        Sample before = run.Samples.Last(s => s.T <= 2.0);
        var after = run.Samples.Where(s => s.T > 2.3).Take(4).ToList();
        _out.WriteLine($"p{physicsRate}: vz before {before.Velocity.Z:0.000}; vz at the first heartbeats after physics on: " +
                       string.Join(" ", after.Select(s => s.Velocity.Z.ToString("0.000"))) + $"; z at end {run.Samples[^1].Position.Z:0.00} (home {home.Z:0.00})");
        Assert.True(before.Velocity.Z < -1.5f, $"the motor had it moving down: {before.Velocity.Z:0.000}");
        Assert.All(after, s => Assert.True(Math.Abs(s.Velocity.Z) < 0.01f, $"vz {s.Velocity.Z:0.000} after physics on"));
        Assert.True(Math.Abs(run.Samples[^1].Position.Z - home.Z) < 0.05f, "it stays where it was put");
    }

    private bool _on;
}
