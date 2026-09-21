using OpenMetaverse;
using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

/// <summary>
/// The compositor against the embedded avatar_lad.xml and mask files, with synthetic wearables whose textures
/// are flat colours, so every assertion is about semantics: scaling instead of tiling, layer order,
/// parameter-driven masks, tints, alpha wearables masking the body, and the fidelity gate.
/// Ported from the web-viewer gateway's CompositorTests (sessions 11–13) in S0b.
/// </summary>
public class CompositorTests
{
    private static AvatarLad Lad => AvatarLad.Embedded;
    private static TexLayerCompositor NewCompositor() => new(Lad, ResourceImages.Embedded);

    internal static RgbaPlanes Flat(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var p = new RgbaPlanes(w, h, hasAlpha: a != 255);
        Array.Fill(p.R, r); Array.Fill(p.G, g); Array.Fill(p.B, b); Array.Fill(p.A, a);
        return p;
    }

    private static WornWearable Wear(WearableKind kind, Dictionary<int, float>? prms = null, params (TextureSlot Slot, RgbaPlanes Tex)[] textures)
    {
        var ids = textures.ToDictionary(t => t.Slot, _ => UUID.Random());
        return new WornWearable { Kind = kind, Label = kind.ToString(), Params = prms ?? new(), TextureIds = ids, Textures = textures.ToDictionary(t => t.Slot, t => t.Tex) };
    }

    /// <summary>Shape, skin (flat tan, no makeup), hair, eyes: the minimum a system avatar needs.</summary>
    private static List<WornWearable> BaseOutfit(bool male = false) => new()
    {
        Wear(WearableKind.Shape, new() { [80] = male ? 1f : 0f }),
        Wear(WearableKind.Skin, new() { [111] = 0.5f }, (TextureSlot.HeadBodypaint, Flat(64, 64, 200, 150, 120)), (TextureSlot.UpperBodypaint, Flat(64, 64, 200, 150, 120)), (TextureSlot.LowerBodypaint, Flat(64, 64, 200, 150, 120))),
        Wear(WearableKind.Hair, new() { [114] = 0.5f }, (TextureSlot.Hair, Flat(32, 32, 255, 255, 255, 0))),
        Wear(WearableKind.Eyes, new() { [99] = 0f }, (TextureSlot.EyesIris, Flat(32, 32, 255, 255, 255))),
    };

    private static (byte R, byte G, byte B, byte A) Px(RgbaPlanes img, int x, int y) { var i = y * img.W + x; return (img.R[i], img.G[i], img.B[i], img.A[i]); }

    /// <summary>A pixel of the given mask file (at bake resolution) with the requested grey value, searched from the centre outwards.</summary>
    private static (int X, int Y) FindMaskPixel(string file, int size, Func<byte, bool> want, params string[] alsoWhite)
    {
        var res = ResourceImages.Embedded;
        var r = Raster.Resample(res.Mask(file)!, size, size);
        var white = alsoWhite.Select(f => Raster.Resample(res.Mask(f)!, size, size)).ToList();
        for (var y = 8; y < size - 8; y += 4)
            for (var x = 8; x < size - 8; x += 4)
                if (want(r.Data[y * size + x]) && want(r.Data[y * size + x + 3]) && want(r.Data[(y + 3) * size + x]) && white.All(w => w.Data[y * size + x] > 250 && w.Data[y * size + x + 3] > 250 && w.Data[(y + 3) * size + x] > 250)) return (x, y);
        throw new InvalidOperationException($"no pixel in {file} matches");
    }

    [Fact]
    public void every_resource_the_bake_layers_name_is_embedded()
    {
        // avatar_lad.xml also names head_wrinkles_highlights_alpha.tga, but only from a bump-pass layer, which is never baked.
        var c = NewCompositor();
        var files = Enum.GetValues<BakeChannel>().SelectMany(c.ResourceFilesOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = files.Where(f => !ResourceImages.Embedded.Exists(f)).ToList();
        Assert.True(files.Count > 40, $"expected the layer sets to name dozens of files, got {files.Count}");
        Assert.Empty(missing);
        Assert.False(ResourceImages.Embedded.Exists("head_wrinkles_highlights_alpha.tga"));
    }

    [Fact]
    public void avatar_lad_loads_the_bake_layer_sets_and_driver_parameters()
    {
        var lad = Lad;
        Assert.True(lad.LayerSets.ContainsKey("head") && lad.LayerSets.ContainsKey("upper_body") && lad.LayerSets.ContainsKey("lower_body") && lad.LayerSets.ContainsKey("eyes") && lad.LayerSets.ContainsKey("hair") && lad.LayerSets.ContainsKey("skirt"));
        Assert.Equal(new[] { "base", "nipples", "shadow", "highlight", "upper_bodypaint" }, lad.LayerSets["upper_body"].Layers.Where(l => !l.Bump).Take(5).Select(l => l.Name).ToArray());
        var sleeve = lad.Params[800];                       // the shirt's stored "Sleeve Length" drives the layer's mask parameter 600
        Assert.Contains(sleeve.Driven, d => d.Id == 600);
        Assert.Equal("shirt_sleeve_alpha.tga", lad.Params[600].Alpha!.TgaFile);
        Assert.False(lad.Params[600].Alpha!.MultiplyBlend);
        Assert.True(lad.Params[601].Alpha!.MultiplyBlend);
        Assert.Equal(ParamSex.Male, lad.Params[1005].Sex); // sideburns: men only
        Assert.Equal(new[] { 111, 110, 108 }, lad.GlobalColors["skin_color"]);
        Assert.Equal(ColorOp.Blend, lad.Params[110].Color!.Op);
    }

    [Fact]
    public void a_small_texture_is_scaled_over_the_whole_bake_not_tiled()
    {
        // Left half red, right half blue, 64 px wide: on a faithful bake the halves land at 25% and 75% of the width.
        var tex = new RgbaPlanes(64, 64, hasAlpha: false);
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) { var i = y * 64 + x; if (x < 32) tex.R[i] = 255; else tex.B[i] = 255; }
        var outfit = BaseOutfit();
        outfit[1] = Wear(WearableKind.Skin, new() { [111] = 0.5f }, (TextureSlot.UpperBodypaint, tex), (TextureSlot.HeadBodypaint, tex), (TextureSlot.LowerBodypaint, tex));
        var bake = NewCompositor().Bake(BakeChannel.Upper, outfit, 256).Image;
        var left = Px(bake, 64, 128);
        var right = Px(bake, 192, 128);
        Assert.True(left.R > 200 && left.B < 40, $"left quarter should be red, got {left}");
        Assert.True(right.B > 200 && right.R < 40, $"right quarter should be blue, got {right}");
        var farRight = Px(bake, 250, 250);
        Assert.True(farRight.B > 200, $"a tiled bake would repeat red here, got {farRight}");
    }

    [Fact]
    public void the_shirt_covers_the_arms_only_when_its_sleeve_length_says_so()
    {
        const int size = 256;
        var shirt = (TextureSlot.UpperShirt, Flat(64, 64, 0, 0, 255));
        var others = new[] { "shirt_bottom_alpha.tga", "shirt_collar_alpha.tga", "shirt_collar_back_alpha.tga" };   // the shirt's multiply masks must be open there
        var torso = FindMaskPixel("shirt_sleeve_alpha.tga", size, v => v == 255, others);   // always covered by the sleeve mask
        var wrist = FindMaskPixel("shirt_sleeve_alpha.tga", size, v => v is > 60 and < 120, others); // covered only by long sleeves (below ~38 is past the wrist even at full length)
        var c = NewCompositor();

        var longSleeves = BaseOutfit(); longSleeves.Add(Wear(WearableKind.Shirt, new() { [800] = 1f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, shirt));
        var bakeLong = c.Bake(BakeChannel.Upper, longSleeves, size).Image;
        Assert.True(Px(bakeLong, torso.X, torso.Y).B > 200, "torso is shirt-blue with long sleeves");
        Assert.True(Px(bakeLong, wrist.X, wrist.Y).B > 200, $"wrist is shirt-blue with long sleeves, got {Px(bakeLong, wrist.X, wrist.Y)}");

        var shortSleeves = BaseOutfit(); shortSleeves.Add(Wear(WearableKind.Shirt, new() { [800] = 0f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, shirt));
        var bakeShort = c.Bake(BakeChannel.Upper, shortSleeves, size).Image;
        Assert.True(Px(bakeShort, torso.X, torso.Y).B > 200, "torso is still shirt-blue with short sleeves");
        var w = Px(bakeShort, wrist.X, wrist.Y);
        Assert.True(w.R > 150 && w.B < 150, $"wrist shows skin with short sleeves, got {w}");
        Assert.Equal(255, Px(bakeShort, wrist.X, wrist.Y).A);   // clothing masks never make the body transparent
    }

    [Fact]
    public void clothing_is_tinted_by_its_colour_parameters()
    {
        var outfit = BaseOutfit();
        outfit.Add(Wear(WearableKind.Shirt, new() { [800] = 1f, [803] = 0f, [804] = 1f, [805] = 0f }, (TextureSlot.UpperShirt, Flat(16, 16, 255, 255, 255))));
        var bake = NewCompositor().Bake(BakeChannel.Upper, outfit, 128).Image;
        var torso = FindMaskPixel("shirt_sleeve_alpha.tga", 128, v => v == 255);
        var p = Px(bake, torso.X, torso.Y);
        Assert.True(p.G > 200 && p.R < 40 && p.B < 40, $"white shirt tinted green, got {p}");
    }

    [Fact]
    public void layers_stack_in_avatar_lad_order_undershirt_below_shirt_below_jacket()
    {
        var outfit = BaseOutfit();
        outfit.Add(Wear(WearableKind.Undershirt, new() { [603] = 1f, [604] = 1f, [605] = 1f, [779] = 1f, [821] = 1f, [822] = 0f, [823] = 0f }, (TextureSlot.UpperUndershirt, Flat(16, 16, 255, 255, 255))));
        outfit.Add(Wear(WearableKind.Shirt, new() { [800] = 1f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 1f, [805] = 0f }, (TextureSlot.UpperShirt, Flat(16, 16, 255, 255, 255))));
        var torso = FindMaskPixel("shirt_sleeve_alpha.tga", 128, v => v == 255);
        var c = NewCompositor();
        var result = c.Bake(BakeChannel.Upper, outfit, 128);
        Assert.True(Px(result.Image, torso.X, torso.Y).G > 200, "the shirt (green) is above the undershirt (red)");
        var drawn = result.Layers.Where(l => l.Status == "drawn").Select(l => l.Layer).ToList();
        Assert.True(drawn.IndexOf("upper_undershirt") < drawn.IndexOf("upper_clothes"), string.Join(",", drawn));
        Assert.Contains(result.Layers, l => l.Layer == "upper_jacket" && l.Status == "skipped" && l.Detail.Contains("no Jacket worn"));
    }

    [Fact]
    public void an_alpha_wearable_masks_the_body_and_nothing_else_touches_the_alpha()
    {
        var alphaTex = new RgbaPlanes(64, 64, hasAlpha: true);
        Array.Fill(alphaTex.R, (byte)255); Array.Fill(alphaTex.G, (byte)255); Array.Fill(alphaTex.B, (byte)255);
        for (var i = 0; i < 64 * 64; i++) alphaTex.A[i] = (byte)(i % 64 < 32 ? 0 : 255);   // hide the left half of the upper body
        var outfit = BaseOutfit();
        outfit.Add(Wear(WearableKind.Shirt, new() { [800] = 1f }, (TextureSlot.UpperShirt, Flat(16, 16, 0, 0, 255))));
        var without = NewCompositor().Bake(BakeChannel.Upper, outfit, 128).Image;
        Assert.All(without.A, a => Assert.Equal(255, a));
        outfit.Add(Wear(WearableKind.Alpha, null, (TextureSlot.UpperAlpha, alphaTex)));
        var with = NewCompositor().Bake(BakeChannel.Upper, outfit, 128).Image;
        Assert.Equal(0, Px(with, 20, 64).A);
        Assert.Equal(255, Px(with, 100, 64).A);
        Assert.True(Px(with, 20, 64).B > 200 || Px(with, 20, 64).R > 100, "the colour channels are left intact under the mask");
    }

    [Fact]
    public void the_iris_takes_the_eye_colour_and_the_head_bake_carries_the_eyelash_mask()
    {
        var c = NewCompositor();
        var eyes = c.Bake(BakeChannel.Eyes, BaseOutfit(), 128).Image;
        var centre = Px(eyes, 64, 64);
        Assert.True(centre.R < 120 && centre.G < 100 && centre.B < 60, $"a white iris with eye colour 0 is dark brown, got {centre}");
        Assert.Equal(255, centre.A);
        var head = c.Bake(BakeChannel.Head, BaseOutfit(), 256).Image;
        Assert.Contains(head.A, a => a == 0);      // head_alpha.tga (eyelashes) is a visibility mask
        Assert.Contains(head.A, a => a == 255);
        var hair = c.Bake(BakeChannel.Hair, BaseOutfit(), 64).Image;
        Assert.All(hair.A, a => Assert.Equal(0, a));  // a fully transparent hair texture gives a transparent hair bake
    }

    [Fact]
    public void makeup_parameters_on_the_skin_paint_over_the_skin_texture()
    {
        // Makeup layers sit below head_bodypaint in avatar_lad.xml: an opaque skin texture covers them (as in the viewers), a transparent one shows them.
        var covered = BaseOutfit();
        covered[1] = Wear(WearableKind.Skin, new() { [111] = 0.5f, [700] = 0.15f, [701] = 1f }, (TextureSlot.HeadBodypaint, Flat(64, 64, 200, 150, 120)));
        var lips = FindMaskPixel("lipstick_alpha.tga", 256, v => v > 200);
        var plainOpaque = NewCompositor().Bake(BakeChannel.Head, BaseOutfit(), 256).Image;
        Assert.Equal(Px(plainOpaque, lips.X, lips.Y), Px(NewCompositor().Bake(BakeChannel.Head, covered, 256).Image, lips.X, lips.Y));
        var bare = BaseOutfit();
        bare[1] = Wear(WearableKind.Skin, new() { [111] = 0.5f }, (TextureSlot.HeadBodypaint, Flat(64, 64, 200, 150, 120, 0)));
        var plain = NewCompositor().Bake(BakeChannel.Head, bare, 256).Image;
        var outfit = BaseOutfit();
        outfit[1] = Wear(WearableKind.Skin, new() { [111] = 0.5f, [700] = 0.15f, [701] = 1f }, (TextureSlot.HeadBodypaint, Flat(64, 64, 200, 150, 120, 0)));
        var result = NewCompositor().Bake(BakeChannel.Head, outfit, 256);
        Assert.NotEqual(Px(plain, lips.X, lips.Y), Px(result.Image, lips.X, lips.Y));
        Assert.Contains(result.Layers, l => l.Layer == "lipstick" && l.Status == "drawn");
        Assert.Contains(result.Layers, l => l.Layer == "facialhair" && l.Detail.Contains("skip"));  // male-only masks fall back to their defaults on a female shape
    }

    [Fact]
    public void the_fidelity_gate_names_what_it_cannot_bake_and_passes_a_classic_outfit()
    {
        var c = NewCompositor();
        var bakes = new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair };
        var classic = new List<FidelityCheck.WornSummary>
        {
            new(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>()),
            new(WearableKind.Skin, "Skin", new Dictionary<TextureSlot, UUID> { [TextureSlot.HeadBodypaint] = UUID.Random(), [TextureSlot.UpperBodypaint] = UUID.Random(), [TextureSlot.LowerBodypaint] = UUID.Random() }),
            new(WearableKind.Hair, "Hair", new Dictionary<TextureSlot, UUID> { [TextureSlot.Hair] = UUID.Random() }),
            new(WearableKind.Eyes, "Eyes", new Dictionary<TextureSlot, UUID> { [TextureSlot.EyesIris] = UUID.Random() }),
            new(WearableKind.Shirt, "Shirt", new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = UUID.Random() }),
            new(WearableKind.Tattoo, "Tattoo", new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperTattoo] = UUID.Random() }),
            new(WearableKind.Alpha, "Alpha", new Dictionary<TextureSlot, UUID> { [TextureSlot.LowerAlpha] = UUID.Random() }),
            new(WearableKind.Physics, "Physics", new Dictionary<TextureSlot, UUID>()),
        };
        Assert.Empty(FidelityCheck.Check(classic, c, bakes));
        var only = Array.Empty<string>();

        // a universal painting a Bakes-on-Mesh extra slot is fine once that extra bake is being made, and refused otherwise
        var bom = classic.Append(new FidelityCheck.WornSummary(WearableKind.Universal, "Universal 1234", new Dictionary<TextureSlot, UUID> { [TextureSlot.LeftArmTattoo] = UUID.Random() })).ToList();
        var reasons = FidelityCheck.Check(bom, c, bakes).Except(only).ToList();
        Assert.Single(reasons);
        Assert.Contains("LeftArmTattoo", reasons[0]);
        Assert.Empty(FidelityCheck.Check(bom, c, bakes.Append(BakeChannel.LeftArm)).Except(only));

        var universalClassicOnly = classic.Append(new FidelityCheck.WornSummary(WearableKind.Universal, "Universal", new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperUniversalTattoo] = UUID.Random() })).ToList();
        Assert.Empty(FidelityCheck.Check(universalClassicOnly, c, bakes).Except(only));

        var unknown = classic.Append(new FidelityCheck.WornSummary((WearableKind)99, "Mystery", new Dictionary<TextureSlot, UUID>())).ToList();
        Assert.Contains(FidelityCheck.Check(unknown, c, bakes), r => r.Contains("not composited"));

        var twoShirts = classic.Append(new FidelityCheck.WornSummary(WearableKind.Shirt, "Shirt 2", new Dictionary<TextureSlot, UUID>())).ToList();
        Assert.Empty(FidelityCheck.Check(twoShirts, c, bakes).Except(only));   // multi-wearables are layered
        var twoSkins = classic.Append(new FidelityCheck.WornSummary(WearableKind.Skin, "Skin 2", new Dictionary<TextureSlot, UUID>())).ToList();
        Assert.Contains(FidelityCheck.Check(twoSkins, c, bakes), r => r.Contains("2 Skin"));
    }

    [Fact]
    public void the_morph_mask_follows_gatherMorphMaskAlpha()
    {
        // Docs/MORPH-MASK-PASS.md: 255 everywhere, times the mask of each worn instance of the set's morph-mask layers
        // (head: facialhair per hair; upper_body: upper_clothes per shirt; lower_body: lower_pants per pants).
        var lad = Lad;
        Assert.Equal(new[] { "facialhair" }, lad.MorphMaskLayers["head"].ToArray());
        Assert.Contains("upper_clothes", lad.MorphMaskLayers["upper_body"]);
        Assert.Contains("lower_pants", lad.MorphMaskLayers["lower_body"]);
        Assert.False(lad.MorphMaskLayers.ContainsKey("eyes"));
        var c = NewCompositor();

        // female shape: every facialhair parameter is male-only and skip_if_zero, so the mask is 0 and the head's morph mask is 0
        var head = c.Bake(BakeChannel.Head, BaseOutfit(male: false), 64);
        Assert.All(head.MorphMask, v => Assert.Equal(0, v));
        Assert.Contains(head.Layers, l => l.Layer == "facialhair" && l.Status == "morph");
        // no morph-mask layers at all: 255
        Assert.All(c.Bake(BakeChannel.Eyes, BaseOutfit(), 64).MorphMask, v => Assert.Equal(255, v));
        Assert.All(c.Bake(BakeChannel.Hair, BaseOutfit(), 64).MorphMask, v => Assert.Equal(255, v));
        // upper body with no shirt worn: the upper_clothes layer has no instance, mask stays 255
        Assert.All(c.Bake(BakeChannel.Upper, BaseOutfit(), 64).MorphMask, v => Assert.Equal(255, v));
        // with a short-sleeved shirt: the morph mask is the shirt's mask (torso 255, wrist 0)
        var others = new[] { "shirt_bottom_alpha.tga", "shirt_collar_alpha.tga", "shirt_collar_back_alpha.tga" };
        var torso = FindMaskPixel("shirt_sleeve_alpha.tga", 256, v => v == 255, others);
        var wrist = FindMaskPixel("shirt_sleeve_alpha.tga", 256, v => v is > 60 and < 120, others);
        var outfit = BaseOutfit();
        outfit.Add(Wear(WearableKind.Shirt, new() { [800] = 0f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, (TextureSlot.UpperShirt, Flat(16, 16, 0, 0, 255))));
        var upper = c.Bake(BakeChannel.Upper, outfit, 256);
        Assert.Equal(255, upper.MorphMask[torso.Y * 256 + torso.X]);
        Assert.Equal(0, upper.MorphMask[wrist.Y * 256 + wrist.X]);
        Assert.Contains(upper.Layers, l => l.Layer == "upper_clothes" && l.Status == "morph");
    }

    [Fact]
    public void a_no_texture_layer_renders_once_with_the_last_worn_wearables_parameters()
    {
        // lltexlayer.cpp:64 isUserSettable() is mLocalTexture != -1; :290-297 only such layers become LLTexLayerTemplate (rendered
        // once per worn wearable, :1659-1689). facialhair has no local texture, so it is a plain LLTexLayer: rendered ONCE, from the
        // avatar's parameters, which each worn wearable's writeToAvatar overwrites in wear order — the last hair's values apply.
        // Predicted: two hairs (moustache 0 then 1) == a single hair with moustache 1, != a single hair with moustache 0, and the
        // facialhair layer appears exactly once in the report and once in the morph-mask gather.
        var c = NewCompositor();
        WornWearable Hair(float moustache) => Wear(WearableKind.Hair, new() { [114] = 0.5f, [1007] = moustache }, (TextureSlot.Hair, Flat(32, 32, 255, 255, 255, 0)));
        List<WornWearable> Outfit(params WornWearable[] hairs)
        {
            var o = BaseOutfit(male: true);
            // facialhair sits below head_bodypaint in avatar_lad.xml; a transparent skin texture lets it show (as in the makeup test)
            o[1] = Wear(WearableKind.Skin, new() { [111] = 0.5f }, (TextureSlot.HeadBodypaint, Flat(64, 64, 200, 150, 120, 0)));
            o.RemoveAt(2);   // the base hair
            o.AddRange(hairs);
            return o;
        }
        var first = c.Bake(BakeChannel.Head, Outfit(Hair(0f)), 128);
        var second = c.Bake(BakeChannel.Head, Outfit(Hair(1f)), 128);
        var both = c.Bake(BakeChannel.Head, Outfit(Hair(0f), Hair(1f)), 128);

        string Diag(string tag, CompositeResult r) => $"{tag}: " + string.Join(" | ", r.Layers.Where(l => l.Layer == "facialhair").Select(l => $"{l.Status}: {l.Detail}")) + $" | mask max {r.MorphMask.Max()}";
        var diag = Diag("first", first) + "\n" + Diag("second", second) + "\n" + Diag("both", both);
        Assert.True(1 == both.Layers.Count(l => l.Layer == "facialhair" && l.Status is "drawn" or "skipped"), diag);
        Assert.True(1 == both.Layers.Count(l => l.Layer == "facialhair" && l.Status == "morph"), diag);
        Assert.True(both.Layers.Any(l => l.Layer == "facialhair" && l.Status == "morph" && l.Detail.StartsWith("once (plain layer)")), diag);

        // colour pass: the moustache is drawn from the last hair's parameter
        Assert.True(second.Layers.Any(l => l.Layer == "facialhair" && l.Status == "drawn"), diag);
        Assert.True(first.Layers.Any(l => l.Layer == "facialhair" && l.Status == "skipped"), diag);
        Assert.Equal(second.Image.R, both.Image.R); Assert.Equal(second.Image.G, both.Image.G); Assert.Equal(second.Image.B, both.Image.B);
        Assert.NotEqual(first.Image.R, both.Image.R);
        // morph mask (5th component): once, with the last hair's parameter — not the product of both hairs' masks (which would be 0)
        Assert.Equal(second.MorphMask, both.MorphMask);
        Assert.All(first.MorphMask, v => Assert.Equal(0, v));
        Assert.True(both.MorphMask.Max() > 32, diag);   // non-zero (the mask is box-averaged 16:1 at this size, so its peak is well below 255)
    }

    [Fact]
    public void the_male_parameter_is_read_from_the_shape()
    {
        var c = NewCompositor();
        Assert.False(c.IsMale(BaseOutfit(male: false)));
        Assert.True(c.IsMale(BaseOutfit(male: true)));
    }

    // ---------------- Bakes-on-Mesh extra bakes and multi-wearables ----------------

    [Fact]
    public void the_extra_bakes_composite_the_aux_base_and_the_universal_tattoo()
    {
        var c = NewCompositor();
        var plain = c.Bake(BakeChannel.LeftArm, BaseOutfit(), 64).Image;
        var mid = Px(plain, 32, 32);
        Assert.True(Math.Abs(mid.R - 128) < 6 && Math.Abs(mid.G - 128) < 6 && Math.Abs(mid.B - 128) < 6, $"aux_base.tga x fixed grey, got {mid}");
        Assert.True(mid.A < 8, $"an unpainted extra bake is (almost) transparent, as aux_base.tga's alpha says; got {mid.A}");
        var outfit = BaseOutfit();
        outfit.Add(Wear(WearableKind.Universal, new() { [1238] = 1f, [1239] = 0f, [1240] = 0f }, (TextureSlot.LeftArmTattoo, Flat(16, 16, 255, 255, 255))));
        var result = c.Bake(BakeChannel.LeftArm, outfit, 64);
        var p = Px(result.Image, 32, 32);
        Assert.True(p.R > 200 && p.G < 40 && p.B < 40, $"white tattoo tinted red by tattoo_universal_red, got {p}");
        Assert.Equal(255, p.A);
        Assert.Contains(result.Layers, l => l.Layer == "leftarm_tattoo" && l.Status == "drawn");
        foreach (var bt in new[] { BakeChannel.LeftLeg, BakeChannel.Aux1, BakeChannel.Aux2, BakeChannel.Aux3 })
            Assert.Equal(64, c.Bake(bt, outfit, 64).Image.W);
    }

    [Fact]
    public void two_shirts_layer_in_wear_order_each_with_its_own_parameters()
    {
        // first shirt long-sleeved red, second short-sleeved blue: the torso is blue (on top), the wrist stays red (only the first covers it)
        var others = new[] { "shirt_bottom_alpha.tga", "shirt_collar_alpha.tga", "shirt_collar_back_alpha.tga" };
        var torso = FindMaskPixel("shirt_sleeve_alpha.tga", 256, v => v == 255, others);
        var wrist = FindMaskPixel("shirt_sleeve_alpha.tga", 256, v => v is > 60 and < 120, others);
        var outfit = BaseOutfit();
        outfit.Add(Wear(WearableKind.Shirt, new() { [800] = 1f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 1f, [804] = 0f, [805] = 0f }, (TextureSlot.UpperShirt, Flat(16, 16, 255, 255, 255))));
        outfit.Add(Wear(WearableKind.Shirt, new() { [800] = 0f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, (TextureSlot.UpperShirt, Flat(16, 16, 255, 255, 255))));
        var result = NewCompositor().Bake(BakeChannel.Upper, outfit, 256);
        var t = Px(result.Image, torso.X, torso.Y);
        var w = Px(result.Image, wrist.X, wrist.Y);
        Assert.True(t.B > 200 && t.R < 40, $"torso shows the second (blue) shirt, got {t}");
        Assert.True(w.R > 200 && w.B < 40, $"wrist shows the first (red, long-sleeved) shirt, got {w}");
        Assert.Equal(2, result.Layers.Count(l => l.Layer == "upper_clothes" && l.Status == "drawn"));
    }
}
