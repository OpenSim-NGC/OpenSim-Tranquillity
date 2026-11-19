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
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.AvatarService;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// A bake channel the outfit stops using must go back to "no bake": its baked face holds IMG_DEFAULT_AVATAR
/// (c228d1cf-4b5d-4ba8-84f4-899a0796aa97), its bake-index entry is dropped so the appearance service no longer
/// serves the old bake. The old bake asset itself is left in place.
///
/// <para>
/// Why it matters to every viewer: the official viewer treats a baked slot as defined only while it holds
/// something other than IMG_DEFAULT_AVATAR and IMG_DEFAULT (llvoavatar.cpp:11741-11742, isTextureDefined). The
/// skirt mesh of another avatar is drawn only while the skirt bake slot is defined (llvoavatar.cpp:8113-8125),
/// and a Bakes-on-Mesh face draws whatever its slot holds (llviewerobject.cpp:5185-5194). A slot left holding
/// the previous bake therefore keeps drawing a skirt that was taken off, and keeps painting the old universal
/// layer on every Bakes-on-Mesh face that uses that channel.
/// </para>
/// </summary>
public class UnusedBakeSlotTests
{
    private static readonly UUID Agent = new("5b0e6f43-2a0c-4f5e-9d61-7c3a8e21b9d4");
    private const int Size = 64;

    /// <summary>IMG_DEFAULT_AVATAR (indra_constants.cpp:51).</summary>
    private static readonly UUID ImgDefaultAvatar = new("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");
    /// <summary>IMG_DEFAULT (indra_constants.cpp:43).</summary>
    private static readonly UUID ImgDefault = new("d2114404-dd59-4a4d-8e6c-49359e91bbf0");

    /// <summary>
    /// The official viewer's test for a baked slot, LLVOAvatar::isTextureDefined (llvoavatar.cpp:11741-11742):
    /// <c>return (tex->getID() != IMG_DEFAULT_AVATAR &amp;&amp; tex->getID() != IMG_DEFAULT);</c>
    /// A slot holding c228d1cf counts as empty.
    /// </summary>
    private static bool ViewerSeesSlotDefined(UUID id) => id != ImgDefaultAvatar && id != ImgDefault;

    private static readonly (BakeChannel Channel, TextureSlot Slot, int Face, string Token)[] Aux =
    {
        (BakeChannel.LeftArm, TextureSlot.LeftArmTattoo, 40, "leftarm"),
        (BakeChannel.LeftLeg, TextureSlot.LeftLegTattoo, 41, "leftleg"),
        (BakeChannel.Aux1,    TextureSlot.Aux1Tattoo,    42, "aux1"),
        (BakeChannel.Aux2,    TextureSlot.Aux2Tattoo,    43, "aux2"),
        (BakeChannel.Aux3,    TextureSlot.Aux3Tattoo,    44, "aux3"),
    };

    private sealed class TestableAppearanceService : AppearanceService
    {
        public TestableAppearanceService(IAvatarService avatars, IAssetService assets)
            : base(new IniConfigSource(), avatars, assets) { }
    }

    /// <summary>One agent's world: assets, the avatar service holding the bake index, the appearance and the service that serves bakes.</summary>
    private sealed class Rig
    {
        public readonly FakeAssetService Assets = new();
        public readonly FakeAvatarService Avatars = new();
        public readonly AvatarAppearance Appearance = new();
        public readonly AvatarBakeCompositor Compositor = new();
        public readonly TestableAppearanceService Service;
        private readonly (UUID Item, UUID Asset) m_shape, m_skin;

        public Rig()
        {
            Service = new TestableAppearanceService(Avatars, Assets);
            m_shape = Store(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>());
            var skinTex = Texture(200, 170, 140);
            m_skin = Store(WearableKind.Skin, "Skin", new Dictionary<TextureSlot, UUID>
            {
                [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex,
            });
        }

        public UUID Texture(byte r, byte g, byte b)
        {
            var id = UUID.Random();
            Assets.Put(new AssetBase(id, "tex", (sbyte)AssetType.Texture, Agent.ToString()) { Data = BomAuxChannelTests.Texture(r, g, b) });
            return id;
        }

        /// <summary>Store a wearable asset; returns (item id, asset id).</summary>
        public (UUID Item, UUID Asset) Store(WearableKind kind, string name, IReadOnlyDictionary<TextureSlot, UUID> textures)
        {
            var assetId = UUID.Random();
            Assets.Put(new AssetBase(assetId, name, (sbyte)(kind is WearableKind.Shape or WearableKind.Skin ? AssetType.Bodypart : AssetType.Clothing), Agent.ToString())
            {
                Data = Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(kind, name, textures)),
            });
            return (UUID.Random(), assetId);
        }

        /// <summary>The wearable set: shape and skin always, plus the given extra items.</summary>
        public AvatarWearable[] Outfit(params (WearableType Type, (UUID Item, UUID Asset) Ids)[] extra)
        {
            var w = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
            for (var i = 0; i < w.Length; i++) w[i] = new AvatarWearable();
            w[(int)WearableType.Shape].Add(m_shape.Item, m_shape.Asset);
            w[(int)WearableType.Skin].Add(m_skin.Item, m_skin.Asset);
            foreach (var (type, ids) in extra) w[(int)type].Add(ids.Item, ids.Asset);
            return w;
        }

        public BakeOutcome Bake(AvatarWearable[] wearables, int cofVersion)
            => BakeOrchestrator.Run(Agent, BakeReason.CofChanged, wearables, null, Appearance, Assets, Avatars,
                new SkiaBakeBackend(Compositor) { Quality = 0.5 }, Compositor, Size, cofVersion, CancellationToken.None);

        public UUID Face(int face) => Appearance.Texture.GetFace((uint)face).TextureID;
    }

    private static Dictionary<TextureSlot, UUID> Paint(Rig rig, IEnumerable<TextureSlot> slots)
    {
        var d = new Dictionary<TextureSlot, UUID>();
        foreach (var s in slots) d[s] = rig.Texture(20, 200, 60);
        return d;
    }

    /// <summary>Everything a channel that the outfit stopped using must satisfy after the next bake.</summary>
    private static void AssertSlotReset(Rig rig, BakeOutcome outcome, BakeChannel ch, int face, string token, UUID oldBake)
    {
        // the TextureEntry: back to IMG_DEFAULT_AVATAR, which the viewer reads as "no bake"
        Assert.Equal(ImgDefaultAvatar, rig.Face(face));
        Assert.False(ViewerSeesSlotDefined(rig.Face(face)), $"{ch}: the viewer would still see face {face} as defined");

        // the outcome says so
        var o = outcome.Channels.Single(c => c.Channel == ch);
        Assert.True(o.Status == ChannelStatus.Cleared, $"{ch}: {o.Status} {o.Reason}");

        // the bake index: no entry left for the channel, so the appearance service has nothing to serve
        Assert.False(BakeIndex.Read(rig.Avatars, Agent).TryGet(ch, out _), $"{ch}: bake index still holds an entry");
        Assert.False(rig.Avatars.Records[Agent].ContainsKey(BakeIndex.BakeKey(ch)), $"{ch}: {BakeIndex.BakeKey(ch)} still stored");
        Assert.False(rig.Avatars.Records[Agent].ContainsKey(BakeIndex.HashKey(ch)), $"{ch}: {BakeIndex.HashKey(ch)} still stored");
        Assert.Null(rig.Service.GetBake(Agent, token, oldBake));

        // the old bake asset itself stays: other copies may still name it (see TheOldBakeAssetIsLeftInPlace)
        Assert.NotNull(rig.Assets.GetUnchecked(oldBake.ToString()));
        Assert.DoesNotContain(oldBake, outcome.Superseded);
    }

    // ------------------------------------------------------------------ skirt, slot 19

    [Fact]
    public void ASkirtTakenOffResetsSlot19AndTheServiceStopsServingItsBake()
    {
        var rig = new Rig();
        var skirt = rig.Store(WearableKind.Skirt, "Skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(30, 60, 220) });

        var worn = rig.Bake(rig.Outfit((WearableType.Skirt, skirt)), 1);
        var skirtBake = worn.Channels.Single(c => c.Channel == BakeChannel.Skirt);
        Assert.Equal(ChannelStatus.Baked, skirtBake.Status);
        Assert.Equal(skirtBake.AssetId, rig.Face(19));
        Assert.True(ViewerSeesSlotDefined(rig.Face(19)));
        Assert.NotNull(rig.Service.GetBake(Agent, "skirt", skirtBake.AssetId));

        var off = rig.Bake(rig.Outfit(), 2);

        AssertSlotReset(rig, off, BakeChannel.Skirt, 19, "skirt", skirtBake.AssetId);
    }

    // ------------------------------------------------------------------ universal, slots 40-44

    [Fact]
    public void AUniversalLayerTakenOffResetsSlots40To44AndTheServiceStopsServingThem()
    {
        var rig = new Rig();
        var universal = rig.Store(WearableKind.Universal, "Universal", Paint(rig, Aux.Select(a => a.Slot)));

        var worn = rig.Bake(rig.Outfit((WearableType.Universal, universal)), 1);
        var old = new Dictionary<BakeChannel, UUID>();
        foreach (var (ch, _, face, token) in Aux)
        {
            var c = worn.Channels.Single(x => x.Channel == ch);
            Assert.True(c.Status == ChannelStatus.Baked, $"{ch}: {c.Status} {c.Reason}");
            Assert.Equal(c.AssetId, rig.Face(face));
            Assert.NotNull(rig.Service.GetBake(Agent, token, c.AssetId));
            old[ch] = c.AssetId;
        }

        var off = rig.Bake(rig.Outfit(), 2);

        foreach (var (ch, _, face, token) in Aux)
            AssertSlotReset(rig, off, ch, face, token, old[ch]);
    }

    // ------------------------------------------------------------------ tattoo on the aux channels

    /// <summary>
    /// In the official viewer the five aux tattoo slots belong to the Universal wearable
    /// (llavatarappearancedefines.cpp:74-78), but the bake server switches a channel on for any worn wearable that
    /// carries a texture in that slot (SkiaBakeBackend.ChannelsFor), so a Tattoo wearable that carries them turns
    /// the channels on too. Taking it off must turn them off the same way.
    /// </summary>
    [Fact]
    public void ATattooLayerOnTheAuxChannelsTakenOffResetsSlots40To44()
    {
        var rig = new Rig();
        var tattoo = rig.Store(WearableKind.Tattoo, "Tattoo", Paint(rig, Aux.Select(a => a.Slot)));

        var worn = rig.Bake(rig.Outfit((WearableType.Tattoo, tattoo)), 1);
        var old = new Dictionary<BakeChannel, UUID>();
        foreach (var (ch, _, face, token) in Aux)
        {
            var c = worn.Channels.Single(x => x.Channel == ch);
            Assert.True(c.Status == ChannelStatus.Baked, $"{ch}: {c.Status} {c.Reason}");
            Assert.NotNull(rig.Service.GetBake(Agent, token, c.AssetId));
            old[ch] = c.AssetId;
        }

        var off = rig.Bake(rig.Outfit(), 2);

        foreach (var (ch, _, face, token) in Aux)
            AssertSlotReset(rig, off, ch, face, token, old[ch]);
    }

    // ------------------------------------------------------------------ no false resets

    /// <summary>
    /// Only the channels the outfit stopped using are reset. Here the skirt stays on, the universal layer is
    /// swapped for one painting only left arm and aux 1, and the classic channels are always in use: every
    /// one of those keeps a live bake that the service serves; only left leg, aux 2 and aux 3 are reset.
    /// </summary>
    [Fact]
    public void AChannelStillInUseKeepsItsBake()
    {
        var rig = new Rig();
        var skirt = rig.Store(WearableKind.Skirt, "Skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(30, 60, 220) });
        var full = rig.Store(WearableKind.Universal, "Universal", Paint(rig, Aux.Select(a => a.Slot)));
        var partial = rig.Store(WearableKind.Universal, "Universal 2", Paint(rig, new[] { TextureSlot.LeftArmTattoo, TextureSlot.Aux1Tattoo }));

        var first = rig.Bake(rig.Outfit((WearableType.Skirt, skirt), (WearableType.Universal, full)), 1);
        var second = rig.Bake(rig.Outfit((WearableType.Skirt, skirt), (WearableType.Universal, partial)), 2);

        // Hair is left out: this outfit has no hair wearable, so that channel draws nothing and keeps its face by
        // the existing "nothing drawn" rule, which this change does not touch.
        var stillUsed = new[]
        {
            (BakeChannel.Head, 8, "head"), (BakeChannel.Upper, 9, "upper"), (BakeChannel.Lower, 10, "lower"),
            (BakeChannel.Eyes, 11, "eyes"), (BakeChannel.Skirt, 19, "skirt"),
            (BakeChannel.LeftArm, 40, "leftarm"), (BakeChannel.Aux1, 42, "aux1"),
        };
        foreach (var (ch, face, token) in stillUsed)
        {
            var c = second.Channels.Single(x => x.Channel == ch);
            Assert.True(c.Status is ChannelStatus.Baked or ChannelStatus.Reused, $"{ch}: {c.Status} {c.Reason}");
            Assert.Equal(c.AssetId, rig.Face(face));
            Assert.True(ViewerSeesSlotDefined(rig.Face(face)), $"{ch}: face {face} was reset though still in use");
            Assert.True(BakeIndex.Read(rig.Avatars, Agent).TryGet(ch, out var stored), $"{ch}: index entry lost");
            Assert.Equal(c.AssetId, stored.AssetId);
            Assert.NotNull(rig.Service.GetBake(Agent, token, c.AssetId));
            Assert.NotNull(rig.Assets.GetUnchecked(c.AssetId.ToString()));
        }

        foreach (var (ch, _, face, token) in Aux.Where(a => a.Channel is BakeChannel.LeftLeg or BakeChannel.Aux2 or BakeChannel.Aux3))
            AssertSlotReset(rig, second, ch, face, token, first.Channels.Single(x => x.Channel == ch).AssetId);
    }

    /// <summary>
    /// A skirt that is still worn but whose wearable asset cannot be fetched this time is not "taken off": the
    /// channel fails and keeps the bake it has, as every other failed channel does.
    /// </summary>
    [Fact]
    public void AWornSkirtWhoseAssetIsMissingKeepsItsBake()
    {
        var rig = new Rig();
        var skirt = rig.Store(WearableKind.Skirt, "Skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(30, 60, 220) });
        var worn = rig.Bake(rig.Outfit((WearableType.Skirt, skirt)), 1);
        var skirtBake = worn.Channels.Single(c => c.Channel == BakeChannel.Skirt).AssetId;

        Assert.True(rig.Assets.Remove(skirt.Asset.ToString()));
        var again = rig.Bake(rig.Outfit((WearableType.Skirt, skirt)), 2);

        Assert.Equal(ChannelStatus.Failed, again.Channels.Single(c => c.Channel == BakeChannel.Skirt).Status);
        Assert.Equal(skirtBake, rig.Face(19));
        Assert.NotNull(rig.Service.GetBake(Agent, "skirt", skirtBake));
    }

    /// <summary>
    /// A worn Tattoo painting the aux channels whose wearable asset cannot be fetched this time is not "taken off".
    /// Its channels are not failed by kind (the aux slots belong to the Universal type), so the guard is that a run
    /// with any unusable wearable clears nothing.
    /// </summary>
    [Fact]
    public void AWornTattooOnTheAuxChannelsWhoseAssetIsMissingKeepsItsBakes()
    {
        var rig = new Rig();
        var tattoo = rig.Store(WearableKind.Tattoo, "Tattoo", Paint(rig, Aux.Select(a => a.Slot)));
        var worn = rig.Bake(rig.Outfit((WearableType.Tattoo, tattoo)), 1);

        Assert.True(rig.Assets.Remove(tattoo.Asset.ToString()));
        var again = rig.Bake(rig.Outfit((WearableType.Tattoo, tattoo)), 2);

        foreach (var (ch, _, face, token) in Aux)
        {
            var bake = worn.Channels.Single(c => c.Channel == ch).AssetId;
            Assert.NotEqual(ChannelStatus.Cleared, again.Channels.Single(c => c.Channel == ch).Status);
            Assert.Equal(bake, rig.Face(face));
            Assert.NotNull(rig.Service.GetBake(Agent, token, bake));
            Assert.NotNull(rig.Assets.GetUnchecked(bake.ToString()));
        }
    }

    /// <summary>
    /// If the avatar service cannot remove the index entry, the channel is still reported Cleared and the entry
    /// stays; the next bake finds the entry and removes it.
    /// </summary>
    [Fact]
    public void WhenTheIndexEntryCannotBeRemovedTheOldBakeIsKeptAndTheNextBakeRetries()
    {
        var rig = new Rig();
        var skirt = rig.Store(WearableKind.Skirt, "Skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(30, 60, 220) });
        var skirtBake = rig.Bake(rig.Outfit((WearableType.Skirt, skirt)), 1).Channels.Single(c => c.Channel == BakeChannel.Skirt).AssetId;

        rig.Avatars.FailRemoveItems = true;
        var off = rig.Bake(rig.Outfit(), 2);
        Assert.Equal(ImgDefaultAvatar, rig.Face(19));
        Assert.Equal(ChannelStatus.Cleared, off.Channels.Single(c => c.Channel == BakeChannel.Skirt).Status);
        Assert.True(BakeIndex.Read(rig.Avatars, Agent).TryGet(BakeChannel.Skirt, out _));
        Assert.NotNull(rig.Assets.GetUnchecked(skirtBake.ToString()));
        Assert.DoesNotContain(skirtBake, off.Superseded);

        rig.Avatars.FailRemoveItems = false;
        var retry = rig.Bake(rig.Outfit(), 3);
        AssertSlotReset(rig, retry, BakeChannel.Skirt, 19, "skirt", skirtBake);
    }

    /// <summary>
    /// The asset of a cleared bake is not deleted. Its id can live on outside this avatar's TextureEntry: a script
    /// that saves the avatar's appearance to a notecard writes every baked face, the skirt and aux slots included
    /// (AvatarAppearance.PackForNotecard, called by the OSSL save-appearance functions), and an NPC created from that
    /// notecard uses those ids. Deleting the asset would break them; clearing the slot and the index entry is enough
    /// to stop the avatar showing it and the appearance service serving it.
    /// </summary>
    [Fact]
    public void TheOldBakeAssetIsLeftInPlace()
    {
        var rig = new Rig();
        var skirt = rig.Store(WearableKind.Skirt, "Skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(30, 60, 220) });
        var skirtBake = rig.Bake(rig.Outfit((WearableType.Skirt, skirt)), 1).Channels.Single(c => c.Channel == BakeChannel.Skirt).AssetId;

        // a saved appearance taken while the skirt is worn names the skirt bake
        var saved = rig.Appearance.PackForNotecard();
        Assert.Contains(saved["textures"] as OpenMetaverse.StructuredData.OSDArray, t => t.AsUUID() == skirtBake);

        var off = rig.Bake(rig.Outfit(), 2);

        Assert.Equal(ImgDefaultAvatar, rig.Face(19));
        Assert.Null(rig.Service.GetBake(Agent, "skirt", skirtBake));
        Assert.NotNull(rig.Assets.GetUnchecked(skirtBake.ToString()));
        Assert.DoesNotContain("delete " + skirtBake, rig.Assets.Ops);
        Assert.Empty(off.Superseded);
    }

    /// <summary>
    /// Re-baking a channel does not delete the bake it replaces either, for the same reason: a saved appearance
    /// notecard (and an NPC made from it) still names the old id. Stock OpenSim never deletes bakes. The index still
    /// moves to the new bake, so the appearance service serves only the current one.
    /// </summary>
    [Fact]
    public void ARebakeLeavesThePreviousBakeAssetInPlace()
    {
        var rig = new Rig();
        var blue = rig.Store(WearableKind.Skirt, "Blue skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(30, 60, 220) });
        var red = rig.Store(WearableKind.Skirt, "Red skirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.Skirt] = rig.Texture(220, 40, 30) });

        var oldBake = rig.Bake(rig.Outfit((WearableType.Skirt, blue)), 1).Channels.Single(c => c.Channel == BakeChannel.Skirt).AssetId;
        var saved = rig.Appearance.PackForNotecard();
        Assert.Contains(saved["textures"] as OpenMetaverse.StructuredData.OSDArray, t => t.AsUUID() == oldBake);

        var second = rig.Bake(rig.Outfit((WearableType.Skirt, red)), 2);
        var skirt = second.Channels.Single(c => c.Channel == BakeChannel.Skirt);
        Assert.Equal(ChannelStatus.Baked, skirt.Status);
        Assert.NotEqual(oldBake, skirt.AssetId);
        Assert.Equal(skirt.AssetId, rig.Face(19));

        // the old bake is still in the asset service and was never deleted
        Assert.NotNull(rig.Assets.GetUnchecked(oldBake.ToString()));
        Assert.DoesNotContain("delete " + oldBake, rig.Assets.Ops);
        Assert.Empty(second.Superseded);

        // the index moved on: the service serves the new bake and refuses the old one
        Assert.NotNull(rig.Service.GetBake(Agent, "skirt", skirt.AssetId));
        Assert.Null(rig.Service.GetBake(Agent, "skirt", oldBake));
    }

    /// <summary>A first bake with nothing on the extra channels has nothing to clear: those channels stay Skipped.</summary>
    [Fact]
    public void AChannelThatNeverHadABakeIsSkippedNotCleared()
    {
        var rig = new Rig();
        var outcome = rig.Bake(rig.Outfit(), 1);

        foreach (var ch in new[] { BakeChannel.Skirt, BakeChannel.LeftArm, BakeChannel.LeftLeg, BakeChannel.Aux1, BakeChannel.Aux2, BakeChannel.Aux3 })
        {
            Assert.Equal(ChannelStatus.Skipped, outcome.Channels.Single(c => c.Channel == ch).Status);
            Assert.Equal(ImgDefaultAvatar, rig.Face(BakeOrchestrator.FaceOf(ch)));
        }
    }

    // ------------------------------------------------------------------ the viewer's rule

    /// <summary>
    /// The value written to an unused slot is the one the official viewer reads as "no bake":
    /// llvoavatar.cpp:11741-11742 returns false for IMG_DEFAULT_AVATAR (and IMG_DEFAULT). It is also the value a
    /// fresh appearance starts every face at, so a reset slot looks exactly like a slot that was never baked.
    /// </summary>
    [Fact]
    public void ASlotHoldingC228d1cfCountsAsEmptyToTheOfficialViewer()
    {
        Assert.False(ViewerSeesSlotDefined(ImgDefaultAvatar));
        Assert.False(ViewerSeesSlotDefined(ImgDefault));
        Assert.True(ViewerSeesSlotDefined(UUID.Random()));

        Assert.Equal(ImgDefaultAvatar, BakeConstants.DefaultAvatarTexture);
        Assert.Equal(ImgDefaultAvatar, AppearanceManager.DEFAULT_AVATAR_TEXTURE);
        Assert.Equal(ImgDefaultAvatar, new AvatarAppearance().Texture.GetFace(19).TextureID);
    }
}
