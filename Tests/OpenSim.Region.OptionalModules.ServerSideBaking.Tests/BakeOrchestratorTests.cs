using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// Seam tests for <see cref="BakeOrchestrator"/> (S1 Part 2): no Scene, no ScenePresence, a fake asset service.
/// Tests 1 and 3 use Truly Bazar's golden fixtures (fetched, never committed) and skip when they are absent.
/// </summary>
public class BakeOrchestratorTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    private static string FixtureDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden", "truly-stock", "fixtures"));

    // xunit 2.9 has no dynamic skip: absent fixtures make tests 1 and 3 vacuous passes that say so on the console.
    private const string SkipNote = "SKIPPED: golden fixtures not fetched (Source/OpenSimNGC.Appearance.Baking.Tests/Golden/truly-stock/fixtures)";

    private static bool FixturesPresent => File.Exists(Path.Combine(FixtureDir(), "avatar.json"));

    /// <summary>Loads every fixture file as an asset and the avatar's wearable table + visual params.</summary>
    private static (FakeAssetService assets, AvatarWearable[] wearables, byte[] visualParams) LoadFixtures()
    {
        var dir = FixtureDir();
        var assets = new FakeAssetService();
        foreach (var f in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(f);
            sbyte type = ext switch { ".bodypart" => (sbyte)AssetType.Bodypart, ".clothing" => (sbyte)AssetType.Clothing, ".j2c" => (sbyte)AssetType.Texture, _ => -1 };
            if (type < 0) continue;
            var id = Path.GetFileNameWithoutExtension(f);
            assets.Put(new AssetBase(new UUID(id), id, type, Agent.ToString()) { Data = File.ReadAllBytes(f) });
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "avatar.json")));
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();
        foreach (var w in doc.RootElement.GetProperty("wearables").EnumerateArray())
            wearables[w.GetProperty("type").GetInt32()].Add(new UUID(w.GetProperty("itemId").GetString()), new UUID(w.GetProperty("assetId").GetString()));
        var vp = doc.RootElement.GetProperty("visualParams").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        return (assets, wearables, vp);
    }

    private static FidelityReport Empty => new(Array.Empty<string>(), Array.Empty<UUID>(), Array.Empty<string>(), Array.Empty<string>());

    private static UUID[] Faces(AvatarAppearance a)
        => Enumerable.Range(0, (int)AvatarAppearance.TEXTURE_COUNT).Select(i => a.Texture.FaceTextures[i]?.TextureID ?? UUID.Zero).ToArray();

    // ------------------------------------------------------------------ 1. resolver

    [Fact]
    public void Resolve_GoldenFixtures_Yields7WearablesAnd7Textures()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var (assets, wearables, vp) = LoadFixtures();
        var compositor = new TexLayerCompositor();

        var r = BakeOrchestrator.Resolve(wearables, vp, assets, compositor, 512);

        Assert.Empty(r.Failures);
        Assert.Equal(7, r.Request.Wearables.Count);
        Assert.Equal(7, r.Request.Textures.Count);
        Assert.Equal(512, r.Request.BakeSize);
        Assert.All(r.Request.Wearables, w => Assert.StartsWith("LLWearable", w.RawText));
        // every texture the wearables reference (other than the default) was fetched, once
        var referenced = r.Request.Wearables.Select(w => WearableParser.Parse(w.RawText))
            .SelectMany(p => p.Textures.Values).Where(id => !id.IsZero() && id != BakeConstants.DefaultAvatarTexture).ToHashSet();
        Assert.Equal(referenced, r.Request.Textures.Keys.ToHashSet());
        // the presence's VisualParams overlay decoded through the parameter table (or one note saying why not)
        Assert.True(r.Request.VisualParams.Count > 0 || r.Notes.Count == 1, string.Join("; ", r.Notes));
    }

    // ------------------------------------------------------------------ 2. store + TE

    [Fact]
    public void StoreAndApply_FiveResults_StoresFiveAssetsAndWritesExactlyThoseFaces()
    {
        var assets = new FakeAssetService();
        var appearance = new AvatarAppearance();
        var before = Faces(appearance);
        var inputs = new BakeOrchestrator.ResolvedInputs(
            new BakeRequest(Array.Empty<WearableInput>(), new Dictionary<int, float>(), new Dictionary<UUID, TextureInput>(), 512),
            Array.Empty<BakeOrchestrator.InputFailure>(), Array.Empty<string>());
        var channels = new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair };
        var results = channels.Select(ch => new BakeResult(ch, Encoding.ASCII.GetBytes("j2k-" + ch), "hash-" + ch, Empty)).ToList();

        var outcomes = BakeOrchestrator.StoreAndApply(results, inputs, Agent, assets, appearance);

        // five assets, right name / description / flags / creator
        Assert.Equal(5, assets.Stored.Count);
        foreach (var ch in channels)
        {
            var a = Assert.Single(assets.Stored, s => s.Name == $"bake:{Agent}:{ch.ToString().ToLowerInvariant()}");
            Assert.Equal("hash-" + ch, a.Description);
            Assert.Equal((sbyte)AssetType.Texture, a.Type);
            Assert.False(a.Temporary);
            Assert.False(a.Local);
            Assert.Equal(Agent.ToString(), a.Metadata.CreatorID);
            Assert.Equal(Encoding.ASCII.GetBytes("j2k-" + ch), a.Data);
        }
        // outcomes: those five Baked with the stored id and hash, the other six Skipped
        Assert.Equal(11, outcomes.Count);
        foreach (var o in outcomes)
        {
            if (channels.Contains(o.Channel))
            {
                Assert.Equal(ChannelStatus.Baked, o.Status);
                Assert.Equal("hash-" + o.Channel, o.InputHash);
                Assert.Contains(assets.Stored, s => s.FullID == o.AssetId);
            }
            else Assert.Equal(ChannelStatus.Skipped, o.Status);
        }
        // exactly faces 8, 9, 10, 11, 20 written; 19 (skirt), 40-44 and every other face untouched
        var written = new[] { 8, 9, 10, 11, 20 };
        var after = Faces(appearance);
        for (var i = 0; i < before.Length; i++)
        {
            if (written.Contains(i))
            {
                var ch = channels[Array.IndexOf(written, i)];
                Assert.Equal(outcomes.Single(o => o.Channel == ch).AssetId, after[i]);
                Assert.NotEqual(before[i], after[i]);
            }
            else Assert.Equal(before[i], after[i]);
        }
        Assert.Equal(before[19], after[19]);
    }

    // ------------------------------------------------------------------ 3. missing texture

    [Fact]
    public void Run_MissingEyesTexture_FailsEyesAndBakesTheRest()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var (assets, wearables, vp) = LoadFixtures();
        var compositor = new TexLayerCompositor();
        // the eyes wearable's Eyes-slot texture goes missing. (Not the hair texture: in this fixture set the hair
        // and the shoes share one texture id, so its absence correctly fails Hair *and* Lower.)
        var eyesAsset = assets.Get(wearables[(int)WearableType.Eyes][0].AssetID.ToString());
        var eyes = WearableParser.Parse(Encoding.UTF8.GetString(eyesAsset.Data));
        var eyesTex = eyes.Textures[TextureSlot.EyesIris];
        Assert.True(assets.Remove(eyesTex.ToString()), "fixture set should contain the eyes texture");
        var appearance = new AvatarAppearance();
        var before = Faces(appearance);

        var outcome = BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, appearance, assets,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 128, CancellationToken.None);

        var byCh = outcome.Channels.ToDictionary(c => c.Channel);
        Assert.Equal(ChannelStatus.Failed, byCh[BakeChannel.Eyes].Status);
        Assert.Contains(eyesTex.ToString(), byCh[BakeChannel.Eyes].Reason);
        Assert.Equal(UUID.Zero, byCh[BakeChannel.Eyes].AssetId);
        var after = Faces(appearance);
        Assert.Equal(before[11], after[11]);
        foreach (var ch in new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Hair })
        {
            Assert.True(byCh[ch].Status == ChannelStatus.Baked, $"{ch}: {byCh[ch].Status} {byCh[ch].Reason}");
            Assert.NotEqual(UUID.Zero, byCh[ch].AssetId);
            Assert.NotEmpty(byCh[ch].InputHash);
            Assert.Equal(byCh[ch].AssetId, after[BakeOrchestrator.FaceOf(ch)]);
            var stored = assets.Get(byCh[ch].AssetId.ToString());
            Assert.NotNull(stored);
            Assert.Equal(byCh[ch].InputHash, stored.Description);
            Assert.True(stored.Data.Length > 0);
        }
        Assert.Equal(4, outcome.Count(ChannelStatus.Baked));
        Assert.Equal(1, outcome.Count(ChannelStatus.Failed));
        Assert.Equal(6, outcome.Count(ChannelStatus.Skipped));
        Assert.Equal(4, assets.Stored.Count);
    }

    // ------------------------------------------------------------------ 4. worn but assetless (S1d)

    /// <summary>
    /// A worn slot with no asset behind it is a real worn wearable, not an empty slot: the viewer counts
    /// wearables, not textures (LLTexLayerTemplate::updateWearableCache, lltexlayer.cpp:1615-1638), so it still
    /// contributes its layers' morph masks. Resolve must hand it to the library as an empty WearableInput of
    /// its type rather than dropping it (Ledger Q-12; MORPH-MASK-PASS.md 2.4).
    /// </summary>
    [Fact]
    public void Resolve_WornButAssetlessSlot_ReachesTheLibraryAsAWearableInput()
    {
        var assets = new FakeAssetService();
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();
        // a shirt slot that is worn (a real item) with no asset, exactly as Aleric Fenwood wears his
        wearables[(int)WearableType.Shirt].Add(new UUID("77c41e39-38f9-f75a-0000-585989bf0000"), UUID.Zero);

        var r = BakeOrchestrator.Resolve(wearables, null, assets, new TexLayerCompositor(), 512);

        var shirt = Assert.Single(r.Request.Wearables);
        Assert.Equal((int)WearableType.Shirt, shirt.WearableType);
        Assert.Equal(UUID.Zero, shirt.AssetId);
        Assert.Equal("", shirt.RawText);
        Assert.Empty(r.Failures);            // a missing asset is not a failure: there is nothing to fetch
        Assert.Empty(r.Request.Textures);
        Assert.Contains(r.Notes, n => n.Contains("worn with no asset"));
    }

    /// <summary>
    /// S1c decision 3, answered end to end. RED through S1d; green from S1e, which fixed it.
    ///
    /// An assetless Skirt slot makes the library produce a Skirt channel (SkiaBakeBackend.ChannelsFor adds it
    /// whenever a Skirt wearable is worn, textures or not). Every layer of that channel is then skipped —
    /// skirt_fabric has no texture, skirt_fabric_alpha is a mask layer with nothing to mask, skirt_tattoo needs a
    /// Universal — so nothing is drawn. But the bake is not empty: the layer set's alpha starts opaque and only
    /// the (skipped) mask layer would have carved the skirt out of it, so what gets encoded is a 96.5% opaque
    /// near-black image, which the orchestrator then stores and writes into face 19.
    ///
    /// On any avatar whose Skirt slot is occupied by a default item with no asset, a server bake would therefore
    /// paint a solid dark skirt over whatever face 19 held. The pre-existing trigger is a real Skirt wearable
    /// carrying no skirt texture; S1d widened it to assetless slots, which are common.
    ///
    /// S1e closes it: the library reports BakeResult.NothingDrawn for a channel where every layer was skipped,
    /// and StoreAndApply neither stores nor applies such a bake — the outcome is Skipped, "nothing drawn for
    /// this channel", and the face keeps what it had.
    /// </summary>
    [Fact]
    public void Run_AssetlessSkirtSlot_MustNotOverwriteFace19WithAnUndrawnBake()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var (assets, wearables, vp) = LoadFixtures();
        wearables[(int)WearableType.Skirt].Add(UUID.Random(), UUID.Zero);   // worn, no asset
        var appearance = new AvatarAppearance();
        var before = Faces(appearance);
        var compositor = new TexLayerCompositor();

        var outcome = BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, appearance, assets,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 128, CancellationToken.None);

        var skirt = outcome.Channels.Single(c => c.Channel == BakeChannel.Skirt);
        var after = Faces(appearance);
        var stored = skirt.AssetId.IsZero() ? null : assets.Get(skirt.AssetId.ToString());
        var img = stored is null ? null : J2kCodec.Decode(stored.Data);
        var opaque = img is null ? 0 : img.A.Count(a => a > 128) * 100.0 / img.A.Length;
        var diag = $"skirt={skirt.Status} face19 {before[19]} -> {after[19]} opaque={opaque:F1}% "
                 + $"layers=[{string.Join(" | ", skirt.Fidelity.Notes)}]";

        // nothing was drawn into this channel: every layer skipped
        Assert.All(skirt.Fidelity.Notes, n => Assert.Contains("skipped", n));
        // therefore the face must be left as it was
        Assert.True(before[19] == after[19], "an undrawn channel must not overwrite its face. " + diag);
    }

    /// <summary>
    /// The other side of the S1e rule, so it cannot be satisfied by discarding transparent bakes instead: a
    /// channel that DID draw is stored and applied even when every pixel it drew is transparent. Truly Bazar's
    /// bald hair is exactly this — a 4x4 fully transparent hair texture — and its bake is the correct bake.
    /// </summary>
    [Fact]
    public void Run_DrawnButFullyTransparentChannel_IsStillStoredAndApplied()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var (assets, wearables, vp) = LoadFixtures();
        var appearance = new AvatarAppearance();
        var before = Faces(appearance);
        var compositor = new TexLayerCompositor();

        var outcome = BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, appearance, assets,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 128, CancellationToken.None);

        var hair = outcome.Channels.Single(c => c.Channel == BakeChannel.Hair);
        var after = Faces(appearance);
        Assert.Equal(ChannelStatus.Baked, hair.Status);
        var stored = assets.Get(hair.AssetId.ToString());
        Assert.NotNull(stored);
        Assert.Equal(hair.AssetId, after[BakeOrchestrator.FaceOf(BakeChannel.Hair)]);
        Assert.NotEqual(before[BakeOrchestrator.FaceOf(BakeChannel.Hair)], after[BakeOrchestrator.FaceOf(BakeChannel.Hair)]);
        // it really is all-transparent: this is the case a pixel-based rule would have wrongly discarded
        var img = J2kCodec.Decode(stored!.Data);
        Assert.True(img.A.All(a => a <= 2), $"Truly's hair bake should be fully transparent; max alpha {img.A.Max()}");
    }
}
