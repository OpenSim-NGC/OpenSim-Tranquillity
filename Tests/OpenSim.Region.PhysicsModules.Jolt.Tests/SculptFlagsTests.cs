/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using CoreJ2K;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A sculpt whose type byte carries the invert (64) or mirror (128) flag is the type's shape, as the viewer builds it:
/// invert reverses the faces, mirror flips x and keeps them outward, both flip x and leave them reversed. The module
/// cooks what the mesher gives it. Non-physical, a ray or a box arriving from outside has to meet the surface the
/// unflagged shape has there; physical, the single hull is the unflagged one, mirrored where mirror is set.
/// Serial with the other native tests (they share the native library).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class SculptFlagsTests
{
    private readonly ITestOutputHelper _out;
    public SculptFlagsTests(ITestOutputHelper output) { _out = output; }

    private const byte Invert = 64;
    private const byte Mirror = 128;
    private const float PrimSize = 2f;
    private static readonly Vector3 Centre = new(CentreX, CentreY, Ground + 3f);

    // Offset from the origin so that a mirrored shape is a different shape, not the same one.
    private const float Shift = 0.12f;

    private static SKColor Colour(float x, float y, float z)
    {
        static byte B(float v) => (byte)Math.Clamp((int)Math.Round((v + 0.5f) * 255f), 0, 255);
        return new SKColor(B(x + Shift), B(y), B(z));
    }

    // A 64 x 64 sculpt map of the given type, whose faces point outwards when it has no flag.
    internal static byte[] Map(SculptType type)
    {
        const int N = 64;
        using var bmp = new SKBitmap(N, N);
        for (int row = 0; row < N; row++)
            for (int col = 0; col < N; col++)
            {
                double u = col / (double)N;
                double v = row / (double)(N - 1);
                double phi = (type == SculptType.Torus ? -2 : 2) * Math.PI * u;
                double theta = Math.PI * v;
                SKColor c = type switch
                {
                    SculptType.Sphere => Colour((float)(0.35 * Math.Sin(theta) * Math.Cos(phi)), (float)(0.3 * Math.Sin(theta) * Math.Sin(phi)), (float)(0.4 * Math.Cos(theta))),
                    SculptType.Torus => Colour((float)((0.28 + 0.15 * Math.Cos(2 * theta)) * Math.Cos(phi)), (float)((0.28 + 0.15 * Math.Cos(2 * theta)) * Math.Sin(phi)), (float)(0.15 * Math.Sin(2 * theta))),
                    SculptType.Cylinder => Colour((float)(0.4 * Math.Cos(phi)), (float)(0.3 * Math.Sin(phi)), (float)(v - 0.5)),
                    _ => Colour((float)(col / (double)(N - 1) - 0.5), (float)(0.5 - v), (float)(0.1 + 0.3 * Math.Sin(Math.PI * col / (N - 1)) * Math.Sin(Math.PI * v))),
                };
                bmp.SetPixel(col, row, c);
            }
        return J2kImage.ToBytes(bmp);
    }

    internal static PrimitiveBaseShape Sculpt(byte[] map, byte sculptType)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = sculptType;
        pbs.SculptTexture = UUID.Random();   // the mesher caches by this
        pbs.SculptData = map;
        return pbs;
    }

    private static PhysicsActor Add(JoltScene scene, byte[] map, byte sculptType, bool physical, Vector3? at = null)
        => scene.AddPrimShape("sculpt", Sculpt(map, sculptType), at ?? Centre, new Vector3(PrimSize, PrimSize, PrimSize), Quaternion.Identity,
                              physical, false, (byte)PhysShapeType.prim, _id++);

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    // The Jolt volume and centre of mass of the prim's own shape (the same shape the body is made from).
    private static (float volume, Vector3 centre) ShapeProperties(JoltScene scene, PhysicsActor a)
    {
        object backend = typeof(JoltScene).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
        object shape = typeof(JoltPrim).GetField("_shape", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a)!;
        MethodInfo volume = backend.GetType().GetMethod("GetShapeVolume")!;
        MethodInfo com = backend.GetType().GetMethod("GetShapeCenterOfMass")!;
        var centre = (System.Numerics.Vector3)com.Invoke(backend, new[] { shape })!;
        return ((float)volume.Invoke(backend, new[] { shape })!, new Vector3(centre.X, centre.Y, centre.Z));
    }

    // Where the first thing along the ray is, or null when it hits nothing.
    private static Vector3? Cast(JoltScene scene, Vector3 from, Vector3 direction, float length)
    {
        List<ContactResult> hits = scene.RaycastWorld(from, direction, length, 8);
        if (hits == null || hits.Count == 0)
            return null;
        return hits.OrderBy(h => h.Depth).First().Pos;
    }

    private static readonly float[] Offsets = { -0.45f, 0f, 0.45f };

    public static IEnumerable<object[]> Cases()
    {
        foreach (SculptType type in new[] { SculptType.Sphere, SculptType.Torus, SculptType.Plane, SculptType.Cylinder })
            foreach (byte flags in new byte[] { Invert, Mirror, Invert | Mirror })
                yield return new object[] { type, flags };
    }

    // One ray from outside, straight down from above and straight up from below, at nine places over the prim. The
    // flagged prim is hit where the unflagged one is (its mirror image for mirror: x reflected about the prim's centre).
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_ray_from_outside_hits_a_flagged_non_physical_sculpt_where_it_hits_the_unflagged_one(SculptType type, byte flags)
    {
        byte[] map = Map(type);
        bool mirror = (flags & Mirror) != 0;
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            // The two prims are far apart so that a ray meets one of them only.
            Vector3 plainAt = Centre;
            Vector3 flaggedAt = Centre + new Vector3(40f, 0f, 0f);
            Add(scene, map, (byte)type, false, plainAt);
            Add(scene, map, (byte)((byte)type | flags), false, flaggedAt);

            int hitPlain = 0, compared = 0;
            foreach (bool up in new[] { false, true })
                foreach (float dx in Offsets)
                    foreach (float dy in Offsets)
                    {
                        Vector3 dir = up ? Vector3.UnitZ : -Vector3.UnitZ;
                        float z0 = up ? Centre.Z - 6f : Centre.Z + 6f;
                        Vector3? plain = Cast(scene, new Vector3(plainAt.X + dx, plainAt.Y + dy, z0), dir, 12f);
                        Vector3? flagged = Cast(scene, new Vector3(flaggedAt.X + (mirror ? -dx : dx), flaggedAt.Y + dy, z0), dir, 12f);
                        if (plain.HasValue) hitPlain++;
                        _out.WriteLine($"SCULPT-RAYS {type} flags {flags} {(up ? "up" : "down")} dx {dx:0.00} dy {dy:0.00}: unflagged {(plain.HasValue ? (plain.Value.Z - Ground).ToString("0.00") : "none")}, flagged {(flagged.HasValue ? (flagged.Value.Z - Ground).ToString("0.00") : "none")}");
                        Assert.Equal(plain.HasValue, flagged.HasValue);
                        if (plain.HasValue)
                        {
                            compared++;
                            Assert.InRange(flagged!.Value.Z, plain.Value.Z - 0.05f, plain.Value.Z + 0.05f);
                        }
                    }
            Assert.True(hitPlain >= 6, "the unflagged prim is hit at most of the places: " + hitPlain);
            Assert.Equal(hitPlain, compared);
        }
        finally { scene.Dispose(); }
    }

    // A 0.4 m physical box dropped from 3 m above the sheet's high point (a sphere's top would roll it off) comes to
    // rest on the surface the unflagged sheet gives.
    [Theory]
    [InlineData(Invert)]
    [InlineData(Mirror)]
    [InlineData(Invert | Mirror)]
    public void A_box_dropped_from_outside_lands_on_a_flagged_non_physical_sculpt_as_on_the_unflagged_one(byte flags)
    {
        const SculptType type = SculptType.Plane;
        byte[] map = Map(type);
        bool mirror = (flags & Mirror) != 0;
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Vector3 plainAt = Centre;
            Vector3 flaggedAt = Centre + new Vector3(40f, 0f, 0f);
            PhysicsActor plainPrim = Add(scene, map, (byte)type, false, plainAt);
            PhysicsActor flaggedPrim = Add(scene, map, (byte)((byte)type | flags), false, flaggedAt);

            float dx = Shift * PrimSize;   // the sheet's middle, where it is level
            PhysicsActor a = scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), new Vector3(plainAt.X + dx, plainAt.Y, Centre.Z + 3f),
                                                new Vector3(0.4f, 0.4f, 0.4f), Quaternion.Identity, true, _id++);
            PhysicsActor b = scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), new Vector3(flaggedAt.X + (mirror ? -dx : dx), flaggedAt.Y, Centre.Z + 3f),
                                                new Vector3(0.4f, 0.4f, 0.4f), Quaternion.Identity, true, _id++);
            Step(scene, 4f);
            _out.WriteLine($"SCULPT-FLAGS {type} flags {flags}: unflagged box rests at z {a.Position.Z - Ground:0.00}, flagged box at z {b.Position.Z - Ground:0.00} above ground (sculpt kinds {KindOf(plainPrim)} / {KindOf(flaggedPrim)})");

            // The unflagged sheet holds the box well above the ground; the flagged one holds it at the same height.
            Assert.InRange(a.Position.Z, Centre.Z, Centre.Z + 1.5f);
            Assert.InRange(b.Position.Z, a.Position.Z - 0.1f, a.Position.Z + 0.1f);
        }
        finally { scene.Dispose(); }
    }

    // Physical: one hull, the unflagged one mirrored where mirror is set. A hull has no faces to reverse, so invert
    // changes nothing.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_physical_flagged_sculpt_cooks_to_the_unflagged_single_hull_mirrored_where_mirror_is_set(SculptType type, byte flags)
    {
        byte[] map = Map(type);
        bool mirror = (flags & Mirror) != 0;
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            PhysicsActor plain = Add(scene, map, (byte)type, true);
            PhysicsActor flagged = Add(scene, map, (byte)((byte)type | flags), true, Centre + new Vector3(40f, 0f, 0f));
            Assert.Equal("hull(mesher)", KindOf(plain));
            Assert.Equal("hull(mesher)", KindOf(flagged));

            (float plainVolume, Vector3 plainCom) = ShapeProperties(scene, plain);
            (float flaggedVolume, Vector3 flaggedCom) = ShapeProperties(scene, flagged);

            Assert.True(plainVolume > 0.5f, "the hull encloses a volume: " + plainVolume);
            Assert.InRange(flaggedVolume, plainVolume * 0.99f, plainVolume * 1.01f);
            Assert.InRange(flaggedCom.X, (mirror ? -plainCom.X : plainCom.X) - 0.01f, (mirror ? -plainCom.X : plainCom.X) + 0.01f);
            Assert.InRange(flaggedCom.Y, plainCom.Y - 0.01f, plainCom.Y + 0.01f);
            Assert.InRange(flaggedCom.Z, plainCom.Z - 0.01f, plainCom.Z + 0.01f);
        }
        finally { scene.Dispose(); }
    }
}
