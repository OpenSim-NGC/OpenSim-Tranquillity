/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Seam scenarios: a ball, a sled (a flat box) and an avatar crossing ground made of many triangles, to count the
// vertical hitches the shared edges inside it cause. Two grounds:
//   - a fixed triangle-mesh floor, a flat slab 40 m square whose top is a grid of 0.5 m cells (12800 triangles), a
//     mesh prim built from an asset the harness writes;
//   - the terrain itself, rolling hills (a heightfield, whose cell edges are the seams).
// A hitch is a sample whose vertical speed differs from what the two before it predict by more than HitchSpeed (0.1 m/s):
// a spike in the vertical speed that the ground's own shape cannot explain. HitchCount(samples) is the figure.
//
// [Jolt] BodyEnhancedEdgeRemoval and AvatarEnhancedEdgeRemoval are what is being compared (--jolt KEY=true).

using System.IO.Compression;
using OpenMetaverse;
using Nini.Config;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class SeamScenarios
{
    /// <summary>The floor's centre, side and thickness, and the cells along a side.</summary>
    public const float FloorX = 128f, FloorY = 128f, FloorSide = 40f, FloorThickness = 0.5f;
    public const int FloorCells = 80;
    /// <summary>Where a mover starts and how fast it goes along +x.</summary>
    public const float StartX = FloorX - 18f, MoverSpeed = 6f, AvatarWalk = 3f;
    /// <summary>Samples before this time are not counted: the mover is settling and being given its speed.</summary>
    public const double SettleSeconds = 1.5;
    /// <summary>The smallest departure of the vertical speed from the straight line through the two samples before it that
    /// is a hitch (m/s).</summary>
    public const float HitchSpeed = 0.1f;

    /// <summary>The floor's top above the terrain it stands on.</summary>
    public const float FloorTop = FloorThickness * 0.5f;

    /// <summary>Counts the hitches in a run's samples after the settle time, and returns the largest departure (m/s).</summary>
    public static (int hitches, float largest) HitchCount(IReadOnlyList<Sample> samples)
    {
        int hitches = 0;
        float largest = 0f;
        for (int i = 2; i < samples.Count; i++)
        {
            if (samples[i].T < SettleSeconds)
                continue;
            float departure = MathF.Abs(samples[i].Velocity.Z - 2f * samples[i - 1].Velocity.Z + samples[i - 2].Velocity.Z);
            largest = MathF.Max(largest, departure);
            if (departure > HitchSpeed)
                hitches++;
        }
        return (hitches, largest);
    }

    // ------------------------------------------------------------------ the floor asset

    private static byte[] Zlib(OSD osd)
    {
        byte[] raw = OSDParser.SerializeLLSDBinary(osd, false);
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);   // the mesher skips the two header bytes
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true))
            d.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] U16(IEnumerable<ushort> values)
    {
        var b = new List<byte>();
        foreach (ushort u in values) { b.Add((byte)(u & 0xFF)); b.Add((byte)(u >> 8)); }
        return b.ToArray();
    }

    private static ushort Q(float v, float min, float max) => (ushort)Math.Clamp(MathF.Round((v - min) / (max - min) * 65535f), 0f, 65535f);

    private static OSDArray V3(Vector3 v) => new() { OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z) };

    /// <summary>A slab <paramref name="side"/> m square and <paramref name="thickness"/> thick, centred on the origin. Its top is
    /// a grid of cells x cells quads (two triangles each, all in one plane); the rest is its bottom and four sides. Every
    /// triangle faces out.</summary>
    internal static (Vector3[] verts, int[] tris) Slab(float side, float thickness, int cells, Func<float, float, float> ripple = null)
    {
        float h = side * 0.5f, t = thickness * 0.5f;
        var v = new List<Vector3>();
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
            {
                float x = -h + side * i / cells, y = -h + side * j / cells;
                v.Add(new Vector3(x, y, t + (ripple?.Invoke(x, y) ?? 0f)));
            }
        int Top(int i, int j) => j * (cells + 1) + i;
        int cornerStart = v.Count;
        v.Add(new Vector3(-h, -h, -t)); v.Add(new Vector3(h, -h, -t)); v.Add(new Vector3(h, h, -t)); v.Add(new Vector3(-h, h, -t));
        var tris = new List<int>();
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                int a = Top(i, j), b = Top(i + 1, j), c = Top(i + 1, j + 1), d = Top(i, j + 1);
                tris.AddRange(new[] { a, b, c, a, c, d });
            }

        // The other faces, each triangle turned outward by its own normal.
        void Face(int p, int q, int r, Vector3 outward)
        {
            Vector3 n = Vector3.Cross(v[q] - v[p], v[r] - v[p]);
            if (Vector3.Dot(n, outward) < 0f) { (q, r) = (r, q); }
            tris.AddRange(new[] { p, q, r });
        }
        int bA = cornerStart, bB = cornerStart + 1, bC = cornerStart + 2, bD = cornerStart + 3;
        int tA = Top(0, 0), tB = Top(cells, 0), tC = Top(cells, cells), tD = Top(0, cells);
        Face(bA, bC, bB, -Vector3.UnitZ); Face(bA, bD, bC, -Vector3.UnitZ);
        Face(tA, bA, bB, -Vector3.UnitY); Face(tA, bB, tB, -Vector3.UnitY);
        Face(tB, bB, bC, Vector3.UnitX); Face(tB, bC, tC, Vector3.UnitX);
        Face(tC, bC, bD, Vector3.UnitY); Face(tC, bD, tD, Vector3.UnitY);
        Face(tD, bD, bA, -Vector3.UnitX); Face(tD, bA, tA, -Vector3.UnitX);
        return (v.ToArray(), tris.ToArray());
    }

    private static byte[] MeshBlock(Vector3[] verts, int[] tris)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (Vector3 p in verts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
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

    /// <summary>A mesh asset as the viewer's upload writes it: a header of offsets, then the display LODs and a physics mesh,
    /// all the one mesh.</summary>
    internal static byte[] MeshAsset(Vector3[] verts, int[] tris)
    {
        byte[] mesh = MeshBlock(verts, tris);
        var blocks = new List<(string name, byte[] data)> { ("medium_lod", mesh), ("high_lod", mesh), ("physics_mesh", mesh) };
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

    /// <summary>Adds the floor as a fixed mesh prim on the terrain (its top <see cref="FloorTop"/> above the ground there) and
    /// returns the height of its top.</summary>
    public static float AddFloor(Run r, bool rippled = false)
    {
        (Vector3[] verts, int[] tris) = Slab(FloorSide, FloorThickness, FloorCells, rippled ? (x, y) => 0.04f * MathF.Sin(x * 2.1f) * MathF.Cos(y * 1.7f) : null);
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;   // as the viewer sends a mesh: never the box fast path
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Mesh;
        pbs.SculptTexture = UUID.Random();
        pbs.SculptData = MeshAsset(verts, tris);
        float ground = r.GroundAt(FloorX, FloorY);
        if (r.Mesher == null)
        {
            // The run has no region mesher (--no-region-mesher): the module's own class, as the earlier harness made it.
            var config = new IniConfigSource();
            config.AddConfig("Startup").Set("DecodedSculptMapPath", Path.Combine(Path.GetTempPath(), "harness-sculpt-cache"));
            var mesher = new Meshmerizer();
            mesher.Initialise(config);
            r.UseMesher(mesher);
        }
        PhysicsActor floor = r.Scene.AddPrimShape("harness floor", pbs, new Vector3(FloorX, FloorY, ground + FloorThickness * 0.5f),
                                                  Vector3.One, Quaternion.Identity, false, false, (byte)PhysShapeType.prim, 2000u);
        if (floor == null || floor == PhysicsActor.Null)
            throw new InvalidOperationException("the floor was not added");
        return ground + FloorThickness;
    }

    // ------------------------------------------------------------------ the movers

    private static void Launch(Run r, float speed)
    {
        // Given its speed two heartbeats in, once it has landed; the first heartbeat is the region's load.
        if (!r.Released && r.Now >= 2 * r.Dt - 1e-9)
        {
            r.Actor.Velocity = new Vector3(speed, 0f, 0f);
            r.Released = true;
        }
    }

    private static Scenario Ball(string name, string description, Func<Run, float> ground, Action<Run> world = null)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                world?.Invoke(r);
                r.AddSphere(0.4f, new Vector3(StartX, FloorY, ground(r) + 0.2f + 0.01f));
            },
            Input = r => Launch(r, MoverSpeed),
        };

    private static Scenario Sled(string name, string description, Func<Run, float> ground, Action<Run> world = null)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                world?.Invoke(r);
                r.AddBox(new Vector3(1f, 0.6f, 0.15f), new Vector3(StartX, FloorY, ground(r) + 0.075f + 0.01f), Quaternion.Identity);
                r.Actor.Friction = 0.1f;
            },
            Input = r => Launch(r, MoverSpeed),
        };

    private static Scenario Walker(string name, string description, Func<Run, float> ground, Action<Run> world = null)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                world?.Invoke(r);
                r.AddAvatarAt(new Vector3(StartX, FloorY, ground(r) + r.AvatarStandHalf + 0.01f), false);
            },
            Input = r =>
            {
                if (r.Now >= 2 * r.Dt - 1e-9)
                    r.Actor.TargetVelocity = new Vector3(AvatarWalk, 0f, 0f);
            },
        };

    // Rolling hills for the terrain scenarios: gentle (a few degrees), so only the cell edges, not the hills, hitch.
    private static float Hill(int x, int y) => Course.Ground + 0.9f * MathF.Sin(x / 9f) + 0.7f * MathF.Cos(y / 11f);

    private static (float[] heights, float water) Hills(float _)
    {
        var h = new float[Course.Size * Course.Size];
        for (int y = 0; y < Course.Size; y++)
            for (int x = 0; x < Course.Size; x++)
                h[y * Course.Size + x] = Hill(x, y);
        return (h, Course.Water);
    }

    private static float FlatFloorTop(Run r) => r.GroundAt(FloorX, FloorY) + FloorThickness;
    private static float RippleFloorTop(Run r) => FlatFloorTop(r) + 0.05f;   // clear of the ripple's crests

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Ball("seam-ball", "A 0.4 m physical ball rolling along +x at 6 m/s over a flat mesh floor of 12800 triangles.", FlatFloorTop, r => AddFloor(r)),
        Sled("seam-sled", "A 1 x 0.6 x 0.15 m physical box (friction 0.1) sliding along +x at 6 m/s over a flat mesh floor of 12800 triangles.", FlatFloorTop, r => AddFloor(r)),
        Walker("seam-walker", "An avatar walking along +x at 3 m/s over a flat mesh floor of 12800 triangles.", FlatFloorTop, r => AddFloor(r)),
        Ball("seam-ripple-ball", "As seam-ball over a floor whose top ripples (4 cm, every 3 m).", RippleFloorTop, r => AddFloor(r, rippled: true)),
        Sled("seam-ripple-sled", "As seam-sled over a floor whose top ripples (4 cm, every 3 m).", RippleFloorTop, r => AddFloor(r, rippled: true)),
        Walker("seam-ripple-walker", "As seam-walker over a floor whose top ripples (4 cm, every 3 m).", RippleFloorTop, r => AddFloor(r, rippled: true)),
        WithHills(Ball("seam-terrain-ball", "A 0.4 m physical ball rolling along +x at 6 m/s over rolling terrain.", r => r.GroundAt(StartX, FloorY))),
        WithHills(Sled("seam-terrain-sled", "A 1 x 0.6 x 0.15 m physical box (friction 0.1) sliding along +x at 6 m/s over rolling terrain.", r => r.GroundAt(StartX, FloorY))),
        WithHills(Walker("seam-terrain-walker", "An avatar walking along +x at 3 m/s over rolling terrain.", r => r.GroundAt(StartX, FloorY))),
    };

    private static Scenario WithHills(Scenario s)
    {
        s.World = Hills;
        return s;
    }
}
