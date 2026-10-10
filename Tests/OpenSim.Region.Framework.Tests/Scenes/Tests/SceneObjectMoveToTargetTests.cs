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
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// SceneObjectGroup.MoveToTarget, which llMoveToTarget calls in both script engines. "Calling llMoveToTarget
    /// with a tau of 0.0 or less will silently fail, and do nothing." (SL wiki llMoveToTarget); stopping is
    /// llStopMoveToTarget's job.
    /// </summary>
    public class SceneObjectMoveToTargetTests : OpenSimTestCase
    {
        /// <summary>A stand-in prim actor that records what it is told about its move target.</summary>
        private sealed class Recorder : NullPhysicsActor
        {
            public Vector3 Target;
            public float Tau;
            public bool Active;
            public int ActiveSets;

            public override Vector3 PIDTarget { set { Target = value; } }
            public override float PIDTau { set { Tau = value; } }
            public override bool PIDActive
            {
                get { return Active; }
                set { Active = value; ActiveSets++; }
            }
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
        public void APositiveTauSetsTheTarget()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();

            so.MoveToTarget(new Vector3(10, 20, 30), 1.5f);

            Assert.True(actor.Active);
            Assert.Equal(new Vector3(10, 20, 30), actor.Target);
            Assert.Equal(1.5f, actor.Tau);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void ATauOfZeroOrLessLeavesAnEarlierTargetAlone(float tau)
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();
            so.MoveToTarget(new Vector3(10, 20, 30), 1.5f);
            int setsBefore = actor.ActiveSets;

            so.MoveToTarget(new Vector3(50, 60, 70), tau);

            Assert.True(actor.Active);
            Assert.Equal(setsBefore, actor.ActiveSets);
            Assert.Equal(new Vector3(10, 20, 30), actor.Target);
            Assert.Equal(1.5f, actor.Tau);
        }

        [Fact]
        public void ATauOfZeroWithNoTargetStartsNothing()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();

            so.MoveToTarget(new Vector3(50, 60, 70), 0f);

            Assert.False(actor.Active);
            Assert.Equal(0, actor.ActiveSets);
        }

        [Fact]
        public void StopMoveToTargetStillStops()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor) = NewObject();
            so.MoveToTarget(new Vector3(10, 20, 30), 1.5f);

            so.StopMoveToTarget();

            Assert.False(actor.Active);
        }
    }
}
