/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Shot scenarios: a small physical ball shot fast at something it must not pass through (a thin fixed wall, a physical
// box resting on the ground, the ground itself), and at things it must pass through (a volume-detect slab, a physical
// phantom box). The ball's diameter and speed come from the options (--ball, --shot-speed), so one scenario covers a
// whole sweep. A run's summary has tunneled = 1 when the ball went through what it was shot at.
//
// The ball is added at the start and given its speed one heartbeat later, as a script's llSetVelocity reaches a body
// that has just gone physical (the region's load heartbeat keeps no horizontal velocity; JoltPrim.Velocity).

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class TunnelScenarios
{
    public const float DefaultBall = 0.2f;
    public const float DefaultSpeed = 25f;

    /// <summary>What the ball is shot at: centred on x = TargetX, y = ShotY.</summary>
    public const float TargetX = 128f, ShotY = 128f;
    /// <summary>The gap between the ball's front and the target's near face at the start (m).</summary>
    public const float StartGap = 2f;
    /// <summary>The ball's centre above the ground when it is shot at a wall (m); the walls stand twice as tall.</summary>
    public const float ShotHeight = 3f;
    public const float WallSpan = 2f * ShotHeight;
    public const float BoxSize = 0.5f;
    /// <summary>The volume-detect slab of tunnel-vd-wall: its thickness, and the gap from its far face to the wall.</summary>
    public const float SlabThickness = 1f, SlabToWall = 2f;
    public const uint SlabLocalId = PhantomScenarios.FirstPartId;
    /// <summary>The thickness of the volume-detect slab of tunnel-vd-slab (m).</summary>
    public const float ThinSlab = 0.1f;

    private static float Ball(Run r) => r.Options.ShotBall;
    private static float Ground(Run r) => r.GroundAt(TargetX, ShotY);

    // The ball, its front StartGap before a face at x = face, its centre at height z.
    private static void AddBall(Run r, float face, float z, bool phantom = false)
    {
        float d = Ball(r);
        r.AddSphere(d, new Vector3(face - StartGap - d * 0.5f, ShotY, z), phantom);
    }

    // Shoot on the first heartbeat after the load heartbeat.
    private static void Shoot(Run r, Vector3 direction)
    {
        if (!r.Released && r.Now >= r.Dt - 1e-9)
        {
            r.Actor.Velocity = direction * r.Options.ShotSpeed;
            r.Released = true;
        }
    }

    private static void AddWall(Run r, float thickness)
        => r.AddOtherBox(new Vector3(thickness, WallSpan, WallSpan), new Vector3(TargetX, ShotY, Ground(r) + WallSpan * 0.5f),
                         Quaternion.Identity, false);

    // A wall of the given thickness, the ball shot at it along x. Went through it: the ball's centre got past the
    // wall's middle.
    private static Scenario Wall(string name, float thickness, bool ballPhantom = false)
        => new()
        {
            Name = name,
            Description = ballPhantom
                ? $"A physical phantom ball shot along x at a fixed wall {thickness:0.##} m thick; it should pass through."
                : $"A physical ball shot along x at a fixed wall {thickness:0.##} m thick, {WallSpan} m tall and wide.",
            UsesShot = true,
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                AddWall(r, thickness);
                AddBall(r, TargetX - thickness * 0.5f, Ground(r) + ShotHeight, ballPhantom);
                r.ReleaseAt = r.Dt;
            },
            Input = r => Shoot(r, Vector3.UnitX),
            PassedThrough = (r, s) => s.Position.X > TargetX,
        };

    // A 0.5 m box resting on the ground, the ball shot at it along x low enough to hit its face.
    private static Scenario Box(string name, bool boxPhantom)
        => new()
        {
            Name = name,
            Description = boxPhantom
                ? "A physical ball shot along x at a physical phantom 0.5 m box resting on the ground; it should pass through."
                : "A physical ball shot along x at a physical 0.5 m box resting on the ground.",
            UsesShot = true,
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                float ground = Ground(r);
                r.AddOtherBox(new Vector3(BoxSize, BoxSize, BoxSize), new Vector3(TargetX, ShotY, ground + BoxSize * 0.5f),
                              Quaternion.Identity, true, boxPhantom);
                AddBall(r, TargetX - BoxSize * 0.5f, ground + MathF.Max(BoxSize * 0.5f, Ball(r) * 0.5f + 0.01f));
                r.ReleaseAt = r.Dt;
            },
            Input = r => Shoot(r, Vector3.UnitX),
            PassedThrough = PastBox,
        };

    // Went through the box: the ball's centre got past the box's along the shot while the two still overlap across it
    // (in y and in z, taking the box at its largest reach, half its diagonal, however a hit has turned it). A ball that
    // overtakes the box clear of it, over, under or beside it after both were thrown by the hit, did not go through.
    // Samples taken after either has left the region do not count: a fast hit can throw the box out ahead of the ball,
    // and from then on the box waits just outside the edge (Harness.BothInRegion), so the ball, leaving after it,
    // reaches and passes where the box waits without having gone through it.
    private static bool PastBox(Run r, Sample s)
    {
        if (!Harness.BothInRegion(r, s))
            return false;
        float reach = Ball(r) * 0.5f + BoxSize * 0.5f * MathF.Sqrt(3f);
        return s.Position.X > s.Other.X && MathF.Abs(s.Position.Y - s.Other.Y) < reach && MathF.Abs(s.Position.Z - s.Other.Z) < reach;
    }

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Wall("tunnel-wall-1cm", 0.01f),
        Wall("tunnel-wall-10cm", 0.1f),
        Box("tunnel-box", false),
        new()
        {
            Name = "tunnel-ground",
            Description = "A physical ball shot straight down at level ground from 3 m above it.",
            UsesShot = true,
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                r.AddSphere(Ball(r), new Vector3(TargetX, ShotY, Ground(r) + ShotHeight));
                r.ReleaseAt = r.Dt;
            },
            Input = r => Shoot(r, -Vector3.UnitZ),
            // Went through the ground: the ball's centre got below it.
            PassedThrough = (r, s) => s.Position.Z < r.GroundAt(s.Position.X, s.Position.Y),
        },
        new()
        {
            Name = "tunnel-vd-wall",
            Description = $"A physical ball shot along x through a fixed volume-detect slab {SlabThickness} m thick, then at a fixed wall 0.1 m thick {SlabToWall} m beyond it.",
            UsesShot = true,
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                float ground = Ground(r);
                AddWall(r, 0.1f);
                float slabX = TargetX - 0.05f - SlabToWall - SlabThickness * 0.5f;
                PhantomScenarios.AddPart(r, "slab", new Vector3(SlabThickness, WallSpan, WallSpan), new Vector3(slabX, ShotY, ground + WallSpan * 0.5f),
                                         false, false, true, true);
                AddBall(r, slabX - SlabThickness * 0.5f, ground + ShotHeight);
                r.ReleaseAt = r.Dt;
            },
            Input = r => Shoot(r, Vector3.UnitX),
            PassedThrough = (r, s) => s.Position.X > TargetX,
        },
        new()
        {
            Name = "tunnel-vd-slab",
            Description = "A physical ball shot along x through a fixed volume-detect slab 0.1 m thick, with nothing beyond it.",
            UsesShot = true,
            DefaultDuration = _ => 0.5f,
            Setup = r =>
            {
                float ground = Ground(r);
                PhantomScenarios.AddPart(r, "slab", new Vector3(ThinSlab, WallSpan, WallSpan), new Vector3(TargetX, ShotY, ground + WallSpan * 0.5f),
                                         false, false, true, true);
                AddBall(r, TargetX - ThinSlab * 0.5f, ground + ShotHeight);
                r.ReleaseAt = r.Dt;
            },
            Input = r => Shoot(r, Vector3.UnitX),
            PassedThrough = (r, s) => s.Position.X > TargetX,
        },
        Box("tunnel-phantom-box", true),
        Wall("tunnel-phantom-ball", 0.1f, ballPhantom: true),
    };
}
