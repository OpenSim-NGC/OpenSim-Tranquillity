/*
 * Copyright (c) Contributors, http://opensimulator.org/
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

using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    public class ScenePresenceSitTests : OpenSimTestCase
    {
        private TestScene m_scene;
        private ScenePresence m_sp;

        public ScenePresenceSitTests()
        {
            m_scene = new SceneHelpers().SetupScene();
            m_sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
        }

        [Fact]
        public void TestSitOutsideRangeNoTarget()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // More than 10 meters away from 0, 0, 0 (default part position)
            Vector3 startPos = new Vector3(10.1f, 0, 0);
            m_sp.AbsolutePosition = startPos;

            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene).RootPart;

            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, part.UUID, Vector3.Zero);

            Assert.Equal(UUID.Zero, part.SitTargetAvatar);
            Assert.Equal(0, part.GetSittingAvatarsCount());
            Assert.Null(part.GetSittingAvatars());
            Assert.Equal(0u, m_sp.ParentID);
            Assert.Equal(startPos, m_sp.AbsolutePosition);
        }

        [Fact]
        public void TestSitWithinRangeNoTarget()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Less than 10 meters away from 0, 0, 0 (default part position)
            Vector3 startPos = new Vector3(9.9f, 0, 0);
            m_sp.AbsolutePosition = startPos;

            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene).RootPart;

            // We need to preserve this here because phys actor is removed by the sit.
            Vector3 spPhysActorSize = m_sp.PhysicsActor.Size;
            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, part.UUID, Vector3.Zero);

            Assert.Null(m_sp.PhysicsActor);

            Assert.Equal(part.AbsolutePosition + new Vector3(0, 0, spPhysActorSize.Z / 2), m_sp.AbsolutePosition);

            Assert.Equal(UUID.Zero, part.SitTargetAvatar);
            Assert.Equal(1, part.GetSittingAvatarsCount());
            HashSet<ScenePresence> sittingAvatars = part.GetSittingAvatars();
            Assert.Equal(1, sittingAvatars.Count);
            Assert.True(sittingAvatars.Contains(m_sp));
            Assert.Equal(part.LocalId, m_sp.ParentID);
        }

        [Fact]
        public void TestSitAndStandWithNoSitTarget()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Make sure we're within range to sit
            Vector3 startPos = new Vector3(1, 1, 1);
            m_sp.AbsolutePosition = startPos;

            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene).RootPart;

            // We need to preserve this here because phys actor is removed by the sit.
            Vector3 spPhysActorSize = m_sp.PhysicsActor.Size;
            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, part.UUID, Vector3.Zero);

            Assert.Equal(part.AbsolutePosition + new Vector3(0, 0, spPhysActorSize.Z / 2), m_sp.AbsolutePosition);

            m_sp.StandUp();

            Assert.Equal(UUID.Zero, part.SitTargetAvatar);
            Assert.Equal(0, part.GetSittingAvatarsCount());
            Assert.Null(part.GetSittingAvatars());
            Assert.Equal(0u, m_sp.ParentID);
            Assert.NotNull(m_sp.PhysicsActor);
        }

        [Fact]
        public void TestSitAndStandWithNoSitTargetChildPrim()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Make sure we're within range to sit
            Vector3 startPos = new Vector3(1, 1, 1);
            m_sp.AbsolutePosition = startPos;

            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 2, m_sp.UUID, "part", 0x10).Parts[1];
            part.OffsetPosition = new Vector3(2, 3, 4);

            // We need to preserve this here because phys actor is removed by the sit.
            Vector3 spPhysActorSize = m_sp.PhysicsActor.Size;
            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, part.UUID, Vector3.Zero);

            Assert.Equal(part.AbsolutePosition + new Vector3(0, 0, spPhysActorSize.Z / 2), m_sp.AbsolutePosition);

            m_sp.StandUp();

            Assert.Equal(UUID.Zero, part.SitTargetAvatar);
            Assert.Equal(0, part.GetSittingAvatarsCount());
            Assert.Null(part.GetSittingAvatars());
            Assert.Equal(0u, m_sp.ParentID);
            Assert.NotNull(m_sp.PhysicsActor);
        }

        [Fact]
        public void TestSitAndStandWithSitTarget()
        {
/*  sit position math as changed, this needs to be fixed later
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // If a prim has a sit target then we can sit from any distance away
            Vector3 startPos = new Vector3(128, 128, 30);
            m_sp.AbsolutePosition = startPos;

            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene).RootPart;
            part.SitTargetPosition = new Vector3(0, 0, 1);

            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, part.UUID, Vector3.Zero);

            Assert.Equal(m_sp.UUID, part.SitTargetAvatar);
            Assert.Equal(part.LocalId, m_sp.ParentID);

            // This section is copied from ScenePresence.HandleAgentSit().  Correctness is not guaranteed.
            double x, y, z, m1, m2;

            Quaternion r = part.SitTargetOrientation;;
            m1 = r.X * r.X + r.Y * r.Y;
            m2 = r.Z * r.Z + r.W * r.W;

            // Rotate the vector <0, 0, 1>
            x = 2 * (r.X * r.Z + r.Y * r.W);
            y = 2 * (-r.X * r.W + r.Y * r.Z);
            z = m2 - m1;

            // Set m to be the square of the norm of r.
            double m = m1 + m2;

            // This constant is emperically determined to be what is used in SL.
            // See also http://opensimulator.org/mantis/view.php?id=7096
            double offset = 0.05;

            Vector3 up = new Vector3((float)x, (float)y, (float)z);
            Vector3 sitOffset = up * (float)offset;
            // End of copied section.

            Assert.Equal(part.AbsolutePosition + part.SitTargetPosition - sitOffset + ScenePresence.SIT_TARGET_ADJUSTMENT, m_sp.AbsolutePosition);
            Assert.Null(m_sp.PhysicsActor);

            Assert.Equal(1, part.GetSittingAvatarsCount());
            HashSet<ScenePresence> sittingAvatars = part.GetSittingAvatars();
            Assert.Equal(1, sittingAvatars.Count);
            Assert.True(sittingAvatars.Contains(m_sp));

            m_sp.StandUp();

            Assert.Equal(UUID.Zero, part.SitTargetAvatar);
            Assert.Equal(0u, m_sp.ParentID);
            Assert.NotNull(m_sp.PhysicsActor);

            Assert.Equal(UUID.Zero, part.SitTargetAvatar);
            Assert.Equal(0, part.GetSittingAvatarsCount());
            Assert.Null(part.GetSittingAvatars());
*/
        }

        [Fact]
        public void TestSitAndStandOnGround()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // If a prim has a sit target then we can sit from any distance away
//            Vector3 startPos = new Vector3(128, 128, 30);
//            sp.AbsolutePosition = startPos;

            m_sp.HandleAgentSitOnGround();

            Assert.True(m_sp.SitGround);
            Assert.Null(m_sp.PhysicsActor);

            m_sp.StandUp();

            Assert.False(m_sp.SitGround);
            Assert.NotNull(m_sp.PhysicsActor);
        }
    }
}
