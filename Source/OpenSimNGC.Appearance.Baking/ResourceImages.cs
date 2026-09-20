namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The viewer's bundled character images (parameter masks, skin base, eye whites, aux base) that the layer
/// sets of avatar_lad.xml name by file, decoded once. The library embeds the 56 files avatar_lad.xml references
/// (see THIRD-PARTY-NOTICES.md); a different source can be supplied for tests.
/// </summary>
public sealed class ResourceImages
{
    /// <summary>Prefix of the embedded character images' manifest resource names.</summary>
    public const string EmbeddedPrefix = "OpenSimNGC.Appearance.Baking.Data.character.";

    private static readonly Lazy<ResourceImages> s_embedded = new(() => new ResourceImages(OpenEmbedded), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The images embedded in this assembly.</summary>
    public static ResourceImages Embedded => s_embedded.Value;

    private readonly Func<string, Stream?> _open;
    private readonly Dictionary<string, TgaImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <param name="open">Returns the bytes of a named file (for example "shirt_sleeve_alpha.tga"), or null when absent.</param>
    public ResourceImages(Func<string, Stream?> open) { _open = open; }

    /// <summary>Images from a directory on disk (a viewer's <c>character/</c> folder).</summary>
    public static ResourceImages FromDirectory(string dir)
        => new(file => { var p = Path.Combine(dir, file); return File.Exists(p) ? File.OpenRead(p) : null; });

    private static Stream? OpenEmbedded(string file) => typeof(ResourceImages).Assembly.GetManifestResourceStream(EmbeddedPrefix + file);

    private TgaImage? Load(string file)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(file, out var img)) return img;
            img = null;
            try
            {
                using var s = _open(file);
                if (s is not null)
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    img = Tga.Decode(ms.ToArray());
                }
            }
            catch { img = null; }
            _cache[file] = img;
            return img;
        }
    }

    public bool Exists(string file) => Load(file) is not null;

    /// <summary>Grey (mask) reading of a file: grey files as-is, RGBA files by their alpha, RGB files as solid 255.</summary>
    public Plane? Mask(string file)
    {
        var img = Load(file);
        if (img is null) return null;
        var n = img.W * img.H;
        var data = new byte[n];
        if (img.IsGray) Array.Copy(img.R, data, n);
        else if (img.HasAlpha) Array.Copy(img.A, data, n);
        else Array.Fill(data, (byte)255);
        return new Plane(img.W, img.H, data);
    }

    /// <summary>Colour reading of a file: grey files become luminance in RGB with alpha 255, as GL_LUMINANCE would.</summary>
    public RgbaPlanes? Image(string file)
    {
        var img = Load(file);
        if (img is null) return null;
        var n = img.W * img.H;
        var p = new RgbaPlanes(img.W, img.H, img.HasAlpha);
        Array.Copy(img.R, p.R, n); Array.Copy(img.G, p.G, n); Array.Copy(img.B, p.B, n);
        if (img.HasAlpha) Array.Copy(img.A, p.A, n);
        return p;
    }
}
