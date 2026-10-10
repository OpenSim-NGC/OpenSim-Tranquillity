/*
 * Copyright (c) Legion Builds
 * All rights reserved.
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
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// EventManager.OnScriptPermissionsRemoved: when the core takes permissions from a script item without the script
    /// asking (a stand-up, a detach, Release Keys), it is raised once per item whose grant changed, with the item, the
    /// granter and the bits removed. SL llSetCameraParams: "The PERMISSION_CONTROL_CAMERA permission is automatically
    /// revoked when the avatar stands up from or detaches the object".
    /// </summary>
    /// <remarks>Each test builds its own scene and subscribes only to it: nothing process-wide.</remarks>
    public class ScriptPermissionsRemovedTests : OpenSimTestCase
    {
        private const int TakeControls = 0x4, TriggerAnimation = 0x10, ControlCamera = 0x800;

        private readonly TestScene m_scene;
        private readonly ScenePresence m_sp;
        private readonly SceneObjectPart m_part;
        private readonly List<(UUID part, UUID item, UUID granter, int removed)> m_removed = new();

        public ScriptPermissionsRemovedTests()
        {
            m_scene = new SceneHelpers().SetupScene();
            m_sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
            m_part = SceneHelpers.AddSceneObject(m_scene).RootPart;
            m_scene.EventManager.OnScriptPermissionsRemoved += (part, item, granter, removed) => m_removed.Add((part, item, granter, removed));
        }

        private TaskInventoryItem GrantedScript(SceneObjectPart part, UUID granter, int mask)
        {
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(m_scene.AssetService, part);
            item.PermsGranter = granter;
            item.PermsMask = mask;
            return item;
        }

        private void Sit()
        {
            m_sp.AbsolutePosition = new Vector3(1, 0, 0);
            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, m_part.UUID, Vector3.Zero);
            Assert.Equal(m_part.LocalId, m_sp.ParentID);
        }

        [Fact]
        public void StandingUpRaisesItForACameraGrantWithNoControlsTaken()
        {
            TestHelpers.InMethod();
            TaskInventoryItem item = GrantedScript(m_part, m_sp.UUID, ControlCamera | TriggerAnimation);
            Sit();

            m_sp.StandUp();

            Assert.Equal(TriggerAnimation, item.PermsMask);
            Assert.Equal(m_sp.UUID, item.PermsGranter);
            var raised = Assert.Single(m_removed);
            Assert.Equal((m_part.UUID, item.ItemID, m_sp.UUID, ControlCamera), raised);
        }

        [Fact]
        public void StandingUpRaisesNothingForAnotherAvatarsGrantOrAGrantWithoutTheBits()
        {
            TestHelpers.InMethod();
            TaskInventoryItem other = GrantedScript(m_part, TestHelpers.ParseTail(0x2), ControlCamera);
            TaskInventoryItem animOnly = GrantedScript(m_part, m_sp.UUID, TriggerAnimation);
            Sit();

            m_sp.StandUp();

            Assert.Equal(ControlCamera, other.PermsMask);
            Assert.Equal(TriggerAnimation, animOnly.PermsMask);
            Assert.Empty(m_removed);
        }

        [Fact]
        public void RemovingFromEveryGranterRaisesItPerChangedItemAndClearsAnEmptyGrant()
        {
            TestHelpers.InMethod();
            UUID otherAvatar = TestHelpers.ParseTail(0x2);
            TaskInventoryItem both = GrantedScript(m_part, m_sp.UUID, TakeControls | ControlCamera);
            TaskInventoryItem camera = GrantedScript(m_part, otherAvatar, ControlCamera | TriggerAnimation);
            TaskInventoryItem untouched = GrantedScript(m_part, m_sp.UUID, TriggerAnimation);

            // The detach path: every script's grant, whoever gave it.
            m_part.ParentGroup.RemoveScriptsPermissions(TakeControls | ControlCamera);

            Assert.Equal(0, both.PermsMask);
            Assert.Equal(UUID.Zero, both.PermsGranter);
            Assert.Equal(TriggerAnimation, camera.PermsMask);
            Assert.Equal(TriggerAnimation, untouched.PermsMask);
            Assert.Equal(2, m_removed.Count);
            Assert.Contains((m_part.UUID, both.ItemID, m_sp.UUID, TakeControls | ControlCamera), m_removed);
            Assert.Contains((m_part.UUID, camera.ItemID, otherAvatar, ControlCamera), m_removed);
        }
    }
}
