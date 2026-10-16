/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The byte arithmetic of the bake compositor: the byte product (SPEC.md section 1), the mask ramp (section 8.3)
/// and plane resampling (section 12).
/// </summary>
public static class BakePixelMath
{
    /// <summary>The byte product of a and b, both 0..255: (a*b + 127) / 255 in integers. 255 is the identity (SPEC.md section 1).</summary>
    public static byte ByteProduct(int a, int b) => (byte)((a * b + 127) / 255);

    /// <summary>
    /// The ramp of SPEC.md 8.3 for one grey value. The weight is limited to 0..1. With a domain above 0 the result is a
    /// linear ramp whose soft edge is <paramref name="domain"/> wide and moves with the weight; with a domain of 0 or
    /// less it is a hard step. All arithmetic is single precision, in the order the section gives.
    /// </summary>
    public static byte Ramp(byte grey, float domain, float weight)
    {
        var w = Math.Clamp(weight, 0f, 1f);
        if (domain > 0f)
        {
            var scale = 1f / domain;
            var offset = (1f - domain) * (1f - w);
            var bias = -(scale * offset);
            var v = 255f * (grey / 255f * scale + bias);
            return (byte)Math.Clamp(v, 0f, 255f);
        }
        var threshold = (byte)(255f * (1f - w));
        return grey >= threshold ? (byte)255 : (byte)0;
    }

    /// <summary>The ramp applied to every value of a plane (SPEC.md 8.2, step 2).</summary>
    public static byte[] RampPlane(byte[] grey, float domain, float weight)
    {
        var table = new byte[256];
        for (var g = 0; g < 256; g++) table[g] = Ramp((byte)g, domain, weight);
        var o = new byte[grey.Length];
        for (var i = 0; i < o.Length; i++) o[i] = table[grey[i]];
        return o;
    }

    /// <summary>
    /// Resamples one plane from sw x sh to dw x dh (SPEC.md section 12): unchanged for the same size, a rounded box
    /// average for an exact integer downscale, bilinear (single precision, edge pixels repeated) for everything else.
    /// A plane's aspect ratio is not kept.
    /// </summary>
    public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return src;
        var dst = new byte[dw * dh];
        if (dw <= sw && dh <= sh && sw % dw == 0 && sh % dh == 0)
        {
            int bw = sw / dw, bh = sh / dh, area = bw * bh;
            for (var y = 0; y < dh; y++)
                for (var x = 0; x < dw; x++)
                {
                    var sum = 0;
                    for (var yy = 0; yy < bh; yy++)
                    {
                        var row = (y * bh + yy) * sw + x * bw;
                        for (var xx = 0; xx < bw; xx++) sum += src[row + xx];
                    }
                    dst[y * dw + x] = (byte)((sum + area / 2) / area);
                }
            return dst;
        }

        var rx = (float)sw / dw;
        var ry = (float)sh / dh;
        var xi0 = new int[dw]; var xi1 = new int[dw]; var xt = new float[dw];
        for (var x = 0; x < dw; x++)
        {
            var fx = (x + 0.5f) * rx - 0.5f;
            var x0 = (int)MathF.Floor(fx);
            xt[x] = fx - x0;
            xi0[x] = Math.Clamp(x0, 0, sw - 1);
            xi1[x] = Math.Clamp(x0 + 1, 0, sw - 1);
        }
        for (var y = 0; y < dh; y++)
        {
            var fy = (y + 0.5f) * ry - 0.5f;
            var y0 = (int)MathF.Floor(fy);
            var ty = fy - y0;
            var r0 = Math.Clamp(y0, 0, sh - 1) * sw;
            var r1 = Math.Clamp(y0 + 1, 0, sh - 1) * sw;
            for (var x = 0; x < dw; x++)
            {
                float a = src[r0 + xi0[x]], b = src[r0 + xi1[x]];
                float c = src[r1 + xi0[x]], d = src[r1 + xi1[x]];
                var top = a + (b - a) * xt[x];
                var bottom = c + (d - c) * xt[x];
                var v = top + (bottom - top) * ty + 0.5f;
                dst[y * dw + x] = (byte)Math.Clamp(v, 0f, 255f);
            }
        }
        return dst;
    }
}
