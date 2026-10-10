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

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// When a physical object's actor reports it out of bounds (ubODE below -100 m or above 100000 m, BulletS after
    /// too many crossing failures), core makes the whole object non-physical, the same way as
    /// llSetStatus(STATUS_PHYSICS, FALSE): every part loses its Physics flag, the region's physical-prim count drops,
    /// and the parts stop listening to their actors' terse-update and out-of-bounds events.
    /// </summary>
    public class SceneObjectOutOfBoundsTests : OpenSimTestCase
    {
        /// <summary>A stand-in prim actor that remembers whether it was made physical, as an engine's actor does.</summary>
        private sealed class Body : NullPhysicsActor
        {
            public override bool IsPhysical { get; set; }
        }

        private static Delegate Handlers(PhysicsActor pa, string eventName)
            => (Delegate)typeof(PhysicsActor).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pa);

        private static (TestScene, SceneObjectGroup) NewPhysicalObject(int parts)
        {
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, parts, TestHelpers.ParseTail(0x1), "part", 0x10);
            foreach (SceneObjectPart part in so.Parts)
                part.PhysActor = new Body();
            so.ScriptSetPhysicsStatus(true);
            return (scene, so);
        }

        [Fact]
        public void EveryPartOfALinksetBecomesNonPhysical()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so) = NewPhysicalObject(3);
            Assert.All(so.Parts, p => Assert.True((p.Flags & PrimFlags.Physics) != 0));
            Assert.All(so.Parts, p => Assert.True(p.PhysActor.IsPhysical));

            so.RootPart.PhysActor.RaiseOutOfBounds(new Vector3(128, 128, -150));

            Assert.False(so.UsesPhysics);
            Assert.All(so.Parts, p => Assert.Equal((PrimFlags)0, p.Flags & PrimFlags.Physics));
            Assert.All(so.Parts, p => Assert.False(p.PhysActor.IsPhysical));
        }

        [Fact]
        public void ThePhysicalPrimCountDrops()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so) = NewPhysicalObject(1);
            int physical = scene.SceneGraph.GetActiveObjectsCount();

            so.RootPart.PhysActor.RaiseOutOfBounds(new Vector3(128, 128, -150));

            Assert.Equal(physical - 1, scene.SceneGraph.GetActiveObjectsCount());
        }

        [Fact]
        public void ThePartStopsListeningToItsActor()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so) = NewPhysicalObject(1);
            PhysicsActor pa = so.RootPart.PhysActor;
            Assert.NotNull(Handlers(pa, "OnOutOfBounds"));

            pa.RaiseOutOfBounds(new Vector3(128, 128, -150));

            Assert.Null(Handlers(pa, "OnOutOfBounds"));
            Assert.Null(Handlers(pa, "OnRequestTerseUpdate"));
        }

        [Fact]
        public void ItCanBeMadePhysicalAgain()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so) = NewPhysicalObject(2);
            int physical = scene.SceneGraph.GetActiveObjectsCount();
            so.RootPart.PhysActor.RaiseOutOfBounds(new Vector3(128, 128, -150));

            so.ScriptSetPhysicsStatus(true);

            Assert.True(so.UsesPhysics);
            Assert.All(so.Parts, p => Assert.True(p.PhysActor.IsPhysical));
            Assert.Equal(physical, scene.SceneGraph.GetActiveObjectsCount());
        }
    }
}
