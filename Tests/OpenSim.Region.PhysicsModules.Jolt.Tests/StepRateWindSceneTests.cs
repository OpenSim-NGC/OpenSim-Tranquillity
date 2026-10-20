/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using Rig = OpenSim.Region.PhysicsModules.Jolt.Tests.StepRateRig;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vehicle's wind on a real backend, at 32, 45 and 64 Hz with 1 to 4 collision steps: a vehicle in a constant wind drifts
/// the same (within 2%), the wind is read in every collision step, and a collision step takes none of the locks the
/// Update-rate path, a script thread or the wind module's own update could be holding. The figures are written to the test
/// output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class StepRateWindSceneTests
{
    private readonly ITestOutputHelper _out;
    public StepRateWindSceneTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = Rig.Heartbeat;

    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private double Check(string test, double rate, int n, double t, double got, double expected, double scale, double percent)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        Say($"[rate-table] {test} | {rate} Hz x {n} | t {t:0.000} | {got:0.0000} against {expected:0.0000} | {error:0.00}% of {scale}");
        Assert.True(error <= percent, $"{test} at {rate} Hz with {n} collision steps, t = {t:0.000}: {got:0.0000} against {expected:0.0000} (within {percent}% of {scale})");
        return error;
    }

    // ---- The wind, in every collision step ----

    private static Rig WindRig(double rate, int n, float efficiency, out ConstantWind wind)
    {
        var rig = new Rig(rate, n, jolt => jolt.Set("VehicleBehaviour", "InWorldz"));
        var (module, log) = ConstantWind.Make(new Vector3(5f, 0f, 0f));
        wind = log;
        rig.Scene.UseWindModule(module);
        rig.AddBox(new Vector3(2f, 1f, 0.5f), new Vector3(100f, 128f, 100f), vehicle: true);
        rig.Prim.VehicleFlags(0x20000, false);   // VEHICLE_FLAG_REACT_TO_WIND, as a script numbers it
        rig.Vector(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(efficiency, efficiency, 0f));
        return rig;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_vehicle_in_a_constant_wind_drifts_alike(double rate, int n)
    {
        using Rig rig = WindRig(rate, n, 0.5f, out ConstantWind wind);
        rig.RunTo(1.0);
        double t = rig.Time;
        Check("wind drift", rate, n, t, rig.Prim.Velocity.X, 2.5 * t, 5.0, 2.0);
        rig.RunTo(1.6);
        t = rig.Time;
        Check("wind drift", rate, n, t, rig.Prim.Velocity.X, 2.5 * t, 5.0, 2.0);
        // Read in every collision step: at least n reads for each Update that stepped the vehicle.
        Assert.True(Volatile.Read(ref wind.Reads) >= (int)(rig.Scene.VehicleOnStepCalls * 0.99),
            $"{wind.Reads} wind reads for {rig.Scene.VehicleOnStepCalls} collision steps");
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_vehicle_in_a_constant_wind_settles_at_its_speed_alike(double rate, int n)
    {
        using Rig rig = WindRig(rate, n, 0.2f, out _);
        rig.RunTo(10.0);
        Check("wind settle", rate, n, rig.Time, rig.Prim.Velocity.X, 5.0, 5.0, 2.0);
    }

    // ---- A collision step takes no lock ----

    // The Update-rate path arms the vehicle; every lock a script thread, the Update-rate path or the wind module's own update
    // could be holding is then held by another thread while the engine steps. A collision step that took any of them would
    // wait for it for ever: the step has ten seconds to finish. (The backend's own region lock and pool gate are the step's
    // own and are not among them: the step takes them itself, before it runs a provider.)
    [Fact]
    public void A_collision_step_takes_none_of_the_locks_the_other_threads_hold()
    {
        using Rig rig = WindRig(64.0, 2, 0.5f, out ConstantWind wind);
        for (int i = 0; i < 11; i++)
            rig.Scene.Simulate(Heartbeat);
        JoltPrim car = rig.Prim;

        var locks = new List<object>();
        object Field(object owner, string name)
        {
            FieldInfo f = owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return f.GetValue(owner);
        }
        object controller = typeof(JoltPrim).GetField("_vehicle", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(car);
        locks.Add(Field(car, "_vehicleStepGate"));
        locks.Add(Field(car, "_scriptForceLock"));
        locks.Add(Field(controller, "_propsGate"));
        locks.Add(Field(controller, "_pendingGate"));
        locks.Add(Field(rig.Scene, "_vehicles"));
        locks.Add(Field(rig.Scene, "_scriptForced"));

        // The Update-rate path, as JoltScene.Simulate runs it before the step.
        car.StepVehicle(1f / 64f);
        long calls0 = rig.Scene.VehicleOnStepCalls;
        float VelocityX() { Assert.True(rig.Backend.TryGetBodyState(car.BodyHandle, out BodyState st)); return st.LinearVelocity.X; }
        float vx0 = VelocityX();

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            void Take(int i)
            {
                if (i == locks.Count)
                {
                    held.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return;
                }
                lock (locks[i])
                    Take(i + 1);
            }
            Take(0);
        }) { IsBackground = true };
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)), "the holder thread could not take the locks");

        bool finished;
        try
        {
            Task step = Task.Run(() => rig.Backend.Step(1f / 64f, Span<BodyState>.Empty, Span<CharacterState>.Empty, Span<ContactReport>.Empty));
            // the test needs to know whether the step returned within a bound, so it waits on the task here
#pragma warning disable xUnit1031
            finished = step.Wait(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1031
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }
        Assert.True(finished, "the engine step did not finish while the locks were held: a collision step took one");
        Assert.Equal(calls0 + 2, rig.Scene.VehicleOnStepCalls);   // both collision steps ran the vehicle
        Assert.True(VelocityX() > vx0, "the wind did not push the vehicle in the step");
        Assert.True(wind.Reads > 0);
    }
}
