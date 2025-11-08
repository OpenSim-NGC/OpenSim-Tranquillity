using System;
using System.Net;
using SkiaSharp;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Texture.
    /// </summary>

    public class warp_Texture : IDisposable
    {
        private bool disposed = false;
        public int width;
        public int height;
        public int bitWidth;
        public int bitHeight;
        public int[] pixel;
        public String path = null;
        public int averageColor;
        public bool hasAlpha;
        public bool opaque;
        public bool hasmips;
        public int[][] mips;
        public int[] mipsBitWidth;
        public int[] mipsBitHeight;
        public int[] mipstw;
        public int[] mipsth;
        public int maxmips;

        public static int ALPHA = unchecked((int)0xFF000000); // alpha mask

        public warp_Texture(int w, int h)
        {
            height = h;
            width = w;
            pixel = new int[w * h];
            cls();
        }

        public warp_Texture(int w, int h, int[] data)
        {
            height = h;
            width = w;
            pixel = new int[width * height];

            System.Array.Copy(data, pixel, pixel.Length);
        }

        public warp_Texture(SKBitmap map, int maxBitSize = -1)
        {
            loadTextureFromSKBitmap(map, maxBitSize);
        }

        public warp_Texture(string path, int maxBitSize = -1)
        {
            using (var map = SKBitmap.Decode(path))
            {
                if (map != null)
                    loadTextureFromSKBitmap(map, maxBitSize);
            }
        }

        public void resize()
        {
            double log2inv = 1 / Math.Log(2);

            int w = (int)Math.Pow(2, bitWidth = (int)Math.Ceiling((Math.Log(width) * log2inv)));
            int h = (int)Math.Pow(2, bitHeight = (int)Math.Ceiling((Math.Log(height) * log2inv)));

            if(!(w == width && h == height))
                resize(w, h);
        }

        public void resize(int w, int h)
        {
            setSize(w, h);
        }

        public void cls()
        {
            warp_Math.clearBuffer(pixel, 0);
        }

        public warp_Texture toAverage()
        {
            for(int i = width * height - 1; i >= 0; i--)
                pixel[i] = warp_Color.getAverage(pixel[i]);

            return this;
        }

        public warp_Texture toGray()
        {
            for(int i = width * height - 1; i >= 0; i--)
                pixel[i] = warp_Color.getGray(pixel[i]);

            return this;
        }

        public warp_Texture valToGray()
        {
            int intensity;
            for(int i = width * height - 1; i >= 0; i--)
            {
                intensity = warp_Math.crop(pixel[i], 0, 255);
                pixel[i] = warp_Color.getColor(intensity, intensity, intensity);
            }

            return this;
        }

        public warp_Texture colorize(int[] pal)
        {
            int range = pal.GetLength(0) - 1;
            for(int i = width * height - 1; i >= 0; i--)
                pixel[i] = pal[warp_Math.crop(pixel[i], 0, range)];

            return this;
        }

        private void loadTextureFromSKBitmap(SKBitmap pmap, int maxBitSize = -1)
        {
            SKBitmap map = pmap;
            bool disposemap = false;

            opaque = true;
            width = map.Width;
            height = map.Height;

            // determine bit sizes (power of two)
            const double log2inv = 1.4426950408889634073599246810019;
            bitWidth = (int)Math.Ceiling((Math.Log(width) * log2inv));
            bitHeight = (int)Math.Ceiling((Math.Log(height) * log2inv));

            if (maxBitSize > 3)
            {
                if (bitWidth > bitHeight)
                {
                    if (bitWidth > maxBitSize)
                    {
                        int ex = bitWidth - maxBitSize;
                        bitWidth = maxBitSize;
                        bitHeight -= ex;
                        if (bitHeight < 0)
                            bitHeight = 0;
                    }
                }
                else
                {
                    if (bitHeight > maxBitSize)
                    {
                        int ex = bitHeight - maxBitSize;
                        bitHeight = maxBitSize;
                        bitWidth -= ex;
                        if (bitWidth < 0)
                            bitWidth = 0;
                    }
                }
            }

            int w = (int)Math.Pow(2, bitWidth);
            int h = (int)Math.Pow(2, bitHeight);

            // Ensure format is BGRA8888 for easy copying and consistent layout
            if (map.ColorType != SKColorType.Bgra8888)
            {
                var converted = map.Copy(SKColorType.Bgra8888);
                map = converted ?? map;
                disposemap = true;
            }

            if (width != w || height != h)
            {
                var resized = ResizeSKBitmap(map, w, h);
                if (resized != null)
                {
                    if (disposemap) map.Dispose();
                    map = resized;
                    disposemap = true;
                    width = w;
                    height = h;
                }
            }

            ulong avgR = 0;
            ulong avgG = 0;
            ulong avgB = 0;
            ulong avgA = 0;

            pixel = new int[width * height];
            hasAlpha = true; // SKBitmap.Bgra8888 includes alpha channel

            // Copy pixels from SKBitmap (BGRA order)
            IntPtr srcPtr = map.GetPixels();
            int rowBytes = map.RowBytes;
            byte[] buf = new byte[rowBytes * height];
            System.Runtime.InteropServices.Marshal.Copy(srcPtr, buf, 0, buf.Length);

            int nPixel = 0;
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowStart + x * 4;
                    byte blue = buf[idx + 0];
                    byte green = buf[idx + 1];
                    byte red = buf[idx + 2];
                    byte alpha = buf[idx + 3];

                    avgB += blue;
                    avgG += green;
                    avgR += red;
                    avgA += alpha;

                    if (opaque && alpha != 0xff)
                        opaque = false;

                    pixel[nPixel++] = alpha << 24 | red << 16 | green << 8 | blue;
                }
            }

            if (disposemap)
                map.Dispose();

            ulong np = (ulong)(width * height);
            byte blueAvg = (byte)(avgB / np);
            byte greenAvg = (byte)(avgG / np);
            byte redAvg = (byte)(avgR / np);
            byte alphaAvg = (byte)(avgA / np);
            averageColor = alphaAvg << 24 | redAvg << 16 | greenAvg << 8 | blueAvg;

            maxmips = bitHeight;
            if (maxmips > bitWidth)
                maxmips = bitWidth;

            maxmips -= 3; // less 1x1 2x2 and max
            hasmips = false;
        }

        public void GenMips()
        {
            int nmips = maxmips + 1;
            mips = new int[nmips + 1][];
            mips[nmips] = pixel;

            mipsBitWidth = new int[maxmips + 1];
            mipsBitHeight = new int[maxmips + 1];
            mipstw = new int[maxmips + 1];
            mipsth = new int[maxmips + 1];
  
            int w = width;
            int h = height;
            int bw = bitWidth;
            int bh = bitHeight;

            int[] src;
            int[] dst = null;
            for(int n = maxmips; n >= 0; --n)
            {
                src = mips[n + 1];
                if(w > 1)
                {
                    w >>= 1;
                    --bw;
                    if(h > 1)
                    {
                        h >>= 1;
                        --bh;
                        dst = dec2XY(src, w, h);
                    }
                    else
                        dst = dec2X(src, w, h);
                }
                else
                {
                    if(h > 1)
                    {
                        h >>= 1;
                        --bh;
                        dst = dec2Y(src, w, h);
                    }
                }
                mips[n] = dst;
                mipsBitWidth[n] = bw;
                mipsBitHeight[n] = bh;
                mipstw[n] = w;
                mipsth[n] = h;
            }
            hasmips = true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private unsafe int[] dec2XY(int[] src, int w, int h)
        {
            int[] dst = new int[w * h];
            int sw = 2 * w;
            int stot = 2 * h * sw;
            int c1;
            int c2;
            int c3;
            int c4;
            int sxl2 = sw;
            int dx = 0;
            fixed (int* s = src, d = dst)
            {
                for (int sx = 0 ; sx < stot; sx += sw, sxl2 += sw)
                {
                    for(int i = 0; i < w ; i++, sx += 2, sxl2 += 2)
                    {
                        c1 = s[sx];
                        c2 = s[sx + 1];
                        c3 = s[sxl2];
                        c4 = s[sxl2 + 1];
                        d[dx++] = warp_Color.avg4c(c1, c2, c3, c4);
                    }
                }
            }
            return dst;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int[] dec2X(int[] src, int w, int h)
        {
            int dstsize = w * h;
            int[] dst = new int[dstsize];
            int c1;
            int c2;
            int sx = 0;

            for(int dx = 0 ; dx < dstsize; ++dx)
            {
                c1 = src[sx];
                c2 = src[sx + 1];

                dst[dx] = warp_Color.avg2c(c1, c2);
                sx += 2;
            }

            return dst;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private unsafe int[] dec2Y(int[] src, int w, int h)
        {
            int[] dst = new int[w * h];
            int stot = 2 * h * w;
            int c1;
            int c2;
            int sxl2 = w;
            int dx = 0;
            fixed (int* s = src, d = dst)
            {
                for (int sx = 0 ; sx < stot; sx += w, sxl2 += w)
                {
                    for(int i = 0; i < w ; i++, ++sx, ++sxl2)
                    {
                        c1 = s[sx];
                        c2 = s[sxl2];
                        d[dx++] = warp_Color.avg2c(c1, c2);
                    }
                }
            }
            return dst;
        }

        private unsafe void setSize(int w, int h)
        {
            int offset = w * h;
            int offset2;
            if(w * h != 0)
            {
                int[] newpixels = new int[w * h];
                fixed(int* np = newpixels, p = pixel)
                {
                    for(int j = h - 1; j >= 0; j--)
                    {
                        offset -= w;
                        offset2 = (j * height / h) * width;
                        for(int i = w - 1; i >= 0; i--)
                            np[i + offset] = p[(i * width / w) + offset2];
                    }
                }
                width = w;
                height = h;
                pixel = newpixels;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool inrange(int a, int b, int c)
        {
            return (a >= b) & (a < c);
        }

        public warp_Texture getClone()
        {
            warp_Texture t = new warp_Texture(width, height);
            warp_Math.copyBuffer(pixel, t.pixel);
            return t;
        }

        private static SKBitmap ResizeSKBitmap(SKBitmap src, int width, int height)
        {
            if (src == null) return null;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var dst = new SKBitmap(info);
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                var srcRect = new SKRect(0, 0, src.Width, src.Height);
                var dstRect = new SKRect(0, 0, width, height);
                var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
                canvas.DrawBitmap(src, srcRect, dstRect, paint);
            }
            return dst;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources if any
                    pixel = null;
                    mips = null;
                    mipsBitWidth = null;
                    mipsBitHeight = null;
                    mipstw = null;
                    mipsth = null;
                }

                disposed = true;
            }
        }

        ~warp_Texture()
        {
            Dispose(false);
        }

    }
}
