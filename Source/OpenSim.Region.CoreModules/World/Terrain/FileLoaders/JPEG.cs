/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using SkiaSharp;
using System;
using System.IO;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Terrain.FileLoaders
{
    public class JPEG : ITerrainLoader
    {
        #region ITerrainLoader Members

        public string FileExtension
        {
            get { return ".jpg"; }
        }

        public ITerrainChannel LoadFile(string filename)
        {
            throw new NotImplementedException();
        }

        public ITerrainChannel LoadFile(string filename, int x, int y, int fileWidth, int fileHeight, int w, int h)
        {
            throw new NotImplementedException();
        }

        public ITerrainChannel LoadStream(Stream stream)
        {
            throw new NotImplementedException();
        }

        public void SaveFile(string filename, ITerrainChannel map)
        {
            using (SKBitmap colours = CreateBitmapFromMap(map))
            using (SKFileWStream stream = new SKFileWStream(filename))
            using (SKPixmap pixmap = colours.PeekPixels())
            {
                SKJpegEncoderOptions options = new SKJpegEncoderOptions(90); // quality 0-100
                pixmap.Encode(stream, SKEncodedImageFormat.Jpeg, options.Quality);
            }
        }

        /// <summary>
        /// Exports a stream using a System.Drawing exporter.
        /// </summary>
        /// <param name="stream">The target stream</param>
        /// <param name="map">The terrain channel being saved</param>
        public void SaveStream(Stream stream, ITerrainChannel map)
        {
            using (SKBitmap colours = CreateBitmapFromMap(map))
            using (SKWStream skStream = new SKManagedWStream(stream))
            using (SKPixmap pixmap = colours.PeekPixels())
            {
                SKJpegEncoderOptions options = new SKJpegEncoderOptions(90); // quality 0-100
                pixmap.Encode(skStream, SKEncodedImageFormat.Jpeg, options.Quality);
            }
        }

        public virtual void SaveFile(ITerrainChannel m_channel, string filename,
                             int offsetX, int offsetY,
                             int fileWidth, int fileHeight,
                             int regionSizeX, int regionSizeY)
        {
            // We need to do this because saving directly to the same file we read from
            // can cause issues on some platforms. Create a temp copy if the file exists.
            string tempName = Path.GetTempFileName();

            SKBitmap existingBitmap = null;
            SKBitmap thisBitmap = null;
            SKBitmap newBitmap = null;

            int expectedWidth = fileWidth * regionSizeX;
            int expectedHeight = fileHeight * regionSizeY;

            try
            {
                if (File.Exists(filename))
                {
                    File.Copy(filename, tempName, true);
                    existingBitmap = SKBitmap.Decode(tempName);

                    if (existingBitmap == null || existingBitmap.Width != expectedWidth || existingBitmap.Height != expectedHeight)
                    {
                        // old file or decode failed, create a fresh target
                        if (existingBitmap != null)
                        {
                            existingBitmap.Dispose();
                            existingBitmap = null;
                        }

                        newBitmap = new SKBitmap(expectedWidth, expectedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                    }
                    else
                    {
                        // reuse decoded bitmap as the base
                        newBitmap = existingBitmap;
                    }
                }
                else
                {
                    newBitmap = new SKBitmap(expectedWidth, expectedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                }

                // Create an image of the provided channel
                thisBitmap = CreateBitmapFromMap(m_channel);

                // Copy this region into the target image at the tile offset
                for (int x = 0; x < thisBitmap.Width; x++)
                {
                    for (int y = 0; y < thisBitmap.Height; y++)
                    {
                        int targetX = x + offsetX * regionSizeX;
                        int targetY = y + (fileHeight - 1 - offsetY) * regionSizeY;

                        // Bounds-check just in case
                        if (targetX >= 0 && targetX < newBitmap.Width && targetY >= 0 && targetY < newBitmap.Height)
                        {
                            SKColor c = thisBitmap.GetPixel(x, y);
                            newBitmap.SetPixel(targetX, targetY, c);
                        }
                    }
                }

                // Save the composed image as JPEG
                using (var img = SKImage.FromBitmap(newBitmap))
                using (var fs = File.Open(filename, FileMode.Create, FileAccess.Write))
                using (var data = img.Encode(SKEncodedImageFormat.Jpeg, 90))
                {
                    data.SaveTo(fs);
                }
            }
            finally
            {
                if (existingBitmap != null)
                    existingBitmap.Dispose();

                if (thisBitmap != null)
                    thisBitmap.Dispose();

                // If newBitmap is a different object to existingBitmap we must dispose it too
                if (newBitmap != null && newBitmap != existingBitmap)
                    newBitmap.Dispose();

                if (File.Exists(tempName))
                    File.Delete(tempName);
            }
        }

        #endregion

        public override string ToString()
        {
            return "JPEG";
        }

        //Returns true if this extension is supported for terrain save-tile
        public bool SupportsTileSave()
        {
            return true;
        }

        private static SKBitmap CreateBitmapFromMap(ITerrainChannel map)
        {
            int pallete;
            SKBitmap bmp;
            SKColor[] colours;

            using (SKBitmap gradientmapLd = SKBitmap.Decode("defaultstripe.png"))
            {
                pallete = gradientmapLd.Height;

                bmp = new SKBitmap(map.Width, map.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                colours = new SKColor[pallete];

                for (int i = 0; i < pallete; i++)
                {
                    colours[i] = gradientmapLd.GetPixel(0, i);
                }
            }

            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    // 512 is the largest possible height before colours clamp
                    int colorindex = (int) (Math.Max(Math.Min(1.0, map[x, y] / 512.0), 0.0) * (pallete - 1));
                    bmp.SetPixel(x, map.Height - y - 1, colours[colorindex]);
                }
            }
            return bmp;
        }
    }
}
