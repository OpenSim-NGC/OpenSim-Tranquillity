/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// A bake whose presence closes before or while it runs.
///
/// <para>
/// Logging out with an appearance save still queued is the common way in. <c>Scene.RemoveClient</c> raises
/// <c>OnRemovePresence</c>; <c>AvatarFactoryModule.FlushAppearanceSaveOnClose</c> runs the pending save, whose
/// <c>TriggerAvatarAppearanceChanged</c> queues a change bake on the thread pool; the region's
/// <c>ServerSideBakingRegion.Forget</c> drops the agent; and <c>RemoveClient</c> then disposes the presence,
/// which sets <c>ScenePresence.Appearance</c> to null. The queued bake runs after some or all of that.
/// </para>
/// </summary>
// Process-wide state: SceneHelpers sets MainConsole.Instance and adds the scene to SceneManager. No other class in
// this project touches either, and the tests within one class run one at a time.
public class BakeAfterPresenceClosedTests
{
    /// <summary>
    /// The project's <see cref="FakeAvatarService"/> with a gate on <see cref="GetAvatar"/>, which the bake calls to
    /// read its index (<c>BakeIndex.Read</c>) on the bake's own thread. Closed, the gate holds the bake there so
    /// the test can close the presence part-way through.
    /// </summary>
    private sealed class GatedAvatarService : IAvatarService
    {
        private readonly FakeAvatarService m_inner = new();
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Open = new(true);

        public AvatarData GetAvatar(UUID userID)
        {
            Entered.Set();
            Open.Wait(TimeSpan.FromSeconds(60));
            return m_inner.GetAvatar(userID);
        }

        public bool SetAvatar(UUID userID, AvatarData avatar) => m_inner.SetAvatar(userID, avatar);
        public AvatarAppearance GetAppearance(UUID userID) => m_inner.GetAppearance(userID);
        public bool SetAppearance(UUID userID, AvatarAppearance appearance) => m_inner.SetAppearance(userID, appearance);
        public bool ResetAvatar(UUID userID) => m_inner.ResetAvatar(userID);
        public bool SetItems(UUID userID, string[] names, string[] values) => m_inner.SetItems(userID, names, values);
        public bool RemoveItems(UUID userID, string[] names) => m_inner.RemoveItems(userID, names);
    }

    private sealed record Rig(Scene Scene, ServerSideBakingModule Module, ServerSideBakingRegion Region,
        GatedAvatarService Avatars, ScenePresence Presence);

    /// <summary>
    /// A flag-on region with one root agent wearing a Shape and a Skin. The agent is made root before the module
    /// is added, so no login bake runs alongside the one under test.
    /// </summary>
    private static Rig Setup()
    {
        var scene = new SceneHelpers().SetupScene();
        var avatars = new GatedAvatarService();
        scene.RegisterModuleInterface<IAvatarService>(avatars);

        var sp = SceneHelpers.AddScenePresence(scene, UUID.Random());
        Wear(scene, sp);

        var config = new IniConfigSource();
        var section = config.AddConfig(ServerSideBakingModule.ConfigSection);
        section.Set("ServerSideBaking", "true");
        section.Set("BakeSize", "512");
        var module = new ServerSideBakingModule();
        SceneHelpers.SetupSceneModules(scene, config, module);

        var region = module.RegionOf(scene);
        Assert.True(region.ServerSideBakingEnabled);
        return new Rig(scene, module, region, avatars, sp);
    }

    private static void Wear(Scene scene, ScenePresence sp)
    {
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();

        void Put(UUID id, string name, AssetType type, byte[] data)
            => scene.AssetService.Store(new AssetBase(id, name, (sbyte)type, sp.UUID.ToString()) { Data = data });

        var skinTex = UUID.Random();
        Put(skinTex, "skin", AssetType.Texture, BomAuxChannelTests.Texture(200, 170, 140));

        var shape = UUID.Random();
        Put(shape, "Shape", AssetType.Bodypart,
            Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>())));
        wearables[(int)WearableType.Shape].Add(UUID.Random(), shape);

        var skin = UUID.Random();
        Put(skin, "Skin", AssetType.Bodypart, Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Skin, "Skin",
            new Dictionary<TextureSlot, UUID>
            {
                [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex,
            })));
        wearables[(int)WearableType.Skin].Add(UUID.Random(), skin);

        sp.Appearance.Wearables = wearables;
    }

    /// <summary>The control: with the presence still here, the bake is recorded for it.</summary>
    [Fact]
    public async Task ABakeForAPresenceThatStaysIsRecordedForIt()
    {
        var rig = Setup();

        var outcome = await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        Assert.True(outcome.Count(ChannelStatus.Baked) > 0, "the outfit baked nothing");
        Assert.NotEqual(-1, rig.Region.BakedCofVersion(rig.Presence.UUID));
    }

    /// <summary>The bake starts after the presence has closed: there is no appearance left to bake from.</summary>
    [Fact]
    public async Task ABakeQueuedForAPresenceThatHasClosedEndsQuietly()
    {
        var rig = Setup();
        var agent = rig.Presence.UUID;

        rig.Scene.RemoveClient(agent, false);
        Assert.Null(rig.Presence.Appearance);   // what ScenePresence.Dispose leaves

        var outcome = await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        Assert.Empty(outcome.Channels);
        Assert.False(rig.Avatars.Entered.IsSet, "a bake ran for a presence with no appearance");
        Assert.Equal(-1, rig.Region.BakedCofVersion(agent));
    }

    /// <summary>
    /// The presence closes while its bake runs. The bake finishes and is kept - stored and indexed in the avatar
    /// service, where the next login finds it - but nothing is written back to the presence or the region: the
    /// region forgot the agent on close and must not be told of a bake again.
    /// </summary>
    [Fact]
    public async Task APresenceThatClosesWhileItsBakeRunsHasNothingWrittenBack()
    {
        var rig = Setup();
        var agent = rig.Presence.UUID;

        rig.Avatars.Open.Reset();
        var bake = Task.Run(() => rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None));
        Assert.True(rig.Avatars.Entered.Wait(TimeSpan.FromSeconds(60)), "the bake never reached its index read");

        rig.Scene.RemoveClient(agent, false);
        Assert.Null(rig.Presence.Appearance);
        rig.Avatars.Open.Set();

        var outcome = await bake;

        Assert.True(outcome.Count(ChannelStatus.Baked) > 0, "the outfit baked nothing");
        Assert.True(outcome.IndexWritten);
        Assert.Equal(-1, rig.Region.BakedCofVersion(agent));
    }
}
