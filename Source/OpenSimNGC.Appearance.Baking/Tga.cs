namespace OpenSimNGC.Appearance.Baking;

/// <summary>A decoded TGA: planar 8-bit channels plus what the file actually carried.</summary>
public sealed class TgaImage
{
    public readonly int W, H;
    public readonly bool IsGray, HasAlpha;
    /// <summary>Grey files: the grey values. Colour files: red.</summary>
    public readonly byte[] R, G, B, A;
    internal TgaImage(int w, int h, bool isGray, bool hasAlpha, byte[] r, byte[] g, byte[] b, byte[] a) { W = w; H = h; IsGray = isGray; HasAlpha = hasAlpha; R = r; G = g; B = b; A = a; }
}

/// <summary>
/// A small Truevision TGA reader for the viewer's bundled character images: grey (types 3/11), true-colour
/// 24/32-bit (types 2/10) and colour-mapped 8-bit (types 1/9), uncompressed or RLE, either row origin.
/// Replaces the client library's Targa decoder so the library carries no client dependency.
/// </summary>
public static class Tga
{
    public static TgaImage Decode(byte[] data)
    {
        if (data.Length < 18) throw new FormatException("TGA: header too short");
        int idLen = data[0], cmapType = data[1], type = data[2];
        int cmapFirst = data[3] | data[4] << 8, cmapLen = data[5] | data[6] << 8, cmapBits = data[7];
        int w = data[12] | data[13] << 8, h = data[14] | data[15] << 8, bpp = data[16], desc = data[17];
        var topLeft = (desc & 0x20) != 0;
        if (w <= 0 || h <= 0) throw new FormatException("TGA: bad size");
        var pos = 18 + idLen;

        byte[]? pal = null;
        var palBytes = 0;
        if (cmapType == 1)
        {
            palBytes = (cmapBits + 7) / 8;
            pal = new byte[cmapLen * palBytes];
            Array.Copy(data, pos, pal, 0, pal.Length);
            pos += pal.Length;
        }

        var rle = type >= 9;
        var baseType = rle ? type - 8 : type;
        var pixBytes = (bpp + 7) / 8;
        var n = w * h;
        var raw = new byte[n * pixBytes];
        if (!rle)
        {
            if (data.Length < pos + raw.Length) throw new FormatException("TGA: truncated pixel data");
            Array.Copy(data, pos, raw, 0, raw.Length);
        }
        else
        {
            var o = 0;
            while (o < raw.Length)
            {
                if (pos >= data.Length) throw new FormatException("TGA: truncated RLE data");
                int packet = data[pos++];
                var count = (packet & 0x7F) + 1;
                if ((packet & 0x80) != 0)
                {
                    if (pos + pixBytes > data.Length) throw new FormatException("TGA: truncated RLE run");
                    for (var i = 0; i < count && o < raw.Length; i++) { Array.Copy(data, pos, raw, o, pixBytes); o += pixBytes; }
                    pos += pixBytes;
                }
                else
                {
                    var len = Math.Min(count * pixBytes, raw.Length - o);
                    if (pos + len > data.Length) throw new FormatException("TGA: truncated RLE literal");
                    Array.Copy(data, pos, raw, o, len);
                    o += len; pos += len;
                }
            }
        }

        bool isGray = baseType == 3, hasAlpha = false;
        var r = new byte[n]; var g = new byte[n]; var b = new byte[n]; var a = new byte[n];
        Array.Fill(a, (byte)255);
        for (var y = 0; y < h; y++)
        {
            var srcRow = topLeft ? y : h - 1 - y;
            for (var x = 0; x < w; x++)
            {
                var s = (srcRow * w + x) * pixBytes;
                var d = y * w + x;
                switch (baseType)
                {
                    case 3:
                        r[d] = g[d] = b[d] = raw[s];
                        if (pixBytes == 2) { a[d] = raw[s + 1]; hasAlpha = true; }
                        break;
                    case 2:
                        if (pixBytes >= 3) { b[d] = raw[s]; g[d] = raw[s + 1]; r[d] = raw[s + 2]; }
                        else { var v = raw[s] | raw[s + 1] << 8; b[d] = (byte)((v & 0x1F) << 3); g[d] = (byte)(((v >> 5) & 0x1F) << 3); r[d] = (byte)(((v >> 10) & 0x1F) << 3); }
                        if (pixBytes == 4) { a[d] = raw[s + 3]; hasAlpha = true; }
                        break;
                    case 1:
                        if (pal is null) throw new FormatException("TGA: colour-mapped image without a colour map");
                        var idx = (pixBytes == 2 ? raw[s] | raw[s + 1] << 8 : raw[s]) - cmapFirst;
                        var p = Math.Clamp(idx, 0, cmapLen - 1) * palBytes;
                        if (palBytes >= 3) { b[d] = pal[p]; g[d] = pal[p + 1]; r[d] = pal[p + 2]; if (palBytes == 4) { a[d] = pal[p + 3]; hasAlpha = true; } }
                        else { var v = pal[p] | pal[p + 1] << 8; b[d] = (byte)((v & 0x1F) << 3); g[d] = (byte)(((v >> 5) & 0x1F) << 3); r[d] = (byte)(((v >> 10) & 0x1F) << 3); }
                        break;
                    default:
                        throw new FormatException($"TGA: unsupported image type {type}");
                }
            }
        }
        return new TgaImage(w, h, isGray, hasAlpha, r, g, b, a);
    }
}
