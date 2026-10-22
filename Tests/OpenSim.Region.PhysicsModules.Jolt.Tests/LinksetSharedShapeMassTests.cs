/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The mass and centre of mass of a physical linkset whose mesh parts share one cooked shape (the same asset, at the root's
/// density) equal those of the same linkset whose parts each hold a shape of their own. A part may take the shape another prim of
/// its asset holds, or a scaled shape over it, unless its density differs from the root's (JoltPrim.MayShareShape); the shared
/// shape was cooked for another prim, so the linkset's totals, which core adds up from each part's mass and reads from the
/// root's centre of mass, have to come out as they do from private shapes. The reference linkset is the same parts built from
/// copies of the asset under other ids, which are cooked once each. Each result is a line "LINKSET-SHARED ..." in the test
/// output. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class LinksetSharedShapeMassTests
{
    private readonly ITestOutputHelper _out;
    public LinksetSharedShapeMassTests(ITestOutputHelper output) { _out = output; }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static readonly Quaternion Turned = Quaternion.CreateFromEulers(0.3f, 0f, 0.8f);

    // The scene answers each request for one of the ids with the asset, at once.
    private static void Serve(JoltScene scene, byte[] asset, params UUID[] ids)
        => scene.RequestAssetMethod = (assetId, callback) =>
            callback(Array.IndexOf(ids, assetId) >= 0 ? new AssetBase(assetId, "mesh", (sbyte)AssetType.Mesh, UUID.Zero.ToString()) { Data = asset } : null);

    private sealed record Totals(float RootMass, float PartMassSum, Vector3 RootCentre, bool SharedShape);

    // A box root and two mesh parts, linked, physical; the parts' assets are ids[0] and ids[1] (the same id: one shared shape).
    private static Totals Build(byte[] asset, byte shapeType, UUID id0, UUID id1, Vector3 size0, Vector3 size1)
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Serve(scene, asset, id0, id1);
            var at = new Vector3(CentreX, CentreY, 60f);
            PhysicsActor root = scene.AddPrimShape("root", PrimitiveBaseShape.CreateBox(), at, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
            PhysicsActor Part(UUID id, Vector3 offset, Quaternion rot, Vector3 size)
            {
                PrimitiveBaseShape pbs = MeshPbs(asset);
                pbs.SculptTexture = id;
                PhysicsActor p = scene.AddPrimShape("mesh part", pbs, at + offset, size, rot, true, false, shapeType, _id++);
                p.link(root);
                return p;
            }
            PhysicsActor a = Part(id0, new Vector3(1.5f, 0.4f, 0.2f), Turned, size0);
            PhysicsActor b = Part(id1, new Vector3(-1.2f, 1.1f, -0.3f), Quaternion.Identity, size1);
            Step(scene, 1f / 5f);
            return new Totals(root.Mass, a.Mass + b.Mass, root.CenterOfMass - root.Position, ((JoltPrim)a).ShapeHandle.Equals(((JoltPrim)b).ShapeHandle));
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(Prim, false)]    // a hull list, same size parts (one shared shape)
    [InlineData(Prim, true)]     // a hull list, parts of two sizes (a scaled shape over the shared one)
    [InlineData(Convex, false)]  // one hull
    [InlineData(Convex, true)]
    public void A_linkset_of_parts_that_share_a_mesh_shape_weighs_and_balances_as_one_with_shapes_of_their_own(byte shapeType, bool differentSizes)
    {
        byte[] asset = Asset(Soup(Trough), Trough, false);
        Vector3 s0 = Vector3.One;
        Vector3 s1 = differentSizes ? new Vector3(1.6f, 0.9f, 1.3f) : Vector3.One;
        UUID same = UUID.Random();
        Totals shared = Build(asset, shapeType, same, same, s0, s1);
        Totals own = Build(asset, shapeType, UUID.Random(), UUID.Random(), s0, s1);
        Say($"LINKSET-SHARED shape_type={shapeType} sizes {(differentSizes ? "differ" : "same")}: shared root mass {shared.RootMass:0.0000} parts {shared.PartMassSum:0.0000} centre {shared.RootCentre}; own root mass {own.RootMass:0.0000} parts {own.PartMassSum:0.0000} centre {own.RootCentre}; shape shared {shared.SharedShape}, own {own.SharedShape}");
        if (!differentSizes)
            Assert.True(shared.SharedShape, "the two parts of one asset did not share a shape (the case the test is about)");
        Assert.False(own.SharedShape);
        Assert.True(shared.PartMassSum > 0f && own.PartMassSum > 0f);
        Assert.InRange(shared.PartMassSum, own.PartMassSum * 0.999f, own.PartMassSum * 1.001f);
        Assert.InRange(shared.RootMass, own.RootMass * 0.999f, own.RootMass * 1.001f);
        Assert.True(Vector3.Distance(shared.RootCentre, own.RootCentre) < 0.005f, $"centre of mass {shared.RootCentre} against {own.RootCentre}");
    }
}
