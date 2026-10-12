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

using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A part delinked from a moving linkset gets a new physics actor. That actor starts with the velocity the part
    /// had in the linkset: the root's velocity plus the root's spin crossed with the part's offset from the root's
    /// centre of mass (from the root's position when the engine does not report one).
    /// </summary>
    public class SceneObjectDelinkVelocityTests : OpenSimTestCase
    {
        /// <summary>An actor that keeps what it is told, as an engine does between steps.</summary>
        private sealed class StandInActor : NullPhysicsActor
        {
            public bool Physical;
            public Vector3 Linear;
            public Vector3 Angular;
            public Vector3 MassCentre;

            public Vector3 Place;
            public Quaternion Turn = Quaternion.Identity;

            public override Vector3 Position { get => Place; set => Place = value; }
            public override Quaternion Orientation { get => Turn; set => Turn = value; }
            public override bool IsPhysical { get => Physical; set => Physical = value; }
            public override Vector3 Velocity { get => Linear; set => Linear = value; }
            public override Vector3 RotationalVelocity { get => Angular; set => Angular = value; }
            public override Vector3 CenterOfMass => MassCentre;
        }

        /// <summary>A scene that hands out a <see cref="StandInActor"/> for every prim.</summary>
        private sealed class StandInScene : PhysicsScene
        {
            public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
            {
                return new StandInActor { Physical = isPhysical, Place = position, Turn = rotation };
            }

            public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying) => null;
            public override void RemoveAvatar(PhysicsActor actor) { }
            public override void RemovePrim(PhysicsActor prim) { }
            public override float Simulate(float timeStep) => 0f;
            public override void SetTerrain(float[] heightMap) { }
            public override void SetWaterLevel(float baseheight) { }
            public override void DeleteTerrain() { }
            public override void Dispose() { }
            public override Dictionary<uint, float> GetTopColliders() => new();
        }

        private static readonly Vector3 GroupPosition = new(10f, 10f, 20f);
        private static readonly Vector3 ChildOffset = new(2f, 0f, 0f);

        /// <summary>
        /// A two-part linkset at (10, 10, 20) whose child sits 2 m along x from the root. When <paramref name="physical"/>
        /// the whole linkset is physical, with actors from the stand-in scene.
        /// </summary>
        private static SceneObjectGroup MakeLinkset(bool physical, out StandInActor rootActor)
        {
            TestScene scene = new SceneHelpers().SetupScene();
            scene.PhysicsScene = new StandInScene();

            SceneObjectGroup sog = SceneHelpers.AddSceneObject(scene);
            SceneObjectGroup other = SceneHelpers.AddSceneObject(scene);
            sog.AbsolutePosition = GroupPosition;
            other.AbsolutePosition = GroupPosition + ChildOffset;
            Assert.Equal(GroupPosition, sog.AbsolutePosition);
            Assert.Equal(GroupPosition + ChildOffset, other.AbsolutePosition);

            // Required for linking
            sog.RootPart.ClearUpdateSchedule();
            other.RootPart.ClearUpdateSchedule();
            sog.LinkToGroup(other);
            Assert.Equal(2, sog.Parts.Length);
            Assert.Equal(ChildOffset, sog.Parts.First(p => p != sog.RootPart).OffsetPosition);

            if (physical)
            {
                foreach (SceneObjectPart part in sog.Parts)
                    part.UpdatePrimFlags(true, false, false, false, false);
            }

            rootActor = (StandInActor)sog.RootPart.PhysActor;
            Assert.NotNull(rootActor);
            Assert.Equal(physical, rootActor.IsPhysical);
            return sog;
        }

        private static StandInActor Delink(SceneObjectGroup sog, out SceneObjectPart child)
        {
            child = sog.Parts.First(p => p != sog.RootPart);
            Assert.Equal(GroupPosition + ChildOffset, child.GetWorldPosition());

            SceneObjectGroup delinked = sog.DelinkFromGroup(child, true);
            Assert.NotNull(delinked);
            Assert.Same(child, delinked.RootPart);
            return (StandInActor)child.PhysActor;
        }

        [Fact]
        public void ADelinkedPartStartsWithTheVelocityOfTheMovingRoot()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(true, out StandInActor rootActor);
            rootActor.Linear = new Vector3(3f, -1f, 0.5f);

            StandInActor newActor = Delink(sog, out SceneObjectPart child);

            Assert.NotNull(newActor);
            Assert.True(newActor.IsPhysical);
            newActor.Linear.Should().Be(new Vector3(3f, -1f, 0.5f));
            newActor.Angular.Should().Be(Vector3.Zero);
            child.Velocity.Should().Be(new Vector3(3f, -1f, 0.5f));
        }

        [Fact]
        public void ADelinkedPartStartsWithTheSpinOfTheRootAboutItsCentreOfMass()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(true, out StandInActor rootActor);
            rootActor.Linear = new Vector3(1f, 2f, 0f);
            rootActor.Angular = new Vector3(0f, 0f, 2f);
            // The centre of mass is 2 m before the child, so the lever is (2, 0, 0) and spin x lever = (0, 4, 0).
            rootActor.MassCentre = new Vector3(10f, 10f, 20f);

            StandInActor newActor = Delink(sog, out _);

            newActor.Linear.Should().Be(new Vector3(1f, 6f, 0f));
            newActor.Angular.Should().Be(new Vector3(0f, 0f, 2f));
        }

        [Fact]
        public void TheLeverStartsAtTheCentreOfMassWhenTheEngineReportsOne()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(true, out StandInActor rootActor);
            rootActor.Angular = new Vector3(0f, 0f, 2f);
            // Centre of mass 1 m before the child along x and 1 m to the side: lever (1, -1, 0), spin x lever = (2, 2, 0).
            rootActor.MassCentre = new Vector3(11f, 11f, 20f);

            StandInActor newActor = Delink(sog, out _);

            newActor.Linear.Should().Be(new Vector3(2f, 2f, 0f));
        }

        [Fact]
        public void TheLeverStartsAtTheRootPositionWhenTheEngineReportsNoCentreOfMass()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(true, out StandInActor rootActor);
            rootActor.Angular = new Vector3(0f, 0f, 2f);
            rootActor.MassCentre = Vector3.Zero;

            StandInActor newActor = Delink(sog, out _);

            // The root is at (10, 10, 20), so the lever is the child's offset (2, 0, 0).
            newActor.Linear.Should().Be(new Vector3(0f, 4f, 0f));
            newActor.Angular.Should().Be(new Vector3(0f, 0f, 2f));
        }

        [Fact]
        public void ADelinkedPartFromALinksetAtRestStartsAtRest()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(true, out StandInActor rootActor);
            rootActor.MassCentre = GroupPosition;

            StandInActor newActor = Delink(sog, out _);

            Assert.NotNull(newActor);
            Assert.True(newActor.IsPhysical);
            newActor.Linear.Should().Be(Vector3.Zero);
            newActor.Angular.Should().Be(Vector3.Zero);
        }

        [Fact]
        public void ADelinkedPartFromANonPhysicalLinksetIsLeftAlone()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(false, out StandInActor rootActor);
            // A non-physical root keeps a stored velocity and spin that mean nothing to the part.
            sog.RootPart.Velocity = new Vector3(5f, 0f, 0f);
            sog.RootPart.AngularVelocity = new Vector3(0f, 0f, 3f);

            StandInActor newActor = Delink(sog, out SceneObjectPart child);

            Assert.NotNull(newActor);
            Assert.False(newActor.IsPhysical);
            child.Velocity.Should().Be(Vector3.Zero);
            child.AngularVelocity.Should().Be(Vector3.Zero);
            newActor.Linear.Should().Be(Vector3.Zero);
            newActor.Angular.Should().Be(Vector3.Zero);
        }

        [Fact]
        public void TheRootKeepsItsOwnVelocityWhenAPartIsDelinked()
        {
            TestHelpers.InMethod();

            SceneObjectGroup sog = MakeLinkset(true, out StandInActor rootActor);
            rootActor.Linear = new Vector3(3f, -1f, 0.5f);
            rootActor.Angular = new Vector3(0f, 0f, 2f);

            Delink(sog, out _);

            Assert.Same(rootActor, sog.RootPart.PhysActor);
            rootActor.Linear.Should().Be(new Vector3(3f, -1f, 0.5f));
            rootActor.Angular.Should().Be(new Vector3(0f, 0f, 2f));
            sog.RootPart.Velocity.Should().Be(new Vector3(3f, -1f, 0.5f));
        }
    }
}
