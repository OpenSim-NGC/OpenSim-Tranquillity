/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical vehicle that crossed into the region with riders is held still like any physical object
/// (PhysicsActor.HoldStill), and is the same vehicle when the hold ends: the speed it arrived with, its motor and its
/// hover where they were. The vehicle controller's own clock runs on during the hold (the hold lasts seconds), and
/// the controller releases its motors after a gap of over a second between two of its steps; a hold is not such a gap.
/// Both step modes. Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class HeldVehicleTests
{
    private readonly ITestOutputHelper _out;
    public HeldVehicleTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float StartHeight = Ground + 30f;
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JoltScene NewScene(float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        scene.Simulate(Heartbeat);
        return scene;
    }

    private sealed class HeldRig
    {
        public JoltScene Scene;
        public JoltPrim Box;
        public double Now;

        // one heartbeat of the region: the vehicle's clock and the physics both move on
        public void Step(int heartbeats)
        {
            for (int i = 0; i < heartbeats; i++)
            {
                Now += Heartbeat;
                Scene.Simulate(Heartbeat);
            }
        }
    }

    // A balloon with a motor and a hover height, arriving with a velocity, as core makes an arriving vehicle.
    private static HeldRig Arrive(float physicsRate)
    {
        var v = new HeldRig { Scene = NewScene(physicsRate) };
        v.Box = Assert.IsType<JoltPrim>(v.Scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(),
            new Vector3(100f, 128f, StartHeight), new Vector3(2f, 1f, 0.5f), Quaternion.Identity, true, 700u));
        v.Box.VehicleType = (int)Vehicle.TYPE_BALLOON;
        v.Box.VehicleFloatParam((int)Vehicle.HOVER_HEIGHT, 10f);
        v.Box.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(6f, 0f, 0f));
        v.Box.Velocity = new Vector3(2f, 0f, 0f);
        v.Box.VehicleControl.Clock = () => Epoch.AddTicks((long)(v.Now * TimeSpan.TicksPerSecond));
        return v;
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_held_vehicle_stays_where_it_is_and_builds_up_no_motor_or_hover(float physicsRate)
    {
        HeldRig v = Arrive(physicsRate);
        try
        {
            v.Step(2);
            v.Box.HoldStill();
            Vector3 start = v.Box.Position;
            float age = v.Box.VehicleControl.LinearMotorAge;
            v.Step(55);   // five seconds
            Say($"HELD vehicle, rate {physicsRate:0}: {start} -> {v.Box.Position}, velocity {v.Box.Velocity}, motor age {age} -> {v.Box.VehicleControl.LinearMotorAge}");
            Assert.True((v.Box.Position - start).Length() < 0.02f, $"the held vehicle moved: {start} -> {v.Box.Position}");
            Assert.True(v.Box.Velocity.Length() < 0.01f, $"the held vehicle reports a velocity: {v.Box.Velocity}");
            Assert.Equal(age, v.Box.VehicleControl.LinearMotorAge);
        }
        finally { v.Scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_vehicle_released_after_a_hold_goes_on_as_one_that_was_never_held(float physicsRate)
    {
        // the control: the same vehicle, never held, one second after the point where the other is held
        HeldRig control = Arrive(physicsRate);
        Vector3 controlPos, controlVel;
        try
        {
            control.Step(2);
            Vector3 from = control.Box.Position;
            control.Step(11);
            controlPos = control.Box.Position - from;
            controlVel = control.Box.Velocity;
            Say($"HELD vehicle control, rate {physicsRate:0}: moved {controlPos}, velocity {controlVel}");
        }
        finally { control.Scene.Dispose(); }

        HeldRig held = Arrive(physicsRate);
        try
        {
            held.Step(2);
            held.Box.HoldStill();
            Vector3 from = held.Box.Position;
            held.Step(55);   // five seconds of region time with no step of the vehicle
            held.Box.ReleaseHold();
            held.Step(11);
            Vector3 movedPos = held.Box.Position - from;
            Say($"HELD vehicle released, rate {physicsRate:0}: moved {movedPos}, velocity {held.Box.Velocity}");

            Assert.True(held.Box.VehicleControl.IsActive);
            Assert.True(held.Box.VehicleControl.LinearMotorSet, "the motor was released during the hold");
            Assert.True((movedPos - controlPos).Length() < 0.3f,
                $"after the hold it moved {movedPos} in a second, the same vehicle never held moved {controlPos}");
            Assert.True((held.Box.Velocity - controlVel).Length() < 0.3f,
                $"after the hold its velocity is {held.Box.Velocity}, the same vehicle never held has {controlVel}");
        }
        finally { held.Scene.Dispose(); }
    }
}
