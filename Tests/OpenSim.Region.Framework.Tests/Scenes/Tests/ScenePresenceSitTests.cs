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

using System.Reflection;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
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
            Assert.Single(sittingAvatars);
            Assert.Contains(m_sp, sittingAvatars);
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

        // --- PRIM_ALLOW_UNSIT / PRIM_SCRIPTED_SIT_ONLY (SL wiki pages of those names) ---

        /// <summary>
        /// IExperienceModule stand-in: answers GetExperiencePermission from a table, throws for anything else.
        /// </summary>
        public class FakeExperiencePermissions : DispatchProxy
        {
            public Dictionary<(UUID, UUID), ExperiencePermission> Permissions = new();

            public static (IExperienceModule module, FakeExperiencePermissions fake) Create()
            {
                IExperienceModule module = DispatchProxy.Create<IExperienceModule, FakeExperiencePermissions>();
                return (module, (FakeExperiencePermissions)(object)module);
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IExperienceModule.GetExperiencePermission))
                    return Permissions.TryGetValue(((UUID)args[0], (UUID)args[1]), out ExperiencePermission p) ? p : ExperiencePermission.None;
                throw new NotSupportedException(targetMethod.Name);
            }
        }

        private FakeExperiencePermissions AddExperienceModule()
        {
            var (module, fake) = FakeExperiencePermissions.Create();
            m_scene.RegisterModuleInterface<IExperienceModule>(module);
            return fake;
        }

        private void PressStand()
        {
            m_sp.HandleAgentUpdate(m_sp.ControllingClient, new AgentUpdateArgs
            {
                ControlFlags = (uint)AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP,
                BodyRotation = Quaternion.Identity,
                HeadRotation = Quaternion.Identity,
            });
        }

        private void SitManually(SceneObjectPart part)
        {
            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, part.UUID, Vector3.Zero);
        }

        [Fact]
        public void AManuallySeatedAvatarStandsFromAPrimThatDisallowsUnsitWithNoExperienceModule()
        {
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;

            SitManually(part);
            Assert.Equal(part.LocalId, m_sp.ParentID);
            part.AllowUnsit = false;
            Assert.Null(m_scene.ExperienceModule);

            PressStand();

            Assert.Equal(0u, m_sp.ParentID);
            Assert.Equal(0, part.GetSittingAvatarsCount());
        }

        [Fact]
        public void AManuallySeatedAvatarChangesSeatFromAPrimThatDisallowsUnsitWithNoExperienceModule()
        {
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart first = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "first", 0x10).RootPart;
            SceneObjectPart second = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "second", 0x20).RootPart;

            SitManually(first);
            Assert.Equal(first.LocalId, m_sp.ParentID);
            first.AllowUnsit = false;

            SitManually(second);

            Assert.Equal(second.LocalId, m_sp.ParentID);
            Assert.Equal(0, first.GetSittingAvatarsCount());
        }

        [Fact]
        public void AManuallySeatedAvatarStandsFromAPrimThatDisallowsUnsitWhenTheExperienceModuleIsPresent()
        {
            // SL: "This flag has no effect on agents who had seated manually".
            FakeExperiencePermissions fake = AddExperienceModule();
            fake.Permissions[(m_sp.UUID, UUID.Zero)] = ExperiencePermission.Allowed;
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;

            SitManually(part);
            part.AllowUnsit = false;
            PressStand();

            Assert.Equal(0u, m_sp.ParentID);
        }

        [Fact]
        public void AnExperienceSeatedAvatarCannotStandOrChangeSeatUntilTheExperienceIsNoLongerAllowed()
        {
            UUID experience = TestHelpers.ParseTail(0xE1);
            FakeExperiencePermissions fake = AddExperienceModule();
            fake.Permissions[(m_sp.UUID, experience)] = ExperiencePermission.Allowed;
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;
            part.SitTargetPosition = new Vector3(0, 0, 1);
            part.AllowUnsit = false;
            SceneObjectPart other = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "other", 0x20).RootPart;

            // llSitOnLink under an experience.
            m_sp.ScriptedSit(part, m_sp.UUID, experience);
            Assert.Equal(part.LocalId, m_sp.ParentID);
            Assert.Equal(m_sp.UUID, part.SitTargetAvatar);

            PressStand();
            Assert.Equal(part.LocalId, m_sp.ParentID);

            SitManually(other);
            Assert.Equal(part.LocalId, m_sp.ParentID);

            // SL: the restriction ends on "experience disablement".
            fake.Permissions[(m_sp.UUID, experience)] = ExperiencePermission.Blocked;
            PressStand();
            Assert.Equal(0u, m_sp.ParentID);
        }

        [Fact]
        public void AManualSitIsNotRedirectedOntoAScriptedSitOnlySitTarget()
        {
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectGroup so = SceneHelpers.AddSceneObject(m_scene, 3, m_sp.UUID, "chair", 0x10);
            SceneObjectPart scripted = so.GetLinkNumPart(2);
            SceneObjectPart open = so.GetLinkNumPart(3);
            scripted.SitTargetPosition = new Vector3(0, 0, 1);
            scripted.ScriptedSitOnly = true;
            open.SitTargetPosition = new Vector3(0, 0, 1);

            SitManually(so.RootPart);

            Assert.Equal(open.LocalId, m_sp.ParentID);
            Assert.Equal(m_sp.UUID, open.SitTargetAvatar);
            Assert.Equal(UUID.Zero, scripted.SitTargetAvatar);

            // A script-driven sit onto the scripted-only prim is unaffected.
            m_sp.StandUp();
            m_sp.ScriptedSit(scripted, m_sp.UUID, UUID.Zero);
            Assert.Equal(scripted.LocalId, m_sp.ParentID);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnObjectWithAScriptedSitOnlyPrimAndNoOtherSitTargetCannotBeSatOnManually(bool scriptedPrimHasSitTarget)
        {
            // SL: "If any prim in a linkset has PRIM_SCRIPTED_SIT_ONLY set and no other prim in the linkset has a
            // sit target then an avatar cannot manually sit on the object."
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectGroup so = SceneHelpers.AddSceneObject(m_scene, 2, m_sp.UUID, "bench", 0x10);
            SceneObjectPart scripted = so.GetLinkNumPart(2);
            scripted.ScriptedSitOnly = true;
            if (scriptedPrimHasSitTarget)
                scripted.SitTargetPosition = new Vector3(0, 0, 1);

            SitManually(so.RootPart);

            Assert.Equal(0u, m_sp.ParentID);
            Assert.Equal(0, so.RootPart.GetSittingAvatarsCount());
            Assert.Equal(0, scripted.GetSittingAvatarsCount());
        }

        [Fact]
        public void AnObjectWithAScriptedSitOnlyPrimAndAnotherSitTargetCanBeSatOnManually()
        {
            // SL: "If some other prim in the linkset does have a sit target (that is not filled or marked
            // PRIM_SCRIPTED_SIT_ONLY), the agent can sit on that prim."
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectGroup so = SceneHelpers.AddSceneObject(m_scene, 2, m_sp.UUID, "bench", 0x10);
            SceneObjectPart scripted = so.GetLinkNumPart(2);
            scripted.ScriptedSitOnly = true;
            so.RootPart.SitTargetPosition = new Vector3(0, 0, 1);

            SitManually(scripted.ParentGroup.RootPart);

            Assert.Equal(so.RootPart.LocalId, m_sp.ParentID);
        }

        [Fact]
        public void AManualSitAndStandWithNeitherFlagSetIsUnchanged()
        {
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectGroup so = SceneHelpers.AddSceneObject(m_scene, 2, m_sp.UUID, "bench", 0x10);
            SceneObjectPart child = so.GetLinkNumPart(2);
            child.SitTargetPosition = new Vector3(0, 0, 1);
            Assert.True(so.RootPart.AllowUnsit);
            Assert.False(child.ScriptedSitOnly);

            SitManually(so.RootPart);
            Assert.Equal(child.LocalId, m_sp.ParentID);
            Assert.Equal(m_sp.UUID, child.SitTargetAvatar);

            PressStand();

            Assert.Equal(0u, m_sp.ParentID);
            Assert.Equal(UUID.Zero, child.SitTargetAvatar);
            Assert.NotNull(m_sp.PhysicsActor);
        }

        // --- Stand-up, Release Keys and llSitOnLink (SL wiki llSetCameraParams, llTakeControls, llRequestPermissions) ---

        private const int TakeControls = 4;     // PERMISSION_TAKE_CONTROLS
        private const int ControlCamera = 2048; // PERMISSION_CONTROL_CAMERA

        private static TaskInventoryItem AddGrantedScript(SceneObjectPart part, UUID granter, int mask)
        {
            TaskInventoryItem item = new TaskInventoryItem
            {
                Name = "script", ItemID = UUID.Random(), AssetID = UUID.Random(),
                Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL,
                PermsGranter = granter, PermsMask = mask,
            };
            part.Inventory.AddInventoryItem(item, true);
            return item;
        }

        private List<UUID> ClearedCameras => ((TestClient)m_sp.ControllingClient).ReceivedClearFollowCams;

        [Fact]
        public void StandingRevokesCameraAndControlsFromScriptsInEveryPrimOfTheObject()
        {
            // SL: "The PERMISSION_CONTROL_CAMERA permission is automatically revoked when the avatar stands up from
            // or detaches the object, and any scripted camera parameters are automatically cleared."
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectGroup so = SceneHelpers.AddSceneObject(m_scene, 2, m_sp.UUID, "car", 0x10);
            SceneObjectPart child = so.GetLinkNumPart(2);
            TaskInventoryItem inSeat = AddGrantedScript(so.RootPart, m_sp.UUID, TakeControls | ControlCamera | 16);
            TaskInventoryItem inChild = AddGrantedScript(child, m_sp.UUID, ControlCamera);

            SitManually(so.RootPart);
            Assert.Equal(so.RootPart.LocalId, m_sp.ParentID);
            m_sp.StandUp();

            Assert.Equal(16, inSeat.PermsMask);
            Assert.Equal(m_sp.UUID, inSeat.PermsGranter);
            Assert.Equal(0, inChild.PermsMask);
            Assert.Contains(so.UUID, ClearedCameras);
        }

        [Fact]
        public void StandingLeavesAnotherAvatarsGrantAlone()
        {
            // SL llRequestPermissions: "Scripts may hold permissions for only one agent at a time." A grant from
            // someone else is not the standing avatar's to lose.
            ScenePresence other = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x2));
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;
            TaskInventoryItem othersGrant = AddGrantedScript(part, other.UUID, TakeControls | ControlCamera);

            SitManually(part);
            m_sp.StandUp();

            Assert.Equal(TakeControls | ControlCamera, othersGrant.PermsMask);
            Assert.Equal(other.UUID, othersGrant.PermsGranter);
        }

        [Fact]
        public void ReleaseKeysStandsASeatedAvatarUp()
        {
            // Halcyon ScenePresence.HandleForceReleaseControls: "SL stands up the user on a forced controls release".
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;
            SitManually(part);
            Assert.Equal(part.LocalId, m_sp.ParentID);

            m_sp.HandleForceReleaseControls(m_sp.ControllingClient, m_sp.UUID);

            Assert.Equal(0u, m_sp.ParentID);
            Assert.Equal(0, part.GetSittingAvatarsCount());
        }

        [Fact]
        public void ReleaseKeysRevokesTakeControlsFromTheScriptsThatHeldThem()
        {
            // SL llTakeControls: the permission "can be revoked ... if the user chooses Release Keys from the viewer."
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "hud", 0x10).RootPart;
            TaskInventoryItem item = AddGrantedScript(part, m_sp.UUID, TakeControls | 16);
            m_sp.RegisterControlEventsToScript(1, 1, 0, part.LocalId, item.ItemID);
            Assert.True(m_sp.HasScriptControls(item.ItemID));

            m_sp.HandleForceReleaseControls(m_sp.ControllingClient, m_sp.UUID);

            Assert.False(m_sp.HasScriptControls(item.ItemID));
            Assert.Equal(16, item.PermsMask);
            Assert.Equal(m_sp.UUID, item.PermsGranter);
        }

        [Fact]
        public void ReleaseKeysDoesNotStandAnAvatarAnExperienceHoldsInItsSeat()
        {
            // SL PRIM_ALLOW_UNSIT: the seated avatar "will be unable to stand"; Release Keys is not on SL's list of
            // what lifts it.
            UUID experience = TestHelpers.ParseTail(0xE1);
            FakeExperiencePermissions fake = AddExperienceModule();
            fake.Permissions[(m_sp.UUID, experience)] = ExperiencePermission.Allowed;
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;
            part.SitTargetPosition = new Vector3(0, 0, 1);
            part.AllowUnsit = false;
            m_sp.ScriptedSit(part, m_sp.UUID, experience);
            Assert.Equal(part.LocalId, m_sp.ParentID);

            m_sp.HandleForceReleaseControls(m_sp.ControllingClient, m_sp.UUID);

            Assert.Equal(part.LocalId, m_sp.ParentID);
        }

        [Fact]
        public void AScriptedSitOntoThePrimTheAvatarAlreadySitsOnDoesNotStandItUp()
        {
            // HandleAgentRequestSit ignores a sit on the prim the avatar already sits on; llSitOnLink does the same.
            m_sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, 1, m_sp.UUID, "seat", 0x10).RootPart;
            TaskInventoryItem item = AddGrantedScript(part, m_sp.UUID, ControlCamera);
            SitManually(part);
            Assert.Equal(part.LocalId, m_sp.ParentID);

            m_sp.ScriptedSit(part, m_sp.UUID, UUID.Zero);

            Assert.Equal(part.LocalId, m_sp.ParentID);
            Assert.Equal(ControlCamera, item.PermsMask);
            Assert.Empty(ClearedCameras);
        }
    }
}
