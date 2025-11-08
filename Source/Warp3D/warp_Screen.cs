using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Warp3D
{
    /// <summary>
    /// Summary description for warp_Screen.
    /// </summary>
    unsafe public class warp_Screen : IDisposable
    {
        public int width;
        public int height;
        SKBitmap image = null;
        public int[] pixels;
        private GCHandle handle;

        public warp_Screen(int w, int h)
        {
            width = w;
            height = h;

            pixels = new int[w * h];

            handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            IntPtr pointer = Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0);

            // Create SKBitmap that uses the pinned pixels (BGRA, premultiplied)
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            image = new SKBitmap();
            // InstallPixels expects rowBytes in bytes
            int rowBytes = w * 4;
            // Install the existing pixel memory so Skia draws directly into our array
            if (!image.InstallPixels(info, pointer, rowBytes))
            {
                // Fallback: create independent bitmap and copy later
                image = new SKBitmap(info);
            }
        }

        public void clear(int c)
        {
            warp_Math.clearBuffer(pixels, c);
        }

        public void draw(warp_Texture texture, int posx, int posy, int xsize, int ysize)
        {
            draw(width, height, texture, posx, posy, xsize, ysize);
        }

        public void drawBackground(warp_Texture texture, int posx, int posy, int xsize, int ysize)
        {
            draw(width, height, texture, posx, posy, xsize, ysize);
        }

        public SKBitmap getImage()
        {
            // Return a copy of the SKBitmap to avoid external mutation of our backing array
            SKBitmap copy = new SKBitmap();
            image.CopyTo(copy);
            return copy;
        }

        private unsafe void draw(int width, int height, warp_Texture texture, int posx, int posy, int xsize, int ysize)
        {
            if (texture == null)
            {
                return;
            }

            int w = xsize;
            int h = ysize;
            int xBase = posx;
            int yBase = posy;
            int tx = texture.width * 255;
            int ty = texture.height * 255;
            int tw = texture.width;
            int dtx = tx / w;
            int dty = ty / h;
            int txBase = warp_Math.crop(-xBase * dtx, 0, 255 * tx);
            int tyBase = warp_Math.crop(-yBase * dty, 0, 255 * ty);
            int xend = warp_Math.crop(xBase + w, 0, width);
            int yend = warp_Math.crop(yBase + h, 0, height);
            int offset1, offset2;
            xBase = warp_Math.crop(xBase, 0, width);
            yBase = warp_Math.crop(yBase, 0, height);

            fixed(int* px = pixels, txp = texture.pixel)
            {
                ty = tyBase;
                for (int j = yBase; j < yend; j++)
                {
                    tx = txBase;
                    offset1 = j * width;
                    offset2 = (ty >> 8) * tw;
                    for (int i = xBase; i < xend; i++)
                    {
                        px[i + offset1] = unchecked((int)0xff000000) | txp[(tx >> 8) + offset2];
                        tx += dtx;
                    }
                    ty += dty;
                }
            }
        }

        public void add(warp_Texture texture, int posx, int posy, int xsize, int ysize)
        {
            add(width, height, texture, posx, posy, xsize, ysize);
        }

        private void add(int width, int height, warp_Texture texture, int posx, int posy, int xsize, int ysize)
        {
            if (texture == null)
            {
                return;
            }

            int w = xsize;
            int h = ysize;
            int xBase = posx;
            int yBase = posy;
            int tx = texture.width * 255;
            int ty = texture.height * 255;
            int tw = texture.width;
            int dtx = tx / w;
            int dty = ty / h;
            int txBase = warp_Math.crop(-xBase * dtx, 0, 255 * tx);
            int tyBase = warp_Math.crop(-yBase * dty, 0, 255 * ty);
            int xend = warp_Math.crop(xBase + w, 0, width);
            int yend = warp_Math.crop(yBase + h, 0, height);
            int offset1, offset2;
            xBase = warp_Math.crop(xBase, 0, width);
            yBase = warp_Math.crop(yBase, 0, height);

            ty = tyBase;
            fixed (int* px = pixels, txp = texture.pixel)
            {
                for (int j = yBase; j < yend; j++)
                {
                    tx = txBase;
                    offset1 = j * width;
                    offset2 = (ty >> 8) * tw;
                    for (int i = xBase; i < xend; i++)
                    {
                        px[i + offset1] = unchecked((int)0xff000000) | warp_Color.add(txp[(tx >> 8) + offset2], px[i + offset1]);
                        tx += dtx;
                    }
                    ty += dty;
                }
            }
        }

        public void Dispose()
        {
            if (image != null)
            {
                handle.Free();
                image.Dispose();
                image = null;
                pixels = null;
            }
        }
    }
}
