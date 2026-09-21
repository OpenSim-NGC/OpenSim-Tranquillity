using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S5 Part 2 — the five Bakes-on-Mesh aux channels (leftarm, leftleg, aux1, aux2, aux3). The library has produced
/// them since S0b and nothing has ever exercised them, because no test avatar wears a Universal wearable and
/// neither reference set has one (Ledger Q-11). This drives them with a synthetic Universal instead.
///
/// <para>
/// A synthetic fixture is the honest limit of what can be tested without content. See
/// <see cref="WhatIsStillUntestedForWantOfRealContent"/> for what it does not cover.
/// </para>
/// </summary>
public class BomAuxChannelTests
{
    private readonly ITestOutputHelper _out;
    public BomAuxChannelTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    /// <summary>The five aux channels and the Universal texture slot each is switched on by.</summary>
    private static readonly (BakeChannel Channel, TextureSlot Slot, int Face)[] Aux =
    {
        (BakeChannel.LeftArm, TextureSlot.LeftArmTattoo, 40),
        (BakeChannel.LeftLeg, TextureSlot.LeftLegTattoo, 41),
        (BakeChannel.Aux1,    TextureSlot.Aux1Tattoo,    42),
        (BakeChannel.Aux2,    TextureSlot.Aux2Tattoo,    43),
        (BakeChannel.Aux3,    TextureSlot.Aux3Tattoo,    44),
    };

    /// <summary>A small solid-colour JPEG 2000, so the compositor has something real to draw.</summary>
    private static byte[] Texture(byte r, byte g, byte b)
    {
        var img = new RgbaPlanes(32, 32, hasAlpha: true);
        for (var i = 0; i < img.R.Length; i++) { img.R[i] = r; img.G[i] = g; img.B[i] = b; img.A[i] = 255; }
        return J2kCodec.Encode(img);
    }

    /// <summary>An LLWearable body of the given type carrying the given texture slots.</summary>
    private static string WearableText(WearableKind kind, string name, IReadOnlyDictionary<TextureSlot, UUID> textures)
    {
        var sb = new StringBuilder();
        sb.Append("LLWearable version 22\n").Append(name).Append("\n\n");
        sb.Append("\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n")
          .Append("\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t00082000\n\t\tcreator_id\t11111111-1111-0000-0000-000100bba000\n")
          .Append("\t\towner_id\t11111111-1111-0000-0000-000100bba000\n\t\tlast_owner_id\t00000000-0000-0000-0000-000000000000\n")
          .Append("\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n");
        sb.Append("\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n");
        sb.Append("type ").Append((int)kind).Append('\n');
        sb.Append("parameters 0\n");
        sb.Append("textures ").Append(textures.Count).Append('\n');
        foreach (var (slot, id) in textures) sb.Append((int)slot).Append(' ').Append(id).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// An agent wearing a Shape, a Skin and one Universal that paints the named aux slots. The Shape and Skin are
    /// there because without a body the classic channels produce nothing interesting; the Universal is the point.
    /// </summary>
    private static (FakeAssetService Assets, AvatarWearable[] Wearables, Dictionary<TextureSlot, UUID> Painted)
        Outfit(params TextureSlot[] slots)
    {
        var assets = new FakeAssetService();
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();

        void Wear(WearableKind kind, WearableType type, string name, Dictionary<TextureSlot, UUID> tex)
        {
            var assetId = UUID.Random();
            assets.Put(new AssetBase(assetId, name, (sbyte)(kind is WearableKind.Shape or WearableKind.Skin ? AssetType.Bodypart : AssetType.Clothing), Agent.ToString())
            {
                Data = Encoding.UTF8.GetBytes(WearableText(kind, name, tex)),
            });
            wearables[(int)type].Add(UUID.Random(), assetId);
        }

        Wear(WearableKind.Shape, WearableType.Shape, "Shape", new Dictionary<TextureSlot, UUID>());

        var skinTex = UUID.Random();
        assets.Put(new AssetBase(skinTex, "skin", (sbyte)AssetType.Texture, Agent.ToString()) { Data = Texture(200, 170, 140) });
        Wear(WearableKind.Skin, WearableType.Skin, "Skin", new Dictionary<TextureSlot, UUID>
        {
            [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex,
        });

        var painted = new Dictionary<TextureSlot, UUID>();
        foreach (var s in slots)
        {
            var id = UUID.Random();
            assets.Put(new AssetBase(id, "aux " + s, (sbyte)AssetType.Texture, Agent.ToString()) { Data = Texture(20, 200, 60) });
            painted[s] = id;
        }
        Wear(WearableKind.Universal, WearableType.Universal, "Universal", painted);

        return (assets, wearables, painted);
    }

    // ------------------------------------------------------------------ the library

    /// <summary>An aux channel appears only when a Universal actually paints its slot.</summary>
    [Fact]
    public void TheLibraryProducesAnAuxChannelOnlyForASlotThatIsPainted()
    {
        var compositor = new TexLayerCompositor();

        foreach (var (channel, slot, _) in Aux)
        {
            var (assets, wearables, _) = Outfit(slot);
            var inputs = BakeOrchestrator.Resolve(wearables, null, assets, compositor, 64);
            var results = new SkiaBakeBackend(compositor) { Quality = 0.5 }.Bake(inputs.Request);
            var produced = results.Select(r => r.Channel).ToHashSet();

            Assert.Contains(channel, produced);
            foreach (var (other, _, _) in Aux)
                if (other != channel) Assert.DoesNotContain(other, produced);
        }
    }

    [Fact]
    public void AllFiveAuxChannelsCompositeAndEncodeTogether()
    {
        var compositor = new TexLayerCompositor();
        var (assets, wearables, painted) = Outfit(Aux.Select(a => a.Slot).ToArray());
        Assert.Equal(5, painted.Count);

        var inputs = BakeOrchestrator.Resolve(wearables, null, assets, compositor, 64);
        var results = new SkiaBakeBackend(compositor) { Quality = 0.5 }.Bake(inputs.Request);

        foreach (var (channel, _, _) in Aux)
        {
            var r = Assert.Single(results, x => x.Channel == channel);
            Assert.False(r.NothingDrawn, $"{channel} drew nothing");
            Assert.NotEmpty(r.J2kBytes);
            Assert.Equal(64, J2kCodec.Decode(r.J2kBytes).W);
            Assert.Equal(64, r.InputHash.Length);

            // the Universal's texture actually reached the canvas, not just the aux_base fill: the fixture paints
            // (20,200,60) and the base layer is a flat (128,128,128), so a green-dominant pixel can only be the
            // tattoo. Without this the test would pass on a channel that drew nothing but its base.
            var img = J2kCodec.Decode(r.J2kBytes);
            var green = 0;
            for (var i = 0; i < img.R.Length; i++)
                if (img.G[i] > 150 && img.R[i] < 100 && img.B[i] < 120) green++;
            Assert.True(green > 0, $"{channel}: the Universal's texture never reached the canvas");

            _out.WriteLine($"{channel,-8} {r.J2kBytes.Length,7} bytes  {100.0 * green / img.R.Length,5:F1}% tattoo  hash {r.InputHash[..12]}");
        }
    }

    // ------------------------------------------------------------------ the orchestrator: store and apply

    [Fact]
    public void TheOrchestratorStoresEachAuxChannelAndWritesFaces40To44()
    {
        var compositor = new TexLayerCompositor();
        var (assets, wearables, _) = Outfit(Aux.Select(a => a.Slot).ToArray());
        var avatars = new FakeAvatarService();
        var appearance = new AvatarAppearance();

        var outcome = BakeOrchestrator.Run(Agent, BakeReason.Login, wearables, null, appearance, assets, avatars,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 64, 4, CancellationToken.None);

        foreach (var (channel, _, face) in Aux)
        {
            var c = outcome.Channels.Single(x => x.Channel == channel);
            Assert.True(c.Status == ChannelStatus.Baked, $"{channel}: {c.Status} {c.Reason}");

            // the face index the brief names
            Assert.Equal(face, BakeOrchestrator.FaceOf(channel));
            Assert.Equal(c.AssetId, appearance.Texture.FaceTextures[face].TextureID);

            // stored as a real asset with the ADR-004 marker
            var stored = assets.GetUnchecked(c.AssetId.ToString());
            Assert.NotNull(stored);
            Assert.Equal(BakeOrchestrator.AssetNameFor(Agent, channel), stored.Name);
            Assert.Equal(c.InputHash, stored.Description);
        }

        // and the index carries all five alongside the classic ones
        var index = BakeIndex.Read(avatars, Agent);
        foreach (var (channel, _, _) in Aux)
        {
            Assert.True(index.TryGet(channel, out var bake), $"no index entry for {channel}");
            Assert.Equal(outcome.Channels.Single(x => x.Channel == channel).AssetId, bake.AssetId);
        }
    }

    /// <summary>The whole point of the aux channels: the Robust route serves them under the viewer's own token.</summary>
    [Fact]
    public void TheRobustRouteServesEveryAuxChannel()
    {
        var compositor = new TexLayerCompositor();
        var (assets, wearables, _) = Outfit(Aux.Select(a => a.Slot).ToArray());
        var avatars = new FakeAvatarService();
        var appearance = new AvatarAppearance();

        var outcome = BakeOrchestrator.Run(Agent, BakeReason.Login, wearables, null, appearance, assets, avatars,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 64, 4, CancellationToken.None);

        var svc = new AuxAppearanceService(avatars, assets);

        foreach (var (channel, _, _) in Aux)
        {
            var c = outcome.Channels.Single(x => x.Channel == channel);
            var token = channel.ToString().ToLowerInvariant();

            // the token the viewer sends resolves to this channel (S4's map)
            Assert.Equal(BakeIndex.BakeKey(channel), AppearanceChannels.BakeKeyFor(token));

            var got = svc.GetBake(Agent, token, c.AssetId);
            Assert.True(got is not null, $"{token} should be served");
            Assert.Equal(c.AssetId, got.FullID);
            Assert.Equal(assets.GetUnchecked(c.AssetId.ToString()).Data, got.Data);

            // and a stale UUID is still refused on these channels
            Assert.Null(svc.GetBake(Agent, token, UUID.Random()));
        }
    }

    private sealed class AuxAppearanceService : OpenSim.Services.AvatarService.AppearanceService
    {
        public AuxAppearanceService(IAvatarService a, IAssetService b) : base(new Nini.Config.IniConfigSource(), a, b) { }
    }

    // ------------------------------------------------------------------ the recorded gap

    /// <summary>
    /// What this does <b>not</b> establish, recorded so the gap is not mistaken for coverage:
    ///
    /// <list type="bullet">
    ///   <item><b>Fidelity.</b> There is no reference bake for any aux channel — neither golden set has a
    ///     Universal, so nothing compares these pixels against the LL compositor's. The classic channels are
    ///     diffed against references at 1024; these are only asserted to composite, encode and round-trip.</item>
    ///   <item><b>Real Universal content.</b> The fixture paints flat 32x32 colours through slots the layer sets
    ///     name. It does not exercise a real Universal's parameters, colour drivers, alpha masks, or the
    ///     interaction between a Universal and the classic channels it can also paint (head/upper/lower/skirt/
    ///     hair/eyes universal tattoo slots are untouched here).</item>
    ///   <item><b>The viewer.</b> No LL or Firestorm client has ever rendered a sim-baked aux channel from this
    ///     simulator. Faces 40-44 are asserted from <c>AvatarAppearance.BAKE_INDICES</c>, not observed in-world,
    ///     and whether a viewer requests them at all depends on it having a mesh body bound to those slots.</item>
    ///   <item><b>Multi-Universal outfits.</b> One Universal is worn. Two Universals painting the same aux slot
    ///     is the layered case the classic channels get wrong most often, and it is untested.</item>
    /// </list>
    ///
    /// This test asserts only the two facts that make the gap precise: the reference sets carry no Universal, and
    /// the aux channels therefore have no golden thresholds.
    /// </summary>
    [Fact]
    public void WhatIsStillUntestedForWantOfRealContent()
    {
        var goldenDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden"));

        foreach (var manifest in Directory.GetFiles(goldenDir, "manifest.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(manifest);
            foreach (var (channel, _, _) in Aux)
                Assert.DoesNotContain($"\"{channel.ToString().ToLowerInvariant()}\":", text);
            // each set records these as not set, which is the fact this leans on
            Assert.Contains("notSet", text);
        }
        _out.WriteLine("no reference set carries a Universal, so no aux channel has a fidelity threshold");
    }
}
