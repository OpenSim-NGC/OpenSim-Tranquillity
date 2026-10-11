/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// ScenePresence.GetMass is what llGetMass returns for a script in an attachment. An avatar has no
    /// physics actor for a moment during a teleport inside the region and while standing up from a seat;
    /// the mass must not read as zero then.
    /// </summary>
    public class ScenePresenceMassTests : OpenSimTestCase
    {
        private const float ActorMass = 80f;

        private sealed class MassActor : BasicActor
        {
            public MassActor(Vector3 size) : base(size) { }
            public override float Mass => ActorMass;
        }

        private sealed class MassScene : BasicScene
        {
            public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
                => new MassActor(size) { Position = position, Velocity = velocity, Flying = isFlying };

            public override void RemoveAvatar(PhysicsActor actor) { }
        }

        private static ScenePresence PresenceWithMassiveActor()
        {
            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            scene.PhysicsScene = new MassScene();
            return SceneHelpers.AddScenePresence(scene, UUID.Random());
        }

        [Fact]
        public void MassIsTheActorsWhileThereIsAnActor()
        {
            TestHelpers.InMethod();

            ScenePresence sp = PresenceWithMassiveActor();

            sp.PhysicsActor.Should().NotBeNull();
            sp.GetMass().Should().Be(ActorMass);
        }

        [Fact]
        public void MassIsTheLastKnownOneWhileThereIsNoActor()
        {
            TestHelpers.InMethod();

            ScenePresence sp = PresenceWithMassiveActor();
            sp.GetMass().Should().Be(ActorMass);

            sp.RemoveFromPhysicalScene();

            sp.PhysicsActor.Should().BeNull();
            sp.GetMass().Should().Be(ActorMass);
        }

        [Fact]
        public void MassIsTheLastKnownOneEvenIfItWasNeverReadBeforeTheActorWasRemoved()
        {
            TestHelpers.InMethod();

            ScenePresence sp = PresenceWithMassiveActor();

            // No GetMass call before the removal: the mass has to be taken from the actor as it goes.
            sp.RemoveFromPhysicalScene();
            sp.GetMass().Should().Be(ActorMass);
        }

        [Fact]
        public void MassIsStillZeroForAPresenceThatNeverHadAnActor()
        {
            TestHelpers.InMethod();

            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, UUID.Random());
            sp.RemoveFromPhysicalScene();
            sp.PhysicsActor.Should().BeNull();

            sp.GetMass().Should().Be(0f);
        }
    }
}
