/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The bundled character images (SPEC.md section 9) as the compositor reads them: decoded once, with the grey
/// reading (9.2) and the colour reading (9.3) of each file. A file that is missing or cannot be decoded reads as null.
/// </summary>
public sealed class BakeResourceFiles
{
    private static readonly Lazy<BakeResourceFiles> s_embedded = new(
        () => new BakeResourceFiles(file => typeof(BakeResourceFiles).Assembly.GetManifestResourceStream(ResourceImages.EmbeddedPrefix + file)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The images embedded in this assembly.</summary>
    public static BakeResourceFiles Embedded => s_embedded.Value;

    private readonly Func<string, Stream?> _open;
    private readonly Dictionary<string, TgaImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <param name="open">Returns the bytes of a named file (for example "shirt_sleeve_alpha.tga"), or null when absent.</param>
    public BakeResourceFiles(Func<string, Stream?> open) { _open = open; }

    /// <summary>Images from a directory on disk.</summary>
    public static BakeResourceFiles FromDirectory(string dir)
        => new(file => { var p = Path.Combine(dir, file); return File.Exists(p) ? File.OpenRead(p) : null; });

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
            catch (Exception ex) when (ex is FormatException or IOException or ArgumentException or IndexOutOfRangeException) { img = null; }
            _cache[file] = img;
            return img;
        }
    }

    /// <summary>Whether the file is present and decodes.</summary>
    public bool Exists(string file) => Load(file) is not null;

    /// <summary>The grey reading (SPEC.md 9.2): a grey file gives its values, a colour file with alpha its alpha plane, a colour file without alpha 255 everywhere.</summary>
    public (int W, int H, byte[] Data)? Grey(string file)
    {
        var img = Load(file);
        if (img is null) return null;
        var n = img.W * img.H;
        var data = new byte[n];
        if (img.IsGray) Array.Copy(img.R, data, n);
        else if (img.HasAlpha) Array.Copy(img.A, data, n);
        else Array.Fill(data, (byte)255);
        return (img.W, img.H, data);
    }

    /// <summary>The colour reading (SPEC.md 9.3): a grey file gives red = green = blue = the value and no alpha; a colour file gives its colours and its alpha plane if it has one.</summary>
    public BakeBitmap? Colour(string file)
    {
        var img = Load(file);
        if (img is null) return null;
        var hasAlpha = !img.IsGray && img.HasAlpha;
        return new BakeBitmap(img.W, img.H, img.R, img.G, img.B, hasAlpha ? img.A : null);
    }
}
