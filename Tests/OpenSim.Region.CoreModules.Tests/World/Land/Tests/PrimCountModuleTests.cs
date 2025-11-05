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

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.Land.Tests
{
    public class PrimCountModuleTests : OpenSimTestCase
    {
        protected UUID m_userId = new UUID("00000000-0000-0000-0000-100000000000");
        protected UUID m_groupId = new UUID("00000000-0000-0000-8888-000000000000");
        protected UUID m_otherUserId = new UUID("99999999-9999-9999-9999-999999999999");
        protected TestScene m_scene;
        protected PrimCountModule m_pcm;

        /// <summary>
        /// A parcel that covers the entire sim except for a 1 unit wide strip on the eastern side.
        /// </summary>
        protected ILandObject m_lo;

        /// <summary>
        /// A parcel that covers just the eastern strip of the sim.
        /// </summary>
        protected ILandObject m_lo2;

        public override void SetUp()
        {
            base.SetUp();

            m_pcm = new PrimCountModule();
            LandManagementModule lmm = new LandManagementModule();
            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, lmm, m_pcm);

            int xParcelDivider = (int)Constants.RegionSize - 1;

            ILandObject lo = new LandObject(m_userId, false, m_scene);
            lo.LandData.Name = "m_lo";
            lo.SetLandBitmap(
                lo.GetSquareLandBitmap(0, 0, xParcelDivider, (int)Constants.RegionSize));
            m_lo = lmm.AddLandObject(lo);

            ILandObject lo2 = new LandObject(m_userId, false, m_scene);
            lo2.SetLandBitmap(
                lo2.GetSquareLandBitmap(xParcelDivider, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            lo2.LandData.Name = "m_lo2";
            m_lo2 = lmm.AddLandObject(lo2);
        }

        /// <summary>
        /// Test that counts before we do anything are correct.
        /// </summary>
        [Fact]
        public void TestInitialCounts()
        {
            IPrimCounts pc = m_lo.PrimCounts;

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        /// <summary>
        /// Test count after a parcel owner owned object is added.
        /// </summary>
        [Fact]
        public void TestAddOwnerObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            IPrimCounts pc = m_lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, m_userId, "a", 0x01);
            m_scene.AddNewSceneObject(sog, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion

            // Add a second object and retest
            SceneObjectGroup sog2 = SceneHelpers.CreateSceneObject(2, m_userId, "b", 0x10);
            m_scene.AddNewSceneObject(sog2, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        /// <summary>
        /// Test count after a parcel owner owned copied object is added.
        /// </summary>
        [Fact]
        public void TestCopyOwnerObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            IPrimCounts pc = m_lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, m_userId, "a", 0x01);
            m_scene.AddNewSceneObject(sog, false);
            m_scene.SceneGraph.DuplicateObject(sog.LocalId, Vector3.Zero, m_userId, UUID.Zero, Quaternion.Identity, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        /// <summary>
        /// Test that parcel counts update correctly when an object is moved between parcels, where that movement
        /// is not done directly by the user/
        /// </summary>
        [Fact]
        public void TestMoveOwnerObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, m_userId, "a", 0x01);
            m_scene.AddNewSceneObject(sog, false);
            SceneObjectGroup sog2 = SceneHelpers.CreateSceneObject(2, m_userId, "b", 0x10);
            m_scene.AddNewSceneObject(sog2, false);

            // Move the first scene object to the eastern strip parcel
            sog.AbsolutePosition = new Vector3(254, 2, 2);

            IPrimCounts pclo1 = m_lo.PrimCounts;

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion

            IPrimCounts pclo2 = m_lo2.PrimCounts;

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion

            // Now move it back again
            sog.AbsolutePosition = new Vector3(2, 2, 2);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        /// <summary>
        /// Test count after a parcel owner owned object is removed.
        /// </summary>
        [Fact]
        public void TestRemoveOwnerObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            IPrimCounts pc = m_lo.PrimCounts;

            m_scene.AddNewSceneObject(SceneHelpers.CreateSceneObject(1, m_userId, "a", 0x1), false);
            SceneObjectGroup sogToDelete = SceneHelpers.CreateSceneObject(3, m_userId, "b", 0x10);
            m_scene.AddNewSceneObject(sogToDelete, false);
            m_scene.DeleteSceneObject(sogToDelete, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        [Fact]
        public void TestAddGroupObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            m_lo.DeedToGroup(m_groupId);

            IPrimCounts pc = m_lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, m_otherUserId, "a", 0x01);
            sog.GroupID = m_groupId;
            m_scene.AddNewSceneObject(sog, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion

            // Is this desired behaviour?  Not totally sure.
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion

            // TODO: Assert.Equal(,); - incomplete assertion
        }

        /// <summary>
        /// Test count after a parcel owner owned object is removed.
        /// </summary>
        [Fact]
        public void TestRemoveGroupObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            m_lo.DeedToGroup(m_groupId);

            IPrimCounts pc = m_lo.PrimCounts;

            SceneObjectGroup sogToKeep = SceneHelpers.CreateSceneObject(1, m_userId, "a", 0x1);
            sogToKeep.GroupID = m_groupId;
            m_scene.AddNewSceneObject(sogToKeep, false);

            SceneObjectGroup sogToDelete = SceneHelpers.CreateSceneObject(3, m_userId, "b", 0x10);
            m_scene.AddNewSceneObject(sogToDelete, false);
            m_scene.DeleteSceneObject(sogToDelete, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        [Fact]
        public void TestAddOthersObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            IPrimCounts pc = m_lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, m_otherUserId, "a", 0x01);
            m_scene.AddNewSceneObject(sog, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        [Fact]
        public void TestRemoveOthersObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            IPrimCounts pc = m_lo.PrimCounts;

            m_scene.AddNewSceneObject(SceneHelpers.CreateSceneObject(1, m_otherUserId, "a", 0x1), false);
            SceneObjectGroup sogToDelete = SceneHelpers.CreateSceneObject(3, m_otherUserId, "b", 0x10);
            m_scene.AddNewSceneObject(sogToDelete, false);
            m_scene.DeleteSceneObject(sogToDelete, false);

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        /// <summary>
        /// Test the count is correct after is has been tainted.
        /// </summary>
        [Fact]
        public void TestTaint()
        {
            TestHelpers.InMethod();
            IPrimCounts pc = m_lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, m_userId, "a", 0x01);
            m_scene.AddNewSceneObject(sog, false);

            m_pcm.TaintPrimCount();

            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }
    }
}