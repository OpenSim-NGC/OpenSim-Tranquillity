/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using Omv = OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object driven into the edge of a region that has no neighbour. Core cannot hand it over, so it puts the group back
/// half a metre inside the edge, stops it and calls the actor's CrossingFailure (the same sequence <see cref="LeavingTheRegionTests"/>
/// stands in for). A physics engine that moves the body by a position write at that point reports no velocity for the move, so
/// anything that works out speed from the change of position sees a very large one for a step: the "apparent velocity". Two
/// drivers: a basic physical box whose script holds a speed with llSetForce, and a vehicle whose linear motor drives it. For
/// each: the speed the module reports, the speed a position watcher computes from consecutive heartbeats (change of position over
/// the heartbeat), how many times it crosses again, and for the vehicle whether its stall check fires before the edge. Serial
/// with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PhysxVoidEdgeTests
{
    private readonly ITestOutputHelper _out;
    public PhysxVoidEdgeTests(ITestOutputHelper output) { _out = output; }
    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = StepRateRig.Heartbeat;
    private const float RegionSize = StepRateRig.Size;

    public static IEnumerable<object[]> Cells() => SpikeProbe.Cells();

    private static readonly FieldInfo StalledField = typeof(VehicleController).GetField("_linearMotorStalled", BindingFlags.NonPublic | BindingFlags.Instance);

    private sealed class Outcome
    {
        public double PreEdgeMaxSpeed, MaxReportedAfter, MaxApparentAtKick, MaxApparentOther;
        public int Crossings, Failures, PutBackOutside;
        public bool StalledBeforeEdge, StalledAfterKick;
        public float FinalX;
        public double FinalSpeed;
    }

    // `driver`: "basic" holds `speed` with a script's force, "vehicle" drives a car with its linear motor at `speed`.
    private Outcome DriveIntoTheEdge(double rate, int n, string driver, double speed, float nudge)
    {
        using var p = new SpikeProbe(rate, n);
        JoltPrim obj;
        if (driver == "basic")
            obj = p.Box(new Omv.Vector3(2f, 1f, 0.5f), new Omv.Vector3(RegionSize - 30f, 128f + nudge, 25.3f));
        else
        {
            obj = p.Rig.AddBox(new Omv.Vector3(2f, 1f, 0.5f), new Omv.Vector3(RegionSize - 30f, 128f + nudge, 25.3f), vehicle: true);
            p.Rig.Float(Vehicle.BUOYANCY, 0f);
            p.Rig.Float(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            p.Rig.Float(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        }
        float mass = obj.Mass;
        var o = new Outcome();
        bool inTransit = false, startPending = false, left = false;
        double crossStarted = 0, leftAt = double.NaN, now = 0;
        Omv.Vector3 lastPosition = obj.Position;
        bool firstBeat = true, justFailed = false;
        obj.OnRequestTerseUpdate += () =>
        {
            Omv.Vector3 pos = obj.Position;
            if (inTransit || (pos.X >= 0f && pos.X < RegionSize && pos.Y >= 0f && pos.Y < RegionSize)) return;
            inTransit = true; startPending = true; o.Crossings++;
            if (!left) { left = true; leftAt = now; }
        };
        obj.SubscribeEvents(100);

        for (int beat = 0; beat < 110; beat++)   // 10 s
        {
            now = beat * (double)Heartbeat;
            if (startPending) { startPending = false; crossStarted = now; obj.CrossingStart(); }
            else if (inTransit && now >= crossStarted + 1.0 - 1e-9)
            {
                // No neighbour: core clamps the position half a metre inside, stops the object and calls CrossingFailure.
                inTransit = false;
                Omv.Vector3 back = obj.Position;
                back.X = Math.Clamp(back.X, 0.5f, RegionSize - 0.5f);
                back.Y = Math.Clamp(back.Y, 0.5f, RegionSize - 0.5f);
                obj.Position = back;
                obj.Velocity = Omv.Vector3.Zero;
                obj.RotationalVelocity = Omv.Vector3.Zero;
                obj.CrossingFailure();
                o.Failures++;
                justFailed = true;
                if (obj.Position.X >= RegionSize - 0.4f || obj.Position.Y >= RegionSize - 0.4f || obj.Position.Y <= 0.4f) o.PutBackOutside++;
            }
            if (!inTransit && !startPending)
            {
                if (driver == "basic")
                {
                    float vx = obj.Velocity.X;
                    obj.Force = new Omv.Vector3(Math.Clamp(mass * ((float)speed - vx) / 0.3f, -20f * mass, 20f * mass) + (vx > 0.1f ? 0.7f * mass * SpikeProbe.G : 0f), 0f, 0f);
                }
                else if (beat == 0)
                    p.Rig.Vector(Vehicle.LINEAR_MOTOR_DIRECTION, new Omv.Vector3((float)speed, 0f, 0f));
            }
            p.Rig.Scene.Simulate(Heartbeat);

            Omv.Vector3 position = obj.Position;
            double apparent = firstBeat ? 0 : Omv.Vector3.Distance(position, lastPosition) / Heartbeat;
            double reported = obj.Velocity.Length();
            bool stalled = driver == "vehicle" && obj.VehicleControl != null && (bool)StalledField.GetValue(obj.VehicleControl);
            if (!left) { o.PreEdgeMaxSpeed = Math.Max(o.PreEdgeMaxSpeed, reported); o.StalledBeforeEdge |= stalled && beat > 12; }
            else
            {
                if (justFailed) { o.MaxApparentAtKick = Math.Max(o.MaxApparentAtKick, apparent); o.StalledAfterKick |= stalled; }
                else if (!inTransit) o.MaxApparentOther = Math.Max(o.MaxApparentOther, apparent);
                if (justFailed || !inTransit) o.MaxReportedAfter = Math.Max(o.MaxReportedAfter, reported);
            }
            justFailed = false;
            lastPosition = position;
            firstBeat = false;
        }
        o.FinalX = obj.Position.X;
        o.FinalSpeed = obj.Velocity.Length();
        Assert.True(p.Rig.Backend.StepForceProviderFaults == 0, $"a step provider threw: {p.Rig.Backend.LastStepForceProviderFault}");
        return o;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Tvoid_an_object_driven_into_an_edge_with_no_neighbour_comes_back_without_a_reported_spike(double rate, int n)
    {
        var failures = new List<string>();
        foreach (string driver in new[] { "basic", "vehicle" })
            foreach (double speed in new[] { 5.0, 15.0 })
            {
                var runs = new[] { 0f, 0.37e-3f, 0.74e-3f, 1.11e-3f }.Select(k => DriveIntoTheEdge(rate, n, driver, speed, k)).ToList();
                double kick = runs.Max(r => r.MaxApparentAtKick), other = runs.Max(r => r.MaxApparentOther), reported = runs.Max(r => r.MaxReportedAfter), pre = runs.Min(r => r.PreEdgeMaxSpeed);
                Say($"[physx] Tvoid {driver} {speed} m/s | {rate} Hz x {n} | reported max before the edge {pre:0.00} m/s | crossings {runs.Max(r => r.Crossings)} failures {runs.Max(r => r.Failures)} | apparent speed on the step the object is put back {kick:0.00} m/s, on other steps outside a crossing {other:0.00} | reported max after the first crossing {reported:0.00} | stalled before the edge {runs.Any(r => r.StalledBeforeEdge)}, on the kick step {runs.Any(r => r.StalledAfterKick)} | ends x {runs[0].FinalX:0.00} speed {runs[0].FinalSpeed:0.00}");
                if (runs.Any(r => r.Failures == 0)) failures.Add($"{driver} {speed}: the crossing never failed");
                if (reported > pre + 1.0) failures.Add($"{driver} {speed}: reported speed {reported:0.00} after the crossing against {pre:0.00} before the edge");
                if (runs.Any(r => r.PutBackOutside > 0)) failures.Add($"{driver} {speed}: put back less than 0.4 m inside the edge");
                if (kick > 30.0) failures.Add($"{driver} {speed}: apparent speed {kick:0.0} m/s when put back (the module waits at most 2 m outside, so 2.5 m in one heartbeat is 27 m/s)");
                if (driver == "vehicle" && runs.Any(r => r.FinalSpeed > 0.1)) failures.Add($"{driver} {speed}: not at rest at the end ({runs.Max(r => r.FinalSpeed):0.00} m/s)");
                if (runs.Any(r => r.StalledAfterKick)) failures.Add($"{driver} {speed}: the stall check fired on the step the object was put back");
                if (runs.Any(r => r.StalledBeforeEdge)) failures.Add($"{driver} {speed}: the stall check fired before the edge");
            }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: " + string.Join("; ", failures));
    }
}
