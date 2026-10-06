/*
 * Copyright (c) Legion Builds
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

using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    /// <summary>
    /// An avatar blocking an Experience ends the permission grants it gave to that Experience's scripts, whichever
    /// script engine runs them. SL wiki, experience_permissions_denied: the event is raised when "The agent has
    /// blocked the experience from the experience profile", with XP_ERROR_NOT_PERMITTED (4).
    /// </summary>
    // No test reaches a network service. Test grouping: each test builds its own scene and stand-ins and touches no
    // process-wide state (the class does not derive from OpenSimTestCase, which clears MainServer), so it runs in
    // parallel.
    public class ExperienceBlockGrantTests
    {
        /// <summary>The mask YEngine records for an Experience grant (LSL_Api.cs, llRequestExperiencePermissions).</summary>
        private const int YEngineExperienceMask = 408628;

        /// <summary>
        /// The permissions an Experience grants, as SL lists them for llRequestExperiencePermissions:
        /// PERMISSION_TAKE_CONTROLS | PERMISSION_TRIGGER_ANIMATION | PERMISSION_ATTACH | PERMISSION_TRACK_CAMERA |
        /// PERMISSION_CONTROL_CAMERA | PERMISSION_TELEPORT.
        /// </summary>
        private const int SLExperienceMask = 0x1C34;

        private const int XP_ERROR_NOT_PERMITTED = 4;
        private const int XP_ERROR_NOT_PERMITTED_LAND = 17;

        private readonly UUID m_blocked = UUID.Random();
        private readonly UUID m_other = UUID.Random();

        private readonly TestScene m_scene;
        private readonly ExperienceModule m_module;
        private readonly ScenePresence m_avatar;
        private readonly RecordingEngine m_engineA;
        private readonly RecordingEngine m_engineB;
        private readonly List<(UUID partId, UUID itemId, UUID granter, UUID experience, int mask, int reason)> m_revoked = new();

        public ExperienceBlockGrantTests()
        {
            m_scene = new SceneHelpers().SetupScene();

            m_scene.RegisterModuleInterface<IExperienceService>(StubExperienceService.Create());
            m_engineA = RecordingEngine.Create("EngineA");
            m_engineB = RecordingEngine.Create("EngineB");
            m_scene.StackModuleInterface<IScriptModule>((IScriptModule)(object)m_engineA);
            m_scene.StackModuleInterface<IScriptModule>((IScriptModule)(object)m_engineB);

            IConfigSource config = new IniConfigSource();
            config.AddConfig("Experience");
            config.Configs["Experience"].Set("Enabled", "true");

            m_module = new ExperienceModule();
            SceneHelpers.SetupSceneModules(m_scene, config, m_module);

            m_avatar = SceneHelpers.AddScenePresence(m_scene, UUID.Random());

            m_scene.EventManager.OnExperiencePermissionsRevoked +=
                (partId, itemId, granter, experience, mask, reason) => m_revoked.Add((partId, itemId, granter, experience, mask, reason));
        }

        [Fact]
        public void AGrantWithAnotherMaskEndsWhenTheAvatarBlocksTheExperience()
        {
            var (_, item) = AddScript(m_blocked, m_avatar.UUID, SLExperienceMask);

            Assert.True(m_module.SetExperiencePermissions(m_avatar.UUID, m_blocked, false));

            Assert.Equal(UUID.Zero, item.PermsGranter);
            Assert.Equal(0, item.PermsMask);
            foreach (RecordingEngine engine in new[] { m_engineA, m_engineB })
            {
                var post = Assert.Single(engine.Posts);
                Assert.Equal(item.ItemID, post.itemId);
                Assert.Equal("experience_permissions_denied", post.name);
                Assert.Equal(new object[] { m_avatar.UUID.ToString(), XP_ERROR_NOT_PERMITTED }, post.args);
            }
        }

        [Fact]
        public void TheSignalIsRaisedOncePerScriptWithTheGrantItLost()
        {
            var (partA, itemA) = AddScript(m_blocked, m_avatar.UUID, SLExperienceMask);
            // PERMISSION_TAKE_CONTROLS | PERMISSION_TRIGGER_ANIMATION, in another object
            var (partB, itemB) = AddScript(m_blocked, m_avatar.UUID, 0x14);

            m_module.SetExperiencePermissions(m_avatar.UUID, m_blocked, false);

            Assert.Equal(2, m_revoked.Count);
            Assert.Single(m_revoked, r => r.itemId == itemA.ItemID);
            Assert.Single(m_revoked, r => r.itemId == itemB.ItemID);
            Assert.Contains((partA.UUID, itemA.ItemID, m_avatar.UUID, m_blocked, SLExperienceMask, XP_ERROR_NOT_PERMITTED), m_revoked);
            Assert.Contains((partB.UUID, itemB.ItemID, m_avatar.UUID, m_blocked, 0x14, XP_ERROR_NOT_PERMITTED), m_revoked);
        }

        [Fact]
        public void TheSignalComesAfterTheGrantIsClearedAndTheScriptIsTold()
        {
            var (_, item) = AddScript(m_blocked, m_avatar.UUID, SLExperienceMask);
            int maskSeen = -1;
            int postsSeen = -1;
            m_scene.EventManager.OnExperiencePermissionsRevoked += (partId, itemId, granter, experience, mask, reason) =>
            {
                maskSeen = item.PermsMask;
                postsSeen = m_engineA.Posts.Count + m_engineB.Posts.Count;
            };

            m_module.SetExperiencePermissions(m_avatar.UUID, m_blocked, false);

            Assert.Equal(0, maskSeen);
            Assert.Equal(2, postsSeen);
        }

        [Fact]
        public void GrantsFromAnotherAvatarOrForAnotherExperienceAreUntouched()
        {
            UUID otherAvatar = UUID.Random();
            var (_, fromOtherAvatar) = AddScript(m_blocked, otherAvatar, SLExperienceMask);
            var (_, otherExperience) = AddScript(m_other, m_avatar.UUID, SLExperienceMask);
            // A script in no Experience holding the avatar's ordinary grant.
            var (_, noExperience) = AddScript(UUID.Zero, m_avatar.UUID, 0x14);
            // YEngine's grant for another Experience the estate allows and the avatar allowed stays, as before.
            m_scene.RegionInfo.EstateSettings.AllowedExperiences = new[] { m_other };
            m_module.SetExperiencePermissions(m_avatar.UUID, m_other, true);
            var (_, otherExperienceYEngine) = AddScript(m_other, m_avatar.UUID, YEngineExperienceMask);

            m_module.SetExperiencePermissions(m_avatar.UUID, m_blocked, false);

            AssertGrant(fromOtherAvatar, otherAvatar, SLExperienceMask);
            AssertGrant(otherExperience, m_avatar.UUID, SLExperienceMask);
            AssertGrant(noExperience, m_avatar.UUID, 0x14);
            AssertGrant(otherExperienceYEngine, m_avatar.UUID, YEngineExperienceMask);
            Assert.Empty(m_engineA.Posts);
            Assert.Empty(m_engineB.Posts);
            Assert.Empty(m_revoked);
        }

        [Fact]
        public void AllowingAnExperienceEndsNoGrant()
        {
            var (_, item) = AddScript(m_blocked, m_avatar.UUID, SLExperienceMask);

            m_module.SetExperiencePermissions(m_avatar.UUID, m_blocked, true);

            AssertGrant(item, m_avatar.UUID, SLExperienceMask);
            Assert.Empty(m_engineA.Posts);
            Assert.Empty(m_revoked);
        }

        [Fact]
        public void YEnginesExperienceGrantEndsOnABlockAsBefore()
        {
            var (part, item) = AddScript(m_blocked, m_avatar.UUID, YEngineExperienceMask);
            // As before, a block also re-checks the avatar's other YEngine Experience grants: one for an Experience
            // the estate does not allow ends too.
            var (_, notAllowed) = AddScript(m_other, m_avatar.UUID, YEngineExperienceMask);

            m_module.SetExperiencePermissions(m_avatar.UUID, m_blocked, false);

            Assert.Equal(UUID.Zero, item.PermsGranter);
            Assert.Equal(0, item.PermsMask);
            Assert.Equal(UUID.Zero, notAllowed.PermsGranter);
            Assert.Equal(0, notAllowed.PermsMask);
            foreach (RecordingEngine engine in new[] { m_engineA, m_engineB })
            {
                Assert.Equal(2, engine.Posts.Count);
                Assert.All(engine.Posts, p =>
                {
                    Assert.Equal("experience_permissions_denied", p.name);
                    Assert.Equal(new object[] { m_avatar.UUID.ToString(), XP_ERROR_NOT_PERMITTED }, p.args);
                });
                Assert.Single(engine.Posts, p => p.itemId == item.ItemID);
                Assert.Single(engine.Posts, p => p.itemId == notAllowed.ItemID);
            }
            Assert.Contains((part.UUID, item.ItemID, m_avatar.UUID, m_blocked, YEngineExperienceMask, XP_ERROR_NOT_PERMITTED), m_revoked);
            Assert.Equal(2, m_revoked.Count);
        }

        [Fact]
        public void EnteringAParcelEndsOnlyYEnginesExperienceGrantAsBeforeAndSignalsIt()
        {
            var (part, yengine) = AddScript(m_blocked, m_avatar.UUID, YEngineExperienceMask);
            var (_, other) = AddScript(m_blocked, m_avatar.UUID, SLExperienceMask);

            m_scene.EventManager.TriggerAvatarEnteringNewParcel(m_avatar, 1, m_scene.RegionInfo.RegionID);

            Assert.Equal(UUID.Zero, yengine.PermsGranter);
            Assert.Equal(0, yengine.PermsMask);
            AssertGrant(other, m_avatar.UUID, SLExperienceMask);
            var post = Assert.Single(m_engineA.Posts);
            Assert.Equal(yengine.ItemID, post.itemId);
            Assert.Equal(new object[] { m_avatar.UUID.ToString(), XP_ERROR_NOT_PERMITTED_LAND }, post.args);
            var revoked = Assert.Single(m_revoked);
            Assert.Equal((part.UUID, yengine.ItemID, m_avatar.UUID, m_blocked, YEngineExperienceMask, XP_ERROR_NOT_PERMITTED_LAND), revoked);
        }

        private (SceneObjectPart part, TaskInventoryItem item) AddScript(UUID experience, UUID granter, int mask)
        {
            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene, "Test Object", UUID.Random()).RootPart;
            var item = new TaskInventoryItem
            {
                ItemID = UUID.Random(),
                AssetID = UUID.Random(),
                Name = "Test Script",
                Type = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                ExperienceID = experience,
                PermsGranter = granter,
                PermsMask = mask,
            };
            part.Inventory.AddInventoryItem(item, false);
            return (part, part.Inventory.GetInventoryItem(item.ItemID));
        }

        private static void AssertGrant(TaskInventoryItem item, UUID granter, int mask)
        {
            Assert.Equal(granter, item.PermsGranter);
            Assert.Equal(mask, item.PermsMask);
        }

        /// <summary>IExperienceService stand-in: stores every permission change, knows no Experience.</summary>
        public class StubExperienceService : DispatchProxy
        {
            public static IExperienceService Create() => DispatchProxy.Create<IExperienceService, StubExperienceService>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    nameof(IExperienceService.FetchExperiencePermissions) => new Dictionary<UUID, bool>(),
                    nameof(IExperienceService.UpdateExperiencePermissions) => true,
                    _ => throw new NotSupportedException(targetMethod.Name),
                };
            }
        }

        /// <summary>A script engine stand-in that records the events posted to it.</summary>
        public class RecordingEngine : DispatchProxy
        {
            public string Name;
            public readonly List<(UUID itemId, string name, object[] args)> Posts = new();

            public static RecordingEngine Create(string name)
            {
                var engine = (RecordingEngine)(object)DispatchProxy.Create<IScriptModule, RecordingEngine>();
                engine.Name = name;
                return engine;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case nameof(IScriptModule.PostScriptEvent):
                        Posts.Add(((UUID)args[0], (string)args[1], (object[])args[2]));
                        return true;
                    case "get_" + nameof(IScriptModule.ScriptEngineName):
                        return Name;
                }
                Type ret = targetMethod.ReturnType;
                return ret == typeof(void) || !ret.IsValueType ? null : Activator.CreateInstance(ret);
            }
        }
    }
}
