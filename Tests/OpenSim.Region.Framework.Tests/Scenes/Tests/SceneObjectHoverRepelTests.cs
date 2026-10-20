/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// llGroundRepel is llSetHoverHeight plus a repel flag (Halcyon's PIDHoverFlag.Repel): SceneObjectGroup.SetHoverHeight hands
    /// the root actor <see cref="PhysicsActor.PIDHoverRepel"/> before it switches the hover on, a plain hover clears it, and a
    /// script's repel is put back when a drag ends.
    /// </summary>
    // Each test builds its own scene and stand-in physics actor and touches no process-wide state, so the class runs in parallel.
    public class SceneObjectHoverRepelTests : OpenSimTestCase
    {
        /// <summary>A stand-in prim actor that records the hover members in the order core sets them.</summary>
        private sealed class Recorder : NullPhysicsActor
        {
            public readonly List<string> Calls = new();
            public bool Repel;
            private bool m_hoverActive, m_pidActive;

            public override float PIDHoverHeight { set { Calls.Add("height"); } }
            public override PIDHoverType PIDHoverType { set { Calls.Add("type"); } }
            public override float PIDHoverTau { set { Calls.Add("tau"); } }
            public override bool PIDHoverRepel { set { Repel = value; Calls.Add("repel=" + value); } }
            public override bool PIDHoverActive { get => m_hoverActive; set { m_hoverActive = value; Calls.Add("active=" + value); } }
            public override Vector3 PIDTarget { set { } }
            public override float PIDTau { set { } }
            public override bool PIDActive { get => m_pidActive; set { m_pidActive = value; } }
        }

        private static (SceneObjectGroup, Recorder) NewObject()
        {
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            var actor = new Recorder();
            so.RootPart.PhysActor = actor;
            return (so, actor);
        }

        [Fact]
        public void ARepelHandsTheActorTheFlagBeforeItSwitchesTheHoverOn()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();

            so.SetHoverHeight(3f, PIDHoverType.Ground, 1f, repel: true);

            Assert.True(actor.Repel);
            Assert.Equal(new[] { "height", "type", "tau", "repel=True", "active=True" }, actor.Calls);
        }

        [Fact]
        public void APlainHoverClearsTheFlagAScriptSetBefore()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();
            so.SetHoverHeight(3f, PIDHoverType.Ground, 1f, repel: true);

            so.SetHoverHeight(3f, PIDHoverType.Ground, 1f);

            Assert.False(actor.Repel);
        }

        [Fact]
        public void APartsHoverAndRepelReachTheSameRootActor()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();

            so.RootPart.SetHoverHeight(2f, PIDHoverType.GroundAndWater, 0.5f, repel: true);

            Assert.True(actor.Repel);
            so.RootPart.StopHover();
            Assert.Equal("active=False", actor.Calls[^1]);
        }
    }
}
