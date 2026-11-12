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
/// The login bake reads the Current Outfit folder before it bakes.
///
/// <para>
/// A presence arrives with the wearables of the stored avatar record, and the record can be stale: here it still
/// lists an alpha that the folder no longer links. The viewer's rebake request at login does apply the folder, but
/// the bake it leads to follows an appearance save about 5 s later and goes out under the same COF version as the
/// login appearance, which viewers drop as stale (the LL viewer's <c>LLVOAvatar::processAvatarAppearance</c>). So the
/// first appearance sent at login is the one the viewer keeps, and it must already be the folder's.
/// </para>
/// </summary>
// Process-wide state: SceneHelpers sets MainConsole.Instance and adds the scene to SceneManager (as in
// BakeAfterPresenceClosedTests), and the fallback test swaps the logger factory through LogCapture, so this class
// shares the log-capture collection with the other classes that swap it.
[Collection("ssb-log-capture")]
public class LoginBakeFromCofTests
{
    // Fixed ids, so two rigs bake from identical inputs and their input hashes can be compared.
    private static readonly UUID Agent = new("5d0b1f7e-3c2a-4e7b-9a61-0f2d8c4b7a13");
    private static readonly UUID SkinTexture = new("8f3e2a10-6b4d-4c1e-a7f2-1d9b5e6c3a01");
    private static readonly UUID AlphaTexture = new("2c7a9e41-0d5b-4f38-b6e2-7a1c3f9d8e02");
    private static readonly UUID ShapeAsset = new("b41d6c2e-9a7f-4e03-8c5b-3e2f1a0d9c03");
    private static readonly UUID SkinAsset = new("e9a2c7d4-1f6b-4b85-9d3e-6c0a2b7f1e04");
    private static readonly UUID AlphaAsset = new("71f4b8e2-5c3a-4d96-a0e1-9b2d6f3c8a05");
    private static readonly UUID ShapeItem = new("0a6e3d9b-2f1c-4a87-b5d4-8e7c1f2a6b06");
    private static readonly UUID SkinItem = new("c3b8f1a7-6e2d-4c09-9a5f-1d4e7b3c2a07");
    private static readonly UUID AlphaItem = new("94d2e6c1-8b3f-4a5e-b7c0-2f6a9d1e4b08");

    private const int Shape = (int)WearableType.Shape, Skin = (int)WearableType.Skin, Alpha = (int)WearableType.Alpha;

    /// <summary>What the presence looked like each time an appearance was sent to its own viewer.</summary>
    private sealed record SelfAppearance(int AlphaWorn, UUID LowerBake);

    /// <summary>A test client that records every appearance sent to its own agent.</summary>
    private sealed class RecordingClient : TestClient
    {
        public readonly List<SelfAppearance> Sent = new();
        public ScenePresence Presence;

        public RecordingClient(AgentCircuitData data, Scene scene) : base(data, scene) { }

        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover, int cofVersion)
        {
            var app = Presence?.Appearance;
            if (agentID != AgentId || app is null) return;
            var lower = app.Texture.FaceTextures[BakeOrchestrator.FaceOf(BakeChannel.Lower)]?.TextureID ?? UUID.Zero;
            lock (Sent) Sent.Add(new SelfAppearance(app.Wearables[Alpha].Count, lower));
        }
    }

    private sealed record Rig(Scene Scene, ServerSideBakingModule Module, ScenePresence Presence, RecordingClient Client);

    /// <summary>
    /// A flag-on region and one root agent whose stored record wears <paramref name="recordTypes"/>. The agent's
    /// inventory holds a shape, a skin and an alpha item; the Current Outfit folder links <paramref name="cofTypes"/>.
    /// With <paramref name="withInventory"/> false the agent has no inventory at all, so there is no folder to read.
    /// The agent is made root before the module is added, so no login bake runs on its own.
    /// </summary>
    private static Rig Setup(int[] recordTypes, int[] cofTypes, bool withInventory = true)
    {
        var scene = new SceneHelpers().SetupScene();
        scene.RegisterModuleInterface<IAvatarService>(new FakeAvatarService());

        StoreAssets(scene);
        if (withInventory)
        {
            UserAccountHelpers.CreateUserWithInventory(scene, Agent);
            var inventory = scene.InventoryService;
            var root = inventory.GetRootFolder(Agent);
            var cof = inventory.GetFolderForType(Agent, FolderType.CurrentOutfit);
            Assert.NotNull(cof);

            AddItem(inventory, root.ID, ShapeItem, ShapeAsset, "Shape", AssetType.Bodypart, Shape);
            AddItem(inventory, root.ID, SkinItem, SkinAsset, "Skin", AssetType.Bodypart, Skin);
            AddItem(inventory, root.ID, AlphaItem, AlphaAsset, "Alpha", AssetType.Clothing, Alpha);
            foreach (var type in cofTypes)
                AddLink(inventory, cof.ID, ItemOf(type), type);
        }

        var data = SceneHelpers.GenerateAgentData(Agent);
        var client = new RecordingClient(data, scene);
        var sp = SceneHelpers.AddScenePresence(scene, client, data);
        client.Presence = sp;

        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();
        foreach (var type in recordTypes)
            wearables[type].Add(ItemOf(type), AssetOf(type));
        sp.Appearance.Wearables = wearables;

        var config = new IniConfigSource();
        var section = config.AddConfig(ServerSideBakingModule.ConfigSection);
        section.Set("ServerSideBaking", "true");
        section.Set("BakeSize", "512");
        var module = new ServerSideBakingModule();
        SceneHelpers.SetupSceneModules(scene, config, module);
        Assert.True(module.RegionOf(scene).ServerSideBakingEnabled);

        lock (client.Sent) client.Sent.Clear();
        return new Rig(scene, module, sp, client);
    }

    private static UUID ItemOf(int type) => type == Shape ? ShapeItem : type == Skin ? SkinItem : AlphaItem;
    private static UUID AssetOf(int type) => type == Shape ? ShapeAsset : type == Skin ? SkinAsset : AlphaAsset;

    private static void StoreAssets(Scene scene)
    {
        void Put(UUID id, string name, AssetType type, byte[] data)
            => scene.AssetService.Store(new AssetBase(id, name, (sbyte)type, Agent.ToString()) { Data = data });

        Put(SkinTexture, "skin", AssetType.Texture, BomAuxChannelTests.Texture(200, 170, 140));
        Put(AlphaTexture, "alpha", AssetType.Texture, BomAuxChannelTests.Texture(0, 0, 0));
        Put(ShapeAsset, "Shape", AssetType.Bodypart,
            Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>())));
        Put(SkinAsset, "Skin", AssetType.Bodypart, Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Skin, "Skin",
            new Dictionary<TextureSlot, UUID>
            {
                [TextureSlot.HeadBodypaint] = SkinTexture, [TextureSlot.UpperBodypaint] = SkinTexture, [TextureSlot.LowerBodypaint] = SkinTexture,
            })));
        Put(AlphaAsset, "Alpha", AssetType.Clothing, Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Alpha, "Alpha",
            new Dictionary<TextureSlot, UUID> { [TextureSlot.LowerAlpha] = AlphaTexture, [TextureSlot.UpperAlpha] = AlphaTexture })));
    }

    private static void AddItem(IInventoryService inventory, UUID folder, UUID id, UUID asset, string name, AssetType assetType, int wearableType)
    {
        Assert.True(inventory.AddItem(new InventoryItemBase(id, Agent)
        {
            Name = name, Folder = folder, AssetID = asset, AssetType = (int)assetType,
            InvType = (int)InventoryType.Wearable, Flags = (uint)wearableType,
        }));
    }

    private static void AddLink(IInventoryService inventory, UUID cof, UUID target, int wearableType)
    {
        Assert.True(inventory.AddItem(new InventoryItemBase(UUID.Random(), Agent)
        {
            Name = "link", Folder = cof, AssetID = target, AssetType = (int)AssetType.Link,
            InvType = (int)InventoryType.Wearable, Flags = (uint)wearableType,
        }));
    }

    private static string LowerHash(BakeOutcome o) => o.Channels.Single(c => c.Channel == BakeChannel.Lower).InputHash;

    /// <summary>
    /// The case seen in practice: the stored record still wears an alpha the Current Outfit does not link, at the
    /// same COF version. The first appearance the login sends is baked without it.
    /// </summary>
    [Fact]
    public async Task TheFirstAppearanceAtLoginIsBakedFromTheCurrentOutfitNotTheStaleRecord()
    {
        // control: a record that already matches the folder
        var clean = Setup(recordTypes: new[] { Shape, Skin }, cofTypes: new[] { Shape, Skin });
        var expected = await clean.Module.LoginBakeAsync(clean.Presence, CancellationToken.None);
        Assert.True(expected.Count(ChannelStatus.Baked) > 0, "the control outfit baked nothing");

        var stale = Setup(recordTypes: new[] { Shape, Skin, Alpha }, cofTypes: new[] { Shape, Skin });
        var outcome = await stale.Module.LoginBakeAsync(stale.Presence, CancellationToken.None);

        // the bake composited the folder's outfit: the same inputs as the clean record
        Assert.Equal(LowerHash(expected), LowerHash(outcome));
        Assert.Equal(0, stale.Presence.Appearance.Wearables[Alpha].Count);

        // and that bake is what the first appearance to the agent's own viewer carried
        SelfAppearance first;
        lock (stale.Client.Sent) first = stale.Client.Sent.FirstOrDefault();
        Assert.NotNull(first);
        Assert.Equal(0, first.AlphaWorn);
        Assert.Equal(outcome.Channels.Single(c => c.Channel == BakeChannel.Lower).AssetId, first.LowerBake);
    }

    /// <summary>A record that already matches the folder is baked as it is: the read changes nothing.</summary>
    [Fact]
    public async Task ARecordThatMatchesTheCurrentOutfitIsLeftAsItIs()
    {
        var rig = Setup(recordTypes: new[] { Shape, Skin, Alpha }, cofTypes: new[] { Shape, Skin, Alpha });

        var outcome = await rig.Module.LoginBakeAsync(rig.Presence, CancellationToken.None);

        Assert.True(outcome.Count(ChannelStatus.Baked) > 0, "the outfit baked nothing");
        Assert.Equal(1, rig.Presence.Appearance.Wearables[Alpha].Count);
        Assert.Equal(AlphaItem, rig.Presence.Appearance.Wearables[Alpha][0].ItemID);
    }

    /// <summary>
    /// With no Current Outfit folder to read, the login bake does what it did before: it bakes the record's
    /// wearables. One warning says so.
    /// </summary>
    [Fact]
    public async Task WithNoCurrentOutfitToReadTheLoginBakeUsesTheRecordAndLogsOnce()
    {
        var rig = Setup(recordTypes: new[] { Shape, Skin, Alpha }, cofTypes: Array.Empty<int>(), withInventory: false);

        BakeOutcome outcome;
        List<string> warnings;
        using (var log = new LogCapture())
        {
            outcome = await rig.Module.LoginBakeAsync(rig.Presence, CancellationToken.None);
            warnings = log.Messages.Where(m => m.Contains("could not read the Current Outfit", StringComparison.Ordinal)).ToList();
        }

        Assert.True(outcome.Count(ChannelStatus.Baked) > 0, "the outfit baked nothing");
        Assert.Equal(1, rig.Presence.Appearance.Wearables[Alpha].Count);
        Assert.Single(warnings);
    }
}
