/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests that NPCModule.CreateNPC refuses a caller-chosen agent id that a presence in the scene
 * already holds, instead of registering a second circuit for the same id.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;

namespace osWebRtcVoice.Tests
{
    public class NpcCreateDuplicateIdTests
    {
        [Fact(Timeout = 60000)]
        public void CreateNpc_WithTheIdOfAnExistingPresence_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("NPC");
            config.Configs["NPC"].Set("Enabled", "true");

            var npcModule = new NPCModule();
            Scene scene = new SceneHelpers().SetupScene("npc duplicate id", UUID.Random(), 1000, 1000, config);
            SceneHelpers.SetupSceneModules(scene, config, npcModule);

            UUID existingId = new UUID("2d1f6b0e-83c4-4a59-9e07-5b3a8c4d1f92");
            ScenePresence existing = SceneHelpers.AddScenePresence(scene, existingId);
            int presencesBefore = scene.GetScenePresences().Count;

            UUID result = npcModule.CreateNPC("Test", "Npc", new Vector3(128, 128, 30), existingId,
                UUID.Zero, "", UUID.Zero, true, scene, existing.Appearance);

            {
                var assertedValue = result;
                TestAssert.WithMessage(() => Assert.Equal(UUID.Zero, assertedValue), "a taken agent id is refused");
            }
            {
                var assertedValue = scene.GetScenePresences().Count;
                TestAssert.WithMessage(() => Assert.Equal(presencesBefore, assertedValue), "no presence was added");
            }
            {
                var assertedValue = scene.GetScenePresence(existingId);
                TestAssert.WithMessage(() => Assert.Same(existing, assertedValue), "the existing presence is untouched");
            }
        }
    }
}
