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

using System;
using SkiaSharp;
using System.IO;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Terrain.FileLoaders
{
    /// <summary>
    /// A virtual class designed to have methods overloaded,
    /// this class provides an interface for a generic image
    /// saving and loading mechanism, but does not specify the
    /// format. It should not be insubstantiated directly.
    /// </summary>
    public class GenericSystemDrawing : ITerrainLoader
    {
        #region ITerrainLoader Members

        public string FileExtension
        {
            get { return ".gsd"; }
        }

        /// <summary>
        /// Loads a file from a specified filename on the disk,
        /// parses the image using SkiaSharp
        /// then returns a terrain channel. Values are
        /// returned based on brightness between 0m and 128m
        /// </summary>
        /// <param name="filename">The target image to load</param>
        /// <returns>A terrain channel generated from the image.</returns>
        public virtual ITerrainChannel LoadFile(string filename)
        {
            using (var bitmap = SKBitmap.Decode(filename))
            {
                if (bitmap == null)
                    throw new IOException($"Failed to load image from {filename}");
                return LoadBitmap(bitmap);
            }
        }

        public virtual ITerrainChannel LoadFile(string filename, int offsetX, int offsetY, int fileWidth, int fileHeight, int w, int h)
        {
            using (var bitmap = SKBitmap.Decode(filename))
            {
                if (bitmap == null)
                    throw new IOException($"Failed to load image from {filename}");
                    
                ITerrainChannel retval = new TerrainChannel(w, h);

                for (int x = 0; x < retval.Width; x++)
                {
                    for (int y = 0; y < retval.Height; y++)
                    {
                        int pixelX = offsetX * retval.Width + x;
                        int pixelY = (bitmap.Height - (retval.Height * (offsetY + 1))) + retval.Height - y - 1;
                        
                        if (pixelX >= 0 && pixelX < bitmap.Width && pixelY >= 0 && pixelY < bitmap.Height)
                        {
                            SKColor pixel = bitmap.GetPixel(pixelX, pixelY);
                            // Get brightness: (0.299*R + 0.587*G + 0.114*B) / 255
                            float brightness = (0.299f * pixel.Red + 0.587f * pixel.Green + 0.114f * pixel.Blue) / 255f;
                            retval[x, y] = brightness * 128;
                        }
                    }
                }

                return retval;
            }
        }

        public virtual ITerrainChannel LoadStream(Stream stream)
        {
            using (var bitmap = SKBitmap.Decode(stream))
            {
                if (bitmap == null)
                    throw new IOException("Failed to decode image from stream");
                return LoadBitmap(bitmap);
            }
        }

        protected virtual ITerrainChannel LoadBitmap(SKBitmap bitmap)
        {
            ITerrainChannel retval = new TerrainChannel(bitmap.Width, bitmap.Height);

            int x;
            for (x = 0; x < bitmap.Width; x++)
            {
                int y;
                for (y = 0; y < bitmap.Height; y++)
                {
                    SKColor pixel = bitmap.GetPixel(x, bitmap.Height - y - 1);
                    // Calculate brightness: (0.299*R + 0.587*G + 0.114*B) / 255
                    float brightness = (0.299f * pixel.Red + 0.587f * pixel.Green + 0.114f * pixel.Blue) / 255f;
                    retval[x, y] = brightness * 128;
                }
            }

            return retval;
        }

        /// <summary>
        /// Exports a file to a image on the disk using SkiaSharp.
        /// </summary>
        /// <param name="filename">The target filename</param>
        /// <param name="map">The terrain channel being saved</param>
        public virtual void SaveFile(string filename, ITerrainChannel map)
        {
            using (var bitmap = CreateGrayscaleBitmapFromMap(map))
            {
                using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                {
                    using (var file = File.Create(filename))
                    {
                        data.SaveTo(file);
                    }
                }
            }
        }

        /// <summary>
        /// Exports a stream using SkiaSharp.
        /// </summary>
        /// <param name="stream">The target stream</param>
        /// <param name="map">The terrain channel being saved</param>
        public virtual void SaveStream(Stream stream, ITerrainChannel map)
        {
            using (var bitmap = CreateGrayscaleBitmapFromMap(map))
            {
                using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                {
                    data.SaveTo(stream);
                }
            }
        }

        public virtual void SaveFile(ITerrainChannel m_channel, string filename,
                                     int offsetX, int offsetY,
                                     int fileWidth, int fileHeight,
                                     int regionSizeX, int regionSizeY)

        {
            // We need to do this because:
            // "Saving the image to the same file it was constructed from is not allowed and throws an exception."
            string tempName = Path.GetTempFileName();

            SKBitmap existingBitmap = null;
            SKBitmap thisBitmap = null;
            SKBitmap newBitmap = null;

            try
            {
                if (File.Exists(filename))
                {
                    File.Copy(filename, tempName, true);
                    existingBitmap = SKBitmap.Decode(tempName);
                    if (existingBitmap == null || existingBitmap.Width != fileWidth * regionSizeX || existingBitmap.Height != fileHeight * regionSizeY)
                    {
                        // old file, let's overwrite it
                        existingBitmap?.Dispose();
                        newBitmap = new SKBitmap(fileWidth * regionSizeX, fileHeight * regionSizeY);
                    }
                    else
                    {
                        newBitmap = existingBitmap;
                        existingBitmap = null;
                    }
                }
                else
                {
                    newBitmap = new SKBitmap(fileWidth * regionSizeX, fileHeight * regionSizeY);
                }

                thisBitmap = CreateGrayscaleBitmapFromMap(m_channel);
                
                for (int x = 0; x < regionSizeX; x++)
                {
                    for (int y = 0; y < regionSizeY; y++)
                    {
                        int destX = x + offsetX * regionSizeX;
                        int destY = y + (fileHeight - 1 - offsetY) * regionSizeY;
                        
                        if (destX >= 0 && destX < newBitmap.Width && destY >= 0 && destY < newBitmap.Height)
                        {
                            SKColor sourcePixel = thisBitmap.GetPixel(x, y);
                            newBitmap.SetPixel(destX, destY, sourcePixel);
                        }
                    }
                }

                Save(newBitmap, filename);
            }
            finally
            {
                existingBitmap?.Dispose();
                thisBitmap?.Dispose();
                newBitmap?.Dispose();

                if (File.Exists(tempName))
                    File.Delete(tempName);
            }
        }

        protected virtual void Save(SKBitmap bitmap, string filename)
        {
            using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            {
                using (var file = File.Create(filename))
                {
                    data.SaveTo(file);
                }
            }
        }

        #endregion

        public override string ToString()
        {
            return "SYS.DRAWING";
        }

        //Returns true if this extension is supported for terrain save-tile
        public virtual bool SupportsTileSave()
        {
            return false;
        }

        /// <summary>
        /// Protected method, generates a grayscale bitmap
        /// image from a specified terrain channel using SkiaSharp.
        /// </summary>
        /// <param name="map">The terrain channel to export to bitmap</param>
        /// <returns>A SkiaSharp.SKBitmap containing a grayscale image</returns>
        protected static SKBitmap CreateGrayscaleBitmapFromMap(ITerrainChannel map)
        {
            SKBitmap bmp = new SKBitmap(map.Width, map.Height);

            const int palette = 256;

            // Create grayscale color palette (0-255 gray values)
            SKColor[] grays = new SKColor[palette];
            for (int i = 0; i < grays.Length; i++)
            {
                // RGBA format: (R, G, B, A)
                grays[i] = new SKColor((byte)i, (byte)i, (byte)i, 255);
            }

            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    // to change this, loading also needs change

                    // int colorindex = (int)map[x, y];  // one to one conversion 0 - 255m range
                    // int colorindex = (int)map[x, y] / 2;  // 0 - 510 range

                    int colorindex = (int)map[x, y] * 2; // the original  0 - 127.5 range

                    // clamp it not adding the red warning
                    if (colorindex < 0)
                        colorindex = 0;
                    else if (colorindex >= palette)
                        colorindex = palette - 1;
                    
                    bmp.SetPixel(x, map.Height - y - 1, grays[colorindex]);
                }
            }
            return bmp;
        }
    }
}


