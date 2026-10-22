/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What the vehicle controller costs: the time the drive takes per vehicle and per second of simulated time, on a real
/// backend at 64 physics steps a second with 2 collision steps each, for 1 and for 50 driven cars. The figures are
/// printed; the assertions only check that the vehicles were driven. Serial with the other native tests (the time is
/// measured on the shared job pool).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleControllerCostTests
{
    private readonly ITestOutputHelper _out;
    public VehicleControllerCostTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;

    private static JoltScene NewScene(float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("PhysicsStepCollisionSteps", "2");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 0f, Heartbeat);
        scene.Simulate(Heartbeat);
        return scene;
    }

    /// <summary>Driven cars side by side, each with its linear motor set so every term of the controller acts.</summary>
    private static void AddCars(JoltScene scene, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var car = Assert.IsType<JoltPrim>(scene.AddPrimShape("car", PrimitiveBaseShape.CreateBox(),
                new Vector3(20f, 10f + 4f * i, Ground + 0.6f), new Vector3(2f, 1f, 0.5f), Quaternion.Identity, true, 800u + (uint)i));
            car.VehicleType = (int)Vehicle.TYPE_CAR;
            car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
            car.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 0.1f));
        }
    }

    /// <summary>Microseconds of controller drive per vehicle per physics step (Update), over <paramref name="seconds"/> of
    /// simulated time after a warm-up: the whole, the part that runs once per Update and the part that runs in the
    /// collision steps.</summary>
    internal static (double microsPerVehicleStep, double microsPerVehicleSecond, long steps, double updatePart, double collisionPart, long onStepCalls) Measure(
        int vehicles, float rate, double seconds)
    {
        JoltScene scene = NewScene(rate);
        try
        {
            AddCars(scene, vehicles);
            for (int i = 0; i < 33; i++)   // 3 s: the cars are driving, the JIT has run
                scene.Simulate(Heartbeat);
            long ticks0 = scene.VehicleDriveTicks, calls0 = scene.VehicleStepCalls, update0 = scene.VehicleStepTicks, onStep0 = scene.VehicleOnStepTicks;
            long onStepCalls0 = scene.VehicleOnStepCalls;
            double simulated = 0;
            while (simulated < seconds)
            {
                scene.Simulate(Heartbeat);
                simulated += Heartbeat;
            }
            long ticks = scene.VehicleDriveTicks - ticks0, calls = scene.VehicleStepCalls - calls0;
            Assert.True(calls >= vehicles, "the vehicles were not driven");
            double micros = ticks * 1e6 / Stopwatch.Frequency;
            double updateMicros = (scene.VehicleStepTicks - update0) * 1e6 / Stopwatch.Frequency;
            double onStepMicros = (scene.VehicleOnStepTicks - onStep0) * 1e6 / Stopwatch.Frequency;
            return (micros / calls, micros / simulated / vehicles, calls / vehicles, updateMicros / calls, onStepMicros / calls, scene.VehicleOnStepCalls - onStepCalls0);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void The_drive_costs_this_much_at_64_hz_with_two_collision_steps(int vehicles)
    {
        var best = (microsPerVehicleStep: double.MaxValue, microsPerVehicleSecond: double.MaxValue, steps: 0L, updatePart: 0.0, collisionPart: 0.0, onStepCalls: 0L);
        for (int run = 0; run < 3; run++)
        {
            var m = Measure(vehicles, 64f, 6.0);
            if (m.microsPerVehicleStep < best.microsPerVehicleStep)
                best = m;
        }
        Say($"[cost] {vehicles} vehicles, 64 Hz x 2: {best.microsPerVehicleStep:0.00} us per vehicle per Update " +
            $"({best.updatePart:0.00} at Update rate, {best.collisionPart:0.00} in the collision steps), {best.microsPerVehicleSecond:0.0} us per vehicle per second " +
            $"({best.steps} Updates and {best.onStepCalls} collision-step calls in the best run of 3)");
    }
}
