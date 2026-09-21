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

using System;
using System.Collections.Generic;
using System.Threading;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory;

/// <summary>
/// S8. Two ways the stored appearance was destroyed by a region that was only trying to save it.
///
/// <para><b>The live loss.</b> On 2026-09-05 a stock viewer carrying a stale inventory cache from a previous grid
/// named four item ids that exist nowhere in <c>inventoryitems</c>. <c>SetAppearanceAssets</c> logged
/// "Can't find inventory item ... setting to default" for Skin, Hair, Eyes and Shirt and then, despite the
/// message, <b>removed</b> them; <c>SaveAppearance</c> persisted the result on the next line, and because
/// <c>AvatarService.SetAvatar</c> deletes every row for the agent before rewriting (<c>AvatarService.cs:93</c>),
/// wearable slots 1-4 disappeared from the stored record entirely. The agent's Current Outfit folder still linked
/// perfectly good skin, eyes and hair items throughout.</para>
///
/// <para><b>Why the existing suite could not catch it.</b> <c>AvatarFactoryNowWearingTests</c> covers S0c, which
/// is the same failure from the other direction - there the viewer LISTED fewer slots than were worn. Every one
/// of its cases uses item ids the inventory can resolve, so the "listed but unresolvable" branch was never
/// entered. And no appearance test had ever driven a child presence.</para>
/// </summary>
public class AvatarFactoryAppearanceIntegrityTests : OpenSimTestCase
{
    /// <summary>Records what actually reached the avatar service - the thing both defects corrupted.</summary>
    private sealed class RecordingAvatarService : IAvatarService, ISharedRegionModule
    {
        public readonly List<(UUID User, AvatarAppearance Appearance)> Saved = new();

        public bool SetAppearance(UUID userID, AvatarAppearance appearance)
        {
            lock (Saved) Saved.Add((userID, appearance));
            return true;
        }

        public int Calls { get { lock (Saved) return Saved.Count; } }

        public AvatarAppearance GetAppearance(UUID userID) => null!;
        public AvatarData GetAvatar(UUID userID) => null!;
        public bool SetAvatar(UUID userID, AvatarData avatar) => true;
        public bool ResetAvatar(UUID userID) => true;
        public bool SetItems(UUID userID, string[] names, string[] values) => true;
        public bool RemoveItems(UUID userID, string[] names) => true;

        public string Name => "RecordingAvatarService";
        public Type ReplaceableInterface => null!;
        public void Initialise(IConfigSource source) { }
        public void PostInitialise() { }
        public void Close() { }
        public void AddRegion(Scene scene) => scene.RegisterModuleInterface<IAvatarService>(this);
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
    }

    private static readonly UUID SkinItem = new("db5a4e5f-0000-4000-8000-000000000001");
    private static readonly UUID SkinAsset = new("db5a4e5f-0000-4000-8000-0000000000a1");

    private static (AvatarFactoryModule afm, TestScene scene, RecordingAvatarService avatars, ScenePresence sp) Setup()
    {
        RecordingAvatarService avatars = new RecordingAvatarService();
        AvatarFactoryModule afm = new AvatarFactoryModule();
        TestScene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, avatars, afm);

        // SetAppearanceAssets does nothing at all unless the agent has an inventory root
        // (AvatarFactoryModule.cs:911) - without this the unresolvable branch is unreachable and these tests
        // would pass against the broken code.
        UserAccount user = UserAccountHelpers.CreateUserWithInventory(scene, 0x1);
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, user.PrincipalID);
        return (afm, scene, avatars, sp);
    }

    /// <summary>Wear one skin whose item id the inventory service will not resolve.</summary>
    private static void WearUnresolvableSkin(ScenePresence sp)
    {
        AvatarWearable[] wearables = sp.Appearance.Wearables;
        wearables[(int)WearableType.Skin] = new AvatarWearable(SkinItem, SkinAsset);
        sp.Appearance.Wearables = wearables;
    }

    // ------------------------------------------------------------------ 1. an unresolvable item keeps its slot

    /// <summary>
    /// The item id cannot be resolved, so nothing can be said about the asset behind it - but the agent is still
    /// wearing a skin, and the region must not decide otherwise. Before S8 this slot came out empty.
    /// </summary>
    [Fact]
    public void An_item_the_region_cannot_resolve_keeps_its_slot()
    {
        (AvatarFactoryModule afm, TestScene scene, RecordingAvatarService avatars, ScenePresence sp) = Setup();
        WearUnresolvableSkin(sp);

        afm.QueueAppearanceSave(sp.UUID);
        scene.EventManager.TriggerOnRemovePresence(sp.UUID);   // the close flush runs SaveAppearance synchronously

        Assert.Equal(1, avatars.Calls);
        AvatarAppearance saved = avatars.Saved[0].Appearance;
        Assert.True(saved.Wearables[(int)WearableType.Skin].Count > 0,
            "the skin slot was emptied by a failed inventory lookup; the agent is still wearing a skin");
        Assert.Equal(SkinItem, saved.Wearables[(int)WearableType.Skin][0].ItemID);
    }

    /// <summary>
    /// And the in-memory appearance keeps it too, which is what the next save, the next bake and every viewer in
    /// range read from.
    /// </summary>
    [Fact]
    public void The_presence_keeps_the_slot_as_well_as_the_stored_record()
    {
        (AvatarFactoryModule afm, TestScene scene, RecordingAvatarService _, ScenePresence sp) = Setup();
        WearUnresolvableSkin(sp);

        afm.QueueAppearanceSave(sp.UUID);
        scene.EventManager.TriggerOnRemovePresence(sp.UUID);

        Assert.True(sp.Appearance.Wearables[(int)WearableType.Skin].Count > 0);
        Assert.Equal(SkinItem, sp.Appearance.Wearables[(int)WearableType.Skin][0].ItemID);
    }

    // ------------------------------------------------------------------ 2. a child presence never writes

    /// <summary>
    /// A child presence's appearance is a copy carried for drawing; the root region owns it. Saving from one means
    /// resolving another region's inventory view and writing the answer as fact.
    ///
    /// <para>This drives the real timer drain rather than the close flush, because the close flush has had a child
    /// guard since S0b and would pass either way. <c>DelayBeforeAppearanceSave = 0</c> makes the queue drain on
    /// the next 500 ms tick.</para>
    /// </summary>
    [Fact]
    public void A_child_presence_does_not_write_to_the_avatar_service()
    {
        RecordingAvatarService avatars = new RecordingAvatarService();
        AvatarFactoryModule afm = new AvatarFactoryModule();

        IniConfigSource config = new IniConfigSource();
        config.AddConfig("Appearance").Set("DelayBeforeAppearanceSave", 0);

        TestScene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, config, avatars, afm);
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x2));

        sp.MakeChildAgent(scene.RegionInfo.RegionHandle + 1);   // "the root is somewhere else now"
        Assert.True(sp.IsChildAgent, "the presence must actually be a child for this test to mean anything");

        afm.QueueAppearanceSave(sp.UUID);

        // Give the 500 ms drain timer several chances. If a write is coming, it arrives well inside this.
        for (int i = 0; i < 12 && avatars.Calls == 0; i++)
            Thread.Sleep(250);

        Assert.Equal(0, avatars.Calls);
    }

    /// <summary>The same presence as a root does write, so the guard above is about the child state and nothing else.</summary>
    [Fact]
    public void A_root_presence_still_writes()
    {
        (AvatarFactoryModule afm, TestScene scene, RecordingAvatarService avatars, ScenePresence sp) = Setup();
        Assert.False(sp.IsChildAgent);

        afm.QueueAppearanceSave(sp.UUID);
        scene.EventManager.TriggerOnRemovePresence(sp.UUID);

        Assert.Equal(1, avatars.Calls);
    }
}
