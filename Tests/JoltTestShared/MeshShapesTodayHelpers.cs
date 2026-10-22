/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The scene, asset and shape builders of <c>MeshShapesTodayTests</c>, which the mesh and complexity tests of several Jolt
/// test projects build on (<c>using static ...MeshShapesTodayTests</c>). The tests themselves are in the other part of the class.
/// </summary>
public partial class MeshShapesTodayTests
{
    internal const float Heartbeat = 1f / 11f;
    internal const int Size = 256;
    internal const float Ground = 25f;
    internal const float CentreX = 128f, CentreY = 128f;

    // ---------------------------------------------------------------- geometry (metres, centred on the prim)

    internal readonly record struct Box(float X0, float X1, float Y0, float Y1, float Z0, float Z1);

    // A gateway 4 m wide, 2 m deep and 3 m tall, open along y: two pillars and a lintel. The opening is 1.6 m wide and
    // 2.1 m tall. Its centre is 1.5 m above its foot.
    internal static readonly Box[] Gateway =
    {
        new(-2f, -0.8f, -1f, 1f, -1.5f, 1.5f),
        new(0.8f, 2f, -1f, 1f, -1.5f, 1.5f),
        new(-0.8f, 0.8f, -1f, 1f, 0.6f, 1.5f),
    };
    internal const float GatewayHeight = 3f;

    // A trough 4 m long, 2 m deep and 1.5 m tall, open on top: a floor and two walls. The slot is 3.4 m wide; its floor
    // is 0.3 m thick. Its centre is 0.75 m above its foot.
    internal static readonly Box[] Trough =
    {
        new(-2f, 2f, -1f, 1f, -0.75f, -0.45f),
        new(-2f, -1.7f, -1f, 1f, -0.45f, 0.75f),
        new(1.7f, 2f, -1f, 1f, -0.45f, 0.75f),
    };
    internal const float TroughHeight = 1.5f, TroughFloorTop = 0.3f;

    internal static (Vector3[] verts, int[] tris) Soup(Box[] boxes)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        foreach (Box b in boxes)
        {
            int o = v.Count;
            for (int i = 0; i < 8; i++)
                v.Add(new Vector3((i & 1) == 0 ? b.X0 : b.X1, (i & 2) == 0 ? b.Y0 : b.Y1, (i & 4) == 0 ? b.Z0 : b.Z1));
            int[][] quads = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (int[] q in quads)
            {
                t.AddRange(new[] { o + q[0], o + q[1], o + q[2], o + q[0], o + q[2], o + q[3] });
            }
        }
        return (v.ToArray(), t.ToArray());
    }

    // An open bowl, a grid of n x n quads (2 n^2 triangles), 4 m across and 1 m deep.
    private static (Vector3[] verts, int[] tris) Bowl(int n)
    {
        var v = new Vector3[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                float fx = (x / (float)n - 0.5f) * 2f, fy = (y / (float)n - 0.5f) * 2f;
                v[y * (n + 1) + x] = new Vector3(fx * 2f, fy * 2f, -0.5f + 1f * Math.Min(1f, fx * fx + fy * fy));
            }
        var t = new List<int>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                t.AddRange(new[] { a, b, c, b, d, c });
            }
        return (v, t.ToArray());
    }

    // ---------------------------------------------------------------- the asset

    private static byte[] Zlib(OSD osd)
    {
        byte[] raw = OSDParser.SerializeLLSDBinary(osd, false);
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);   // the mesher skips the two header bytes
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true))
            d.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static ushort Q(float v, float min, float max) => (ushort)Math.Clamp(MathF.Round((v - min) / (max - min) * 65535f), 0f, 65535f);

    private static byte[] U16(IEnumerable<ushort> values)
    {
        var b = new List<byte>();
        foreach (ushort u in values) { b.Add((byte)(u & 0xFF)); b.Add((byte)(u >> 8)); }
        return b.ToArray();
    }

    private static (Vector3 min, Vector3 max) Bounds(IEnumerable<Vector3> pts)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (Vector3 p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    private static OSDArray V3(Vector3 v) => new() { OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z) };

    // A submesh block: the array of maps a "physics_mesh" or a display LOD holds.
    private static byte[] MeshBlock(Vector3[] verts, int[] tris)
    {
        (Vector3 mn, Vector3 mx) = Bounds(verts);
        var pos = new List<ushort>();
        foreach (Vector3 p in verts) { pos.Add(Q(p.X, mn.X, mx.X)); pos.Add(Q(p.Y, mn.Y, mx.Y)); pos.Add(Q(p.Z, mn.Z, mx.Z)); }
        var map = new OSDMap
        {
            ["PositionDomain"] = new OSDMap { ["Min"] = V3(mn), ["Max"] = V3(mx) },
            ["Position"] = OSD.FromBinary(U16(pos)),
            ["TriangleList"] = OSD.FromBinary(U16(tris.Select(i => (ushort)i))),
        };
        return Zlib(new OSDArray { map });
    }

    // The "physics_convex" block: one hull per box (8 points each) and a bounding hull of all the points.
    private static byte[] ConvexBlock(Box[] boxes)
    {
        var hulls = new List<Vector3[]>();
        foreach (Box b in boxes)
        {
            var h = new Vector3[8];
            for (int i = 0; i < 8; i++)
                h[i] = new Vector3((i & 1) == 0 ? b.X0 : b.X1, (i & 2) == 0 ? b.Y0 : b.Y1, (i & 4) == 0 ? b.Z0 : b.Z1);
            hulls.Add(h);
        }
        return ConvexBlockOfHulls(hulls);
    }

    // The "physics_convex" block for any hulls (a hull list of any shape; at most 256 points each, as the byte count says).
    private static byte[] ConvexBlockOfHulls(List<Vector3[]> hulls)
    {
        (Vector3 mn, Vector3 mx) = Bounds(hulls.SelectMany(h => h));
        var pos = new List<ushort>();
        foreach (Vector3[] h in hulls)
            foreach (Vector3 p in h) { pos.Add(Q(p.X, mn.X, mx.X)); pos.Add(Q(p.Y, mn.Y, mx.Y)); pos.Add(Q(p.Z, mn.Z, mx.Z)); }
        var bounding = new List<ushort>();
        for (int i = 0; i < 8; i++)
        {
            bounding.Add(Q((i & 1) == 0 ? mn.X : mx.X, mn.X, mx.X));
            bounding.Add(Q((i & 2) == 0 ? mn.Y : mx.Y, mn.Y, mx.Y));
            bounding.Add(Q((i & 4) == 0 ? mn.Z : mx.Z, mn.Z, mx.Z));
        }
        var map = new OSDMap
        {
            ["Min"] = V3(mn),
            ["Max"] = V3(mx),
            ["HullList"] = OSD.FromBinary(hulls.Select(h => (byte)h.Length).ToArray()),
            ["Positions"] = OSD.FromBinary(U16(pos)),
            ["BoundingVerts"] = OSD.FromBinary(U16(bounding)),
        };
        return Zlib(map);
    }

    /// <summary>The asset as the viewer's upload writes it: a header of offsets, then the blocks. With a physics mesh
    /// the decomposition is just the bounding hull (an upload with a physics file); without one (an upload that used
    /// Analyze) the hull list is there and no physics mesh is.</summary>
    internal static byte[] Asset((Vector3[] verts, int[] tris) display, Box[] boxes, bool withPhysicsMesh)
        => AssetOf(display, withPhysicsMesh ? null : ConvexBlock(boxes), withPhysicsMesh);

    /// <summary>An asset (display LODs, no physics mesh) whose hull list is exactly these hulls.</summary>
    internal static byte[] AssetWithHulls((Vector3[] verts, int[] tris) display, Vector3[][] hulls)
        => AssetOf(display, ConvexBlockOfHulls(hulls.ToList()), false);

    /// <summary>The eight corners of a box, as one hull.</summary>
    internal static Vector3[] BoxHull(Box b)
    {
        var h = new Vector3[8];
        for (int i = 0; i < 8; i++)
            h[i] = new Vector3((i & 1) == 0 ? b.X0 : b.X1, (i & 2) == 0 ? b.Y0 : b.Y1, (i & 4) == 0 ? b.Z0 : b.Z1);
        return h;
    }

    private static byte[] AssetOf((Vector3[] verts, int[] tris) display, byte[] convexBlock, bool withPhysicsMesh)
    {
        var blocks = new List<(string name, byte[] data)>();
        byte[] mesh = MeshBlock(display.verts, display.tris);
        blocks.Add(("medium_lod", mesh));
        blocks.Add(("high_lod", mesh));
        if (withPhysicsMesh)
            blocks.Add(("physics_mesh", mesh));
        else
            blocks.Add(("physics_convex", convexBlock));
        var header = new OSDMap();
        int offset = 0;
        foreach ((string name, byte[] data) in blocks)
        {
            header[name] = new OSDMap { ["offset"] = offset, ["size"] = data.Length };
            offset += data.Length;
        }
        using var ms = new MemoryStream();
        byte[] h = OSDParser.SerializeLLSDBinary(header, true);
        ms.Write(h, 0, h.Length);
        foreach ((_, byte[] data) in blocks)
            ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    internal static PrimitiveBaseShape MeshPbs(byte[] asset)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;   // as the viewer sends a mesh: circle profile, curved path (never the box fast path)
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Mesh;
        pbs.SculptTexture = UUID.Random();   // the mesher caches by this
        pbs.SculptData = asset;
        return pbs;
    }

    // ---------------------------------------------------------------- the scene

    internal static JoltScene NewScene(string scratch, params (string key, string value)[] joltSettings)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", scratch);
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", "45");
        foreach ((string key, string value) in joltSettings)
            jolt.Set(key, value);
        var scene = new JoltScene();
        scene.Initialise(config);
        float[] heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        typeof(JoltScene).GetField("m_mesher", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, mesher);
        return scene;
    }

    /// <summary>A new folder under the system temp path, deleted when the test ends (see <see cref="ScratchFolders"/>).</summary>
    internal static string Scratch() => ScratchFolders.Create();

    internal static void Step(JoltScene scene, float seconds)
    {
        for (int i = 0; i < (int)MathF.Ceiling(seconds / Heartbeat); i++)
            scene.Simulate(Heartbeat);
    }

    internal static uint _id = 1000;

    internal static PhysicsActor AddMesh(JoltScene scene, byte[] asset, float height, bool physical, byte shapeType, out string kind, Vector3? size = null)
    {
        PhysicsActor a = scene.AddPrimShape("mesh", MeshPbs(asset), new Vector3(CentreX, CentreY, Ground + height * 0.5f),
                                            size ?? Vector3.One, Quaternion.Identity, physical, false, shapeType, _id++);
        kind = (string)typeof(JoltPrim).GetField("_shapeKind", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a)!;
        return a;
    }

    internal static void AddBoxes(JoltScene scene, Box[] boxes, float height)
    {
        foreach (Box b in boxes)
        {
            Vector3 size = new(b.X1 - b.X0, b.Y1 - b.Y0, b.Z1 - b.Z0);
            Vector3 centre = new((b.X0 + b.X1) * 0.5f + CentreX, (b.Y0 + b.Y1) * 0.5f + CentreY, (b.Z0 + b.Z1) * 0.5f + Ground + height * 0.5f);
            scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), centre, size, Quaternion.Identity, false, _id++);
        }
    }

    // A 0.4 m ball dropped from 2 m over the trough's middle; where it rests, above the ground.
    internal static float DropBall(JoltScene scene, float top)
    {
        PhysicsActor ball = scene.AddPrimShape("ball", sphere(), new Vector3(CentreX, CentreY, Ground + top + 2f), new Vector3(0.4f, 0.4f, 0.4f),
                                               Quaternion.Identity, true, _id++);
        Step(scene, 4f);
        return ball.Position.Z - Ground;

        static PrimitiveBaseShape sphere()
        {
            PrimitiveBaseShape p = PrimitiveBaseShape.CreateSphere();
            return p;
        }
    }

    // A 0.5 m physical box fired along +y at 8 m/s from 3 m before a gateway; how far past the gateway's far side
    // (y = +1 m) it got in 5 s. Positive means it passed through the opening.
    internal static float FireBox(JoltScene scene)
    {
        PhysicsActor box = scene.AddPrimShape("probe", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY - 3f, Ground + 0.4f),
                                              new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
        Step(scene, Heartbeat);
        box.Velocity = new Vector3(0f, 8f, 0f);
        Step(scene, 5f);
        return box.Position.Y - (CentreY + 1f);
    }

    internal static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);


    // The default appearance's box: the size the physics harness gives an avatar (Run.AvatarSize).
    private static readonly Vector3 AvatarSize = new(0.45f, 0.6f, 1.9f);

    // An avatar set 4 m before the gateway and walked along +y at 3 m/s for 6 s; how far past the far side it got.
    internal static float WalkAvatar(JoltScene scene)
    {
        PhysicsActor av = scene.AddAvatar(777u, "Test User", new Vector3(CentreX, CentreY - 4f, Ground + AvatarSize.Z * 0.5f), AvatarSize, 0f, false);
        Step(scene, 1f);
        av.TargetVelocity = new Vector3(0f, 3f, 0f);
        Step(scene, 6f);
        return av.Position.Y - (CentreY + 1f);
    }
}
