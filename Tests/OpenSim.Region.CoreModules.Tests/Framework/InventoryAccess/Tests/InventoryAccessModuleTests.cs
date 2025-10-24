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
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Framework.InventoryAccess.Tests
{
    public class InventoryAccessModuleTests : OpenSimTestCase
    {
        protected TestScene m_scene;
        protected BasicInventoryAccessModule m_iam;
        protected UUID m_userId = UUID.Parse("00000000-0000-0000-0000-000000000020");
        protected TestClient m_tc;

        public override void SetUp()
        {
            base.SetUp();

            m_iam = new BasicInventoryAccessModule();

            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            config.Configs["Modules"].Set("InventoryAccessModule", "BasicInventoryAccessModule");

            SceneHelpers sceneHelpers = new SceneHelpers();
            m_scene = sceneHelpers.SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config, m_iam);

            // Create user
            string userFirstName = "Jock";
            string userLastName = "Stirrup";
            string userPassword = "troll";
            UserAccountHelpers.CreateUserWithInventory(m_scene, userFirstName, userLastName, m_userId, userPassword);

            AgentCircuitData acd = new AgentCircuitData();
            acd.AgentID = m_userId;
            m_tc = new TestClient(acd, m_scene);
        }

        [Fact]
        public void TestRezCoalescedObject()
        {
/*
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Create asset
            SceneObjectGroup object1 = SceneHelpers.CreateSceneObject(1, m_userId, "Object1", 0x20);
            object1.AbsolutePosition = new Vector3(15, 30, 45);

            SceneObjectGroup object2 = SceneHelpers.CreateSceneObject(1, m_userId, "Object2", 0x40);
            object2.AbsolutePosition = new Vector3(25, 50, 75);

            CoalescedSceneObjects coa = new CoalescedSceneObjects(m_userId, object1, object2);

            UUID asset1Id = UUID.Parse("00000000-0000-0000-0000-000000000060");
            AssetBase asset1 = AssetHelpers.CreateAsset(asset1Id, coa);
            m_scene.AssetService.Store(asset1);

            // Create item
            UUID item1Id = UUID.Parse("00000000-0000-0000-0000-000000000080");
            string item1Name = "My Little Dog";
            InventoryItemBase item1 = new InventoryItemBase();
            item1.Name = item1Name;
            item1.AssetID = asset1.FullID;
            item1.ID = item1Id;
            InventoryFolderBase objsFolder
                = InventoryArchiveUtils.FindFoldersByPath(m_scene.InventoryService, m_userId, "Objects")[0];
            item1.Folder = objsFolder.ID;
            item1.Flags |= (uint)InventoryItemFlags.ObjectHasMultipleItems;
            m_scene.AddInventoryItem(item1);

            SceneObjectGroup so
                = m_iam.RezObject(
                    m_tc, item1Id, new Vector3(100, 100, 100), Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);

            Assert.NotNull();

            Assert.That(m_scene.SceneGraph.GetTotalObjectsCount(), Is.EqualTo(2));

            SceneObjectPart retrievedObj1Part = m_scene.GetSceneObjectPart(object1.Name);
            Assert.Null();

            retrievedObj1Part = m_scene.GetSceneObjectPart(item1.Name);
            Assert.NotNull();
            Assert.Equal(,);

            // Bottom of coalescence is placed on ground, hence we end up with 100.5 rather than 85 since the bottom
            // object is unit square.
            Assert.Equal(,));

            SceneObjectPart retrievedObj2Part = m_scene.GetSceneObjectPart(object2.Name);
            Assert.NotNull();
            Assert.Equal(,);
            Assert.Equal(,));
*/
        }

        [Fact]
        public void TestRezObject()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Create asset
            SceneObjectGroup object1 = SceneHelpers.CreateSceneObject(1, m_userId, "My Little Dog Object", 0x40);

            UUID asset1Id = UUID.Parse("00000000-0000-0000-0000-000000000060");
            AssetBase asset1 = AssetHelpers.CreateAsset(asset1Id, object1);
            m_scene.AssetService.Store(asset1);

            // Create item
            UUID item1Id = UUID.Parse("00000000-0000-0000-0000-000000000080");
            string item1Name = "My Little Dog";
            InventoryItemBase item1 = new InventoryItemBase();
            item1.Name = item1Name;
            item1.AssetID = asset1.FullID;
            item1.ID = item1Id;
            item1.Owner = m_userId;
            InventoryFolderBase objsFolder
                = InventoryArchiveUtils.FindFoldersByPath(m_scene.InventoryService, m_userId, "Objects")[0];
            item1.Folder = objsFolder.ID;
            m_scene.AddInventoryItem(item1);

            SceneObjectGroup so
                = m_iam.RezObject(
                    m_tc, item1Id, UUID.Zero, Vector3.Zero, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);

            Assert.NotNull();

            SceneObjectPart retrievedPart = m_scene.GetSceneObjectPart(so.UUID);
            Assert.NotNull();
        }
    }
}