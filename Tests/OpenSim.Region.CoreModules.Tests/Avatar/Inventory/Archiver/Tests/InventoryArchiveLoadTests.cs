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
using Xunit;
using Nini.Config;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Avatar.Inventory.Archiver.Tests
{
    public class InventoryArchiveLoadTests : InventoryArchiveTestCase
    {
        protected TestScene m_scene = null!;
        protected InventoryArchiverModule m_archiverModule = null!;

        public override void SetUp()
        {
            base.SetUp();

            SerialiserModule serialiserModule = new SerialiserModule();
            m_archiverModule = new InventoryArchiverModule();

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, serialiserModule, m_archiverModule);
        }

        [Fact]
        public void TestLoadCoalesecedItem()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UserAccountHelpers.CreateUserWithInventory(m_scene, m_uaLL1, "password");
            m_archiverModule.DearchiveInventory(UUID.Random(), m_uaLL1.FirstName, m_uaLL1.LastName, "/", "password", m_iarStream);

            InventoryItemBase coaItem
                = InventoryArchiveUtils.FindItemByPath(m_scene.InventoryService, m_uaLL1.PrincipalID, m_coaItemName);

            Assert.NotNull(coaItem);

            string assetXml = AssetHelpers.ReadAssetAsString(m_scene.AssetService, coaItem.AssetID);

            CoalescedSceneObjects coa;
            bool readResult = CoalescedSceneObjectsSerializer.TryFromXml(assetXml, out coa);

            Assert.True(readResult);
            Assert.NotNull(coa);

            List<SceneObjectGroup> coaObjects = coa.Objects;
            Assert.Single(coaObjects);
            Assert.Equal(1, coaObjects[0].PrimCount);

            Assert.NotNull(coaObjects[0]);
            Assert.Single(coaObjects[0].Parts);
        }

        /// <summary>
        /// Test case where a creator account exists for the creator UUID embedded in item metadata and serialized
        /// objects.
        /// </summary>
        [Fact]
        public void TestLoadIarCreatorAccountPresent()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UserAccountHelpers.CreateUserWithInventory(m_scene, m_uaLL1, "meowfood");

            m_archiverModule.DearchiveInventory(UUID.Random(), m_uaLL1.FirstName, m_uaLL1.LastName, "/", "meowfood", m_iarStream);
            InventoryItemBase foundItem1
                = InventoryArchiveUtils.FindItemByPath(m_scene.InventoryService, m_uaLL1.PrincipalID, m_item1Name);

            Assert.NotNull(foundItem1);
            Assert.Equal(m_uaLL1.Name, foundItem1.CreatorId);
            Assert.Equal(m_uaLL1.PrincipalID, foundItem1.CreatorIdAsUuid);
            Assert.Equal(m_uaLL1.PrincipalID, foundItem1.Owner);

            AssetBase asset1 = m_scene.AssetService.Get(foundItem1.AssetID.ToString());
            string xmlData = Utils.BytesToString(asset1.Data);
            SceneObjectGroup sog1 = SceneObjectSerializer.FromOriginalXmlFormat(xmlData);

            Assert.Equal(m_uaLL1.PrincipalID, sog1.OwnerID);
        }

//        /// <summary>
//        /// Test loading a V0.1 OpenSim Inventory Archive (subject to change since there is no fixed format yet) where
//        /// an account exists with the same name as the creator, though not the same id.
//        /// </summary>
//        [Fact]
//        public void TestLoadIarV0_1SameNameCreator()
//        {
//            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();
//
//            UserAccountHelpers.CreateUserWithInventory(m_scene, m_uaMT, "meowfood");
//            UserAccountHelpers.CreateUserWithInventory(m_scene, m_uaLL2, "hampshire");
//
//            m_archiverModule.DearchiveInventory(m_uaMT.FirstName, m_uaMT.LastName, "/", "meowfood", m_iarStream);
//            InventoryItemBase foundItem1
//                = InventoryArchiveUtils.FindItemByPath(m_scene.InventoryService, m_uaMT.PrincipalID, m_item1Name);
//
//            Assert.Equal(,),
//                "Loaded item non-uuid creator doesn't match original");
//            Assert.That(
//                foundItem1.CreatorIdAsUuid),
//                "Loaded item uuid creator doesn't match original");
//            Assert.True(foundItem1.Owner),
//                "Loaded item owner doesn't match inventory reciever");
//
//            AssetBase asset1 = m_scene.AssetService.Get(foundItem1.AssetID.ToString());
//            string xmlData = Utils.BytesToString(asset1.Data);
//            SceneObjectGroup sog1 = SceneObjectSerializer.FromOriginalXmlFormat(xmlData);
//
//            Assert.Equal(,);
//        }

        /// <summary>
        /// Test loading a V0.1 OpenSim Inventory Archive (subject to change since there is no fixed format yet) where
        /// the creator or an account with the creator's name does not exist within the system.
        /// </summary>
        [Fact]
        public void TestLoadIarV0_1AbsentCreator()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UserAccountHelpers.CreateUserWithInventory(m_scene, m_uaMT, "password");
            m_archiverModule.DearchiveInventory(UUID.Random(), m_uaMT.FirstName, m_uaMT.LastName, "/", "password", m_iarStream);

            InventoryItemBase foundItem1
                = InventoryArchiveUtils.FindItemByPath(m_scene.InventoryService, m_uaMT.PrincipalID, m_item1Name);

            Assert.NotNull(foundItem1);
            Assert.Equal(m_uaMT.Name, foundItem1.CreatorId);
            Assert.Equal(m_uaMT.PrincipalID, foundItem1.CreatorIdAsUuid);

            AssetBase asset1 = m_scene.AssetService.Get(foundItem1.AssetID.ToString());
            string xmlData = Utils.BytesToString(asset1.Data);
            SceneObjectGroup sog1 = SceneObjectSerializer.FromOriginalXmlFormat(xmlData);

            Assert.Equal(m_uaMT.PrincipalID, sog1.OwnerID);
        }
    }
}