/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a prim's actor reports for llGetMass, llGetObjectMass and llGetCenterOfMass, through the harness at the 11 Hz
/// heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. Core adds up every part's Mass
/// (SceneObjectGroup.GetMass); it reads a physical object's centre of mass from its root's actor, a child's from the
/// child's, and a non-physical object's as the mass-weighted mean of its parts' (SceneObjectGroup.GetCenterOfMass), and the
/// tests read them the same way. Targets, from the SL wiki: llGetMass gives the object's mass in lindograms
/// (wiki.secondlife.com/wiki/LlGetMass), with no difference for a non-physical object; mass is density times volume (the
/// Physics Material Settings test page), 1 m^3 at the default density being 10; llGetCenterOfMass gives the object's
/// centre of mass in region coordinates, a child prim's own when called from a child
/// (wiki.secondlife.com/wiki/LlGetCenterOfMass).
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MassReadoutTests
{
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private static readonly Vector3 High = new(128f, 128f, 150f);
    private static readonly Vector3 Long = new(2f, 1f, 1f);
    private const float CubeMass = 10f;                                    // 1 m^3 x 1000 kg/m^3 / 100
    private const float SphereMass = 4f / 3f * MathF.PI * 0.125f * 10f;    // 1 m across: 5.236
    private const uint RootId = 1201, ChildId = 1202;
    // Turned 30 degrees about the vertical, so an offset along the root's x is not along the region's.
    private static readonly Quaternion Yawed = Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 6f);

    private static void Run(double physicsHz, float seconds, Action<Run> setup, Action<Run> act = null)
    {
        var sc = new Scenario
        {
            Name = "mass-readout",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r => act?.Invoke(r),
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
    }

    // SceneObjectGroup.GetCenterOfMass for a non-physical object: the parts' centres weighted by their masses.
    private static Vector3 WeightedCenter(params PhysicsActor[] parts)
    {
        Vector3 sum = Vector3.Zero;
        float total = 0f;
        foreach (PhysicsActor p in parts)
        {
            sum += p.CenterOfMass * p.Mass;
            total += p.Mass;
        }
        return sum / total;
    }

    private static void Near(Vector3 expected, Vector3 got, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, got) <= tolerance, $"expected {expected}, got {got} ({Vector3.Distance(expected, got) * 1000f:0.###} mm off)");

    // ---- mass --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_non_physical_prim_has_the_mass_it_has_when_physical(double physicsHz)
    {
        float cube = 0f, sphere = 0f, physicalCube = 0f, physicalSphere = 0f, linkset = 0f;
        Run(physicsHz, 0.3f, r =>
        {
            r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High, Quaternion.Identity, false, RootId);
            cube = r.Actor.Mass;
            sphere = r.AddPart(PrimitiveBaseShape.CreateSphere(), Unit, High + new Vector3(5f, 0f, 0f), Quaternion.Identity, false, ChildId).Mass;
            physicalCube = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High + new Vector3(10f, 0f, 0f), Quaternion.Identity, true, 1203).Mass;
        });
        Run(physicsHz, 0.3f, r => physicalSphere = r.AddSphere(1f, High).Mass);
        // A non-physical two-prim linkset: core adds the parts' actors up. The child is 2 m long at half the density.
        Run(physicsHz, 0.3f, r =>
        {
            PhysicsActor root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High, Quaternion.Identity, false, RootId);
            PhysicsActor child = r.AddPart(PrimitiveBaseShape.CreateBox(), Long, High + new Vector3(1.5f, 0f, 0f), Quaternion.Identity, false, ChildId, density: 500f);
            linkset = root.Mass + child.Mass;
        });

        Assert.Equal(CubeMass, cube, 3);
        Assert.Equal(SphereMass, sphere, 3);
        Assert.Equal(physicalCube, cube, 4);
        Assert.Equal(physicalSphere, sphere, 4);
        Assert.Equal(CubeMass + 2f * CubeMass * 0.5f, linkset, 3);
    }

    // A physical linkset's parts each report their own mass, and core's sum is the linkset's, before and after the
    // weld; a non-physical one reports the same.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_sums_to_the_same_mass_physical_or_not(double physicsHz)
    {
        float before = 0f, welded = 0f, rootOwn = 0f, childOwn = 0f, still = 0f;
        PhysicsActor child = null;
        Run(physicsHz, 0.4f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                child = r.AddPart(PrimitiveBaseShape.CreateBox(), Long, High + new Vector3(1.5f, 0f, 0f), Quaternion.Identity, true, ChildId, r.Actor, 500f);
                before = r.Actor.Mass + child.Mass;
            },
            r =>
            {
                if (r.Heartbeats != 2)
                    return;
                welded = r.Actor.Mass + child.Mass;
                rootOwn = r.Actor.Mass;
                childOwn = child.Mass;
            });
        Run(physicsHz, 0.3f, r =>
        {
            PhysicsActor root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High, Quaternion.Identity, false, RootId);
            PhysicsActor c = r.AddPart(PrimitiveBaseShape.CreateBox(), Long, High + new Vector3(1.5f, 0f, 0f), Quaternion.Identity, false, ChildId, density: 500f);
            still = root.Mass + c.Mass;
        });

        Assert.Equal(20f, before, 3);
        Assert.Equal(20f, welded, 3);
        Assert.Equal(CubeMass, rootOwn, 3);
        Assert.Equal(CubeMass, childOwn, 3);
        Assert.Equal(20f, still, 3);
    }

    // A non-physical prim that is not a box, sphere or cylinder collides as its triangle mesh, which has no volume. It
    // reports the mass and centre of its physical shape, a triangular prism half the volume of its box here, and the
    // same as when it is physical.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_non_physical_meshed_prim_has_the_mass_and_centre_of_its_physical_shape(double physicsHz)
    {
        var size = new Vector3(2f, 1f, 1f);
        float still = 0f, physical = 0f;
        Vector3 stillCenter = Vector3.Zero, physicalCenter = Vector3.Zero;
        Run(physicsHz, 0.3f, r =>
        {
            r.UseMesher(new PrismMesher());
            PhysicsActor s = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), size, High, Yawed, false, RootId);
            PhysicsActor p = r.AddPart(PrimitiveBaseShape.CreateBox(), size, High + new Vector3(10f, 0f, 0f), Yawed, true, ChildId);
            s.Shape = Cut(); p.Shape = Cut();   // not the box fast path: through the mesher
            still = s.Mass; physical = p.Mass;
            stillCenter = s.CenterOfMass - s.Position;
            physicalCenter = p.CenterOfMass - p.Position;
        });

        Assert.Equal(0.5f * 2f * CubeMass, still, 3);
        Assert.Equal(physical, still, 4);
        // The prism's centroid sits a sixth of its size off the middle along its own x and y.
        Vector3 centroid = new Vector3(-size.X / 6f, -size.Y / 6f, 0f) * Yawed;
        Near(centroid, stillCenter);
        Near(centroid, physicalCenter);
    }

    // ---- centre of mass --------------------------------------------------------------------------------------------

    // An off-centre two-prim linkset: a 1 m root and a 2 m child whose middle is 1.5 m along the root's x. The centre of
    // mass is (10 x 0 + m x 1.5) / (10 + m) along the root's x, in region coordinates, where it is now, also while it
    // falls and turns. A child reports its own. Physical (from the root's actor) and non-physical (core's weighted mean)
    // agree.
    [Theory]
    [InlineData(45.0, 1000f)]
    [InlineData(45.0, 500f)]
    [InlineData(0.0, 1000f)]
    [InlineData(0.0, 500f)]
    public void An_off_centre_linkset_reports_its_centre_of_mass(double physicsHz, float childDensity)
    {
        float childMass = 2f * CubeMass * childDensity / 1000f;
        float along = 1.5f * childMass / (CubeMass + childMass);
        PhysicsActor child = null;
        var checks = new List<(Vector3 Expected, Vector3 Got, Vector3 ChildExpected, Vector3 ChildGot)>();
        Run(physicsHz, 2f,
            r =>
            {
                r.AddBox(Unit, High, Yawed);
                child = r.AddPart(PrimitiveBaseShape.CreateBox(), Long, High + new Vector3(1.5f, 0f, 0f) * Yawed, Yawed, true, ChildId, r.Actor, childDensity);
            },
            r =>
            {
                if (r.Heartbeats == 3)
                    r.Actor.RotationalVelocity = new Vector3(0.5f, 0.3f, 1f);   // it turns as it falls
                if (r.Heartbeats < 2)
                    return;
                Vector3 pos = r.Actor.Position;
                Quaternion rot = r.Actor.Orientation;
                checks.Add((pos + new Vector3(along, 0f, 0f) * rot, r.Actor.CenterOfMass,
                            pos + new Vector3(1.5f, 0f, 0f) * rot, child.CenterOfMass));
            });

        Assert.True(checks.Count > 15);
        foreach (var c in checks)
        {
            Near(c.Expected, c.Got);
            Near(c.ChildExpected, c.ChildGot);
        }
        // It did turn: the last check is not along the starting yaw.
        Quaternion start = Yawed;
        Assert.True(Vector3.Distance(checks[^1].ChildExpected - checks[^1].Expected, (new Vector3(1.5f - along, 0f, 0f)) * start) > 0.05f, "it never turned");

        Vector3 still = Vector3.Zero, stillExpected = Vector3.Zero, stillChild = Vector3.Zero;
        Run(physicsHz, 0.3f, r =>
        {
            PhysicsActor root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High, Yawed, false, RootId);
            PhysicsActor c = r.AddPart(PrimitiveBaseShape.CreateBox(), Long, High + new Vector3(1.5f, 0f, 0f) * Yawed, Yawed, false, ChildId, density: childDensity);
            still = WeightedCenter(root, c);
            stillChild = c.CenterOfMass;
            stillExpected = High + new Vector3(along, 0f, 0f) * Yawed;
        });
        Near(stillExpected, still);
        Near(High + new Vector3(1.5f, 0f, 0f) * Yawed, stillChild);
    }

    // A lone prim's centre of mass is its own, physical or not.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_lone_prim_reports_its_own_centre(double physicsHz)
    {
        var checks = new List<(Vector3 Position, Vector3 Center)>();
        Vector3 still = Vector3.Zero;
        Run(physicsHz, 1f,
            r =>
            {
                r.AddBox(Long, High, Yawed);
                still = r.AddPart(PrimitiveBaseShape.CreateBox(), Long, High + new Vector3(10f, 0f, 0f), Yawed, false, RootId).CenterOfMass;
            },
            r => checks.Add((r.Actor.Position, r.Actor.CenterOfMass)));
        foreach (var c in checks)
            Near(c.Position, c.Center);
        Near(High + new Vector3(10f, 0f, 0f), still);
    }

    // ---- a stand-in mesher -------------------------------------------------------------------------------------------

    // Profile cut, so CookPrimShape does not take the box fast path.
    private static PrimitiveBaseShape Cut()
    {
        PrimitiveBaseShape s = PrimitiveBaseShape.CreateBox();
        s.ProfileBegin = 10000;
        return s;
    }

    // Meshes every prim as a closed triangular prism filling half its box: the right triangle (-x,-y), (+x,-y), (-x,+y)
    // across its x and y, the full height in z. Its volume is half the box's, and its centroid is a sixth of the size off
    // the middle along x and y.
    private sealed class PrismMesher : IMesher
    {
        public IMesh CreateMesh(string primName, PrimitiveBaseShape primShape, Vector3 size, float lod) => new Prism(size);
        public IMesh CreateMesh(string primName, PrimitiveBaseShape primShape, Vector3 size, float lod, bool isPhysical) => new Prism(size);
        public IMesh CreateMesh(string primName, PrimitiveBaseShape primShape, Vector3 size, float lod, bool isPhysical, bool convex, bool forOde) => new Prism(size);
        public IMesh CreateMesh(string primName, PrimitiveBaseShape primShape, Vector3 size, float lod, bool isPhysical, bool shouldCache, bool convex, bool forOde) => new Prism(size);
        public IMesh GetMesh(string primName, PrimitiveBaseShape primShape, Vector3 size, float lod, bool isPhysical, bool convex) => new Prism(size);
        public void ReleaseMesh(IMesh mesh) { }
        public void ExpireReleaseMeshs() { }
        public void ExpireFileCache() { }
    }

    private sealed class Prism : IMesh
    {
        private readonly float[] _vertices;
        private static readonly int[] Indices =
        {
            0, 2, 1,  3, 4, 5,           // bottom, top
            0, 1, 4,  0, 4, 3,           // y = -0.5 face
            0, 3, 5,  0, 5, 2,           // x = -0.5 face
            1, 2, 5,  1, 5, 4,           // the slanted face
        };

        public Prism(Vector3 size)
        {
            float hx = size.X / 2f, hy = size.Y / 2f, hz = size.Z / 2f;
            _vertices = new[]
            {
                -hx, -hy, -hz,   hx, -hy, -hz,   -hx, hy, -hz,
                -hx, -hy,  hz,   hx, -hy,  hz,   -hx, hy,  hz,
            };
        }

        public List<Vector3> getVertexList()
        {
            var list = new List<Vector3>();
            for (int i = 0; i < _vertices.Length; i += 3)
                list.Add(new Vector3(_vertices[i], _vertices[i + 1], _vertices[i + 2]));
            return list;
        }
        public int[] getIndexListAsInt() => (int[])Indices.Clone();
        public int[] getIndexListAsIntLocked() => getIndexListAsInt();
        public float[] getVertexListAsFloat() => (float[])_vertices.Clone();
        public float[] getVertexListAsFloatLocked() => getVertexListAsFloat();
        public void getIndexListAsPtrToIntArray(out IntPtr indices, out int triStride, out int indexCount) => throw new NotSupportedException();
        public void getVertexListAsPtrToFloatArray(out IntPtr vertexList, out int vertexStride, out int vertexCount) => throw new NotSupportedException();
        public void releaseSourceMeshData() { }
        public void releasePinned() { }
        public void Append(IMesh newMesh) => throw new NotSupportedException();
        public void TransformLinear(float[,] matrix, float[] offset) => throw new NotSupportedException();
        public Vector3 GetCentroid() => Vector3.Zero;
        public Vector3 GetOBB() => Vector3.Zero;
    }
}
