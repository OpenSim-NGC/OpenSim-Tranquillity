/*
 * SkiaSharp-based SculptMap
 */
using System;
using System.Collections.Generic;
using SkiaSharp;
using OpenMetaverse;

namespace PrimMesher
{
    public class SculptMap
    {
        public int width;
        public int height;
        public byte[] redBytes;
        public byte[] greenBytes;
        public byte[] blueBytes;

        public SculptMap()
        {
        }

        public SculptMap(SKBitmap bm, int lod)
        {
            int bmW = bm.Width;
            int bmH = bm.Height;

            if (bmW == 0 || bmH == 0)
                throw new Exception("SculptMap: bitmap has no data");

            int numLodPixels = lod * lod;

            bool needsScaling = false;
            bool smallMap = false;

            width = bmW;
            height = bmH;

            while (width * height > numLodPixels * 4)
            {
                width >>= 1;
                height >>= 1;
                needsScaling = true;
            }

            if (needsScaling)
                bm = ScaleImage(bm, width, height);

            if (width * height > numLodPixels)
            {
                smallMap = false;
                width >>= 1;
                height >>= 1;
            }
            else
                smallMap = true;

            int numBytes = (width + 1) * (height + 1);
            redBytes = new byte[numBytes];
            greenBytes = new byte[numBytes];
            blueBytes = new byte[numBytes];

            int byteNdx = 0;

            try
            {
                for (int y = 0; y <= height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        SKColor c;
                        if (smallMap)
                            c = bm.GetPixel(x, y < height ? y : y - 1);
                        else
                            c = bm.GetPixel(x * 2, y < height ? y * 2 : y * 2 - 1);

                        redBytes[byteNdx] = c.Red;
                        greenBytes[byteNdx] = c.Green;
                        blueBytes[byteNdx] = c.Blue;

                        ++byteNdx;
                    }

                    SKColor c2;
                    if (smallMap)
                        c2 = bm.GetPixel(width - 1, y < height ? y : y - 1);
                    else
                        c2 = bm.GetPixel(width * 2 - 1, y < height ? y * 2 : y * 2 - 1);

                    redBytes[byteNdx] = c2.Red;
                    greenBytes[byteNdx] = c2.Green;
                    blueBytes[byteNdx] = c2.Blue;

                    ++byteNdx;
                }
            }
            catch (Exception e)
            {
                if (needsScaling)
                    bm.Dispose();
                throw new Exception("Caught exception processing byte arrays in SculptMap(): e: " + e.ToString());
            }

            width++;
            height++;
            if (needsScaling)
                bm.Dispose();
        }

        public List<List<Vector3>> ToRows(bool mirror)
        {
            int numRows = height;
            int numCols = width;

            List<List<Vector3>> rows = new List<List<Vector3>>(numRows);

            float pixScale = 1.0f / 255;

            int smNdx = 0;

            for (int rowNdx = 0; rowNdx < numRows; rowNdx++)
            {
                List<Vector3> row = new List<Vector3>(numCols);
                for (int colNdx = 0; colNdx < numCols; colNdx++)
                {
                    if (mirror)
                        row.Add(new Vector3(-((float)redBytes[smNdx] * pixScale - 0.5f), ((float)greenBytes[smNdx] * pixScale - 0.5f), (float)blueBytes[smNdx] * pixScale - 0.5f));
                    else
                        row.Add(new Vector3((float)redBytes[smNdx] * pixScale - 0.5f, (float)greenBytes[smNdx] * pixScale - 0.5f, (float)blueBytes[smNdx] * pixScale - 0.5f));

                    ++smNdx;
                }
                rows.Add(row);
            }
            return rows;
        }

        private SKBitmap ScaleImage(SKBitmap srcImage, int destWidth, int destHeight)
        {
            SKBitmap scaledImage = new SKBitmap(destWidth, destHeight, srcImage.ColorType, srcImage.AlphaType);

            float xscale = (float)srcImage.Width / (float)destWidth;
            float yscale = (float)srcImage.Height / (float)destHeight;

            int lastsx = srcImage.Width - 1;
            int lastsy = srcImage.Height - 1;
            int lastdx = destWidth - 1;
            int lastdy = destHeight - 1;

            float sy = 0.5f;

            for (int y = 0; y < lastdy; y++)
            {
                float sx = 0.5f;
                for (int x = 0; x < lastdx; x++)
                {
                    try
                    {
                        SKColor c = srcImage.GetPixel((int)(sx), (int)(sy));
                        scaledImage.SetPixel(x, y, new SKColor(c.Red, c.Green, c.Blue));
                    }
                    catch (IndexOutOfRangeException)
                    {
                    }
                    sx += xscale;
                }
                try
                {
                    SKColor c = srcImage.GetPixel(lastsx, (int)(sy));
                    scaledImage.SetPixel(lastdx, y, new SKColor(c.Red, c.Green, c.Blue));
                }
                catch (IndexOutOfRangeException)
                {
                }

                sy += yscale;
            }

            float sx2 = 0.5f;
            for (int x = 0; x < lastdx; x++)
            {
                try
                {
                    SKColor c = srcImage.GetPixel((int)(sx2), lastsy);
                    scaledImage.SetPixel(x, lastdy, new SKColor(c.Red, c.Green, c.Blue));
                }
                catch (IndexOutOfRangeException)
                {
                }

                sx2 += xscale;
            }
            try
            {
                SKColor c = srcImage.GetPixel(lastsx, lastsy);
                scaledImage.SetPixel(lastdx, lastdy, new SKColor(c.Red, c.Green, c.Blue));
            }
            catch (IndexOutOfRangeException)
            {
            }

            srcImage.Dispose();
            return scaledImage;
        }
    }
}