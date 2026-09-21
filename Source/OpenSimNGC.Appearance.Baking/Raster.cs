using SkiaSharp;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>A single 8-bit channel.</summary>
public sealed class Plane
{
    public readonly int W, H;
    public readonly byte[] Data;
    public Plane(int w, int h, byte[]? data = null) { W = w; H = h; Data = data ?? new byte[w * h]; }
    public static Plane Filled(int w, int h, byte v) { var p = new Plane(w, h); if (v != 0) Array.Fill(p.Data, v); return p; }
}

/// <summary>Four planar 8-bit channels. `HasAlpha` false means the source had no alpha (A is all 255).</summary>
public sealed class RgbaPlanes
{
    public readonly int W, H;
    public readonly byte[] R, G, B, A;
    public readonly bool HasAlpha;
    /// <summary>A fifth 8-bit plane when the source carried one: the morph mask of a viewer bake (Docs/MORPH-MASK-PASS.md). Null otherwise.</summary>
    public byte[]? Mask;

    public RgbaPlanes(int w, int h, bool hasAlpha)
    {
        W = w; H = h; HasAlpha = hasAlpha;
        R = new byte[w * h]; G = new byte[w * h]; B = new byte[w * h]; A = new byte[w * h];
        if (!hasAlpha) Array.Fill(A, (byte)255);
    }

    internal RgbaPlanes(int w, int h, byte[] r, byte[] g, byte[] b, byte[] a, bool hasAlpha) { W = w; H = h; R = r; G = g; B = b; A = a; HasAlpha = hasAlpha; }

    /// <summary>To an unpremultiplied RGBA8888 SkiaSharp bitmap (for JPEG 2000 encoding or PNG dumps).</summary>
    public SKBitmap ToSkBitmap()
    {
        var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var px = new SKColor[W * H];
        for (var i = 0; i < px.Length; i++) px[i] = new SKColor(R[i], G[i], B[i], A[i]);
        bmp.Pixels = px;
        return bmp;
    }

    /// <summary>From any SkiaSharp bitmap; alpha is taken as present when the bitmap's alpha type is not opaque.</summary>
    public static RgbaPlanes FromSkBitmap(SKBitmap bmp)
    {
        var hasAlpha = bmp.AlphaType != SKAlphaType.Opaque;
        var p = new RgbaPlanes(bmp.Width, bmp.Height, hasAlpha);
        var px = bmp.Pixels;
        for (var i = 0; i < px.Length; i++) { p.R[i] = px[i].Red; p.G[i] = px[i].Green; p.B[i] = px[i].Blue; if (hasAlpha) p.A[i] = px[i].Alpha; }
        return p;
    }

    public RgbaPlanes Resample(int w, int h)
    {
        if (w == W && h == H) return this;
        var p = new RgbaPlanes(w, h, Raster.Resample(R, W, H, w, h), Raster.Resample(G, W, H, w, h), Raster.Resample(B, W, H, w, h),
            HasAlpha ? Raster.Resample(A, W, H, w, h) : Filled(w * h, 255), HasAlpha);
        if (Mask is not null) p.Mask = Raster.Resample(Mask, W, H, w, h);
        return p;
    }

    private static byte[] Filled(int n, byte v) { var a = new byte[n]; Array.Fill(a, v); return a; }
}

/// <summary>Pixel helpers: resampling and the viewer's alpha-mask ramp.</summary>
public static class Raster
{
    public static byte Mul(byte a, byte b) => (byte)((a * b + 127) / 255);

    /// <summary>
    /// Resample one channel. Downscaling averages the covered source box (no aliasing on 2048 sources),
    /// upscaling is bilinear with clamped edges, which is what the viewer's GL sampling of a smaller layer does.
    /// </summary>
    public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return src;
        var dst = new byte[dw * dh];
        if (dw <= sw && dh <= sh && sw % dw == 0 && sh % dh == 0)
        {
            int fx = sw / dw, fy = sh / dh, area = fx * fy;
            for (var y = 0; y < dh; y++)
                for (var x = 0; x < dw; x++)
                {
                    var sum = 0;
                    for (var yy = 0; yy < fy; yy++)
                    {
                        var row = (y * fy + yy) * sw + x * fx;
                        for (var xx = 0; xx < fx; xx++) sum += src[row + xx];
                    }
                    dst[y * dw + x] = (byte)((sum + area / 2) / area);
                }
            return dst;
        }
        float rx = (float)sw / dw, ry = (float)sh / dh;
        for (var y = 0; y < dh; y++)
        {
            var sy = (y + 0.5f) * ry - 0.5f;
            var y0 = (int)MathF.Floor(sy);
            var ty = sy - y0;
            var ya = Math.Clamp(y0, 0, sh - 1) * sw;
            var yb = Math.Clamp(y0 + 1, 0, sh - 1) * sw;
            for (var x = 0; x < dw; x++)
            {
                var sx = (x + 0.5f) * rx - 0.5f;
                var x0 = (int)MathF.Floor(sx);
                var tx = sx - x0;
                var xa = Math.Clamp(x0, 0, sw - 1);
                var xb = Math.Clamp(x0 + 1, 0, sw - 1);
                var top = src[ya + xa] + (src[ya + xb] - src[ya + xa]) * tx;
                var bot = src[yb + xa] + (src[yb + xb] - src[yb + xa]) * tx;
                dst[y * dw + x] = (byte)Math.Clamp(top + (bot - top) * ty + 0.5f, 0, 255);
            }
        }
        return dst;
    }

    public static Plane Resample(Plane p, int w, int h) => w == p.W && h == p.H ? p : new Plane(w, h, Resample(p.Data, p.W, p.H, w, h));

    /// <summary>
    /// The viewer's LLImageTGA::decodeAndProcess: a mask file's grey value is pushed through a ramp whose
    /// position follows the parameter weight and whose width is `domain` (0 = hard step at 1 - weight).
    /// </summary>
    public static byte[] ProcessAlpha(byte[] gray, float domain, float weight)
    {
        var lut = new byte[256];
        var w = Math.Clamp(weight, 0f, 1f);
        if (domain > 0f)
        {
            var scale = 1f / domain;
            var offset = (1f - domain) * (1f - w);
            var bias = -(scale * offset);
            for (var i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp(255f * (i / 255f * scale + bias), 0f, 255f);
        }
        else
        {
            var threshold = (byte)(255f * (1f - w));
            for (var i = 0; i < 256; i++) lut[i] = i >= threshold ? (byte)255 : (byte)0;
        }
        var dst = new byte[gray.Length];
        for (var i = 0; i < gray.Length; i++) dst[i] = lut[gray[i]];
        return dst;
    }
}
