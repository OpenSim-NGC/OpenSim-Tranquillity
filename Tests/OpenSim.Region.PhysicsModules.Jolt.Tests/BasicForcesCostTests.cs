/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using Rig = OpenSim.Region.PhysicsModules.Jolt.Tests.StepRateRig;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What the move-to, hover and look-at controllers cost: the time they take per object and per second of simulated time, on a
/// real backend at 64 physics steps a second with 2 collision steps each, for 1 and for 50 objects, under both
/// <c>[Jolt] BasicForcesBehaviour</c> values. The figures are printed (the Update-rate part and the collision steps' part); the
/// assertions only check that the controllers acted. Serial with the other native tests (the time is measured on the shared
/// job pool).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class BasicForcesCostTests
{
    private readonly ITestOutputHelper _out;
    public BasicForcesCostTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = Rig.Heartbeat;

    // Objects that keep a controller working for the whole run: a move target 60 m away with a tau of 100 s, a hover 30 m over
    // the ground with a tau of 50 s, and a quarter turn with the strength and damping that take a minute.
    private static void Add(JoltScene scene, int count, string kind)
    {
        for (int i = 0; i < count; i++)
        {
            var box = Assert.IsType<JoltPrim>(scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(),
                new Vector3(20f, 10f + 4f * i, Rig.Ground + 20f), new Vector3(1f, 1f, 1f), Quaternion.Identity, true, 800u + (uint)i));
            if (kind == "move")
            {
                box.PIDTarget = new Vector3(60f, 10f + 4f * i, Rig.Ground + 20f);
                box.PIDTau = 100f;
                box.PIDActive = true;
            }
            else if (kind == "hover")
            {
                box.PIDHoverHeight = 40f;
                box.PIDHoverType = PIDHoverType.Ground;
                box.PIDHoverTau = 50f;
                box.PIDHoverActive = true;
            }
            else
            {
                box.APIDTarget = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.5f);
                box.APIDStrength = 1f;
                box.APIDDamping = 100f;
                box.APIDActive = true;
            }
        }
    }

    internal static (double perObjectSecond, double updatePart, double stepPart, long onStepCalls, long updateCalls) Measure(string behaviour, string kind, int count, double seconds)
    {
        using var rig = new Rig(64.0, 2, jolt => jolt.Set("BasicForcesBehaviour", behaviour));
        Add(rig.Scene, count, kind);
        for (int i = 0; i < 22; i++)   // 2 s: the controllers are working, the JIT has run
            rig.Scene.Simulate(Heartbeat);
        long update0 = rig.Scene.BasicForcesUpdateTicks, onStep0 = rig.Scene.BasicForcesOnStepTicks;
        long updateCalls0 = rig.Scene.BasicForcesUpdateCalls, onStepCalls0 = rig.Scene.BasicForcesOnStepCalls;
        double simulated = 0;
        while (simulated < seconds)
        {
            rig.Scene.Simulate(Heartbeat);
            simulated += Heartbeat;
        }
        long updateCalls = rig.Scene.BasicForcesUpdateCalls - updateCalls0, onStepCalls = rig.Scene.BasicForcesOnStepCalls - onStepCalls0;
        Assert.True(updateCalls >= count && onStepCalls >= count, "the controllers did not act");
        double updateMicros = (rig.Scene.BasicForcesUpdateTicks - update0) * 1e6 / Stopwatch.Frequency;
        double stepMicros = (rig.Scene.BasicForcesOnStepTicks - onStep0) * 1e6 / Stopwatch.Frequency;
        return ((updateMicros + stepMicros) / simulated / count, updateMicros / simulated / count, stepMicros / simulated / count, onStepCalls, updateCalls);
    }

    [Theory]
    [InlineData("InWorldz", "move", 1)]
    [InlineData("InWorldz", "move", 50)]
    [InlineData("InWorldz", "hover", 1)]
    [InlineData("InWorldz", "hover", 50)]
    [InlineData("InWorldz", "look", 1)]
    [InlineData("InWorldz", "look", 50)]
    [InlineData("SecondLife", "move", 50)]
    [InlineData("SecondLife", "hover", 50)]
    [InlineData("SecondLife", "look", 50)]
    public void The_controllers_cost_this_much_at_64_hz_with_two_collision_steps(string behaviour, string kind, int objects)
    {
        var best = (perObjectSecond: double.MaxValue, updatePart: 0.0, stepPart: 0.0, onStepCalls: 0L, updateCalls: 0L);
        for (int run = 0; run < 3; run++)
        {
            var m = Measure(behaviour, kind, objects, 6.0);
            if (m.perObjectSecond < best.perObjectSecond)
                best = m;
        }
        Say($"[cost] {behaviour} {kind}, {objects} objects, 64 Hz x 2: {best.perObjectSecond:0.0} us per object per second " +
            $"({best.updatePart:0.0} at Update rate, {best.stepPart:0.0} in the collision steps; {best.updateCalls} Update-rate and {best.onStepCalls} collision-step calls in the best run of 3)");
    }
}
