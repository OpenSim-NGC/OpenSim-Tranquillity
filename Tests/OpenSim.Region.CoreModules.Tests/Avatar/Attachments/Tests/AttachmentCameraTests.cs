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

using Nini.Config;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.Attachments.Tests
{
    /// <summary>
    /// SL llSetCameraParams: "The PERMISSION_CONTROL_CAMERA permission is automatically revoked when the avatar stands
    /// up from or detaches the object, and any scripted camera parameters are automatically cleared."
    /// </summary>
    public class AttachmentCameraTests : OpenSimTestCase
    {
        private const int TakeControls = 4;     // PERMISSION_TAKE_CONTROLS
        private const int ControlCamera = 2048; // PERMISSION_CONTROL_CAMERA

        private readonly Scene m_scene;
        private readonly ScenePresence m_sp;

        public AttachmentCameraTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config, new AttachmentsModule(), new BasicInventoryAccessModule());
            m_sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
        }

        private List<UUID> ClearedCameras => ((TestClient)m_sp.ControllingClient).ReceivedClearFollowCams;

        private (SceneObjectGroup so, TaskInventoryItem item) AttachScriptedObject()
        {
            SceneObjectGroup so = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "hud", 0x10);
            TaskInventoryItem item = new TaskInventoryItem
            {
                Name = "script", ItemID = UUID.Random(), AssetID = UUID.Random(),
                Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL,
                PermsGranter = m_sp.UUID, PermsMask = TakeControls | ControlCamera,
            };
            so.RootPart.Inventory.AddInventoryItem(item, true);
            Assert.True(m_scene.AttachmentsModule.AttachObject(
                m_sp, so, (uint)AttachmentPoint.Chest, true, false, false, UUID.Zero));
            Assert.Equal(m_sp.UUID, so.AttachedAvatar);
            return (so, item);
        }

        [Fact]
        public void DetachingClearsTheObjectsScriptedCamera()
        {
            var (so, item) = AttachScriptedObject();

            m_scene.AttachmentsModule.DetachSingleAttachmentToInv(m_sp, so);

            Assert.Equal(0, item.PermsMask & (TakeControls | ControlCamera));
            Assert.Equal(new[] { so.UUID }, ClearedCameras);
        }

        [Fact]
        public void DroppingClearsTheObjectsScriptedCamera()
        {
            var (so, item) = AttachScriptedObject();
            so.FromItemID = UUID.Random(); // a drop needs an inventory item; a temporary attachment cannot be dropped

            m_scene.AttachmentsModule.DetachSingleAttachmentToGround(m_sp, so.LocalId);

            Assert.Equal(UUID.Zero, so.AttachedAvatar);
            Assert.Equal(0, item.PermsMask & (TakeControls | ControlCamera));
            Assert.Equal(new[] { so.UUID }, ClearedCameras);
        }
    }
}
