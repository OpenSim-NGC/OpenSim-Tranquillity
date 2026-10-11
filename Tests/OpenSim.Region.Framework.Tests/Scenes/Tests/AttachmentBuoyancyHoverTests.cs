/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// llSetBuoyancy, llSetHoverHeight and llStopHover called from a worn attachment act on the wearer's physics
    /// actor: the attachment's own prims have no actor while worn. The SL wiki's llSetHoverHeight and llStopHover
    /// example scripts say "Put in an attached prim and touch to start floating in air without flying". The pages
    /// for llSetBuoyancy and llGroundRepel say nothing about attachments. The wearer's presence holds the values,
    /// hands them to every new actor, and ends them when the attachment that set them is removed. An object that
    /// is not worn keeps acting on its own actor.
    /// </summary>
    public class AttachmentBuoyancyHoverTests : OpenSimTestCase
    {
        /// <summary>A stand-in actor that remembers the buoyancy and hover it is given.</summary>
        private sealed class Recorder : NullPhysicsActor
        {
            public float BuoyancyValue;
            public bool HoverActive;
            public float HoverHeight;
            public PIDHoverType HoverType;
            public float HoverTau;

            public override float Buoyancy { get => BuoyancyValue; set => BuoyancyValue = value; }
            public override bool PIDHoverActive { get => HoverActive; set => HoverActive = value; }
            public override float PIDHoverHeight { set => HoverHeight = value; }
            public override PIDHoverType PIDHoverType { set => HoverType = value; }
            public override float PIDHoverTau { set => HoverTau = value; }
        }

        private static void SetActor(ScenePresence sp, PhysicsActor pa)
            => typeof(ScenePresence).GetProperty("PhysicsActor").GetSetMethod(true).Invoke(sp, new object[] { pa });

        private static (TestScene scene, ScenePresence wearer, Recorder actor) NewWearer()
        {
            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            var actor = new Recorder();
            SetActor(sp, actor);
            return (scene, sp, actor);
        }

        private static SceneObjectGroup Wear(TestScene scene, ScenePresence wearer, string name)
        {
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, name, wearer.UUID);
            so.AttachedAvatar = wearer.UUID;
            so.IsAttachment = true;
            wearer.AddAttachment(so);
            return so;
        }

        [Fact]
        public void BuoyancyFromAnAttachmentReachesTheWearer()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");

            so.RootPart.SetBuoyancy(0.5f);

            Assert.Equal(0.5f, actor.BuoyancyValue);
        }

        [Fact]
        public void BuoyancyFromAChildPrimOfAnAttachmentReachesTheWearer()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            SceneObjectPart child = SceneHelpers.CreateSceneObjectPart("Child", UUID.Random(), wearer.UUID);
            so.AddPart(child);

            child.SetBuoyancy(0.25f);

            Assert.Equal(0.25f, actor.BuoyancyValue);
        }

        [Fact]
        public void BuoyancyThroughTheGroupReachesTheWearer()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");

            so.SetBuoyancy(-0.5f);

            Assert.Equal(-0.5f, actor.BuoyancyValue);
        }

        [Fact]
        public void HoverFromAnAttachmentReachesTheWearer()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");

            so.RootPart.SetHoverHeight(3.5f, PIDHoverType.GroundAndWater, 0.75f);

            Assert.True(actor.HoverActive);
            Assert.Equal(3.5f, actor.HoverHeight);
            Assert.Equal(PIDHoverType.GroundAndWater, actor.HoverType);
            Assert.Equal(0.75f, actor.HoverTau);
        }

        [Fact]
        public void StopHoverFromAnAttachmentEndsTheWearersHover()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            so.RootPart.SetHoverHeight(3.5f, PIDHoverType.Ground, 1f);

            so.RootPart.StopHover();

            Assert.False(actor.HoverActive);
        }

        [Fact]
        public void AnObjectThatIsNotWornActsOnItsOwnActor()
        {
            TestHelpers.InMethod();
            var (scene, wearer, wearerActor) = NewWearer();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, "Loose", wearer.UUID);
            var own = new Recorder();
            so.RootPart.PhysActor = own;

            so.RootPart.SetBuoyancy(0.5f);
            so.RootPart.SetHoverHeight(2f, PIDHoverType.Ground, 0.5f);

            Assert.Equal(0.5f, own.BuoyancyValue);
            Assert.True(own.HoverActive);
            Assert.Equal(2f, own.HoverHeight);
            Assert.Equal(PIDHoverType.Ground, own.HoverType);
            Assert.Equal(0.5f, own.HoverTau);
            Assert.Equal(0f, wearerActor.BuoyancyValue);
            Assert.False(wearerActor.HoverActive);

            so.RootPart.StopHover();
            Assert.False(own.HoverActive);
        }

        [Fact]
        public void DetachingTheAttachmentThatSetThemRestoresTheWearer()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            so.RootPart.SetBuoyancy(0.5f);
            so.RootPart.SetHoverHeight(3f, PIDHoverType.Ground, 1f);

            so.DetachToInventoryPrep();

            Assert.Equal(0f, actor.BuoyancyValue);
            Assert.False(actor.HoverActive);
            // A new actor made later starts with neither.
            var next = new Recorder();
            SetActor(wearer, next);
            Assert.Equal(0f, next.BuoyancyValue);
            Assert.False(next.HoverActive);
        }

        [Fact]
        public void DetachingAnotherAttachmentLeavesThemAlone()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup setter = Wear(scene, wearer, "Setter");
            SceneObjectGroup other = Wear(scene, wearer, "Other");
            setter.RootPart.SetBuoyancy(0.5f);
            setter.RootPart.SetHoverHeight(3f, PIDHoverType.Ground, 1f);

            other.DetachToInventoryPrep();

            Assert.Equal(0.5f, actor.BuoyancyValue);
            Assert.True(actor.HoverActive);
        }

        [Fact]
        public void RemovingAllAttachmentsRestoresTheWearer()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            so.RootPart.SetBuoyancy(0.5f);
            so.RootPart.SetHoverHeight(3f, PIDHoverType.Ground, 1f);

            wearer.ClearAttachments();

            Assert.Equal(0f, actor.BuoyancyValue);
            Assert.False(actor.HoverActive);
        }

        [Fact]
        public void ANewActorGetsTheHeldBuoyancyAndHover()
        {
            TestHelpers.InMethod();
            var (scene, wearer, _) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            so.RootPart.SetBuoyancy(0.5f);
            so.RootPart.SetHoverHeight(3f, PIDHoverType.GroundAndWater, 2f);

            var next = new Recorder();
            SetActor(wearer, next);

            Assert.Equal(0.5f, next.BuoyancyValue);
            Assert.True(next.HoverActive);
            Assert.Equal(3f, next.HoverHeight);
            Assert.Equal(PIDHoverType.GroundAndWater, next.HoverType);
            Assert.Equal(2f, next.HoverTau);
        }

        [Fact]
        public void LeavingTheRegionEndsThem()
        {
            TestHelpers.InMethod();
            // The presence keeps its own actor here, which the scene can take back out when it becomes a child.
            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence wearer = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            so.RootPart.SetBuoyancy(0.5f);
            so.RootPart.SetHoverHeight(3f, PIDHoverType.Ground, 1f);

            wearer.MakeChildAgent(Utils.UIntsToLong(1000 * Constants.RegionSize, 1000 * Constants.RegionSize));
            var next = new Recorder();
            SetActor(wearer, next);

            Assert.Equal(0f, next.BuoyancyValue);
            Assert.False(next.HoverActive);
        }

        [Fact]
        public void ANonFiniteBuoyancyIsIgnored()
        {
            TestHelpers.InMethod();
            var (scene, wearer, actor) = NewWearer();
            SceneObjectGroup so = Wear(scene, wearer, "Worn");
            so.RootPart.SetBuoyancy(0.5f);

            so.RootPart.SetBuoyancy(float.NaN);

            Assert.Equal(0.5f, actor.BuoyancyValue);
        }
    }
}
