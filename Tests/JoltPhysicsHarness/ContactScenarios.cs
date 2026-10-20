/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Resting-contact scenarios: what a box's collision events do while it rests on something and falls asleep, and when it
// is thrown off again; and that stacked and dropped boxes come to rest.
//
// Second Life documents (wiki.secondlife.com): collision_start is "Triggered when task starts colliding with another
// task", collision "while task is colliding with another task" and collision_end "when task stops colliding with
// another task"; "A collision with a physical object or avatar resting on object does not continuously trigger
// collisions but for a few times, unless there is movement" (the collision event). land_collision_start, land_collision
// and land_collision_end are the same for the land. A volume detector raises "collision_start and collision_end but not
// collision() events".
//
// Each box here is given the prim defaults the scene gives a new prim (wood: friction 0.6, restitution 0.5;
// SceneObjectPart.AddToPhysics), is let fall onto what it rests on, and is left long enough to fall asleep; the resting
// scenarios then throw it off at LiftAt with an impulse, as llApplyImpulse would, which wakes it.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class ContactScenarios
{
    public const float LiftAt = 7f;
    public static readonly Vector3 BoxSize = new(0.5f, 0.5f, 0.5f);
    public const float PlatformHeight = 3f;   // the platform's top, above the ground

    private static void Wood(HarnessPart p)
    {
        p.Actor.Friction = 0.6f;
        p.Actor.Restitution = 0.5f;
    }

    // Notes, before every heartbeat, when each physical part's body first falls asleep after it has been awake (a body is
    // created asleep and woken by the first step), and whether it is asleep now; and where it is, for a part that keeps a
    // trace.
    private static void WatchSleep(Run r)
    {
        foreach (HarnessPart p in r.Parts)
        {
            if (!p.Physical || p.Actor is not JoltPrim jp)
                continue;
            p.SleepWatched = true;
            p.Trace?.Add((r.Now, p.Actor.Position, p.Actor.Velocity));
            if (jp.BodyAwake)
                p.WasAwake = true;
            p.AsleepAtEnd = jp.PhysicalAndAsleep;
            if (p.AsleepAtEnd && p.WasAwake && double.IsNaN(p.SleptAt))
                p.SleptAt = r.Now;
        }
    }

    // Throws the run's box at LiftAt with an impulse giving it `velocity` (an impulse of mass x velocity).
    private static void LiftAtTime(Run r, Vector3 velocity)
    {
        if (r.Stage == 0 && r.Now >= LiftAt - 1e-9)
        {
            r.Actor.AddForce(velocity * r.Actor.Mass, false);
            r.Stage = 1;
        }
    }

    private static HarnessPart DropBox(Run r, float x, float y, float bottom)
    {
        HarnessPart box = PhantomScenarios.AddPart(r, "box", BoxSize, new Vector3(x, y, bottom + BoxSize.Z * 0.5f), true, false, false, true);
        Wood(box);
        r.Actor = box.Actor;
        r.ActorSize = BoxSize;
        return box;
    }

    private static Scenario Resting(string name, string description, Action<Run> setup, Vector3 throwVelocity)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 10f,
            Setup = setup,
            Input = r =>
            {
                WatchSleep(r);
                LiftAtTime(r, throwVelocity);
            },
        };

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Resting("rest-platform",
            "A 0.5 m box falls 1 m onto a fixed 3 x 3 m platform 3 m up and rests there until it is thrown off at 7 s (4 m/s east, 4 m/s up).",
            r =>
            {
                float ground = r.GroundAt(128f, 128f);
                float top = ground + PlatformHeight;
                PhantomScenarios.AddPart(r, "platform", new Vector3(3f, 3f, 0.5f), new Vector3(128f, 128f, top - 0.25f), false, false, false, true);
                DropBox(r, 128f, 128f, top + 1f);
            },
            new Vector3(4f, 0f, 4f)),
        Resting("rest-ground",
            "A 0.5 m box falls 1 m onto the ground and rests there until it is thrown up at 7 s (4 m/s), lands and rests again.",
            r => DropBox(r, 128f, 128f, r.GroundAt(128f, 128f) + 1f),
            new Vector3(0f, 0f, 4f)),
        Resting("rest-vd",
            "A 0.5 m box falls into a fixed 3 x 3 x 2 m volume-detect box standing on a fixed 4 x 4 m platform 3 m up, rests on the platform inside it, and is thrown out at 7 s (6 m/s east, 3 m/s up).",
            r =>
            {
                float ground = r.GroundAt(128f, 128f);
                float top = ground + PlatformHeight;
                PhantomScenarios.AddPart(r, "platform", new Vector3(4f, 4f, 0.5f), new Vector3(128f, 128f, top - 0.25f), false, false, false, false);
                PhantomScenarios.AddPart(r, "detector", new Vector3(3f, 3f, 2f), new Vector3(128f, 128f, top + 1f), false, false, true, true);
                DropBox(r, 128f, 128f, top + 2.5f);
            },
            new Vector3(6f, 0f, 3f)),
        new()
        {
            Name = "tower-10",
            Description = "Ten 0.5 m boxes stacked on the ground, each resting on the one below; they stand and fall asleep.",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                float ground = r.GroundAt(128f, 128f);
                HarnessPart top = null;
                for (int i = 0; i < 10; i++)
                {
                    top = PhantomScenarios.AddPart(r, $"box{i + 1}", BoxSize, new Vector3(128f, 128f, ground + BoxSize.Z * (i + 0.5f)), true, false, false, false);
                    Wood(top);
                    top.Trace = new List<(double, Vector3, Vector3)>();   // every box's sway, in the harness's --out folder
                }
                r.Actor = top.Actor;
                r.ActorSize = BoxSize;
            },
            Input = WatchSleep,
        },
        new()
        {
            Name = "rest-no-bounce",
            Description = "A 0.5 m box with restitution 0 falls 2 m onto the ground; it stays down and falls asleep.",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                HarnessPart box = DropBox(r, 128f, 128f, r.GroundAt(128f, 128f) + 2f);
                box.Actor.Restitution = 0f;
            },
            Input = WatchSleep,
        },
    };
}
