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
    /// Keyframed motion (llSetKeyframedMotion) tells the root prim's physics actor while it is moving the object
    /// (PhysicsActor.MovedByKeyframedMotion): set when the motion starts, cleared when it is stopped, paused,
    /// selected or has played out. An engine can then tell the motion's steps from any other move.
    /// </summary>
    public class KeyframeMotionPhysicsHintTests : OpenSimTestCase
    {
        /// <summary>A stand-in prim actor that records the hint.</summary>
        private sealed class Recorder : NullPhysicsActor
        {
            private volatile bool m_hint;
            public int Sets;

            public override bool MovedByKeyframedMotion
            {
                get { return m_hint; }
                set { m_hint = value; Sets++; }
            }
        }

        private static (SceneObjectGroup, Recorder, KeyframeMotion) NewMotion(int frameMs)
        {
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            var actor = new Recorder();
            so.RootPart.PhysActor = actor;

            var motion = new KeyframeMotion(so, KeyframeMotion.PlayMode.Forward,
                KeyframeMotion.DataFormat.Translation | KeyframeMotion.DataFormat.Rotation);
            motion.SetKeyframes(new[]
            {
                new KeyframeMotion.Keyframe { Position = new Vector3(2, 0, 0), Rotation = Quaternion.Identity, TimeMS = frameMs },
            });
            so.RootPart.KeyframeMotion = motion;
            return (so, actor, motion);
        }

        [Fact]
        public void TheHintIsOffUntilTheMotionStartsAndOnWhileItRuns()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor, KeyframeMotion motion) = NewMotion(60000);
            Assert.False(actor.MovedByKeyframedMotion);

            motion.Start();
            try
            {
                Assert.True(actor.MovedByKeyframedMotion);
            }
            finally
            {
                motion.Stop();
            }
        }

        [Fact]
        public void StopClearsTheHint()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor, KeyframeMotion motion) = NewMotion(60000);
            motion.Start();

            motion.Stop();

            Assert.False(actor.MovedByKeyframedMotion);
        }

        [Fact]
        public void PauseClearsTheHintAndStartSetsItAgain()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor, KeyframeMotion motion) = NewMotion(60000);
            motion.Start();

            motion.Pause();
            Assert.False(actor.MovedByKeyframedMotion);

            motion.Start();
            try
            {
                Assert.True(actor.MovedByKeyframedMotion);
            }
            finally
            {
                motion.Stop();
            }
        }

        [Fact]
        public void SelectingTheObjectClearsTheHintAndLettingGoRestoresIt()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor, KeyframeMotion motion) = NewMotion(60000);
            motion.Start();
            try
            {
                motion.Selected = true;
                Assert.False(actor.MovedByKeyframedMotion);

                motion.Selected = false;
                Assert.True(actor.MovedByKeyframedMotion);
            }
            finally
            {
                motion.Stop();
            }
        }

        [Fact]
        public void TheHintComesOffWhenTheMotionHasPlayedOut()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor, KeyframeMotion motion) = NewMotion(50);   // one timer tick long
            motion.Start();
            Assert.True(actor.MovedByKeyframedMotion);

            // The motion's timer is not running in a test scene (it starts once every region is ready), so the ticks are
            // given by hand: the first plays the one frame, the second finds nothing left.
            motion.OnTimer(50.0);
            Assert.True(actor.MovedByKeyframedMotion);
            motion.OnTimer(50.0);

            Assert.False(actor.MovedByKeyframedMotion);
            motion.Stop();
        }

        [Fact]
        public void UpdatePhysicsHintGivesARunningMotionsHintToAnActorMadeLater()
        {
            TestHelpers.InMethod();
            (SceneObjectGroup so, Recorder actor, KeyframeMotion motion) = NewMotion(60000);
            motion.Start();
            try
            {
                var later = new Recorder();
                so.RootPart.PhysActor = later;
                Assert.False(later.MovedByKeyframedMotion);

                motion.UpdatePhysicsHint();

                Assert.True(later.MovedByKeyframedMotion);
            }
            finally
            {
                motion.Stop();
            }
        }

        [Fact]
        public void AnEngineThatIgnoresTheHintIsUnaffected()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.PhysActor = new NullPhysicsActor();

            var motion = new KeyframeMotion(so, KeyframeMotion.PlayMode.Forward, KeyframeMotion.DataFormat.Translation);
            motion.SetKeyframes(new[] { new KeyframeMotion.Keyframe { Position = new Vector3(2, 0, 0), TimeMS = 60000 } });
            so.RootPart.KeyframeMotion = motion;
            motion.Start();
            motion.Stop();

            Assert.False(so.RootPart.PhysActor.MovedByKeyframedMotion);
        }
    }
}
