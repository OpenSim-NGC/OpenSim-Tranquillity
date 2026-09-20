using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenMetaverse;
using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

/// <summary>The bytes-in/bytes-out surface: wearable text parsing, JPEG 2000 in and out (single tile), the backend end to end, and the input hash.</summary>
public class BackendTests
{
    // ---------------- wearable text ----------------

    private static string WearableText(WearableKind kind, string name, IReadOnlyDictionary<int, float> prms, IReadOnlyDictionary<TextureSlot, UUID> textures)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("LLWearable version 22\n").Append(name).Append('\n').Append("\n");
        sb.Append("\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t00082000\n\t\tcreator_id\t11111111-1111-0000-0000-000100bba000\n\t\towner_id\t11111111-1111-0000-0000-000100bba000\n\t\tlast_owner_id\t00000000-0000-0000-0000-000000000000\n\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n");
        sb.Append("\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n");
        sb.Append("type ").Append((int)kind).Append('\n');
        sb.Append("parameters ").Append(prms.Count).Append('\n');
        foreach (var (id, v) in prms) sb.Append(id).Append(' ').Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("textures ").Append(textures.Count).Append('\n');
        foreach (var (slot, id) in textures) sb.Append((int)slot).Append(' ').Append(id).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void wearable_text_parses_type_parameters_and_textures()
    {
        var tex = UUID.Random();
        var text = WearableText(WearableKind.Shirt, "Blue Shirt", new Dictionary<int, float> { [800] = 0.75f, [803] = 0f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = tex });
        var w = WearableParser.Parse(text);
        Assert.Equal(WearableKind.Shirt, w.Kind);
        Assert.Equal("Blue Shirt", w.Name);
        Assert.Equal(0.75f, w.Params[800]);
        Assert.Equal(0f, w.Params[803]);
        Assert.Equal(tex, w.Textures[TextureSlot.UpperShirt]);
        Assert.Throws<FormatException>(() => WearableParser.Parse("not a wearable"));
        Assert.Throws<FormatException>(() => WearableParser.Parse("LLWearable version 22\nName\n\nparameters 1\n800 x\n"));
    }

    // ---------------- JPEG 2000 ----------------

    [Fact]
    public void encoded_bakes_are_a_single_tile_codestream_and_round_trip()
    {
        var img = CompositorTests.Flat(96, 96, 200, 40, 10, 128);
        for (var i = 0; i < img.R.Length; i++) img.R[i] = (byte)(i % 96 * 2);   // some structure so the encoder has work to do
        var bytes = J2kCodec.Encode(img);
        Assert.True(bytes.Length > 100);
        Assert.Equal(0xFF, bytes[0]); Assert.Equal(0x4F, bytes[1]);   // raw codestream (SOC), not a JP2 file
        var siz = J2kCodec.ParseSiz(bytes);
        Assert.Equal(96, siz.Xsiz); Assert.Equal(96, siz.Ysiz);
        Assert.True(siz.XTsiz >= siz.Xsiz && siz.YTsiz >= siz.Ysiz, $"tile {siz.XTsiz}x{siz.YTsiz} smaller than image {siz.Xsiz}x{siz.Ysiz}");
        Assert.True(siz.SingleTile, $"{siz.TileCount} tiles");
        Assert.Equal(4, siz.Csiz);

        var back = J2kCodec.Decode(bytes);
        Assert.Equal(96, back.W); Assert.Equal(96, back.H);
        Assert.True(back.HasAlpha);
        var mid = 48 * 96 + 48;
        Assert.InRange(back.G[mid], 30, 50);
        Assert.InRange(back.B[mid], 0, 20);
        Assert.InRange(back.A[mid], 118, 138);
    }

    [Fact]
    public void a_bake_encodes_five_components_and_round_trips_the_morph_mask()
    {
        var img = CompositorTests.Flat(96, 96, 180, 90, 30, 200);
        var mask = new byte[96 * 96];
        for (var i = 0; i < mask.Length; i++) mask[i] = (byte)(i % 96 < 48 ? 0 : 255);
        var bytes = J2kCodec.EncodeBake(img, mask);
        var siz = J2kCodec.ParseSiz(bytes);
        Assert.Equal(5, siz.Csiz);
        Assert.True(siz.SingleTile, $"{siz.TileCount} tiles");
        Assert.Equal(96, siz.Xsiz);
        var back = J2kCodec.Decode(bytes);
        Assert.NotNull(back.Mask);
        Assert.True(back.HasAlpha);
        var row = 48 * 96;
        Assert.InRange(back.Mask![row + 10], 0, 8);
        Assert.InRange(back.Mask![row + 80], 247, 255);
        Assert.InRange(back.A[row + 10], 190, 210);
        Assert.InRange(back.R[row + 10], 170, 190);
        // no mask supplied: 255 everywhere, the value gatherMorphMaskAlpha starts from
        var plain = J2kCodec.Decode(J2kCodec.EncodeBake(img, null));
        Assert.NotNull(plain.Mask);
        Assert.All(plain.Mask!, v => Assert.InRange(v, 250, 255));
        // and the backend's output is five-component
        var (req, _) = ClassicRequest();
        foreach (var r in new SkiaBakeBackend().Bake(req)) Assert.Equal(5, J2kCodec.ParseSiz(r.J2kBytes).Csiz);
    }

    [Fact]
    public void the_default_encoder_would_tile_a_512_bake_so_the_config_pins_one_tile()
    {
        var cfg = J2kCodec.EncoderConfig(512, 512);
        Assert.Equal(512, cfg.Tiles.Width);
        Assert.Equal(512, cfg.Tiles.Height);
        Assert.False(cfg.UseFileFormat);
        var siz = J2kCodec.ParseSiz(J2kCodec.Encode(CompositorTests.Flat(512, 512, 10, 20, 30)));
        Assert.Equal(1, siz.TileCount);
    }

    // ---------------- the backend ----------------

    private static (BakeRequest Request, Dictionary<WearableKind, UUID> Assets) ClassicRequest(int size = 64, float sleeve = 1f)
    {
        var skinTex = UUID.Random(); var hairTex = UUID.Random(); var irisTex = UUID.Random(); var shirtTex = UUID.Random();
        var assets = new Dictionary<WearableKind, UUID> { [WearableKind.Shape] = UUID.Random(), [WearableKind.Skin] = UUID.Random(), [WearableKind.Hair] = UUID.Random(), [WearableKind.Eyes] = UUID.Random(), [WearableKind.Shirt] = UUID.Random() };
        var wearables = new List<WearableInput>
        {
            new(assets[WearableKind.Shape], 0, WearableText(WearableKind.Shape, "Shape", new Dictionary<int, float> { [80] = 0f, [33] = 0.5f }, new Dictionary<TextureSlot, UUID>())),
            new(assets[WearableKind.Skin], 1, WearableText(WearableKind.Skin, "Skin", new Dictionary<int, float> { [111] = 0.5f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex })),
            new(assets[WearableKind.Hair], 2, WearableText(WearableKind.Hair, "Hair", new Dictionary<int, float> { [114] = 0.5f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.Hair] = hairTex })),
            new(assets[WearableKind.Eyes], 3, WearableText(WearableKind.Eyes, "Eyes", new Dictionary<int, float> { [99] = 0f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.EyesIris] = irisTex })),
            new(assets[WearableKind.Shirt], 4, WearableText(WearableKind.Shirt, "Shirt", new Dictionary<int, float> { [800] = sleeve, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = shirtTex })),
        };
        var textures = new Dictionary<UUID, TextureInput>
        {
            [skinTex] = new(skinTex, J2kCodec.Encode(CompositorTests.Flat(32, 32, 200, 150, 120))),
            [hairTex] = new(hairTex, J2kCodec.Encode(CompositorTests.Flat(16, 16, 255, 255, 255, 0))),
            [irisTex] = new(irisTex, J2kCodec.Encode(CompositorTests.Flat(16, 16, 255, 255, 255))),
            [shirtTex] = new(shirtTex, J2kCodec.Encode(CompositorTests.Flat(16, 16, 0, 0, 255))),
        };
        return (new BakeRequest(wearables, new Dictionary<int, float>(), textures, size), assets);
    }

    [Fact]
    public async Task the_backend_bakes_the_five_classic_channels_from_bytes_and_reports_fidelity()
    {
        var (req, _) = ClassicRequest();
        var results = await new SkiaBakeBackend().BakeAsync(req, CancellationToken.None);
        Assert.Equal(new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair }, results.Select(r => r.Channel).ToArray());
        foreach (var r in results)
        {
            var siz = J2kCodec.ParseSiz(r.J2kBytes);
            Assert.Equal(64, siz.Xsiz); Assert.True(siz.SingleTile);
            Assert.Equal(64, r.InputHash.Length);
            Assert.Empty(r.Fidelity.Refusals);
            Assert.Empty(r.Fidelity.MissingTextures);
            Assert.NotEmpty(r.Fidelity.Notes);
        }
        var upper = J2kCodec.Decode(results.Single(r => r.Channel == BakeChannel.Upper).J2kBytes);
        Assert.Contains(upper.B, b => b > 150);   // the blue shirt made it onto the upper bake
        Assert.Contains(results.Single(r => r.Channel == BakeChannel.Upper).Fidelity.Notes, n => n.StartsWith("upper_clothes drawn"));

        // a texture the request does not carry is reported on the channel that needs it, and only there
        var missingTex = req.Textures.Keys.First();
        var trimmed = req with { Textures = req.Textures.Where(kv => kv.Key != missingTex).ToDictionary(kv => kv.Key, kv => kv.Value) };
        var results2 = new SkiaBakeBackend().Bake(trimmed);
        Assert.Contains(results2, r => r.Fidelity.MissingTextures.Contains(missingTex));

        // an unsupported wearable type lands in Refusals on every channel; corrupt text is an exception
        var odd = req with { Wearables = req.Wearables.Append(new WearableInput(UUID.Random(), 99, WearableText((WearableKind)99, "Mystery", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID>()))).ToList() };
        Assert.All(new SkiaBakeBackend().Bake(odd), r => Assert.Contains(r.Fidelity.Refusals, s => s.Contains("not composited")));
        var corrupt = req with { Wearables = req.Wearables.Append(new WearableInput(UUID.Random(), 4, "garbage")).ToList() };
        Assert.Throws<ArgumentException>(() => new SkiaBakeBackend().Bake(corrupt));
    }

    /// <summary>
    /// <see cref="BakeRequest.Channels"/> narrows what is composited without changing what comes out. It is the
    /// mechanism behind the ADR-004 skip: the orchestrator leaves out the channels whose inputs are unchanged, so
    /// no texture of theirs is even decoded. Two properties are asserted — the results are exactly the requested
    /// channels intersected with what the outfit needs, and each one is byte-for-byte the bake a full run makes.
    /// </summary>
    [Fact]
    public void requesting_a_subset_of_channels_bakes_only_those_and_changes_no_bytes()
    {
        var (req, _) = ClassicRequest();
        var full = new SkiaBakeBackend().Bake(req);

        var subset = new[] { BakeChannel.Upper, BakeChannel.Eyes };
        var partial = new SkiaBakeBackend().Bake(req with { Channels = subset });
        Assert.Equal(subset, partial.Select(r => r.Channel).ToArray());
        foreach (var r in partial)
        {
            var same = full.Single(f => f.Channel == r.Channel);
            Assert.Equal(same.J2kBytes, r.J2kBytes);
            Assert.Equal(same.InputHash, r.InputHash);
            Assert.Equal(same.Fidelity.Notes, r.Fidelity.Notes);
        }

        // naming a channel the outfit does not feed does not conjure it
        Assert.Empty(new SkiaBakeBackend().Bake(req with { Channels = new[] { BakeChannel.Skirt } }));
        // an empty set bakes nothing; null (the default) bakes everything
        Assert.Empty(new SkiaBakeBackend().Bake(req with { Channels = Array.Empty<BakeChannel>() }));
        Assert.Equal(full.Count, new SkiaBakeBackend().Bake(req with { Channels = null }).Count);
    }

    [Fact]
    public void skirt_and_extra_channels_appear_only_when_worn_or_painted()
    {
        var (req, _) = ClassicRequest();
        var parsed = req.Wearables.Select(w => WearableParser.Parse(w.RawText)).ToList();
        Assert.Equal(5, SkiaBakeBackend.ChannelsFor(parsed).Count);
        parsed.Add(new ParsedWearable(WearableKind.Skirt, "Skirt", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID>()));
        parsed.Add(new ParsedWearable(WearableKind.Universal, "U", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID> { [TextureSlot.Aux2Tattoo] = UUID.Random(), [TextureSlot.LeftArmTattoo] = BakeConstants.DefaultAvatarTexture }));
        var chs = SkiaBakeBackend.ChannelsFor(parsed);
        Assert.Contains(BakeChannel.Skirt, chs);
        Assert.Contains(BakeChannel.Aux2, chs);
        Assert.DoesNotContain(BakeChannel.LeftArm, chs);
    }

    // ---------------- the hash ----------------

    [Fact]
    public void bake_hash_is_stable_across_ordering_and_changes_with_every_input()
    {
        var (req, assets) = ClassicRequest();
        var h = BakeHash.Compute(BakeChannel.Upper, req);
        Assert.Matches("^[0-9a-f]{64}$", h);

        // ordering: reversed wearable list, reversed texture dictionary, reversed visual params
        var reordered = new BakeRequest(
            req.Wearables.Reverse().ToList(),
            new Dictionary<int, float> { [33] = 0.5f, [80] = 0f },
            req.Textures.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value),
            req.BakeSize);
        var baseline = req with { VisualParams = new Dictionary<int, float> { [80] = 0f, [33] = 0.5f } };
        Assert.Equal(BakeHash.Compute(BakeChannel.Upper, baseline), BakeHash.Compute(BakeChannel.Upper, reordered));

        // channel and size
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Lower, req));
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, req with { BakeSize = 128 }));

        // a wearable that feeds the channel: new asset id, or a changed parameter it stores
        var shirt = req.Wearables.Single(w => w.WearableType == 4);
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, req with { Wearables = req.Wearables.Select(w => w == shirt ? w with { AssetId = UUID.Random() } : w).ToList() }));
        var (req2, _) = ClassicRequest(sleeve: 0.25f);
        var sameIds = req2 with { Wearables = req2.Wearables.Select((w, i) => w with { AssetId = req.Wearables[i].AssetId }).ToList(), Textures = req.Textures };
        // (textures differ by id between the two requests; align them so only the sleeve differs)
        var shirtText = sameIds.Wearables.Single(w => w.WearableType == 4);
        var shirtTex = WearableParser.Parse(shirt.RawText).Textures[TextureSlot.UpperShirt];
        var realigned = sameIds with { Wearables = sameIds.Wearables.Select(w => w == shirtText
            ? w with { RawText = WearableText(WearableKind.Shirt, "Shirt", new Dictionary<int, float> { [800] = 0.25f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = shirtTex }) }
            : req.Wearables[sameIds.Wearables.ToList().IndexOf(w)]).ToList() };
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, realigned));

        // a texture id the channel draws
        var retextured = req with { Wearables = req.Wearables.Select(w => w == shirt ? w with { RawText = WearableText(WearableKind.Shirt, "Shirt", WearableParser.Parse(shirt.RawText).Params, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = UUID.Random() }) } : w).ToList() };
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, retextured));

        // a visual param the channel's layers read (skin colour 111 feeds the upper body's global colour)
        Assert.NotEqual(BakeHash.Compute(BakeChannel.Upper, req with { VisualParams = new Dictionary<int, float> { [111] = 0.1f } }),
                        BakeHash.Compute(BakeChannel.Upper, req with { VisualParams = new Dictionary<int, float> { [111] = 0.9f } }));

        // something that does not feed the upper body (pants asset id) leaves it alone
        var pants = new WearableInput(UUID.Random(), 5, WearableText(WearableKind.Pants, "Pants", new Dictionary<int, float> { [615] = 1f }, new Dictionary<TextureSlot, UUID>()));
        Assert.Equal(h, BakeHash.Compute(BakeChannel.Upper, req with { Wearables = req.Wearables.Append(pants).ToList() }));
        Assert.NotEqual(BakeHash.Compute(BakeChannel.Lower, req), BakeHash.Compute(BakeChannel.Lower, req with { Wearables = req.Wearables.Append(pants).ToList() }));
        _ = assets;
    }

    // ---------------- a worn wearable with no texture asset (S1c, Docs/MORPH-MASK-PASS.md 2.4) ----------------

    /// <summary>
    /// The rule: a morph-mask layer with a local_texture contributes the mask of the top worn wearable of its
    /// type, and a wearable counts as worn whether or not it carries a texture asset
    /// (LLTexLayerTemplate::updateWearableCache counts LLWearable objects, lltexlayer.cpp:1615-1638; its
    /// getLayer only needs a local texture object, :1639-1656). The layer draws nothing in the colour pass
    /// without an image, but its parameter alphas still make a morph mask, and their values come from the
    /// avatar parameters rather than from the absent asset.
    /// </summary>
    [Fact]
    public void a_worn_wearable_with_no_texture_asset_still_contributes_its_morph_mask()
    {
        var jacketTex = UUID.Random();
        var textures = new Dictionary<UUID, TextureInput>
        {
            [jacketTex] = new TextureInput(jacketTex, J2kCodec.Encode(CompositorTests.Flat(32, 32, 40, 40, 60))),
        };
        // the shirt-owned drivers of upper_clothes's four param alphas (600/601/602/778): short sleeves, so the
        // mask has a zero region. They live on the avatar, not on the (absent) shirt asset.
        var visual = new Dictionary<int, float> { [800] = 0f, [801] = 1f, [802] = 1f, [781] = 1f };
        var shape = new WearableInput(UUID.Random(), (int)WearableKind.Shape, WearableText(WearableKind.Shape, "S", new Dictionary<int, float> { [80] = 1f }, new Dictionary<TextureSlot, UUID>()));
        var jacket = new WearableInput(UUID.Random(), (int)WearableKind.Jacket, WearableText(WearableKind.Jacket, "J", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperJacket] = jacketTex }));

        byte[] UpperMask(params WearableInput[] worn)
        {
            var r = new SkiaBakeBackend().Bake(new BakeRequest(worn, visual, textures, 128));
            return r.Single(x => x.Channel == BakeChannel.Upper).Fidelity is not null
                ? J2kCodec.Decode(r.Single(x => x.Channel == BakeChannel.Upper).J2kBytes).Mask!
                : throw new InvalidOperationException();
        }

        // jacket alone, no shirt slot at all: upper_clothes has no instance and the morph mask stays flat at 255
        // (the J2C round-trip renders a flat 255 plane back as a flat 254, as the golden tables show)
        var withoutShirtSlot = UpperMask(shape, jacket);
        Assert.True(withoutShirtSlot.Max() - withoutShirtSlot.Min() <= 2, "no shirt slot: the morph mask must be flat");
        Assert.True(withoutShirtSlot.Min() >= 250, $"no shirt slot: the flat mask must be ~255, was {withoutShirtSlot.Min()}");

        // the same outfit, plus a worn Shirt slot with no asset behind it: the mask is now real
        var assetlessShirt = new WearableInput(UUID.Zero, (int)WearableKind.Shirt, "");
        var withShirtSlot = UpperMask(shape, jacket, assetlessShirt);
        Assert.True(withShirtSlot.Any(v => v < 250), "an assetless but worn shirt must still produce a morph mask");
        Assert.True(withShirtSlot.Any(v => v > 250), "the mask must be a mask, not zero everywhere");

        // and it is the shirt layer that did it, not the jacket: upper_jacket is not in <morph_masks>
        Assert.DoesNotContain("upper_jacket", AvatarLad.Embedded.MorphMaskLayers["upper_body"]);
        Assert.Contains("upper_clothes", AvatarLad.Embedded.MorphMaskLayers["upper_body"]);
    }

    /// <summary>
    /// The other half of the same rule: LLTexLayerTemplate::gatherAlphaMasks uses getLayer(num_wearables - 1)
    /// only — "For rendering morph masks, we only want to use the top wearable" (lltexlayer.cpp:1710-1719) —
    /// unlike render(), which loops over every instance. Two shirts must give the second one's mask, not the
    /// product of both.
    /// </summary>
    [Fact]
    public void the_morph_mask_of_a_template_layer_comes_from_the_top_wearable_only()
    {
        var shape = new WearableInput(UUID.Random(), (int)WearableKind.Shape, WearableText(WearableKind.Shape, "S", new Dictionary<int, float> { [80] = 1f }, new Dictionary<TextureSlot, UUID>()));
        WearableInput Shirt(float sleeve) => new(UUID.Random(), (int)WearableKind.Shirt,
            WearableText(WearableKind.Shirt, "shirt", new Dictionary<int, float> { [800] = sleeve, [801] = 1f, [802] = 1f, [781] = 1f }, new Dictionary<TextureSlot, UUID>()));

        byte[] Mask(params WearableInput[] worn)
        {
            var r = new SkiaBakeBackend().Bake(new BakeRequest(worn, new Dictionary<int, float>(), new Dictionary<UUID, TextureInput>(), 128));
            return J2kCodec.Decode(r.Single(x => x.Channel == BakeChannel.Upper).J2kBytes).Mask!;
        }

        var shortOnly = Mask(shape, Shirt(0f));
        var longOnly = Mask(shape, Shirt(1f));
        var shortThenLong = Mask(shape, Shirt(0f), Shirt(1f));

        long Diff(byte[] a, byte[] b) { long d = 0; for (var i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]); return d / a.Length; }
        Assert.True(Diff(shortOnly, longOnly) > 4, "the two sleeve lengths must give different masks for this test to mean anything");
        Assert.True(Diff(shortThenLong, longOnly) <= 2, $"two shirts must give the top (last) shirt's mask; differs by {Diff(shortThenLong, longOnly)}");
        Assert.True(Diff(shortThenLong, shortOnly) > 4, "two shirts must not give the first shirt's mask");
    }

    // ---------------- a channel where nothing was drawn (S1e) ----------------

    private static string TrulyFixtures([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "Golden", "truly-stock", "fixtures"));

    /// <summary>
    /// BakeResult.NothingDrawn is set only when every colour layer of the channel was skipped. An assetless
    /// Skirt slot is exactly that case: skirt_fabric has no texture, skirt_fabric_alpha is a mask layer and
    /// skirt_tattoo needs a Universal, so nothing reaches the canvas — and because the set's alpha starts
    /// opaque, what encodes is a solid image, not a blank one. S1d found this painting a dark skirt onto face 19.
    /// </summary>
    [Fact]
    public void a_channel_whose_every_layer_was_skipped_reports_nothing_drawn()
    {
        var shape = new WearableInput(UUID.Random(), (int)WearableKind.Shape, WearableText(WearableKind.Shape, "S", new Dictionary<int, float> { [80] = 0f }, new Dictionary<TextureSlot, UUID>()));
        var skirt = new WearableInput(UUID.Zero, (int)WearableKind.Skirt, "");   // worn, no asset
        var results = new SkiaBakeBackend().Bake(new BakeRequest(new[] { shape, skirt }, new Dictionary<int, float>(), new Dictionary<UUID, TextureInput>(), 64));

        var s = results.Single(r => r.Channel == BakeChannel.Skirt);
        Assert.True(s.NothingDrawn, "an all-skipped skirt channel must report NothingDrawn: " + string.Join(" | ", s.Fidelity.Notes));
        Assert.All(s.Fidelity.Notes, n => Assert.Contains("skipped", n));
        // it still encodes a full image - that is the hazard: an undrawn channel is not an empty bake, and how
        // opaque it comes out depends on which mask layers the outfit skipped (on a real outfit S1d measured 96.5%
        // opaque near-black). The orchestrator must decide on this flag, never on the pixels.
        Assert.NotEmpty(s.J2kBytes);
        Assert.Equal(64, J2kCodec.Decode(s.J2kBytes).W);
        // and it is per channel, not per outfit: the head of the same outfit did draw
        Assert.False(results.Single(r => r.Channel == BakeChannel.Head).NothingDrawn);
    }

    /// <summary>
    /// Drawn-but-transparent is NOT undrawn. Truly Bazar's hair wearable carries a 4x4 fully transparent
    /// texture: the base layer draws it, the bake is legitimately all-transparent, and it must still be stored.
    /// Asserted on the real truly-stock fixtures, and on a synthetic equivalent so the rule is covered when the
    /// fixtures are not fetched.
    /// </summary>
    [Fact]
    public void a_channel_that_drew_a_fully_transparent_texture_is_not_nothing_drawn()
    {
        // synthetic: a bald hair — a hair wearable whose texture is entirely transparent
        var shape = new WearableInput(UUID.Random(), (int)WearableKind.Shape, WearableText(WearableKind.Shape, "S", new Dictionary<int, float> { [80] = 0f }, new Dictionary<TextureSlot, UUID>()));
        var bald = UUID.Random();
        var hair = new WearableInput(UUID.Random(), (int)WearableKind.Hair, WearableText(WearableKind.Hair, "bald", new Dictionary<int, float> { [114] = 0.5f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.Hair] = bald }));
        var textures = new Dictionary<UUID, TextureInput> { [bald] = new TextureInput(bald, J2kCodec.Encode(CompositorTests.Flat(4, 4, 255, 255, 255, 0))) };
        var synthetic = new SkiaBakeBackend().Bake(new BakeRequest(new[] { shape, hair }, new Dictionary<int, float>(), textures, 64))
            .Single(r => r.Channel == BakeChannel.Hair);
        Assert.False(synthetic.NothingDrawn, "a drawn but transparent layer has drawn: " + string.Join(" | ", synthetic.Fidelity.Notes));
        Assert.Contains(synthetic.Fidelity.Notes, n => n.StartsWith("base drawn"));
        var img = J2kCodec.Decode(synthetic.J2kBytes);
        Assert.True(img.A.All(a => a <= 2), "the bald hair bake is legitimately all-transparent and must still be stored");

        // the real thing: Truly Bazar's stock outfit
        var fx = TrulyFixtures();
        if (!File.Exists(Path.Combine(fx, "avatar.json"))) { Console.WriteLine("SKIPPED (truly-stock fixtures not fetched): synthetic case asserted above"); return; }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fx, "avatar.json")));
        var worn = new List<WearableInput>();
        foreach (var w in doc.RootElement.GetProperty("wearables").EnumerateArray())
        {
            var id = w.GetProperty("assetId").GetString()!;
            var type = w.GetProperty("type").GetInt32();
            if (id == "00000000-0000-0000-0000-000000000000") { worn.Add(new WearableInput(UUID.Zero, type, "")); continue; }
            var f = Directory.GetFiles(fx, id + ".*").First(x => !x.EndsWith(".j2c"));
            worn.Add(new WearableInput(new UUID(id), type, File.ReadAllText(f)));
        }
        var tex = new Dictionary<UUID, TextureInput>();
        foreach (var w in worn.Where(x => x.RawText.Length > 0))
            foreach (var (_, id) in WearableParser.Parse(w.RawText).Textures)
            {
                var f = Path.Combine(fx, id + ".j2c");
                if (File.Exists(f) && !tex.ContainsKey(id)) tex[id] = new TextureInput(id, File.ReadAllBytes(f));
            }
        var real = new SkiaBakeBackend().Bake(new BakeRequest(worn, new Dictionary<int, float>(), tex, 128));
        var realHair = real.Single(r => r.Channel == BakeChannel.Hair);
        Assert.False(realHair.NothingDrawn, "Truly's bald hair drew: " + string.Join(" | ", realHair.Fidelity.Notes));
        // and no channel of a normal outfit reports nothing drawn
        Assert.All(real, r => Assert.False(r.NothingDrawn, $"{r.Channel}: " + string.Join(" | ", r.Fidelity.Notes)));
    }
}
