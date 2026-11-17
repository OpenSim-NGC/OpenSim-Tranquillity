/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical sphere at a region edge that has no neighbour. Core stands in as it does in <see cref="PhysxVoidEdgeTests"/>:
/// when the object is reported outside the region it starts a crossing (CrossingStart), and with no neighbour it puts the
/// object back half a metre inside, stops it and calls CrossingFailure. A sphere at rest stays at rest. A sphere on a
/// slope down to the edge rolls off and is put back again and again, as on ubODE; it keeps its one body all along, as
/// ubODE disables its body while it waits and enables it again (ODEPrim.CrossingStart, CrossingFailure). Serial with the
/// other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VoidEdgeRestTests
{
    private readonly ITestOutputHelper _out;
    public VoidEdgeRestTests(ITestOutputHelper output) { _out = output; }
    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = StepRateRig.Heartbeat;
    private const float Size = StepRateRig.Size;
    private const uint Id = 901u;

    private sealed class Outcome
    {
        public int Crossings, Failures, Bodies, WaitingHits;
        public Vector3 First, Final;
        public float MinY = float.MaxValue;
    }

    // `slope`: the ground rises by this many metres per metre going north (+y), so it falls toward the south edge.
    private Outcome AtTheEdge(Vector3 at, double seconds, float slope = 0f, float diameter = 0.5f)
    {
        using var rig = new StepRateRig(45, 1);
        if (slope != 0f)
        {
            var heights = new float[(int)Size * (int)Size];
            for (int y = 0; y < (int)Size; y++)
                for (int x = 0; x < (int)Size; x++)
                    heights[y * (int)Size + x] = StepRateRig.Ground + slope * y;
            rig.Scene.SetTerrain(heights);
            rig.Scene.Simulate(Heartbeat);
            at.Z += slope * at.Y;
        }
        JoltPrim obj = Assert.IsType<JoltPrim>(rig.Scene.AddPrimShape("sphere", PrimitiveBaseShape.CreateSphere(), at,
            new Vector3(diameter), Quaternion.Identity, true, Id));
        var o = new Outcome { First = obj.Position };
        var bodies = new HashSet<uint>();
        if (obj.BodyHandle.IsValid) bodies.Add(obj.BodyHandle.Value);
        bool inTransit = false, startPending = false;
        double crossStarted = 0, now = 0;
        obj.OnRequestTerseUpdate += () =>
        {
            Vector3 pos = obj.Position;
            if (inTransit || (pos.X >= 0f && pos.X < Size && pos.Y >= 0f && pos.Y < Size)) return;
            inTransit = true; startPending = true; o.Crossings++;
        };
        int beats = (int)Math.Round(seconds / Heartbeat);
        for (int beat = 0; beat < beats; beat++)
        {
            now = beat * (double)Heartbeat;
            if (startPending) { startPending = false; crossStarted = now; obj.CrossingStart(); }
            else if (inTransit && now >= crossStarted + 1.0 - 1e-9)
            {
                inTransit = false;
                Vector3 back = obj.Position;
                back.X = Math.Clamp(back.X, 0.5f, Size - 0.5f);
                back.Y = Math.Clamp(back.Y, 0.5f, Size - 0.5f);
                obj.Position = back;
                obj.Velocity = Vector3.Zero;
                obj.RotationalVelocity = Vector3.Zero;
                obj.CrossingFailure();
                o.Failures++;
            }
            if (inTransit && !startPending)
            {
                // While it waits outside, nothing finds it: a ray straight down through where it waits, with every filter.
                Vector3 p = obj.Position;
                if (rig.Scene.RaycastWorld(new Vector3(p.X, p.Y, p.Z + 5f), -Vector3.UnitZ, 10f, 10).Any(c => c.ConsumerID == Id))
                    o.WaitingHits++;
            }
            rig.Scene.Simulate(Heartbeat);
            o.MinY = Math.Min(o.MinY, obj.Position.Y);
            if (obj.BodyHandle.IsValid) bodies.Add(obj.BodyHandle.Value);
        }
        o.Final = obj.Position;
        o.Bodies = bodies.Count;
        Say($"VOID-EDGE start {at} final {o.Final} min y {o.MinY:0.000} crossings {o.Crossings} failures {o.Failures} bodies {o.Bodies} waiting hits {o.WaitingHits}");
        return o;
    }

    // Live: "Physical sphere" at <6.22, 0.41, 2.56>, "Bouncing Sphere" at <299.24, 0.07, 2.85>; here on flat ground at 25 m.
    [Theory]
    [InlineData(0.41f)]
    [InlineData(0.25f)]
    [InlineData(0.07f)]
    public void A_sphere_at_rest_on_the_south_edge_stays_and_never_crosses(float y)
    {
        Outcome o = AtTheEdge(new Vector3(128f, y, StepRateRig.Ground + 0.25f), 300);
        Assert.Equal(0, o.Crossings);
        Assert.Equal(1, o.Bodies);
        Assert.True(Math.Abs(o.Final.Y - y) < 0.05f, $"moved from y {y} to {o.Final.Y}");
    }

    // A slope of 1 % (0.57 degrees) down to the edge: the sphere rolls off, core puts it back and it rolls off again,
    // about every three seconds, as on ubODE. It stays the one body all along: no body is made or removed per crossing.
    [Fact]
    public void A_sphere_that_rolls_off_a_void_edge_and_is_put_back_keeps_its_one_body()
    {
        Outcome o = AtTheEdge(new Vector3(128f, 2f, StepRateRig.Ground + 0.25f), 60, slope: 0.01f);
        Assert.True(o.Crossings >= 10, $"only {o.Crossings} crossings: the rig did not roll it off the edge");
        Assert.True(o.Failures >= o.Crossings - 1, $"{o.Failures} put-backs for {o.Crossings} crossings");
        Assert.Equal(1, o.Bodies);
    }

    // While it waits past the edge it is out of every collision and query: a ray with every filter does not find it.
    [Fact]
    public void A_body_waiting_past_the_edge_is_found_by_no_ray()
    {
        Outcome o = AtTheEdge(new Vector3(128f, 2f, StepRateRig.Ground + 0.25f), 20, slope: 0.01f);
        Assert.True(o.Crossings >= 3, $"only {o.Crossings} crossings");
        Assert.Equal(0, o.WaitingHits);
    }
}
