/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// An attachment's buoyancy, hover height and constant force acting on its wearer, and the velocity an avatar reports.
//
// Core hands an attachment's llSetForce to the wearer's actor as SetConstantForce (ScenePresence.SetConstantForce): "Used
// on an attachment, it will apply the force to the avatar" (SL wiki llSetForce).
//
// Core hands an attachment's llSetBuoyancy to the wearer's actor as Buoyancy (SceneObjectGroup.SetBuoyancy), and its
// llSetHoverHeight and llStopHover as PIDHoverHeight, PIDHoverType, PIDHoverTau and PIDHoverActive, in that order
// (SceneObjectGroup.SetHoverHeight). These scenarios make the same calls on the avatar's actor.
//
// The Second Life wiki: llSetBuoyancy, "when buoyancy is < 1.0, the object sinks", "when buoyancy equals 1.0 it floats",
// "when buoyancy is > 1.0 the object rises"; llSetHoverHeight, "Critically damps to a height above the ground (or water)
// in tau seconds", and its example is "Put in an attached prim and touch to start floating in air without flying".

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class AvatarForceScenarios
{
    /// <summary>The walk request ScenePresence sends at speed modifier 1 (m/s).</summary>
    public const float WalkRequest = 4.096f;
    /// <summary>When the avatar starts walking in the ledge, wall and hover scenarios (s).</summary>
    public const float WalkAt = 0.5f;

    /// <summary>The ledge: the avatar-platform platform (4 x 4 m, its top 3 m above level ground at 170, 60).</summary>
    public const float LedgeX = 170f, LedgeY = 60f;

    /// <summary>Hover: set at <see cref="HoverAt"/> with this height and tau, walking from <see cref="HoverWalkAt"/>,
    /// stopped at <see cref="HoverStopAt"/>.</summary>
    public const float HoverHeight = 3f, HoverTau = 0.5f, HoverAt = 0.5f, HoverWalkAt = 4f, HoverStopAt = 16f;

    /// <summary>The wall's west face, 3 m east of where the avatar starts.</summary>
    public const float WallFaceX = 173f;

    /// <summary>The moving platform's speed (m/s, east).</summary>
    public const float PlatformSpeed = 2f;

    // An avatar on the ledge's platform, 1 m west of its middle, walking east off it from WalkAt, with this buoyancy.
    private static Scenario Ledge(string name, float buoyancy, bool setBuoyancy) => new()
    {
        Name = name,
        Description = setBuoyancy
            ? $"An avatar wearing an attachment that set buoyancy {buoyancy} walks east ({WalkRequest} m/s asked) off a platform 3 m above level ground, from {WalkAt} s."
            : $"An avatar with no buoyancy set walks east ({WalkRequest} m/s asked) off a platform 3 m above level ground, from {WalkAt} s.",
        DefaultDuration = _ => 12f,
        Setup = r =>
        {
            float top = Harness.AddPlatform(r);
            r.AddAvatarAt(new Vector3(LedgeX - 1f, LedgeY, top + r.AvatarStandHalf), false);
            if (setBuoyancy)
                r.Actor.Buoyancy = buoyancy;
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= WalkAt - 1e-9)
            {
                r.Actor.TargetVelocity = new Vector3(WalkRequest, 0f, 0f);
                r.Stage = 1;
            }
        },
    };

    // Hover set from an attachment on an avatar standing on the ground; it walks once it is held there and the hover is
    // stopped near the end. Level ground: east from (60, 60). Slope: north from (128, 24) onto the ramp, which starts at
    // y 40.
    private static Scenario Hover() => new()
    {
        Name = "avatar-hover",
        Description = $"An avatar standing on the ground gets hover height {HoverHeight} m (tau {HoverTau} s, ground) from an attachment at {HoverAt} s, walks ({WalkRequest} m/s asked) from {HoverWalkAt} s, and the hover is stopped at {HoverStopAt} s. Level ground: east from (60, 60). Slope: north from y 24 up the ramp.",
        Slopes = new[] { 0f, 15f }, UsesSlope = true,
        DefaultDuration = _ => HoverStopAt + 3f,
        Setup = r =>
        {
            if (r.Slope > 0f) r.AddAvatar(128f, 24f);
            else r.AddAvatar(60f, 60f);
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= HoverAt - 1e-9)
            {
                r.Actor.PIDHoverHeight = HoverHeight;
                r.Actor.PIDHoverType = PIDHoverType.Ground;
                r.Actor.PIDHoverTau = HoverTau;
                r.Actor.PIDHoverActive = true;
                r.Stage = 1;
            }
            if (r.Stage == 1 && r.Now >= HoverWalkAt - 1e-9)
            {
                r.Actor.TargetVelocity = r.Slope > 0f ? new Vector3(0f, WalkRequest, 0f) : new Vector3(WalkRequest, 0f, 0f);
                r.Stage = 2;
            }
            if (r.Stage == 2 && r.Now >= HoverStopAt - 1e-9)
            {
                // llStopHover: SceneObjectGroup.SetHoverHeight(0) sets PIDHoverActive false. The avatar stops walking too.
                r.Actor.PIDHoverActive = false;
                r.Actor.TargetVelocity = Vector3.Zero;
                r.Stage = 3;
            }
        },
    };

    /// <summary>Where the constant force scenarios put the avatar: over level ground, this far above it (m), not flying.</summary>
    public const float ForceX = 60f, ForceY = 60f, ForceWeightHeight = 20f, ForceAcrossHeight = 100f;

    /// <summary>The level acceleration (m/s2) the horizontal constant force gives the avatar.</summary>
    public const float ForceAcross = 2f;

    // An avatar put in the air over level ground, not flying, wearing an attachment whose llSetForce sets the constant
    // force: core hands it to the wearer's actor as SetConstantForce (ScenePresence.SetConstantForce), in newtons, here in
    // region axes. The force is the avatar's mass times an acceleration, so it reads the actor's own mass.
    private static Scenario ConstantForce(string name, string description, float height, float duration, Func<float, Vector3> forceForMass) => new()
    {
        Name = name,
        Description = description,
        DefaultDuration = _ => duration,
        Setup = r =>
        {
            r.AddAvatarAt(new Vector3(ForceX, ForceY, r.GroundAt(ForceX, ForceY) + height + r.AvatarStandHalf), false);
            r.Actor.SetConstantForce(forceForMass(r.Actor.Mass), false);
        },
        Input = _ => { },
    };

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        ConstantForce("avatar-force-weight",
            $"An avatar, not flying, is put {ForceWeightHeight} m above level ground wearing an attachment whose llSetForce pushes it up with its own weight (mass x 9.80665 N, region axes), for 12 s.",
            ForceWeightHeight, 12f, m => new Vector3(0f, 0f, m * 9.80665f)),
        ConstantForce("avatar-force-across",
            $"An avatar, not flying, is put {ForceAcrossHeight} m above level ground wearing an attachment whose llSetForce pushes it east with mass x {ForceAcross} N (region axes), and falls for 4 s.",
            ForceAcrossHeight, 4f, m => new Vector3(m * ForceAcross, 0f, 0f)),
        Ledge("avatar-buoyancy-1", 1f, true),
        Ledge("avatar-buoyancy-half", 0.5f, true),
        Ledge("avatar-buoyancy-0", 0f, true),
        Ledge("avatar-ledge", 0f, false),
        Hover(),
        new()
        {
            Name = "avatar-wall",
            Description = $"An avatar walks east ({WalkRequest} m/s asked) on level ground from {WalkAt} s into a fixed wall 3 m ahead, and keeps walking into it.",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                r.AddAvatar(WallFaceX - 3f, LedgeY);
                r.AddOtherBox(new Vector3(0.5f, 4f, 3f), new Vector3(WallFaceX + 0.25f, LedgeY, r.GroundAt(WallFaceX, LedgeY) + 1.5f), Quaternion.Identity, false);
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= WalkAt - 1e-9)
                {
                    r.Actor.TargetVelocity = new Vector3(WalkRequest, 0f, 0f);
                    r.Stage = 1;
                }
            },
        },
        new()
        {
            Name = "avatar-moving-platform",
            Description = $"An avatar arrives standing on a floating 4 x 4 x 0.5 m physical platform of 2000 kg, 5 m above level ground, whose script keeps it moving east at {PlatformSpeed} m/s, and stands 6 s.",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                float top = r.GroundAt(100f, 60f) + 5f;
                var size = new Vector3(4f, 4f, 0.5f);
                HarnessPart platform = PhantomScenarios.AddPart(r, "platform", size, new Vector3(100f, 60f, top - 0.25f), true, false, false, false);
                platform.Actor.Density = 2000f / (size.X * size.Y * size.Z) / 0.01f;   // SceneObjectPart.Density x 0.01 is kg/m3
                platform.Actor.Buoyancy = 1f;
                platform.Actor.Velocity = new Vector3(PlatformSpeed, 0f, 0f);
                platform.Trace = new List<(double, Vector3, Vector3)>();
                r.AddAvatarAt(new Vector3(100f, 60f, top + r.AvatarStandHalf + 0.01f), false);
            },
            Input = r =>
            {
                HarnessPart platform = r.Parts[0];
                platform.Actor.Velocity = new Vector3(PlatformSpeed, 0f, 0f);
                platform.Trace.Add((r.Now, platform.Actor.Position, platform.Actor.Velocity));
            },
        },
    };
}
