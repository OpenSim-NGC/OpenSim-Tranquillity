/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// Composites one baked-texture channel from the worn wearables, the bundled avatar_lad.xml and its character
/// images. It is written from Docs/BAKE-COMPOSITOR-SPEC.md ("SPEC.md" in comments), which describes the pixel rules
/// in plain words; section numbers in comments refer to it. The public Second Life wiki documents the concepts only
/// (wearables as parameter values plus textures, alpha wearables hiding a region):
/// https://wiki.secondlife.com/wiki/Avatar_Appearance and
/// https://wiki.secondlife.com/wiki/Viewerhelp:Appearance_Editor_-_Alpha . The layer sets, parameters and colours
/// come from the avatar shape data described at https://wiki.secondlife.com/wiki/Mesh/Avatar_Shape_XML_Format .
/// <para>
/// All arithmetic follows the spec: bytes with the byte product of section 1, single-precision floats for colours,
/// weights and resampling. The compositor holds no per-bake state, so one instance can serve concurrent bakes.
/// </para>
/// </summary>
public sealed partial class AvatarBakeCompositor
{
    /// <summary>SPEC.md 3.5: a layer's local_texture name, the texture slot it stands for and the wearable type that owns the slot.</summary>
    private static readonly Dictionary<string, (TextureSlot Slot, WearableKind Kind)> SlotTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["head_bodypaint"] = (TextureSlot.HeadBodypaint, WearableKind.Skin),
        ["upper_bodypaint"] = (TextureSlot.UpperBodypaint, WearableKind.Skin),
        ["lower_bodypaint"] = (TextureSlot.LowerBodypaint, WearableKind.Skin),
        ["upper_shirt"] = (TextureSlot.UpperShirt, WearableKind.Shirt),
        ["lower_pants"] = (TextureSlot.LowerPants, WearableKind.Pants),
        ["eyes_iris"] = (TextureSlot.EyesIris, WearableKind.Eyes),
        ["hair_grain"] = (TextureSlot.Hair, WearableKind.Hair),
        ["lower_shoes"] = (TextureSlot.LowerShoes, WearableKind.Shoes),
        ["lower_socks"] = (TextureSlot.LowerSocks, WearableKind.Socks),
        ["upper_jacket"] = (TextureSlot.UpperJacket, WearableKind.Jacket),
        ["lower_jacket"] = (TextureSlot.LowerJacket, WearableKind.Jacket),
        ["upper_gloves"] = (TextureSlot.UpperGloves, WearableKind.Gloves),
        ["upper_undershirt"] = (TextureSlot.UpperUndershirt, WearableKind.Undershirt),
        ["lower_underpants"] = (TextureSlot.LowerUnderpants, WearableKind.Underpants),
        ["skirt"] = (TextureSlot.Skirt, WearableKind.Skirt),
        ["lower_alpha"] = (TextureSlot.LowerAlpha, WearableKind.Alpha),
        ["upper_alpha"] = (TextureSlot.UpperAlpha, WearableKind.Alpha),
        ["head_alpha"] = (TextureSlot.HeadAlpha, WearableKind.Alpha),
        ["eyes_alpha"] = (TextureSlot.EyesAlpha, WearableKind.Alpha),
        ["hair_alpha"] = (TextureSlot.HairAlpha, WearableKind.Alpha),
        ["head_tattoo"] = (TextureSlot.HeadTattoo, WearableKind.Tattoo),
        ["upper_tattoo"] = (TextureSlot.UpperTattoo, WearableKind.Tattoo),
        ["lower_tattoo"] = (TextureSlot.LowerTattoo, WearableKind.Tattoo),
        ["head_universal_tattoo"] = (TextureSlot.HeadUniversalTattoo, WearableKind.Universal),
        ["upper_universal_tattoo"] = (TextureSlot.UpperUniversalTattoo, WearableKind.Universal),
        ["lower_universal_tattoo"] = (TextureSlot.LowerUniversalTattoo, WearableKind.Universal),
        ["skirt_tattoo"] = (TextureSlot.SkirtTattoo, WearableKind.Universal),
        ["hair_tattoo"] = (TextureSlot.HairTattoo, WearableKind.Universal),
        ["eyes_tattoo"] = (TextureSlot.EyesTattoo, WearableKind.Universal),
        ["leftarm_tattoo"] = (TextureSlot.LeftArmTattoo, WearableKind.Universal),
        ["leftleg_tattoo"] = (TextureSlot.LeftLegTattoo, WearableKind.Universal),
        ["aux1_tattoo"] = (TextureSlot.Aux1Tattoo, WearableKind.Universal),
        ["aux2_tattoo"] = (TextureSlot.Aux2Tattoo, WearableKind.Universal),
        ["aux3_tattoo"] = (TextureSlot.Aux3Tattoo, WearableKind.Universal),
    };

    /// <summary>SPEC.md section 2: the layer_set body_region each bake channel selects.</summary>
    private static string RegionOf(BakeChannel ch) => ch switch
    {
        BakeChannel.Head => "head",
        BakeChannel.Upper => "upper_body",
        BakeChannel.Lower => "lower_body",
        BakeChannel.Eyes => "eyes",
        BakeChannel.Hair => "hair",
        BakeChannel.Skirt => "skirt",
        BakeChannel.LeftArm => "leftarm",
        BakeChannel.LeftLeg => "leftleg",
        BakeChannel.Aux1 => "aux1",
        BakeChannel.Aux2 => "aux2",
        BakeChannel.Aux3 => "aux3",
        _ => throw new ArgumentOutOfRangeException(nameof(ch)),
    };

    private readonly AvatarLad _lad;
    private readonly BakeResourceFiles _files;

    /// <summary>A compositor over the embedded avatar_lad.xml and character images.</summary>
    public AvatarBakeCompositor() : this(AvatarLad.Embedded, BakeResourceFiles.Embedded) { }

    public AvatarBakeCompositor(AvatarLad lad, BakeResourceFiles files)
    {
        _lad = lad;
        _files = files;
    }

    public AvatarLad Lad => _lad;
    public BakeResourceFiles Files => _files;

    /// <summary>The texture slot a layer's local_texture name stands for (SPEC.md 3.5), or null for a name the table does not list.</summary>
    public static TextureSlot? SlotFromName(string localTexture)
        => SlotTable.TryGetValue(localTexture, out var e) ? e.Slot : null;

    /// <summary>The texture slots the colour layers of a channel draw (bump layers are never drawn, SPEC.md section 5).</summary>
    public IEnumerable<TextureSlot> SlotsOf(BakeChannel channel)
    {
        if (!_lad.LayerSets.TryGetValue(RegionOf(channel), out var set)) return Enumerable.Empty<TextureSlot>();
        return set.Layers
            .Where(l => !l.Bump && l.LocalTexture is not null && SlotTable.ContainsKey(l.LocalTexture))
            .Select(l => SlotTable[l.LocalTexture!].Slot)
            .Distinct()
            .ToList();
    }

    /// <summary>Whether the avatar is male: parameter 80 above 0.5 (SPEC.md 4.6).</summary>
    public bool IsMale(IReadOnlyList<BakeWearable> worn) => new BakeParameterView(_lad, worn, null).IsMale;

    /// <summary>
    /// Composites one channel at <paramref name="size"/> by <paramref name="size"/> pixels (SPEC.md sections 5 to 13).
    /// </summary>
    /// <param name="channel">The channel to bake.</param>
    /// <param name="worn">The worn wearables in wear order.</param>
    /// <param name="size">The bake size N; the data's own layer set width and height are ignored.</param>
    /// <param name="callerValues">Optional caller-supplied parameter values; a worn wearable's stored value wins (SPEC.md 4.2).</param>
    /// <exception cref="InvalidOperationException">The data has no layer set for the channel.</exception>
    public BakeComposite Bake(BakeChannel channel, IReadOnlyList<BakeWearable> worn, int size, IReadOnlyDictionary<int, float>? callerValues = null)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var region = RegionOf(channel);
        if (!_lad.LayerSets.TryGetValue(region, out var set))
            throw new InvalidOperationException($"avatar_lad.xml has no layer_set for body_region '{region}'");
        return new BakeRun(this, set, worn, size, callerValues).Run();
    }

    /// <summary>The state of one bake. Nothing in it outlives the call.</summary>
    private sealed class BakeRun
    {
        private readonly AvatarBakeCompositor _owner;
        private readonly AvatarLad _lad;
        private readonly BakeResourceFiles _files;
        private readonly LayerSetDef _set;
        private readonly IReadOnlyList<BakeWearable> _worn;
        private readonly int _n;
        private readonly int _pixels;
        private readonly BakeParameterView _params;
        private readonly byte[] _r, _g, _b, _a;
        private readonly byte[] _morph;
        private readonly List<BakeLayerReport> _report = new();
        /// <summary>SPEC.md 6.2: the mask each (layer, wearable instance) made during the colour pass, kept for the morph step.</summary>
        private readonly Dictionary<(int Layer, BakeWearable? Instance), byte[]> _masks = new();
        private readonly Dictionary<(string File, float Domain, int Q), byte[]?> _rampCache = new();

        public BakeRun(AvatarBakeCompositor owner, LayerSetDef set, IReadOnlyList<BakeWearable> worn, int size, IReadOnlyDictionary<int, float>? callerValues)
        {
            _owner = owner;
            _lad = owner._lad;
            _files = owner._files;
            _set = set;
            _worn = worn;
            _n = size;
            _pixels = size * size;
            _params = new BakeParameterView(_lad, worn, callerValues);
            _r = new byte[_pixels];
            _g = new byte[_pixels];
            _b = new byte[_pixels];
            _a = new byte[_pixels];
            Array.Fill(_a, (byte)255);
            _morph = new byte[_pixels];
            Array.Fill(_morph, (byte)255);
        }

        public BakeComposite Run()
        {
            // SPEC.md 11.1: an alpha wearable carrying the invisible texture hides the whole region before anything is drawn.
            if (IsInvisible())
            {
                Array.Clear(_a);
                _report.Add(new BakeLayerReport("", "invisible", "an alpha wearable hides the whole region", WearableKind.Alpha));
                return Finish(invisible: true);
            }

            // SPEC.md section 5, steps 3 to 5.
            for (var li = 0; li < _set.Layers.Count; li++)
            {
                var layer = _set.Layers[li];
                if (layer.Bump || layer.VisibilityMask) continue;
                if (layer.LocalTexture is null)
                {
                    DrawLayer(li, layer, null);
                    continue;
                }
                if (!SlotTable.TryGetValue(layer.LocalTexture, out var slot))
                {
                    _report.Add(new BakeLayerReport(layer.Name, "skipped", $"unknown local texture '{layer.LocalTexture}'", null));
                    continue;
                }
                var instances = _worn.Where(w => w.Kind == slot.Kind).ToList();
                if (instances.Count == 0)
                {
                    _report.Add(new BakeLayerReport(layer.Name, "skipped", $"no {slot.Kind} worn", null));
                    continue;
                }
                foreach (var inst in instances) DrawLayer(li, layer, inst);
            }

            ApplyMorphMask();
            ApplyFinalAlpha();
            return Finish(invisible: false);
        }

        private BakeComposite Finish(bool invisible) => new()
        {
            Size = _n,
            R = _r, G = _g, B = _b, A = _a,
            MorphMask = _morph,
            Invisible = invisible,
            NothingDrawn = !invisible && !_report.Any(e => e.Status == "drawn"),
            Layers = _report,
        };

        // ---- SPEC.md 11.1 ---------------------------------------------------------------------------------------

        private bool IsInvisible()
        {
            foreach (var layer in _set.Layers)
            {
                if (!layer.VisibilityMask || layer.LocalTexture is null) continue;
                if (!SlotTable.TryGetValue(layer.LocalTexture, out var slot)) continue;
                foreach (var w in _worn)
                    if (w.Kind == slot.Kind && w.TextureIds.TryGetValue(slot.Slot, out var id) && id == BakeConstants.InvisibleTexture)
                        return true;
            }
            return false;
        }

        // ---- SPEC.md section 6: drawing one layer instance ------------------------------------------------------

        private void DrawLayer(int layerIndex, LayerDef layer, BakeWearable? inst)
        {
            var kind = inst?.Kind;

            // 6.1 colour
            var (colour, specified) = NetColour(layer, inst);
            if (colour.A < 0.0001f)
            {
                _report.Add(new BakeLayerReport(layer.Name, "skipped", $"colour alpha 0 {colour}", kind));
                return;
            }
            var tr = ToByte(colour.R); var tg = ToByte(colour.G); var tb = ToByte(colour.B); var ta = ToByte(colour.A);

            // 6.2 mask
            byte[]? mask = null;
            if (layer.AlphaParams.Count > 0)
            {
                var built = BuildMask(layer, inst, colour, _a);
                if (built.Mask is null)
                {
                    _report.Add(new BakeLayerReport(layer.Name, "skipped", $"missing mask image: {built.Missing}", kind));
                    return;
                }
                mask = built.Mask;
                Array.Copy(mask, _a, _pixels);
                _masks[(layerIndex, inst)] = mask;
                if (!built.Applied && !built.FirstIsMultiply)
                {
                    _report.Add(new BakeLayerReport(layer.Name, "skipped", "every alpha parameter skipped: mask is zero", kind));
                    return;
                }
            }

            var drawn = false;
            var detail = new List<string>();

            // 6.3 item 1: the wearable's texture
            if (layer.LocalTexture is not null && !layer.LocalTextureAlphaOnly)
            {
                var slot = SlotTable[layer.LocalTexture].Slot;
                if (inst is null || !inst.Textures.TryGetValue(slot, out var tex))
                {
                    _report.Add(new BakeLayerReport(layer.Name, "skipped", $"{kind}: no {slot} texture on the wearable", kind));
                    return;
                }
                DrawImage(Resampled(tex), mask, tr, tg, tb, ta, layer.WriteAllChannels);
                drawn = true;
                detail.Add($"{slot} texture");
            }

            // 6.3 item 2: the bundled image
            if (layer.StaticImage is not null)
            {
                if (layer.StaticIsMask)
                {
                    var grey = Grey(layer.StaticImage);
                    if (grey is null)
                    {
                        _report.Add(new BakeLayerReport(layer.Name, drawn ? "drawn" : "skipped", string.Join("; ", detail.Append($"missing file {layer.StaticImage}")), kind));
                        return;
                    }
                    var perPixel = mask;
                    if (perPixel is null)
                    {
                        perPixel = new byte[_pixels];
                        for (var i = 0; i < _pixels; i++) perPixel[i] = ta == 255 ? grey[i] : BakePixelMath.ByteProduct(grey[i], ta);
                    }
                    DrawFill(tr, tg, tb, perPixel, 255, layer.WriteAllChannels);
                }
                else
                {
                    var img = _files.Colour(layer.StaticImage);
                    if (img is null)
                    {
                        _report.Add(new BakeLayerReport(layer.Name, drawn ? "drawn" : "skipped", string.Join("; ", detail.Append($"missing file {layer.StaticImage}")), kind));
                        return;
                    }
                    DrawImage(Resampled(img), mask, tr, tg, tb, ta, layer.WriteAllChannels);
                }
                drawn = true;
                detail.Add(layer.StaticImage);
            }

            // 6.3 item 3: a flat fill
            if ((layer.LocalTexture is null || layer.LocalTextureAlphaOnly) && layer.StaticImage is null && specified)
            {
                DrawFill(tr, tg, tb, mask, ta, layer.WriteAllChannels);
                drawn = true;
                detail.Add($"fill {colour}");
            }

            _report.Add(new BakeLayerReport(layer.Name, drawn ? "drawn" : "skipped", drawn ? string.Join("; ", detail) : "nothing to draw", kind));
        }

        /// <summary>SPEC.md 6.3: a float colour component as a byte, multiplied by 255 in single precision and rounded to even.</summary>
        private static byte ToByte(float f) => (byte)Math.Clamp(MathF.Round(f * 255f), 0f, 255f);

        /// <summary>SPEC.md 6.4: draws a source image through the tint, with the layer's mask as its alpha when it has one.</summary>
        private void DrawImage((byte[] R, byte[] G, byte[] B, byte[]? A) src, byte[]? mask, byte tr, byte tg, byte tb, byte ta, bool writeAll)
        {
            for (var i = 0; i < _pixels; i++)
            {
                var r = BakePixelMath.ByteProduct(src.R[i], tr);
                var g = BakePixelMath.ByteProduct(src.G[i], tg);
                var b = BakePixelMath.ByteProduct(src.B[i], tb);
                var a = mask is not null ? mask[i] : BakePixelMath.ByteProduct(src.A is null ? 255 : src.A[i], ta);
                if (writeAll)
                {
                    _r[i] = r; _g[i] = g; _b[i] = b; _a[i] = a;
                }
                else if (a != 0)
                {
                    Blend(i, r, g, b, a, alphaFollowsSource: mask is null);
                }
            }
        }

        /// <summary>SPEC.md 6.5: draws the tint colour with a per-pixel alpha (or a constant one); the canvas alpha never follows the source.</summary>
        private void DrawFill(byte tr, byte tg, byte tb, byte[]? perPixelAlpha, byte constantAlpha, bool writeAll)
        {
            for (var i = 0; i < _pixels; i++)
            {
                var a = perPixelAlpha is not null ? perPixelAlpha[i] : constantAlpha;
                if (writeAll)
                {
                    _r[i] = tr; _g[i] = tg; _b[i] = tb; _a[i] = a;
                }
                else if (a != 0)
                {
                    Blend(i, tr, tg, tb, a, alphaFollowsSource: false);
                }
            }
        }

        /// <summary>SPEC.md 6.6.</summary>
        private void Blend(int i, byte sr, byte sg, byte sb, byte a, bool alphaFollowsSource)
        {
            if (a == 255)
            {
                _r[i] = sr; _g[i] = sg; _b[i] = sb;
                if (alphaFollowsSource) _a[i] = 255;
                return;
            }
            _r[i] = (byte)(_r[i] + ((sr - _r[i]) * a + 127) / 255);
            _g[i] = (byte)(_g[i] + ((sg - _g[i]) * a + 127) / 255);
            _b[i] = (byte)(_b[i] + ((sb - _b[i]) * a + 127) / 255);
            if (alphaFollowsSource) _a[i] = (byte)((a * a + _a[i] * (255 - a) + 127) / 255);
        }

        // ---- SPEC.md section 7: colour --------------------------------------------------------------------------

        /// <summary>SPEC.md 7.1: the net colour of a layer and whether a colour was specified.</summary>
        private (Rgba Colour, bool Specified) NetColour(LayerDef layer, BakeWearable? inst)
        {
            if (layer.ColorParams.Count > 0)
            {
                var c = layer.GlobalColor is not null
                    ? GlobalColour(layer.GlobalColor, inst)
                    : layer.FixedColor.A != 0f ? layer.FixedColor : Rgba.Transparent;
                foreach (var id in layer.ColorParams) c = Fold(c, id, inst);
                return (c.Clamp(), true);
            }
            if (layer.GlobalColor is not null) return (GlobalColour(layer.GlobalColor, inst), true);
            if (layer.FixedColor.A != 0f) return (layer.FixedColor, true);
            return (Rgba.White, false);
        }

        /// <summary>SPEC.md 7.2.</summary>
        private Rgba GlobalColour(string name, BakeWearable? inst)
        {
            if (!_lad.GlobalColors.TryGetValue(name, out var ids) || ids.Count == 0) return Rgba.White;
            var c = Rgba.Transparent;
            foreach (var id in ids) c = Fold(c, id, inst);
            return c.Clamp();
        }

        /// <summary>SPEC.md 7.3: folds one colour parameter into the running colour, without clamping.</summary>
        private Rgba Fold(Rgba c, int id, BakeWearable? inst)
        {
            if (!_lad.Params.TryGetValue(id, out var def) || def.Color is null) return c;
            var p = StopColour(def, inst);
            return def.Color.Op switch
            {
                ColorOp.Multiply => c * p,
                ColorOp.Blend => Rgba.Lerp(c, p, _params.Weight(id, inst)),
                _ => c + p,
            };
        }

        /// <summary>SPEC.md 7.4: the colour a parameter gives at its effective weight, interpolated between its colour stops.</summary>
        private Rgba StopColour(ParamDef def, BakeWearable? inst)
        {
            var stops = def.Color!.Colors;
            var count = stops.Count;
            if (count == 0) return Rgba.Transparent;
            var e = _params.EffectiveWeight(def, inst);
            var s = e * (count - 1);
            var i = Math.Clamp((int)s, 0, count - 1);
            if (i == count - 1) return stops[count - 1];
            return Rgba.Lerp(stops[i], stops[i + 1], s - i);
        }

        // ---- SPEC.md section 8: alpha masks ---------------------------------------------------------------------

        private readonly record struct MaskResult(byte[]? Mask, bool Applied, bool FirstIsMultiply, string Missing);

        /// <summary>
        /// SPEC.md 8.1. <paramref name="canvasAlpha"/> is the alpha plane a multiply-first layer starts from; it is only read.
        /// A null mask means a needed mask image could not be loaded.
        /// </summary>
        private MaskResult BuildMask(LayerDef layer, BakeWearable? inst, Rgba colour, byte[] canvasAlpha)
        {
            var firstIsMultiply = _lad.Params.TryGetValue(layer.AlphaParams[0], out var first) && first.Alpha is { MultiplyBlend: true };
            var mask = new byte[_pixels];
            if (firstIsMultiply) Array.Copy(canvasAlpha, mask, _pixels);

            var applied = 0;
            foreach (var id in layer.AlphaParams)
            {
                if (!_lad.Params.TryGetValue(id, out var def) || def.Alpha is null || def.Alpha.TgaFile.Length == 0) continue;
                if (_params.SkipAlphaParam(def, inst)) continue;
                var e = _params.EffectiveWeight(def, inst);
                var img = RampedMask(def.Alpha.TgaFile, def.Alpha.Domain, e);
                if (img is null) return new MaskResult(null, false, firstIsMultiply, def.Alpha.TgaFile);
                if (def.Alpha.MultiplyBlend)
                    for (var i = 0; i < _pixels; i++) mask[i] = BakePixelMath.ByteProduct(mask[i], img[i]);
                else
                    for (var i = 0; i < _pixels; i++) mask[i] = (byte)Math.Min(255, mask[i] + img[i]);
                applied++;
            }
            if (applied == 0 && !firstIsMultiply) return new MaskResult(mask, false, false, "");

            // step 4: the wearable texture's own alpha, for any layer that names a slot
            if (layer.LocalTexture is not null && inst is not null && SlotTable.TryGetValue(layer.LocalTexture, out var slot)
                && inst.Textures.TryGetValue(slot.Slot, out var tex) && tex.A is not null)
            {
                var alpha = BakePixelMath.Resample(tex.A, tex.Width, tex.Height, _n, _n);
                for (var i = 0; i < _pixels; i++) mask[i] = BakePixelMath.ByteProduct(mask[i], alpha[i]);
            }
            // step 5: a bundled file used as a mask
            if (layer.StaticImage is not null && layer.StaticIsMask)
            {
                var grey = Grey(layer.StaticImage);
                if (grey is null) return new MaskResult(null, false, firstIsMultiply, layer.StaticImage);
                for (var i = 0; i < _pixels; i++) mask[i] = BakePixelMath.ByteProduct(mask[i], grey[i]);
            }
            // step 6: the layer colour's own alpha
            if (MathF.Abs(colour.A - 1f) > 0.0001f)
            {
                var ca = ToByte(colour.A);
                for (var i = 0; i < _pixels; i++) mask[i] = BakePixelMath.ByteProduct(mask[i], ca);
            }
            return new MaskResult(mask, applied > 0, firstIsMultiply, "");
        }

        /// <summary>SPEC.md 8.2: the file's grey values ramped for the weight at the file's own size, then resampled to N.</summary>
        private byte[]? RampedMask(string file, float domain, float weight)
        {
            var q = (int)MathF.Round(Math.Clamp(weight, 0f, 1f) * 255f);
            var key = (file, domain, q);
            if (_rampCache.TryGetValue(key, out var cached)) return cached;
            byte[]? result = null;
            if (_files.Grey(file) is { } g)
            {
                var ramped = BakePixelMath.RampPlane(g.Data, domain, q / 255f);
                result = BakePixelMath.Resample(ramped, g.W, g.H, _n, _n);
            }
            _rampCache[key] = result;
            return result;
        }

        // ---- SPEC.md sections 9 and 12: images at the bake size -------------------------------------------------

        /// <summary>The grey reading of a bundled file resampled to N, or null when it cannot be loaded.</summary>
        private byte[]? Grey(string file)
            => _files.Grey(file) is { } g ? BakePixelMath.Resample(g.Data, g.W, g.H, _n, _n) : null;

        private (byte[] R, byte[] G, byte[] B, byte[]? A) Resampled(BakeBitmap bmp)
            => (BakePixelMath.Resample(bmp.R, bmp.Width, bmp.Height, _n, _n),
                BakePixelMath.Resample(bmp.G, bmp.Width, bmp.Height, _n, _n),
                BakePixelMath.Resample(bmp.B, bmp.Width, bmp.Height, _n, _n),
                bmp.A is null ? null : BakePixelMath.Resample(bmp.A, bmp.Width, bmp.Height, _n, _n));

        // ---- SPEC.md section 10: the morph mask -----------------------------------------------------------------

        private void ApplyMorphMask()
        {
            if (!_lad.MorphMaskLayers.TryGetValue(_set.BodyRegion, out var names)) return;
            for (var li = 0; li < _set.Layers.Count; li++)
            {
                var layer = _set.Layers[li];
                if (layer.Bump || layer.AlphaParams.Count == 0 || !names.Contains(layer.Name)) continue;

                BakeWearable? inst = null;
                if (layer.LocalTexture is not null)
                {
                    if (!SlotTable.TryGetValue(layer.LocalTexture, out var slot)) continue;
                    inst = _worn.LastOrDefault(w => w.Kind == slot.Kind);
                    if (inst is null) continue;
                }

                if (!_masks.TryGetValue((li, inst), out var mask))
                {
                    var (colour, _) = NetColour(layer, inst);
                    var built = BuildMask(layer, inst, colour, _a);
                    if (built.Mask is null) continue;
                    mask = built.Mask;
                }
                for (var i = 0; i < _pixels; i++) _morph[i] = (byte)((_morph[i] * (mask[i] + 1)) >> 8);
                _report.Add(new BakeLayerReport(layer.Name, "morph", inst is null ? "once (plain layer): merged into the morph mask" : "merged into the morph mask", inst?.Kind));
            }
        }

        // ---- SPEC.md 11.2: the final alpha ----------------------------------------------------------------------

        private void ApplyFinalAlpha()
        {
            var hasVisibilityLayer = _set.Layers.Any(l => l.VisibilityMask);
            if (!string.IsNullOrEmpty(_set.StaticAlphaFile))
            {
                var grey = Grey(_set.StaticAlphaFile);
                if (grey is not null) Array.Copy(grey, _a, _pixels);
            }
            else if (_set.ClearAlpha || hasVisibilityLayer)
            {
                Array.Fill(_a, (byte)255);
            }

            foreach (var layer in _set.Layers)
            {
                if (!layer.VisibilityMask || layer.Bump) continue;
                if (layer.StaticImage is not null)
                {
                    byte[]? plane;
                    if (layer.StaticIsMask) plane = Grey(layer.StaticImage);
                    else
                    {
                        var img = _files.Colour(layer.StaticImage);
                        plane = img is null ? null : img.A is null ? null : BakePixelMath.Resample(img.A, img.Width, img.Height, _n, _n);
                        if (img is not null && plane is null) continue;   // no alpha plane: multiplying by 255 changes nothing
                    }
                    if (plane is null) continue;
                    MultiplyAlpha(plane);
                }
                else if (layer.LocalTexture is not null && SlotTable.TryGetValue(layer.LocalTexture, out var slot))
                {
                    foreach (var w in _worn)
                    {
                        if (w.Kind != slot.Kind || !w.Textures.TryGetValue(slot.Slot, out var tex) || tex.A is null) continue;
                        MultiplyAlpha(BakePixelMath.Resample(tex.A, tex.Width, tex.Height, _n, _n));
                    }
                }
            }
        }

        private void MultiplyAlpha(byte[] plane)
        {
            for (var i = 0; i < _pixels; i++) _a[i] = BakePixelMath.ByteProduct(_a[i], plane[i]);
        }
    }
}
