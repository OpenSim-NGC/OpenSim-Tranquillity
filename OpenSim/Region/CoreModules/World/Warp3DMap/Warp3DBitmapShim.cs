using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using Warp3D;
using OpenMetaverse.Imaging;

namespace OpenSim.Region.CoreModules.World.Warp3DMap
{
    /// <summary>
    /// Shim that isolates all System.Drawing usage needed by the Warp3D renderer.
    /// The goal is to keep System.Drawing references in a single file so the rest
    /// of the codebase can be migrated to SkiaSharp and the shim can be replaced
    /// later with a System.Drawing-free Warp3D binding.
    /// </summary>
    internal static class Warp3DBitmapShim
    {
        public static SKBitmap BitmapToSKBitmap(object bmpObj)
        {
            if (bmpObj is not Bitmap bitmap)
                return null;

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var skbitmap = new SKBitmap(info);

                IntPtr srcPtr = bmpData.Scan0;
                int srcStride = Math.Abs(bmpData.Stride);
                int dstRowBytes = skbitmap.RowBytes;
                byte[] row = new byte[srcStride];

                IntPtr dstBase = skbitmap.GetPixels();
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(srcPtr + y * bmpData.Stride, row, 0, srcStride);
                    Marshal.Copy(row, 0, dstBase + y * dstRowBytes, bitmap.Width * 4);
                }

                return skbitmap;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        public static Bitmap SKBitmapToBitmap(SKBitmap sk)
        {
            if (sk == null) return null;

            var bmp = new Bitmap(sk.Width, sk.Height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                IntPtr src = sk.GetPixels();
                int srcRowBytes = sk.RowBytes;
                int dstStride = Math.Abs(bmpData.Stride);
                byte[] row = new byte[srcRowBytes];
                IntPtr dstBase = bmpData.Scan0;

                for (int y = 0; y < sk.Height; y++)
                {
                    Marshal.Copy(src + y * srcRowBytes, row, 0, srcRowBytes);
                    Marshal.Copy(row, 0, dstBase + y * dstStride, sk.Width * 4);
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        public static warp_Texture CreateWarpTextureFromSKBitmap(SKBitmap sk, int reduceLevel = 0)
        {
            if (sk == null) return null;
            // Create warp_Texture directly from SKBitmap (new Warp3D API)
            if (reduceLevel == 0)
                return new warp_Texture(sk);
            // When reduceLevel is provided, the warp_Texture implementation
            // accepts SKBitmap and will apply reduction as needed via its API.
            return new warp_Texture(sk, reduceLevel);
        }

        // Helper to encode an SKBitmap to JPEG2000 using the existing OpenJPEG helper
        public static byte[] EncodeSKBitmapToJpeg2000(SKBitmap sk, bool lossless = false)
        {
            if (sk == null) return null;

        // Helper to convert OpenMetaverse.Imaging.ManagedImage to SKBitmap
        public static SKBitmap ManagedImageToSKBitmap(ManagedImage managed)
        {
            if (managed == null) return null;

            var info = new SKImageInfo(managed.Width, managed.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var skbitmap = new SKBitmap(info);

            for (int y = 0; y < managed.Height; y++)
            {
                for (int x = 0; x < managed.Width; x++)
                {
                    int i = y * managed.Width + x;
                    byte r = managed.Red[i];
                    byte g = managed.Green[i];
                    byte b = managed.Blue[i];
                    byte a = managed.Alpha != null ? managed.Alpha[i] : (byte)255;
                    skbitmap.SetPixel(x, y, new SKColor(r, g, b, a));
                }
            }
            return skbitmap;
        }

        // Build a ManagedImage directly from the SKBitmap pixel data so we
        // can call the OpenJPEG encoder without converting through System.Drawing.Bitmap.
        int w = sk.Width;
        int h = sk.Height;            // Determine whether the SKBitmap contains an alpha channel
            bool hasAlpha = sk.Info.AlphaType != SKAlphaType.Opaque;

            var channels = ManagedImage.ImageChannels.Color;
            if (hasAlpha)
                channels |= ManagedImage.ImageChannels.Alpha;

            var managed = new ManagedImage(w, h, channels);
            managed.Red = new byte[w * h];
            managed.Green = new byte[w * h];
            managed.Blue = new byte[w * h];
            if (hasAlpha)
                managed.Alpha = new byte[w * h];

            // Extract pixels. SKBitmap stores pixels row-major; use GetPixel for clarity.
            // This is not the highest-performance path but is safe and readable.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    SKColor c = sk.GetPixel(x, y);
                    managed.Red[i] = c.Red;
                    managed.Green[i] = c.Green;
                    managed.Blue[i] = c.Blue;
                    if (hasAlpha)
                        managed.Alpha[i] = c.Alpha;
                }
            }

            try
            {
                return OpenJPEG.Encode(managed, lossless);
            }
            finally
            {
                // Free managed image buffers if the implementation provides Clear
                try { managed.Clear(); } catch { }
            }
        }
    }
}
