using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>One worn wearable as the compositor sees it: its type, its stored parameters, its decoded textures by slot.</summary>
public sealed class WornWearable
{
    public WearableKind Kind;
    public string Label = "";
    public IReadOnlyDictionary<int, float> Params = new Dictionary<int, float>();
    public IReadOnlyDictionary<TextureSlot, UUID> TextureIds = new Dictionary<TextureSlot, UUID>();
    public IReadOnlyDictionary<TextureSlot, RgbaPlanes> Textures = new Dictionary<TextureSlot, RgbaPlanes>();
}

/// <summary>What happened to one layer of one bake: drawn, or skipped and why. This is the coverage evidence.</summary>
public sealed record LayerReport(string Layer, string Status, string Detail, WearableKind? Wearable);

public sealed class CompositeResult
{
    public required RgbaPlanes Image;
    public required List<LayerReport> Layers;
    public bool Invisible;
    /// <summary>
    /// True when no layer of this set drew anything: every colour layer was skipped. It is a fact about the
    /// layer decisions, never about the pixels — a layer that drew a fully transparent texture (a bald hair) has
    /// drawn, and so has the <see cref="Invisible"/> case, where an alpha wearable deliberately hides the region.
    /// Both of those are legitimate bakes; an undrawn channel is not one, because the set's alpha starts opaque
    /// and nothing carved it, so it encodes as a solid image of whatever the canvas was cleared to.
    /// </summary>
    public bool NothingDrawn;
    /// <summary>
    /// The bake's 5th component: LLTexLayerSet::gatherMorphMaskAlpha — 255 everywhere, multiplied by the alpha
    /// mask of every contributing instance of the set's morph-mask layers (Docs/MORPH-MASK-PASS.md §2).
    /// </summary>
    public required byte[] MorphMask;
}

/// <summary>
/// The bake compositor. It interprets the `layer_set` definitions of avatar_lad.xml the way the viewer's
/// LLTexLayerSet does (layer order, per-layer alpha masks with the domain ramp, colour parameters and global
/// colours, driver parameters, sex gating, write-all-channels layers, visibility masks for the final alpha), on
/// plain byte planes at the requested size. Extracted from the web-viewer gateway (ADR-003), where it replaced
/// the LibreMetaverse Baker, which tiles sub-1024 layers into a mosaic, ignores every parameter-driven layer,
/// and applies masks with hard edges.
/// </summary>
public sealed class TexLayerCompositor
{
    /// <summary>The local_texture names of avatar_lad.xml, in TextureSlot order (the viewer's texture dictionary).</summary>
    private static readonly Dictionary<string, TextureSlot> TextureByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["head_bodypaint"] = TextureSlot.HeadBodypaint, ["upper_shirt"] = TextureSlot.UpperShirt, ["lower_pants"] = TextureSlot.LowerPants,
        ["eyes_iris"] = TextureSlot.EyesIris, ["hair_grain"] = TextureSlot.Hair, ["upper_bodypaint"] = TextureSlot.UpperBodypaint,
        ["lower_bodypaint"] = TextureSlot.LowerBodypaint, ["lower_shoes"] = TextureSlot.LowerShoes, ["lower_socks"] = TextureSlot.LowerSocks,
        ["upper_jacket"] = TextureSlot.UpperJacket, ["lower_jacket"] = TextureSlot.LowerJacket, ["upper_gloves"] = TextureSlot.UpperGloves,
        ["upper_undershirt"] = TextureSlot.UpperUndershirt, ["lower_underpants"] = TextureSlot.LowerUnderpants, ["skirt"] = TextureSlot.Skirt,
        ["lower_alpha"] = TextureSlot.LowerAlpha, ["upper_alpha"] = TextureSlot.UpperAlpha, ["head_alpha"] = TextureSlot.HeadAlpha,
        ["eyes_alpha"] = TextureSlot.EyesAlpha, ["hair_alpha"] = TextureSlot.HairAlpha, ["head_tattoo"] = TextureSlot.HeadTattoo,
        ["upper_tattoo"] = TextureSlot.UpperTattoo, ["lower_tattoo"] = TextureSlot.LowerTattoo, ["head_universal_tattoo"] = TextureSlot.HeadUniversalTattoo,
        ["upper_universal_tattoo"] = TextureSlot.UpperUniversalTattoo, ["lower_universal_tattoo"] = TextureSlot.LowerUniversalTattoo,
        ["skirt_tattoo"] = TextureSlot.SkirtTattoo, ["hair_tattoo"] = TextureSlot.HairTattoo, ["eyes_tattoo"] = TextureSlot.EyesTattoo,
        ["leftarm_tattoo"] = TextureSlot.LeftArmTattoo, ["leftleg_tattoo"] = TextureSlot.LeftLegTattoo, ["aux1_tattoo"] = TextureSlot.Aux1Tattoo,
        ["aux2_tattoo"] = TextureSlot.Aux2Tattoo, ["aux3_tattoo"] = TextureSlot.Aux3Tattoo,
    };

    /// <summary>Which wearable type carries each texture slot (the viewer's texture dictionary).</summary>
    public static WearableKind WearableOf(TextureSlot idx) => idx switch
    {
        TextureSlot.HeadBodypaint or TextureSlot.UpperBodypaint or TextureSlot.LowerBodypaint => WearableKind.Skin,
        TextureSlot.UpperShirt => WearableKind.Shirt,
        TextureSlot.LowerPants => WearableKind.Pants,
        TextureSlot.EyesIris => WearableKind.Eyes,
        TextureSlot.Hair => WearableKind.Hair,
        TextureSlot.LowerShoes => WearableKind.Shoes,
        TextureSlot.LowerSocks => WearableKind.Socks,
        TextureSlot.UpperJacket or TextureSlot.LowerJacket => WearableKind.Jacket,
        TextureSlot.UpperGloves => WearableKind.Gloves,
        TextureSlot.UpperUndershirt => WearableKind.Undershirt,
        TextureSlot.LowerUnderpants => WearableKind.Underpants,
        TextureSlot.Skirt => WearableKind.Skirt,
        TextureSlot.LowerAlpha or TextureSlot.UpperAlpha or TextureSlot.HeadAlpha or TextureSlot.EyesAlpha or TextureSlot.HairAlpha => WearableKind.Alpha,
        TextureSlot.HeadTattoo or TextureSlot.UpperTattoo or TextureSlot.LowerTattoo => WearableKind.Tattoo,
        TextureSlot.HeadUniversalTattoo or TextureSlot.UpperUniversalTattoo or TextureSlot.LowerUniversalTattoo or TextureSlot.SkirtTattoo
            or TextureSlot.HairTattoo or TextureSlot.EyesTattoo or TextureSlot.LeftArmTattoo or TextureSlot.LeftLegTattoo
            or TextureSlot.Aux1Tattoo or TextureSlot.Aux2Tattoo or TextureSlot.Aux3Tattoo => WearableKind.Universal,
        _ => WearableKind.Invalid,
    };

    /// <summary>The `body_region` of the layer_set a bake channel is made from.</summary>
    public static string RegionOf(BakeChannel bt) => bt switch
    {
        BakeChannel.Head => "head", BakeChannel.Upper => "upper_body", BakeChannel.Lower => "lower_body", BakeChannel.Eyes => "eyes",
        BakeChannel.Hair => "hair", BakeChannel.Skirt => "skirt", BakeChannel.LeftArm => "leftarm", BakeChannel.LeftLeg => "leftleg",
        BakeChannel.Aux1 => "aux1", BakeChannel.Aux2 => "aux2", BakeChannel.Aux3 => "aux3", _ => "",
    };

    /// <summary>The wearable type name avatar_lad.xml uses in `wearable=` attributes.</summary>
    public static string TypeName(WearableKind t) => WearableKinds.TypeName(t);

    private readonly AvatarLad _lad;
    private readonly ResourceImages _res;
    private readonly Dictionary<(string File, int Size), Plane> _maskCache = new();
    private readonly Dictionary<(string File, int Size), RgbaPlanes> _imageCache = new();
    private readonly Dictionary<(string File, float Domain, byte Weight, int Size), Plane> _processedCache = new();
    private readonly object _cacheLock = new();

    public TexLayerCompositor(AvatarLad lad, ResourceImages res) { _lad = lad; _res = res; }

    /// <summary>A compositor over the embedded avatar_lad.xml and character images.</summary>
    public TexLayerCompositor() : this(AvatarLad.Embedded, ResourceImages.Embedded) { }

    public AvatarLad Lad => _lad;
    public ResourceImages Resources => _res;

    /// <summary>The texture slots the given bake draws or masks with (its layers' local textures).</summary>
    public IEnumerable<TextureSlot> SlotsOf(BakeChannel bt)
    {
        if (!_lad.LayerSets.TryGetValue(RegionOf(bt), out var set)) yield break;
        foreach (var l in set.Layers)
            if (l.LocalTexture is not null && TextureByName.TryGetValue(l.LocalTexture, out var idx)) yield return idx;
    }

    /// <summary>Every bundled file the given bake's colour layers, masks and visibility masks need.</summary>
    public IEnumerable<string> ResourceFilesOf(BakeChannel bt)
    {
        if (!_lad.LayerSets.TryGetValue(RegionOf(bt), out var set)) yield break;
        if (set.StaticAlphaFile is not null) yield return set.StaticAlphaFile;
        foreach (var l in set.Layers)
        {
            if (l.Bump) continue;
            if (l.StaticImage is not null) yield return l.StaticImage;
            foreach (var pid in l.AlphaParams)
                if (_lad.Params.TryGetValue(pid, out var def) && def.Alpha is { TgaFile: { Length: > 0 } f }) yield return f;
        }
    }

    /// <summary>Every visual parameter id the given bake's layers read: colour and alpha parameters, the global colours' parameters, and the drivers that set them.</summary>
    public IReadOnlyCollection<int> ParamsOf(BakeChannel bt)
    {
        var ids = new HashSet<int>();
        if (!_lad.LayerSets.TryGetValue(RegionOf(bt), out var set)) return ids;
        foreach (var l in set.Layers)
        {
            if (l.Bump) continue;
            ids.UnionWith(l.ColorParams);
            ids.UnionWith(l.AlphaParams);
            if (l.GlobalColor is { Length: > 0 } g && _lad.GlobalColors.TryGetValue(g, out var gids)) ids.UnionWith(gids);
        }
        // drivers: a stored parameter (e.g. the shirt's sleeve length 800) that drives a layer parameter (600)
        for (var depth = 0; depth < 3; depth++)
        {
            var before = ids.Count;
            foreach (var def in _lad.Params.Values)
                if (def.Driven.Count > 0 && def.Driven.Any(d => ids.Contains(d.Id))) ids.Add(def.Id);
            if (ids.Count == before) break;
        }
        return ids;
    }

    // ---------------------------------------------------------------- parameters

    private sealed class ParamState
    {
        public readonly Dictionary<int, float> Direct = new();
        public readonly Dictionary<int, float> Derived = new();
        public readonly HashSet<WearableKind> WornTypes = new();
        /// <summary>Each wearable's own values (direct + driven) for the parameters its type owns, so several
        /// wearables of one type each render their layer with their own sleeve length, colour, etc. (LLWearable::writeToAvatar per instance).</summary>
        public readonly Dictionary<WornWearable, Dictionary<int, float>> PerInstance = new();
        /// <summary>The wearable whose layer is being rendered, if any.</summary>
        public WornWearable? Instance;
        public bool Male;
        /// <summary>Each rendered layer instance's alpha mask (LLTexLayer::mAlphaCache), for the morph-mask gather.</summary>
        public readonly Dictionary<(LayerDef Layer, WornWearable? Instance), byte[]> Masks = new(ReferenceTupleComparer.Instance);
    }

    private sealed class ReferenceTupleComparer : IEqualityComparer<(LayerDef Layer, WornWearable? Instance)>
    {
        public static readonly ReferenceTupleComparer Instance = new();
        public bool Equals((LayerDef Layer, WornWearable? Instance) a, (LayerDef Layer, WornWearable? Instance) b) => ReferenceEquals(a.Layer, b.Layer) && ReferenceEquals(a.Instance, b.Instance);
        public int GetHashCode((LayerDef Layer, WornWearable? Instance) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Layer), k.Instance is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Instance));
    }

    /// <summary>
    /// LLTexLayerInterface::getWearableType: the layer's local texture's wearable type, or, without a local texture,
    /// the single wearable type its colour and alpha parameters belong to; Invalid when they belong to several or none.
    /// </summary>
    public WearableKind LayerKind(LayerDef layer)
    {
        if (layer.LocalTexture is not null && TextureByName.TryGetValue(layer.LocalTexture, out var slot)) return WearableOf(slot);
        var kind = WearableKind.Invalid;
        foreach (var id in layer.ColorParams.Concat(layer.AlphaParams))
        {
            if (!_lad.Params.TryGetValue(id, out var def) || def.Wearable is not { Length: > 0 } owner || WearableKinds.FromName(owner) is not { } k) continue;
            if (kind != WearableKind.Invalid && k != kind) return WearableKind.Invalid;
            kind = k;
        }
        return kind;
    }

    private ParamState ResolveParams(IReadOnlyList<WornWearable> worn, IReadOnlyDictionary<int, float>? overlay)
    {
        var st = new ParamState();
        foreach (var w in worn)
        {
            st.WornTypes.Add(w.Kind);
            var owner = TypeName(w.Kind);
            foreach (var (id, v) in w.Params)
            {
                // A wearable only carries the parameters its type owns (LLWearable::createVisualParams); anything else is noise.
                if (!_lad.Params.TryGetValue(id, out var def) || !string.Equals(def.Wearable, owner, StringComparison.OrdinalIgnoreCase)) continue;
                st.Direct[id] = Math.Clamp(v, def.Min, def.Max);
            }
        }
        // Caller-supplied values (BakeRequest.VisualParams) fill in only what no worn wearable stores.
        if (overlay is not null)
            foreach (var (id, v) in overlay)
                if (!st.Direct.ContainsKey(id) && _lad.Params.TryGetValue(id, out var def)) st.Direct[id] = Math.Clamp(v, def.Min, def.Max);
        // Drivers: a stored parameter that drives others sets them (LLDriverParam::setWeight) unless they are stored themselves.
        PropagateDrivers(st.Direct, st.Derived);
        foreach (var w in worn)
        {
            var owner = TypeName(w.Kind);
            var direct = new Dictionary<int, float>();
            foreach (var (id, v) in w.Params)
                if (_lad.Params.TryGetValue(id, out var def) && string.Equals(def.Wearable, owner, StringComparison.OrdinalIgnoreCase))
                    direct[id] = Math.Clamp(v, def.Min, def.Max);
            var derived = new Dictionary<int, float>();
            PropagateDrivers(direct, derived);
            foreach (var (id, v) in derived) direct.TryAdd(id, v);
            st.PerInstance[w] = direct;
        }
        st.Male = Weight(st, 80) > 0.5f;
        return st;
    }

    private void PropagateDrivers(Dictionary<int, float> direct, Dictionary<int, float> derived)
    {
        var frontier = direct.Keys.ToList();
        for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var id in frontier)
            {
                var def = _lad.Params[id];
                if (def.Driven.Count == 0) continue;
                var input = direct.TryGetValue(id, out var dv) ? dv : derived[id];
                foreach (var d in def.Driven)
                {
                    if (direct.ContainsKey(d.Id) || derived.ContainsKey(d.Id) || !_lad.Params.TryGetValue(d.Id, out var driven)) continue;
                    derived[d.Id] = DrivenWeight(def, d, driven, input);
                    next.Add(d.Id);
                }
            }
            frontier = next;
        }
    }

    /// <summary>LLDriverParam::getDrivenWeight: the trapezoid min1/max1/max2/min2 of the driver maps onto the driven range.</summary>
    private static float DrivenWeight(ParamDef driver, DrivenInfo d, ParamDef driven, float input)
    {
        float dmin = driven.Min, dmax = driven.Max;
        if (input <= d.Min1) return d.Min1 == d.Max1 && d.Min1 <= driver.Min ? dmax : dmin;
        if (input <= d.Max1) return dmin + (input - d.Min1) / (d.Max1 - d.Min1) * (dmax - dmin);
        if (input <= d.Max2) return dmax;
        if (input <= d.Min2) return dmax + (input - d.Max2) / (d.Min2 - d.Max2) * (dmin - dmax);
        return d.Max2 >= driver.Max ? dmax : dmin;
    }

    private float Weight(ParamState st, int id)
    {
        if (!_lad.Params.TryGetValue(id, out var def)) return 0f;
        // the wearable being rendered owns its parameters: its own values first (multi-wearables), then the merged outfit
        if (st.Instance is { } inst && string.Equals(def.Wearable, TypeName(inst.Kind), StringComparison.OrdinalIgnoreCase)
            && st.PerInstance.TryGetValue(inst, out var own) && own.TryGetValue(id, out var ov)) return Math.Clamp(ov, def.Min, def.Max);
        if (st.Direct.TryGetValue(id, out var v) || st.Derived.TryGetValue(id, out v)) return Math.Clamp(v, def.Min, def.Max);
        return def.Default;
    }

    /// <summary>The weight a parameter contributes for this avatar: its value, or the default when the parameter is for the other sex.</summary>
    private float Effective(ParamState st, ParamDef def)
    {
        var applies = def.Sex == ParamSex.Both || (def.Sex == ParamSex.Male) == st.Male;
        return applies ? Weight(st, def.Id) : def.Default;
    }

    /// <summary>LLTexLayerParamAlpha::getSkip: zero weight with skip_if_zero, or the owning wearable type not worn.</summary>
    private bool Skip(ParamState st, ParamDef def)
    {
        if (def.Alpha is { SkipIfZero: true } && MathF.Abs(Effective(st, def)) < 1e-6f) return true;
        if (def.Wearable is { Length: > 0 } w && WearableKinds.FromName(w) is { } t && t != WearableKind.Invalid && !st.WornTypes.Contains(t)) return true;
        return false;
    }

    /// <summary>LLTexLayerParamColor::getNetColor: the stops interpolated by the raw weight (the viewer does not normalise by min/max here).</summary>
    private Rgba ParamColor(ParamState st, ParamDef def)
    {
        var colors = def.Color!.Colors;
        if (colors.Count == 0) return Rgba.Transparent;
        var last = colors.Count - 1;
        var scaled = Effective(st, def) * last;
        var i0 = Math.Clamp((int)scaled, 0, last);
        if (i0 == last) return colors[last];
        return Rgba.Lerp(colors[i0], colors[i0 + 1], scaled - i0);
    }

    /// <summary>LLTexLayer::calculateTexLayerColor.</summary>
    private Rgba FoldColors(ParamState st, IEnumerable<int> ids, Rgba net)
    {
        foreach (var id in ids)
        {
            if (!_lad.Params.TryGetValue(id, out var def) || def.Color is null) continue;
            var c = ParamColor(st, def);
            net = def.Color.Op switch
            {
                ColorOp.Add => net + c,
                ColorOp.Multiply => net * c,
                ColorOp.Blend => Rgba.Lerp(net, c, Weight(st, id)),
                _ => net,
            };
        }
        return net.Clamp();
    }

    private Rgba GlobalColor(ParamState st, string name)
        => _lad.GlobalColors.TryGetValue(name, out var ids) && ids.Count > 0 ? FoldColors(st, ids, Rgba.Transparent) : Rgba.White;

    /// <summary>LLTexLayer::findNetColor. Returns whether a colour was specified (a flat fill is drawn only then).</summary>
    private bool NetColor(ParamState st, LayerDef layer, out Rgba color)
    {
        if (layer.ColorParams.Count > 0)
        {
            var start = layer.GlobalColor is { Length: > 0 } g ? GlobalColor(st, g) : layer.FixedColor.A > 0 ? layer.FixedColor : Rgba.Transparent;
            color = FoldColors(st, layer.ColorParams, start);
            return true;
        }
        if (layer.GlobalColor is { Length: > 0 } gc) { color = GlobalColor(st, gc); return true; }
        if (layer.FixedColor.A > 0) { color = layer.FixedColor; return true; }
        color = Rgba.White;
        return false;
    }

    // ---------------------------------------------------------------- resources at bake size

    private Plane? MaskAt(string file, int size)
    {
        lock (_cacheLock)
        {
            if (_maskCache.TryGetValue((file, size), out var p)) return p;
            var m = _res.Mask(file);
            if (m is null) return null;
            p = Raster.Resample(m, size, size);
            _maskCache[(file, size)] = p;
            return p;
        }
    }

    private RgbaPlanes? ImageAt(string file, int size)
    {
        lock (_cacheLock)
        {
            if (_imageCache.TryGetValue((file, size), out var p)) return p;
            var img = _res.Image(file);
            if (img is null) return null;
            p = img.Resample(size, size);
            _imageCache[(file, size)] = p;
            return p;
        }
    }

    /// <summary>A parameter mask at the given weight: the ramp is applied at the file's own resolution, then resampled (as GL samples the processed texture).</summary>
    private Plane? ProcessedMask(string file, float domain, float weight, int size)
    {
        var q = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(weight, 0f, 1f) * 255f), 0, 255);
        var key = (file, domain, q, size);
        lock (_cacheLock)
        {
            if (_processedCache.TryGetValue(key, out var p)) return p;
            var raw = _res.Mask(file);
            if (raw is null) return null;
            var processed = new Plane(raw.W, raw.H, Raster.ProcessAlpha(raw.Data, domain, q / 255f));
            p = Raster.Resample(processed, size, size);
            _processedCache[key] = p;
            return p;
        }
    }

    // ---------------------------------------------------------------- the bake

    public CompositeResult Bake(BakeChannel bt, IReadOnlyList<WornWearable> worn, int size) => Bake(bt, worn, size, null);

    /// <param name="overlayParams">Values for parameters no worn wearable stores (a caller's merged view); never override a wearable's own value.</param>
    public CompositeResult Bake(BakeChannel bt, IReadOnlyList<WornWearable> worn, int size, IReadOnlyDictionary<int, float>? overlayParams)
    {
        var region = RegionOf(bt);
        if (!_lad.LayerSets.TryGetValue(region, out var set)) throw new InvalidOperationException($"avatar_lad.xml has no layer_set for {region}");
        var st = ResolveParams(worn, overlayParams);
        var n = size * size;
        var canvas = new RgbaPlanes(size, size, hasAlpha: true);
        Array.Fill(canvas.A, (byte)255);   // LLTexLayerSet::render clears to opaque black
        var reports = new List<LayerReport>();
        var maskLayers = set.Layers.Where(l => l.VisibilityMask).ToList();

        // An alpha wearable whose texture is the invisible one hides the whole region.
        foreach (var ml in maskLayers)
            if (ml.LocalTexture is not null && TextureByName.TryGetValue(ml.LocalTexture, out var midx))
                foreach (var w in worn.Where(w => w.Kind == WearableOf(midx)))
                    if (w.TextureIds.TryGetValue(midx, out var mid) && mid == BakeConstants.InvisibleTexture)
                    {
                        Array.Clear(canvas.A);
                        reports.Add(new LayerReport(ml.Name, "invisible", $"{w.Label}: IMG_INVISIBLE hides the whole {region} bake", w.Kind));
                        var m255 = new byte[n]; Array.Fill(m255, (byte)255);
                        return new CompositeResult { Image = canvas, Layers = reports, Invisible = true, MorphMask = m255 };
                    }

        foreach (var layer in set.Layers)
        {
            if (layer.Bump) { continue; }               // bump pass: viewer-side normal maps, never part of the uploaded bake
            if (layer.VisibilityMask) continue;         // applied to the final alpha below
            if (layer.LocalTexture is not null)
            {
                if (!TextureByName.TryGetValue(layer.LocalTexture, out var idx)) { reports.Add(new LayerReport(layer.Name, "skipped", $"unknown local_texture {layer.LocalTexture}", null)); continue; }
                var type = WearableOf(idx);
                var instances = worn.Where(w => w.Kind == type).ToList();
                if (instances.Count == 0) { reports.Add(new LayerReport(layer.Name, "skipped", $"no {type} worn", type)); continue; }
                foreach (var w in instances)
                {
                    w.Textures.TryGetValue(idx, out var tex);
                    st.Instance = w;
                    RenderLayer(set, layer, st, canvas, size, w, idx, tex, reports);
                    st.Instance = null;
                }
            }
            else RenderLayer(set, layer, st, canvas, size, null, TextureSlot.Unknown, null, reports);   // no local texture: a plain LLTexLayer, rendered once with the avatar's merged parameters (lltexlayer.cpp:64, :290-297)
        }

        // LLTexLayerSet::gatherMorphMaskAlpha (Docs/MORPH-MASK-PASS.md §2): 255, times the mask of every contributing
        // instance of the set's morph-mask layers. A layer with a local texture is an LLTexLayerTemplate and contributes
        // once per worn wearable of its type (lltexlayer.cpp:1706-1714); a layer without one is a plain LLTexLayer
        // (isUserSettable() is mLocalTexture != -1, lltexlayer.cpp:64, :290-297) and contributes exactly once, with the
        // avatar's merged parameters (the last-worn wearable's values), whatever is worn.
        var morph = new byte[n];
        Array.Fill(morph, (byte)255);
        if (_lad.MorphMaskLayers.TryGetValue(region, out var morphLayers))
        {
            foreach (var layer in set.Layers)
            {
                if (layer.Bump || !morphLayers.Contains(layer.Name) || layer.AlphaParams.Count == 0) continue;   // addAlphaMask: only hasAlphaParams() layers
                var kind = LayerKind(layer);
                List<WornWearable?> instances;
                if (layer.LocalTexture is null) instances = new List<WornWearable?> { null };   // plain LLTexLayer: once
                else
                {
                    if (kind == WearableKind.Invalid) { reports.Add(new LayerReport(layer.Name, "morph", "no wearable type: no instances", null)); continue; }
                    // LLTexLayerTemplate::gatherAlphaMasks (lltexlayer.cpp:1710-1719) takes getLayer(num_wearables - 1)
                    // only — "For rendering morph masks, we only want to use the top wearable" — unlike render(), which
                    // loops over every instance. A wearable counts as worn whether or not it has a texture asset
                    // (updateWearableCache, :1615-1638). Docs/MORPH-MASK-PASS.md §2.2, §2.4.
                    var ofKind = worn.Where(w => w.Kind == kind).ToList();
                    if (ofKind.Count == 0) { reports.Add(new LayerReport(layer.Name, "morph", $"no {kind} worn: mask left at 255", kind)); continue; }
                    instances = new List<WornWearable?> { ofKind[^1] };
                }
                foreach (var w in instances)
                {
                    if (!st.Masks.TryGetValue((layer, w), out var mask))
                    {
                        // not rendered in the colour pass: render its mask on demand, as addAlphaMask does
                        st.Instance = w;
                        NetColor(st, layer, out var color);
                        RgbaPlanes? tex = null;
                        if (w is not null && layer.LocalTexture is not null && TextureByName.TryGetValue(layer.LocalTexture, out var slot)) w.Textures.TryGetValue(slot, out tex);
                        mask = ComputeMask(layer, st, canvas.A, size, w, tex, color, new List<string>(), out _);
                        st.Instance = null;
                        if (mask is null) { reports.Add(new LayerReport(layer.Name, "morph", $"{(w is null ? "" : w.Label + ": ")}mask file missing; not applied", kind)); continue; }
                    }
                    long sum = 0;
                    for (var i = 0; i < n; i++) { morph[i] = (byte)((morph[i] * (mask[i] + 1)) >> 8); sum += mask[i]; }
                    reports.Add(new LayerReport(layer.Name, "morph", $"{(w is null ? "once (plain layer)" : w.Label)}: morph mask *= layer mask (mean {sum / (double)n:F1})", w?.Kind ?? kind));
                }
            }
        }

        // LLTexLayerSet::renderAlphaMaskTextures: the bake's alpha is 1 (or the set's static alpha file), times every visibility mask.
        if (set.StaticAlphaFile is not null)
        {
            var a = MaskAt(set.StaticAlphaFile, size);
            if (a is not null) Array.Copy(a.Data, canvas.A, n);
        }
        else if (set.ClearAlpha || maskLayers.Count > 0) Array.Fill(canvas.A, (byte)255);
        foreach (var ml in maskLayers)
        {
            if (ml.StaticImage is not null)
            {
                var m = ml.StaticIsMask ? MaskAt(ml.StaticImage, size) : ImageAt(ml.StaticImage, size) is { } im ? new Plane(size, size, im.A) : null;
                if (m is null) { reports.Add(new LayerReport(ml.Name, "skipped", $"resource {ml.StaticImage} missing", null)); continue; }
                for (var i = 0; i < n; i++) canvas.A[i] = Raster.Mul(canvas.A[i], m.Data[i]);
                reports.Add(new LayerReport(ml.Name, "mask", $"alpha *= {ml.StaticImage}", null));
            }
            else if (ml.LocalTexture is not null && TextureByName.TryGetValue(ml.LocalTexture, out var midx))
            {
                var type = WearableOf(midx);
                var instances = worn.Where(w => w.Kind == type).ToList();
                if (instances.Count == 0) { reports.Add(new LayerReport(ml.Name, "skipped", $"no {type} worn", type)); continue; }
                foreach (var w in instances)
                {
                    if (!w.Textures.TryGetValue(midx, out var tex)) { reports.Add(new LayerReport(ml.Name, "skipped", $"{w.Label}: no {midx} texture", type)); continue; }
                    if (!tex.HasAlpha) { reports.Add(new LayerReport(ml.Name, "mask", $"{w.Label}: {midx} has no alpha channel (opaque)", type)); continue; }
                    var t = tex.Resample(size, size);
                    for (var i = 0; i < n; i++) canvas.A[i] = Raster.Mul(canvas.A[i], t.A[i]);
                    reports.Add(new LayerReport(ml.Name, "mask", $"{w.Label}: alpha *= {midx} ({tex.W}x{tex.H})", type));
                }
            }
        }
        // LLTexLayerSet::render draws each layer or skips it; if every one skipped, nothing reached the canvas.
        // Deliberate all-transparent output (a drawn but transparent layer, or the IMG_INVISIBLE short-circuit
        // above) is NOT this: those drew.
        var nothingDrawn = !reports.Any(l => l.Status == "drawn");
        return new CompositeResult { Image = canvas, Layers = reports, MorphMask = morph, NothingDrawn = nothingDrawn };
    }

    /// <summary>
    /// LLTexLayer::renderMorphMasks, the mask part: the layer's alpha parameters accumulated (additive from 0, or a leading
    /// multiply parameter from the current alpha), times the local texture's alpha, times the static mask, times the layer
    /// colour's alpha. Null only when a mask file is missing. <paramref name="allSkipped"/> reports the all-parameters-skipped
    /// case (the viewer then draws the layer through this all-zero mask).
    /// </summary>
    private byte[]? ComputeMask(LayerDef layer, ParamState st, byte[] currentAlpha, int size, WornWearable? w, RgbaPlanes? tex, Rgba color, List<string> used, out bool allSkipped)
    {
        var n = size * size;
        var first = _lad.Params.GetValueOrDefault(layer.AlphaParams[0]);
        var mask = first?.Alpha is { MultiplyBlend: true } ? (byte[])currentAlpha.Clone() : new byte[n];
        var applied = 0;
        foreach (var pid in layer.AlphaParams)
        {
            if (!_lad.Params.TryGetValue(pid, out var def) || def.Alpha is null || string.IsNullOrEmpty(def.Alpha.TgaFile)) continue;
            if (Skip(st, def)) { used.Add($"{def.Name}#{pid}=skip"); continue; }
            var eff = Effective(st, def);
            var p = ProcessedMask(def.Alpha.TgaFile, def.Alpha.Domain, eff, size);
            if (p is null) { allSkipped = false; return null; }
            if (def.Alpha.MultiplyBlend) for (var i = 0; i < n; i++) mask[i] = Raster.Mul(mask[i], p.Data[i]);
            else for (var i = 0; i < n; i++) mask[i] = (byte)Math.Min(255, mask[i] + p.Data[i]);
            used.Add($"{def.Name}#{pid}={eff:F2}{(def.Alpha.MultiplyBlend ? "*" : "+")}");
            applied++;
        }
        allSkipped = applied == 0 && first?.Alpha is not { MultiplyBlend: true };
        if (allSkipped) return mask;   // every parameter skipped and nothing added: an all-zero mask, as the viewer's cleared alpha
        if (tex is { HasAlpha: true } && layer.LocalTexture is not null)
        {
            var t = tex.Resample(size, size);
            for (var i = 0; i < n; i++) mask[i] = Raster.Mul(mask[i], t.A[i]);
            used.Add("texture alpha*");
        }
        if (layer.StaticImage is not null && layer.StaticIsMask)
        {
            var m = MaskAt(layer.StaticImage, size);
            if (m is not null) { for (var i = 0; i < n; i++) mask[i] = Raster.Mul(mask[i], m.Data[i]); used.Add($"{layer.StaticImage}*"); }
        }
        if (MathF.Abs(color.A - 1f) > 1e-4f)
        {
            var ca = (byte)Math.Round(color.A * 255);
            for (var i = 0; i < n; i++) mask[i] = Raster.Mul(mask[i], ca);
        }
        return mask;
    }

    /// <summary>LLTexLayer::render for one layer instance.</summary>
    private void RenderLayer(LayerSetDef set, LayerDef layer, ParamState st, RgbaPlanes canvas, int size, WornWearable? w, TextureSlot idx, RgbaPlanes? tex, List<LayerReport> reports)
    {
        var n = size * size;
        var who = w is null ? "" : $"{w.Label}: ";
        var colorSpecified = NetColor(st, layer, out var color);
        if (color.A < 1e-4f) { reports.Add(new LayerReport(layer.Name, "skipped", $"{who}colour alpha 0 {color}", w?.Kind)); return; }
        var detail = new List<string>();
        if (colorSpecified) detail.Add($"colour {color}");

        byte[]? mask = null;
        if (layer.AlphaParams.Count > 0)
        {
            // LLTexLayer::renderMorphMasks: the alpha channel becomes the mask; additive params start from 0, a leading multiply param from what is there.
            var used = new List<string>();
            mask = ComputeMask(layer, st, canvas.A, size, w, tex, color, used, out var allSkipped);
            if (mask is null) { reports.Add(new LayerReport(layer.Name, "skipped", $"{who}a mask file is missing", w?.Kind)); return; }
            st.Masks[(layer, w)] = mask;   // LLTexLayer::mAlphaCache: reused by the morph-mask gather
            if (allSkipped)
            {
                // Every mask parameter skipped and nothing to add to an empty mask: the viewer draws the layer through an all-zero mask, i.e. nothing.
                Array.Copy(mask, canvas.A, n);
                reports.Add(new LayerReport(layer.Name, "skipped", $"{who}every mask parameter skipped [{string.Join(", ", used)}]", w?.Kind));
                return;
            }
            Array.Copy(mask, canvas.A, n);   // the viewer leaves the mask in the alpha channel (the skirt's shape comes from this)
            detail.Add($"masks [{string.Join(", ", used)}]");
        }

        var drew = false;
        if (layer.LocalTexture is not null && !layer.LocalTextureAlphaOnly)
        {
            if (tex is null) { reports.Add(new LayerReport(layer.Name, "skipped", $"{who}no {idx} texture on the wearable", w?.Kind)); return; }
            var t = tex.Resample(size, size);
            DrawImage(canvas, t, color, mask, layer.WriteAllChannels, n);
            detail.Insert(0, $"{who}{idx} {tex.W}x{tex.H}{(tex.HasAlpha ? "+alpha" : "")} -> {size}");
            drew = true;
        }
        if (layer.StaticImage is not null)
        {
            if (layer.StaticIsMask)
            {
                var m = MaskAt(layer.StaticImage, size);
                if (m is null) { reports.Add(new LayerReport(layer.Name, "skipped", $"{who}resource {layer.StaticImage} missing", w?.Kind)); return; }
                // A mask file colours the layer's own colour through the file's grey (the fixed-function GL_ALPHA reading avatar_lad.xml was written for).
                var alpha = mask ?? MaskTimes(m.Data, color.A, n);
                DrawFill(canvas, color, alpha, layer.WriteAllChannels, n);
                detail.Add($"{layer.StaticImage} (mask)");
            }
            else
            {
                var img = ImageAt(layer.StaticImage, size);
                if (img is null) { reports.Add(new LayerReport(layer.Name, "skipped", $"{who}resource {layer.StaticImage} missing", w?.Kind)); return; }
                DrawImage(canvas, img, color, mask, layer.WriteAllChannels, n);
                detail.Add($"{layer.StaticImage}{(img.HasAlpha ? "+alpha" : "")}");
            }
            drew = true;
        }
        if ((layer.LocalTexture is null || layer.LocalTextureAlphaOnly) && layer.StaticImage is null && colorSpecified)
        {
            var alpha = mask ?? Filled(n, (byte)Math.Round(color.A * 255));
            DrawFill(canvas, color, alpha, layer.WriteAllChannels, n);
            detail.Add("flat fill");
            drew = true;
        }
        reports.Add(new LayerReport(layer.Name, drew ? "drawn" : "skipped", string.Join("; ", detail), w?.Kind));
    }

    private static byte[] Filled(int n, byte v) { var a = new byte[n]; Array.Fill(a, v); return a; }

    private static byte[] MaskTimes(byte[] m, float a, int n)
    {
        var ca = (byte)Math.Round(Math.Clamp(a, 0f, 1f) * 255);
        if (ca == 255) return m;
        var o = new byte[n];
        for (var i = 0; i < n; i++) o[i] = Raster.Mul(m[i], ca);
        return o;
    }

    /// <summary>Texture (or bundled image) modulated by the layer colour, blended by the mask or by its own alpha; replace mode writes all channels.</summary>
    private static void DrawImage(RgbaPlanes dst, RgbaPlanes src, Rgba color, byte[]? mask, bool replace, int n)
    {
        byte cr = (byte)Math.Round(color.R * 255), cg = (byte)Math.Round(color.G * 255), cb = (byte)Math.Round(color.B * 255), ca = (byte)Math.Round(color.A * 255);
        var tint = cr != 255 || cg != 255 || cb != 255;
        for (var i = 0; i < n; i++)
        {
            var r = tint ? Raster.Mul(src.R[i], cr) : src.R[i];
            var g = tint ? Raster.Mul(src.G[i], cg) : src.G[i];
            var b = tint ? Raster.Mul(src.B[i], cb) : src.B[i];
            var a = mask is not null ? mask[i] : Raster.Mul(src.A[i], ca);
            if (replace) { dst.R[i] = r; dst.G[i] = g; dst.B[i] = b; dst.A[i] = a; continue; }
            if (a == 0) continue;
            Blend(dst, i, r, g, b, a, mask is null);
        }
    }

    private static void DrawFill(RgbaPlanes dst, Rgba color, byte[] alpha, bool replace, int n)
    {
        byte cr = (byte)Math.Round(color.R * 255), cg = (byte)Math.Round(color.G * 255), cb = (byte)Math.Round(color.B * 255);
        for (var i = 0; i < n; i++)
        {
            var a = alpha[i];
            if (replace) { dst.R[i] = cr; dst.G[i] = cg; dst.B[i] = cb; dst.A[i] = a; continue; }
            if (a == 0) continue;
            Blend(dst, i, cr, cg, cb, a, alphaFromSource: false);
        }
    }

    /// <summary>src over dst by a; with a source-alpha blend the alpha channel follows GL's (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) too.</summary>
    private static void Blend(RgbaPlanes dst, int i, byte r, byte g, byte b, byte a, bool alphaFromSource)
    {
        if (a == 255) { dst.R[i] = r; dst.G[i] = g; dst.B[i] = b; if (alphaFromSource) dst.A[i] = 255; return; }
        dst.R[i] = (byte)(dst.R[i] + ((r - dst.R[i]) * a + 127) / 255);
        dst.G[i] = (byte)(dst.G[i] + ((g - dst.G[i]) * a + 127) / 255);
        dst.B[i] = (byte)(dst.B[i] + ((b - dst.B[i]) * a + 127) / 255);
        if (alphaFromSource) dst.A[i] = (byte)((a * a + dst.A[i] * (255 - a) + 127) / 255);
    }

    /// <summary>The sex the shape's `male` parameter (80) says; needed by a client to pick a body.</summary>
    public bool IsMale(IReadOnlyList<WornWearable> worn) => ResolveParams(worn, null).Male;
}
