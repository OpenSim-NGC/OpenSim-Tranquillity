using System.Text;
using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Skia;
using SkiaSharp;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// JPEG 2000 in and out via CoreJ2K.Skia (the encoder the rest of the tree uses; S0a V7).
/// Encoding is SINGLE-TILE: CoreJ2K's default 256x256 tiling produces codestreams that render blank in
/// viewers (upstream #201), so the tile is always the whole image.
/// </summary>
public static class J2kCodec
{
    /// <summary>Codestream size fields from the SIZ marker segment.</summary>
    public sealed record SizInfo(int Xsiz, int Ysiz, int XOsiz, int YOsiz, int XTsiz, int YTsiz, int XTOsiz, int YTOsiz, int Csiz)
    {
        public int TilesX => (int)Math.Ceiling((Xsiz - XTOsiz) / (double)XTsiz);
        public int TilesY => (int)Math.Ceiling((Ysiz - YTOsiz) / (double)YTsiz);
        public int TileCount => TilesX * TilesY;
        public bool SingleTile => TileCount == 1;
    }

    /// <summary>
    /// Decode a J2C codestream or JP2 file to planar RGBA. 1 component = grey, 2 = grey+alpha, 3 = RGB,
    /// 4 = RGBA, 5 = RGBA plus the morph mask a viewer appends to its own bakes (exposed as <see cref="RgbaPlanes.Mask"/>).
    /// </summary>
    /// <exception cref="ArgumentException">If the bytes are not a decodable JPEG 2000 image.</exception>
    public static RgbaPlanes Decode(byte[] data)
    {
        if (data is null || data.Length == 0) throw new ArgumentException("empty JPEG 2000 data");
        CoreJ2K.Util.InterleavedImage img;
        try { img = J2kImage.FromBytes(data, new J2KDecoderConfiguration()); }
        catch (Exception ex) { throw new ArgumentException($"JPEG 2000 decode failed: {ex.Message}", ex); }
        using (img)
        {
            int w = img.Width, h = img.Height, n = w * h, c = img.NumberOfComponents;
            var hasAlpha = c == 2 || c >= 4;
            var p = new RgbaPlanes(w, h, hasAlpha);
            byte[] Comp(int i) { var b = img.GetComponentBytes(i); return b.Length == n ? b : throw new ArgumentException($"JPEG 2000 component {i} has {b.Length} samples, expected {n}"); }
            if (c >= 3)
            {
                Array.Copy(Comp(0), p.R, n); Array.Copy(Comp(1), p.G, n); Array.Copy(Comp(2), p.B, n);
                if (c >= 4) Array.Copy(Comp(3), p.A, n);
                if (c >= 5) p.Mask = Comp(4);   // a viewer bake's morph mask (Docs/MORPH-MASK-PASS.md §2)
            }
            else
            {
                var g = Comp(0);
                Array.Copy(g, p.R, n); Array.Copy(g, p.G, n); Array.Copy(g, p.B, n);
                if (c == 2) Array.Copy(Comp(1), p.A, n);
            }
            return p;
        }
    }

    /// <summary>The encoder settings for a bake of the given size: one tile, 9/7 irreversible, RPCL, raw codestream (no JP2 wrapper).</summary>
    public static J2KEncoderConfiguration EncoderConfig(int width, int height, double quality = 0.85)
    {
        // decomposition levels: 7 at 512 and above, fewer for small images (each level halves the image)
        var levels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Min(width, height))) - 2, 1, 7);
        var cfg = new J2KEncoderConfiguration();
        // Below 64 px the quality-derived bitrate is smaller than the codestream headers and CoreJ2K refuses
        // ("target bitrate too low"); such images are only ever small source textures, so encode them losslessly.
        if (Math.Min(width, height) < 64) cfg = cfg.WithLossless();
        else cfg = cfg.WithQuality(quality);
        return cfg
            .WithTiles(t => t.SetSize(width, height))
            .WithWavelet(w => { if (Math.Min(width, height) >= 64) w.UseIrreversible97(); w.WithDecompositionLevels(levels); })
            .WithProgression(p => p.WithOrder(ProgressionOrder.RPCL))
            .WithFileFormat(false);
    }

    /// <summary>Encode planar RGBA to a single-tile J2C codestream (four components).</summary>
    public static byte[] Encode(RgbaPlanes img, double quality = 0.85)
    {
        using var bmp = img.ToSkBitmap();
        return bmp.EncodeToJ2K(EncoderConfig(img.W, img.H, quality));
    }

    /// <summary>
    /// Encode a bake the way a viewer uploads one: five components R, G, B, A (visibility alpha) and M (the
    /// morph mask, Docs/MORPH-MASK-PASS.md), single tile, same settings as <see cref="Encode"/>. A null mask is
    /// written as 255 everywhere, the value <c>gatherMorphMaskAlpha</c> starts from.
    /// </summary>
    public static byte[] EncodeBake(RgbaPlanes img, byte[]? morphMask, double quality = 0.85)
    {
        int w = img.W, h = img.H, n = w * h;
        if (morphMask is not null && morphMask.Length != n) throw new ArgumentException($"morph mask has {morphMask.Length} samples, expected {n}");
        var mask = morphMask;
        if (mask is null) { mask = new byte[n]; Array.Fill(mask, (byte)255); }
        // CoreJ2K builds a multi-component source from one greyscale PGM stream per component.
        var header = Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
        Stream Pgm(byte[] plane) { var ms = new MemoryStream(header.Length + n); ms.Write(header); ms.Write(plane, 0, n); ms.Position = 0; return ms; }
        var streams = new List<Stream> { Pgm(img.R), Pgm(img.G), Pgm(img.B), Pgm(img.A), Pgm(mask) };
        try
        {
            var source = J2kImage.CreateEncodableSource(streams) ?? throw new InvalidOperationException("CoreJ2K returned no encodable source for five planes");
            return J2kImage.ToBytes(source, EncoderConfig(w, h, quality));
        }
        finally { foreach (var s in streams) s.Dispose(); }
    }

    /// <summary>Parse the SIZ marker of a codestream (or of the codestream inside a JP2 file).</summary>
    public static SizInfo ParseSiz(byte[] data)
    {
        // find SOC (FF4F) immediately followed by SIZ (FF51)
        var at = -1;
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] == 0x4F && data[i + 2] == 0xFF && data[i + 3] == 0x51) { at = i + 2; break; }
        if (at < 0) throw new FormatException("no SOC+SIZ marker found");
        int U16(int o) => data[o] << 8 | data[o + 1];
        int U32(int o) => data[o] << 24 | data[o + 1] << 16 | data[o + 2] << 8 | data[o + 3];
        var s = at + 2;   // after the FF51 marker: Lsiz(2) Rsiz(2) Xsiz Ysiz XOsiz YOsiz XTsiz YTsiz XTOsiz YTOsiz Csiz(2)
        if (s + 38 > data.Length) throw new FormatException("truncated SIZ");
        return new SizInfo(U32(s + 4), U32(s + 8), U32(s + 12), U32(s + 16), U32(s + 20), U32(s + 24), U32(s + 28), U32(s + 32), U16(s + 36));
    }
}
