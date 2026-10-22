/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>A 6 x 6 x 0.5 m non-physical platform 3 m up with a rider on it (a 0.5 m cube or an avatar at its middle, or none),
/// and the heartbeat hooks the kinematic tests need.</summary>
internal static class KinematicTestRig
{
    private const uint SampledBoxLocalId = 1999u;

    public static Scenario Platform(string rider, float duration, Action<Run> input, Action<Run> setup = null)
        => new()
        {
            Name = "kinematic-custom",
            DefaultDuration = _ => duration,
            Setup = r =>
            {
                float top = r.GroundAt(KinematicScenarios.CentreX, KinematicScenarios.CentreY) + KinematicScenarios.PlatformHeight;
                var start = new Vector3(KinematicScenarios.CentreX, KinematicScenarios.CentreY, top - KinematicScenarios.PlatformThickness * 0.5f);
                r.AddOtherBox(new Vector3(6f, 6f, 0.5f), start, Quaternion.Identity, false);
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(start.X, start.Y, top + 0.26f), Quaternion.Identity);
                else if (rider == "avatar")
                    r.AddAvatarAt(new Vector3(start.X, start.Y, top + r.AvatarStandHalf + 0.05f), false);
                else
                {
                    // A box on the ground far from the platform: the run needs an actor to sample. It has an id of its own: a test
                    // that adds its rider later (through AddBox or AddAvatarAt, which use the run's actor id) would otherwise have two
                    // bodies with one id, and the rider's place read now and then as this box's.
                    r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(60f, 60f, r.GroundAt(60f, 60f) + 0.26f), Quaternion.Identity, true, SampledBoxLocalId);
                    r.ActorSize = new Vector3(0.5f, 0.5f, 0.5f);
                }
                setup?.Invoke(r);
            },
            Input = input,
        };

    public static RunResult Run(Scenario sc, double physicsRate, params (string Key, string Value)[] jolt)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate };
        foreach ((string key, string value) in jolt)
            o.Jolt[key] = value;
        return Harness.Harness.Run(sc, o);
    }

    public static JoltPrim PlatformPrim(Run r) => (JoltPrim)r.Other;
    public static JoltScene SceneOf(Run r) => (JoltScene)r.PhysicsScene;
}
