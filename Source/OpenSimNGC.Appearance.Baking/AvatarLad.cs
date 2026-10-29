using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>Which avatars a parameter applies to (the `sex` attribute); the other sex sees the default weight.</summary>
public enum ParamSex { Both, Male, Female }

/// <summary>How a colour parameter combines into a layer's net colour (`operation` attribute; add is the default).</summary>
public enum ColorOp { Add, Multiply, Blend }

/// <summary>A colour with components in 0..1.</summary>
public readonly record struct Rgba(float R, float G, float B, float A)
{
    public static readonly Rgba White = new(1, 1, 1, 1);
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    public Rgba Clamp() => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1), Math.Clamp(A, 0, 1));
    public static Rgba operator +(Rgba a, Rgba b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    public static Rgba operator *(Rgba a, Rgba b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);
    public static Rgba Lerp(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>"r, g, b, a" with byte components, as avatar_lad.xml writes colours.</summary>
    public static Rgba ParseBytes(string s)
    {
        var p = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) throw new FormatException($"colour '{s}'");
        float B(int i) => i < p.Length ? float.Parse(p[i], CultureInfo.InvariantCulture) / 255f : 1f;
        return new Rgba(B(0), B(1), B(2), B(3));
    }

    public override string ToString() => $"({R:F2},{G:F2},{B:F2},{A:F2})";
}

/// <summary>`param_alpha`: a greyscale mask file, the width of its soft edge, and how it combines with the layer's other masks.</summary>
public sealed record AlphaParamInfo(string TgaFile, float Domain, bool SkipIfZero, bool MultiplyBlend);

/// <summary>`param_color`: colour stops interpolated by the parameter's weight, and the operation that folds it into the layer colour.</summary>
public sealed record ColorParamInfo(ColorOp Op, IReadOnlyList<Rgba> Colors);

/// <summary>`driven`: how a driver parameter's weight maps onto one driven parameter (trapezoid min1/max1/max2/min2).</summary>
public sealed record DrivenInfo(int Id, float Min1, float Max1, float Max2, float Min2);

/// <summary>One visual parameter as avatar_lad.xml defines it (every occurrence of the id merged).</summary>
public sealed class ParamDef
{
    public int Id;
    public string Name = "";
    /// <summary>The wearable type that owns the parameter ("shirt", "skin", ...); null for parameters no wearable stores.</summary>
    public string? Wearable;
    public ParamSex Sex = ParamSex.Both;
    public int Group;
    public float Min;
    public float Max = 1f;
    public float Default;
    public AlphaParamInfo? Alpha;
    public ColorParamInfo? Color;
    public readonly List<DrivenInfo> Driven = new();
}

/// <summary>One `layer` of a `layer_set`, in file order.</summary>
public sealed class LayerDef
{
    public string Name = "";
    /// <summary>`local_texture`: the wearable texture slot this layer draws (upper_shirt, head_bodypaint, ...).</summary>
    public string? LocalTexture;
    public bool LocalTextureAlphaOnly;
    /// <summary>`tga_file`: a bundled image; with file_is_mask it is an alpha mask for the layer colour.</summary>
    public string? StaticImage;
    public bool StaticIsMask;
    public Rgba FixedColor = Rgba.Transparent;
    public string? GlobalColor;
    public bool Bump;
    public bool WriteAllChannels;
    public bool VisibilityMask;
    public readonly List<int> ColorParams = new();
    public readonly List<int> AlphaParams = new();
}

public sealed class LayerSetDef
{
    public string BodyRegion = "";
    public int Width = 512;
    public int Height = 512;
    public bool ClearAlpha = true;
    public string? StaticAlphaFile;
    public readonly List<LayerDef> Layers = new();
}

/// <summary>
/// The parts of avatar_lad.xml the compositor needs: parameters (alpha masks, colour stops, drivers), the
/// six-plus-five bake layer sets in their exact layer order, and the three global colours. The file is embedded
/// in this assembly (ADR-007; see THIRD-PARTY-NOTICES.md) and is the same data the viewers bake from.
/// </summary>
public sealed class AvatarLad
{
    /// <summary>Manifest resource name of the embedded avatar_lad.xml.</summary>
    public const string EmbeddedResourceName = "OpenSimNGC.Appearance.Baking.Data.avatar_lad.xml";

    private static readonly Lazy<AvatarLad> s_embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The avatar_lad.xml that ships with the library, parsed once.</summary>
    public static AvatarLad Embedded => s_embedded.Value;

    public readonly Dictionary<int, ParamDef> Params = new();
    public readonly Dictionary<string, LayerSetDef> LayerSets = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, List<int>> GlobalColors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The <c>&lt;morph_masks&gt;</c> block: per body_region, the layer names whose alpha mask also drives a mesh
    /// morph. Only these layers contribute to a bake's 5th component (see Docs/MORPH-MASK-PASS.md §2.1).
    /// </summary>
    public readonly Dictionary<string, HashSet<string>> MorphMaskLayers = new(StringComparer.OrdinalIgnoreCase);

    public static AvatarLad Load(string path) => Parse(XDocument.Load(path));

    public static AvatarLad Load(Stream stream) => Parse(XDocument.Load(stream));

    private static AvatarLad LoadEmbedded()
    {
        using var s = typeof(AvatarLad).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"embedded resource {EmbeddedResourceName} is missing");
        return Load(s);
    }

    public static AvatarLad Parse(XDocument doc)
    {
        var lad = new AvatarLad();
        var root = doc.Root ?? throw new FormatException("avatar_lad.xml has no root");
        foreach (var p in root.Descendants("param")) lad.MergeParam(p);
        foreach (var g in root.Descendants("global_color"))
        {
            var name = (string?)g.Attribute("name") ?? "";
            lad.GlobalColors[name] = g.Elements("param").Select(p => (int)p.Attribute("id")!).ToList();
        }
        foreach (var ls in root.Descendants("layer_set"))
        {
            var set = new LayerSetDef
            {
                BodyRegion = (string?)ls.Attribute("body_region") ?? "",
                Width = (int?)ls.Attribute("width") ?? 512,
                Height = (int?)ls.Attribute("height") ?? 512,
                ClearAlpha = Bool(ls.Attribute("clear_alpha"), true),
                StaticAlphaFile = (string?)ls.Attribute("alpha_tga_file"),
            };
            foreach (var l in ls.Elements("layer"))
            {
                var layer = new LayerDef
                {
                    Name = (string?)l.Attribute("name") ?? "",
                    GlobalColor = (string?)l.Attribute("global_color"),
                    Bump = string.Equals((string?)l.Attribute("render_pass"), "bump", StringComparison.OrdinalIgnoreCase),
                    WriteAllChannels = Bool(l.Attribute("write_all_channels"), false),
                    VisibilityMask = Bool(l.Attribute("visibility_mask"), false),
                };
                if (l.Attribute("fixed_color") is { } fc) layer.FixedColor = Rgba.ParseBytes(fc.Value);
                foreach (var t in l.Elements("texture"))
                {
                    if (t.Attribute("tga_file") is { } tga)
                    {
                        layer.StaticImage = tga.Value;
                        layer.StaticIsMask = Bool(t.Attribute("file_is_mask"), false);
                    }
                    else if (t.Attribute("local_texture") is { } lt)
                    {
                        layer.LocalTexture = lt.Value;
                        layer.LocalTextureAlphaOnly = Bool(t.Attribute("local_texture_alpha_only"), false);
                    }
                }
                foreach (var p in l.Elements("param"))
                {
                    var id = (int)p.Attribute("id")!;
                    if (p.Element("param_alpha") is not null) layer.AlphaParams.Add(id);
                    else if (p.Element("param_color") is not null) layer.ColorParams.Add(id);
                }
                set.Layers.Add(layer);
            }
            lad.LayerSets[set.BodyRegion] = set;
        }
        foreach (var m in root.Descendants("morph_masks").Elements("mask"))
        {
            var region = (string?)m.Attribute("body_region") ?? "";
            var layer = (string?)m.Attribute("layer") ?? "";
            if (region.Length == 0 || layer.Length == 0) continue;
            if (!lad.MorphMaskLayers.TryGetValue(region, out var names)) lad.MorphMaskLayers[region] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(layer);
        }
        return lad;
    }

    private void MergeParam(XElement p)
    {
        if (p.Attribute("id") is null) return;
        var id = (int)p.Attribute("id")!;
        if (!Params.TryGetValue(id, out var def))
        {
            def = new ParamDef
            {
                Id = id,
                Name = (string?)p.Attribute("name") ?? "",
                Wearable = (string?)p.Attribute("wearable"),
                Group = (int?)p.Attribute("group") ?? 0,
                Min = Float(p.Attribute("value_min"), 0f),
                Max = Float(p.Attribute("value_max"), 1f),
                Default = Float(p.Attribute("value_default"), 0f),
                Sex = ((string?)p.Attribute("sex"))?.ToLowerInvariant() switch { "male" => ParamSex.Male, "female" => ParamSex.Female, _ => ParamSex.Both },
            };
            Params[id] = def;
        }
        else
        {
            def.Wearable ??= (string?)p.Attribute("wearable");
            if (p.Attribute("sex") is { } sx && def.Sex == ParamSex.Both) def.Sex = sx.Value.ToLowerInvariant() switch { "male" => ParamSex.Male, "female" => ParamSex.Female, _ => ParamSex.Both };
        }
        if (p.Element("param_alpha") is { } pa)
            def.Alpha = new AlphaParamInfo((string?)pa.Attribute("tga_file") ?? "", Float(pa.Attribute("domain"), 0f), Bool(pa.Attribute("skip_if_zero"), false), Bool(pa.Attribute("multiply_blend"), false));
        if (p.Element("param_color") is { } pc)
        {
            var op = ((string?)pc.Attribute("operation"))?.ToLowerInvariant() switch { "multiply" => ColorOp.Multiply, "blend" => ColorOp.Blend, _ => ColorOp.Add };
            def.Color = new ColorParamInfo(op, pc.Elements("value").Select(v => Rgba.ParseBytes((string?)v.Attribute("color") ?? "0,0,0,0")).ToList());
        }
        if (p.Element("param_driver") is { } pd)
        {
            foreach (var d in pd.Elements("driven"))
            {
                var did = (int)d.Attribute("id")!;
                if (def.Driven.Any(x => x.Id == did)) continue;
                var max1 = Float(d.Attribute("max1"), def.Max);
                def.Driven.Add(new DrivenInfo(did, Float(d.Attribute("min1"), def.Min), max1, Float(d.Attribute("max2"), max1), Float(d.Attribute("min2"), max1)));
            }
        }
    }

    private static bool Bool(XAttribute? a, bool dflt) => a is null ? dflt : a.Value.Trim().ToLowerInvariant() is "true" or "1" or "yes";
    private static float Float(XAttribute? a, float dflt) => a is null ? dflt : float.Parse(a.Value.Trim(), CultureInfo.InvariantCulture);
}
