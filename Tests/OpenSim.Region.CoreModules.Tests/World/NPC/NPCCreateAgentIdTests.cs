/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.NPC.Tests;

/// <summary>
/// A caller may choose the agent id of an NPC. NPCModule.CreateNPC refuses an id that a presence in the scene
/// already holds, so a second circuit is never registered for one agent id.
/// </summary>
public class NPCCreateAgentIdTests : OpenSimTestCase
{
    private static readonly Vector3 Position = new(128, 128, 30);

    private Scene m_scene = null!;
    private NPCModule m_npcMod = null!;

    private void SetUpScene()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("NPC").Set("Enabled", "true");

        m_npcMod = new NPCModule();
        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, config, new AvatarFactoryModule(), new AttachmentsModule(), m_npcMod);
    }

    [Fact]
    public void ANpcWithAChosenFreeAgentIdIsCreatedUnderThatId()
    {
        TestHelpers.InMethod();
        SetUpScene();
        ScenePresence avatar = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
        UUID chosen = TestHelpers.ParseTail(0x2);

        UUID created = m_npcMod.CreateNPC("Test", "Bot", Position, chosen, UUID.Zero, "", UUID.Zero, true, m_scene, avatar.Appearance);

        Assert.Equal(chosen, created);
        Assert.True(m_scene.TryGetScenePresence(chosen, out ScenePresence npc));
        Assert.True(npc.IsNPC);
    }

    [Fact]
    public void ANpcWithTheAgentIdOfAnExistingNpcIsRefused()
    {
        TestHelpers.InMethod();
        SetUpScene();
        ScenePresence avatar = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
        UUID chosen = TestHelpers.ParseTail(0x2);
        Assert.Equal(chosen, m_npcMod.CreateNPC("Test", "Bot", Position, chosen, UUID.Zero, "", UUID.Zero, true, m_scene, avatar.Appearance));
        int circuits = m_scene.AuthenticateHandler.GetAgentCircuits().Count;

        UUID second = m_npcMod.CreateNPC("Other", "Bot", Position, chosen, UUID.Zero, "", UUID.Zero, true, m_scene, avatar.Appearance);

        Assert.Equal(UUID.Zero, second);
        Assert.Equal(circuits, m_scene.AuthenticateHandler.GetAgentCircuits().Count);
        Assert.True(m_scene.TryGetScenePresence(chosen, out ScenePresence npc));
        Assert.Equal("Test Bot", npc.Name);
    }

    [Fact]
    public void ANpcWithTheAgentIdOfARealAvatarIsRefused()
    {
        TestHelpers.InMethod();
        SetUpScene();
        UUID avatarId = TestHelpers.ParseTail(0x1);
        ScenePresence avatar = SceneHelpers.AddScenePresence(m_scene, avatarId);
        string name = avatar.Name;

        UUID created = m_npcMod.CreateNPC("Test", "Bot", Position, avatarId, UUID.Zero, "", UUID.Zero, true, m_scene, avatar.Appearance);

        Assert.Equal(UUID.Zero, created);
        Assert.True(m_scene.TryGetScenePresence(avatarId, out ScenePresence still));
        Assert.False(still.IsNPC);
        Assert.Equal(name, still.Name);
    }

    [Fact]
    public void ANpcWithoutAChosenAgentIdStillGetsARandomOne()
    {
        TestHelpers.InMethod();
        SetUpScene();
        ScenePresence avatar = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));

        UUID first = m_npcMod.CreateNPC("Test", "One", Position, UUID.Zero, true, m_scene, avatar.Appearance);
        UUID second = m_npcMod.CreateNPC("Test", "Two", Position, UUID.Zero, true, m_scene, avatar.Appearance);

        Assert.NotEqual(UUID.Zero, first);
        Assert.NotEqual(UUID.Zero, second);
        Assert.NotEqual(first, second);
    }
}
