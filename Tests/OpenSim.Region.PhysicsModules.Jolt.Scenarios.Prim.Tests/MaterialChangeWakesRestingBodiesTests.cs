/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A box rests asleep on a fixed platform. The platform's friction, restitution or PRIM_MATERIAL changes (llSetPhysicsMaterial,
/// llSetPrimitiveParams): what rests on it now touches a different surface, so it wakes, as it does when the platform is
/// moved. At an 11 Hz heartbeat with [Jolt] PhysicsStepRate 45. Serial with the other native tests: every run steps a real
/// backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MaterialChangeWakesRestingBodiesTests
{
    private static readonly Vector3 BoxSize = new(0.5f, 0.5f, 0.5f);
    private const double ActAt = 4.0;

    private static void Run(Action<HarnessPart> change, out bool asleepBefore, out bool awakeAfter)
    {
        HarnessPart box = null, platform = null;
        double actedAt = double.NaN;
        bool before = false, after = false;
        var sc = new Scenario
        {
            Name = "material-change-wakes",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                float top = r.GroundAt(128f, 128f) + 3f;
                platform = PhantomScenarios.AddPart(r, "platform", new Vector3(3f, 3f, 0.5f), new Vector3(128f, 128f, top - 0.25f),
                                                    false, false, false, true);
                box = PhantomScenarios.AddPart(r, "box", BoxSize, new Vector3(128f, 128f, top + 0.5f + BoxSize.Z * 0.5f),
                                               true, false, false, true);
                r.Actor = box.Actor;
                r.ActorSize = BoxSize;
            },
            Input = r =>
            {
                var b = (JoltPrim)box.Actor;
                if (double.IsNaN(actedAt) && r.Now >= ActAt)
                {
                    before = b.PhysicalAndAsleep;
                    change(platform);
                    actedAt = r.Now;
                }
                else if (!double.IsNaN(actedAt) && !after && r.Now <= actedAt + 0.3)
                    after = !b.PhysicalAndAsleep;
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
        asleepBefore = before;
        awakeAfter = after;
    }

    [Fact]
    public void A_changed_friction_on_the_platform_wakes_the_box_resting_on_it()
    {
        Run(p => p.Actor.Friction = 0.05f, out bool asleepBefore, out bool awakeAfter);
        Assert.True(asleepBefore, "the box was not asleep before the change");
        Assert.True(awakeAfter, "the box stayed asleep after the platform's friction changed");
    }

    [Fact]
    public void A_changed_restitution_on_the_platform_wakes_the_box_resting_on_it()
    {
        Run(p => p.Actor.Restitution = 0.9f, out bool asleepBefore, out bool awakeAfter);
        Assert.True(asleepBefore, "the box was not asleep before the change");
        Assert.True(awakeAfter, "the box stayed asleep after the platform's restitution changed");
    }

    [Fact]
    public void A_changed_material_type_on_the_platform_wakes_the_box_resting_on_it()
    {
        // 6 is rubber (JoltPrim.MaterialTable), whose friction and restitution differ from a new prim's.
        Run(p => p.Actor.SetMaterial(6), out bool asleepBefore, out bool awakeAfter);
        Assert.True(asleepBefore, "the box was not asleep before the change");
        Assert.True(awakeAfter, "the box stayed asleep after the platform's material changed");
    }

    [Fact]
    public void An_unchanged_friction_on_the_platform_leaves_the_box_asleep()
    {
        Run(p => p.Actor.Friction = p.Actor.Friction, out bool asleepBefore, out bool awakeAfter);
        Assert.True(asleepBefore, "the box was not asleep before the call");
        Assert.False(awakeAfter, "setting the friction it already has woke the box");
    }
}
