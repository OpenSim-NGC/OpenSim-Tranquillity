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

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The constant force an attachment's llSetForce puts on its wearer ("Used on an attachment, it will apply the
    /// force to the avatar", SL wiki llSetForce). The presence holds it and hands it to its physics actor: on each
    /// change, and again to a new actor (sitting and standing, or a teleport in the region, replaces the actor).
    /// A zero force ends it. When the avatar leaves the region (becoming a child agent here) it ends here and goes
    /// with the avatar in its agent data, as Halcyon's ScenePresence.CopyTo and CopyFrom carry it.
    /// </summary>
    public class ScenePresenceConstantForceTests : OpenSimTestCase
    {
        /// <summary>A stand-in avatar actor that records every constant force it is given.</summary>
        private sealed class Recorder : NullPhysicsActor
        {
            public readonly List<(Vector3 Force, bool Local)> Given = new();
            public override void SetConstantForce(Vector3 force, bool local) { Given.Add((force, local)); }
        }

        private static void SetActor(ScenePresence sp, PhysicsActor pa)
            => typeof(ScenePresence).GetProperty("PhysicsActor").GetSetMethod(true).Invoke(sp, new object[] { pa });

        private static ScenePresence NewPresence()
        {
            TestScene scene = new SceneHelpers().SetupScene();
            return SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        }

        [Fact]
        public void TheForceReachesTheAvatarsActor()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            var actor = new Recorder();
            SetActor(sp, actor);

            sp.SetConstantForce(new Vector3(10, 0, 5), false);

            Assert.Equal(new[] { (new Vector3(10, 0, 5), false) }, actor.Given);
            Assert.Equal(new Vector3(10, 0, 5), sp.ConstantForce);
            Assert.False(sp.ConstantForceIsLocal);
        }

        [Fact]
        public void ALocalForceIsHandedOnUnturnedWithItsFrame()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            sp.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.5707964f);
            var actor = new Recorder();
            SetActor(sp, actor);

            sp.SetConstantForce(new Vector3(3, 0, 0), true);

            // The engine turns it by the avatar's orientation on every step, so the presence does not turn it once.
            Assert.Equal(new[] { (new Vector3(3, 0, 0), true) }, actor.Given);
            Assert.True(sp.ConstantForceIsLocal);
        }

        [Fact]
        public void ANewForceReplacesTheOldOne()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            var actor = new Recorder();
            SetActor(sp, actor);

            sp.SetConstantForce(new Vector3(1, 2, 3), false);
            sp.SetConstantForce(new Vector3(0, 4, 0), true);

            Assert.Equal((new Vector3(0, 4, 0), true), actor.Given[^1]);
            Assert.Equal(new Vector3(0, 4, 0), sp.ConstantForce);
            Assert.True(sp.ConstantForceIsLocal);
        }

        [Fact]
        public void AZeroForceEndsIt()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            var actor = new Recorder();
            SetActor(sp, actor);

            sp.SetConstantForce(new Vector3(1, 2, 3), true);
            sp.SetConstantForce(Vector3.Zero, false);

            Assert.Equal((Vector3.Zero, false), actor.Given[^1]);
            Assert.Equal(Vector3.Zero, sp.ConstantForce);

            // A later actor is given no force.
            var next = new Recorder();
            SetActor(sp, next);
            Assert.All(next.Given, g => Assert.Equal(Vector3.Zero, g.Force));
        }

        [Fact]
        public void ANewActorGetsTheHeldForce()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            sp.SetConstantForce(new Vector3(0, 0, 7), true);

            var next = new Recorder();
            SetActor(sp, next);

            Assert.Equal(new[] { (new Vector3(0, 0, 7), true) }, next.Given);
        }

        [Fact]
        public void SittingAndStandingKeepsTheForce()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            SceneObjectPart part = SceneHelpers.AddSceneObject(scene).RootPart;
            sp.AbsolutePosition = part.AbsolutePosition + new Vector3(0, 0, 1);
            sp.SetConstantForce(new Vector3(5, 0, 0), false);
            PhysicsActor before = sp.PhysicsActor;

            sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, part.UUID, Vector3.Zero);
            sp.StandUp();

            Assert.NotNull(sp.PhysicsActor);
            Assert.NotSame(before, sp.PhysicsActor);
            Assert.Equal(new Vector3(5, 0, 0), sp.ConstantForce);
        }

        [Fact]
        public void LeavingTheRegionEndsIt()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            sp.SetConstantForce(new Vector3(5, 0, 0), false);

            sp.MakeChildAgent(Utils.UIntsToLong(1000 * Constants.RegionSize, 1000 * Constants.RegionSize));

            Assert.Equal(Vector3.Zero, sp.ConstantForce);
            Assert.False(sp.ConstantForceIsLocal);
        }

        [Fact]
        public void TheForceGoesIntoTheAgentDataSentOnward()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            sp.SetConstantForce(new Vector3(3, 0, 4), true);

            AgentData data = new AgentData();
            sp.CopyTo(data, false);

            Assert.Equal(new Vector3(3, 0, 4), data.ConstantForce);
            Assert.True(data.ConstantForceIsLocal);
        }

        [Fact]
        public void AnArrivingAvatarTakesTheForceFromItsAgentData()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence source = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            ScenePresence arriving = SceneHelpers.AddChildScenePresence(scene, TestHelpers.ParseTail(0x2));
            source.SetConstantForce(new Vector3(0, 6, 0), true);
            AgentData data = new AgentData();
            source.CopyTo(data, false);

            arriving.UpdateChildAgent(data);

            Assert.Equal(new Vector3(0, 6, 0), arriving.ConstantForce);
            Assert.True(arriving.ConstantForceIsLocal);
            // The actor it gets on becoming a root agent is given the force.
            var actor = new Recorder();
            SetActor(arriving, actor);
            Assert.Equal(new[] { (new Vector3(0, 6, 0), true) }, actor.Given);
        }

        [Fact]
        public void AnAvatarArrivingWithNoForceInItsAgentDataHasNone()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence source = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            ScenePresence arriving = SceneHelpers.AddChildScenePresence(scene, TestHelpers.ParseTail(0x2));
            source.SetConstantForce(new Vector3(0, 6, 0), true);
            AgentData withForce = new AgentData();
            source.CopyTo(withForce, false);
            arriving.UpdateChildAgent(withForce);

            // Agent data from a simulator that does not send the field unpacks to no force.
            AgentData without = new AgentData();
            source.CopyTo(without, false);
            without.ConstantForce = Vector3.Zero;
            without.ConstantForceIsLocal = false;
            arriving.UpdateChildAgent(without);

            Assert.Equal(Vector3.Zero, arriving.ConstantForce);
            Assert.False(arriving.ConstantForceIsLocal);
        }

        [Fact]
        public void ANonFiniteForceIsIgnored()
        {
            TestHelpers.InMethod();
            ScenePresence sp = NewPresence();
            var actor = new Recorder();
            SetActor(sp, actor);
            sp.SetConstantForce(new Vector3(1, 0, 0), false);

            sp.SetConstantForce(new Vector3(float.NaN, 0, 0), false);
            sp.SetConstantForce(new Vector3(0, float.PositiveInfinity, 0), true);

            Assert.Single(actor.Given);
            Assert.Equal(new Vector3(1, 0, 0), sp.ConstantForce);
        }
    }
}
