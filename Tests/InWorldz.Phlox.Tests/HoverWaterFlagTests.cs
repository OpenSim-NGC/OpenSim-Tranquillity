/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What llSetHoverHeight and llGroundRepel hand the physical prim's actor for their water flag. The Second Life wiki
/// (https://wiki.secondlife.com/wiki/LlSetHoverHeight, https://wiki.secondlife.com/wiki/LlGroundRepel): water TRUE means the
/// height is above the ground "or the water, whichever is higher", FALSE means above the ground alone. Halcyon maps the flag
/// the same way: ground, or ground and water (LSLSystemAPI.cs llSetHoverHeight, llGroundRepel), where the engine measures
/// the height above the higher of the two (PhysxPrim.pid.cs GetHeightAbove). llGroundRepel adds the repel flag.
/// </summary>
// Calls the API directly on the harness prim; no clock and no process-wide state, so the class runs in parallel.
public class HoverWaterFlagTests
{
    private sealed class Recorder : NullPhysicsActor
    {
        public float Height;
        public PIDHoverType Type = (PIDHoverType)(-1);
        public float Tau;
        public bool Repel, Active;
        public override bool IsPhysical { get => true; set { } }
        public override Quaternion Orientation { get; set; } = Quaternion.Identity;
        public override float PIDHoverHeight { set => Height = value; }
        public override PIDHoverType PIDHoverType { set => Type = value; }
        public override float PIDHoverTau { set => Tau = value; }
        public override bool PIDHoverRepel { set => Repel = value; }
        public override bool PIDHoverActive { get => Active; set => Active = value; }
    }

    private static (SchedulerHarness, Recorder, LSLSystemAPI) Make()
    {
        var h = new SchedulerHarness();
        var actor = new Recorder();
        h.Prim.PhysActor = actor;
        h.Prim.AddFlag(PrimFlags.Physics);
        return (h, actor, new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random()));
    }

    [Theory]
    [InlineData(0, PIDHoverType.Ground)]
    [InlineData(1, PIDHoverType.GroundAndWater)]
    [InlineData(-1, PIDHoverType.GroundAndWater)]
    public void llSetHoverHeight_gives_the_actor_the_ground_or_the_higher_of_ground_and_water(int water, PIDHoverType expected)
    {
        var (h, actor, api) = Make();
        using (h)
        {
            api.llSetHoverHeight(3f, water, 1f);

            Assert.Equal(expected, actor.Type);
            Assert.Equal(3f, actor.Height);
            Assert.Equal(1f, actor.Tau);
            Assert.True(actor.Active);
            Assert.False(actor.Repel);
        }
    }

    [Theory]
    [InlineData(0, PIDHoverType.Ground)]
    [InlineData(1, PIDHoverType.GroundAndWater)]
    [InlineData(-1, PIDHoverType.GroundAndWater)]
    public void llGroundRepel_gives_the_actor_the_ground_or_the_higher_of_ground_and_water_and_the_repel_flag(int water, PIDHoverType expected)
    {
        var (h, actor, api) = Make();
        using (h)
        {
            api.llGroundRepel(3f, water, 1f);

            Assert.Equal(expected, actor.Type);
            Assert.Equal(3f, actor.Height);
            Assert.Equal(1f, actor.Tau);
            Assert.True(actor.Active);
            Assert.True(actor.Repel);
        }
    }
}
