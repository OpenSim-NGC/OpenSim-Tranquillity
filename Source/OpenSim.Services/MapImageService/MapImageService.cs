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
 *
 * The design of this map service is based on SimianGrid's PHP-based
 * map service. See this URL for the original PHP version:
 * https://github.com/openmetaversefoundation/simiangrid/
 */

using System.Drawing;
using System.Drawing.Imaging;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;


namespace OpenSim.Services.MapImageService
{
    public class MapImageService : IMapImageService
    {
        private const int ZOOM_LEVELS = 8;
        private const int IMAGE_WIDTH = 256;
        private const int HALF_WIDTH = 128;
        private const int JPEG_QUALITY = 80;

        private static string m_TilesStoragePath = "maptiles";

        private static object m_Sync = new object();
        private static bool m_Initialized = false;
        private static string m_WaterTileFile = string.Empty;
        private static Color m_Watercolor = Color.FromArgb(29, 71, 95);
        private static Bitmap m_WaterBitmap = null;
        private static byte[] m_WaterBytes = null;

        private readonly IConfiguration m_Configuration;
        private readonly ILogger<MapImageService> m_Logger;

        public MapImageService(IConfiguration config, ILogger<MapImageService> logger)
        {
            m_Configuration = config;
            m_Logger = logger;

            if (!m_Initialized)
            {
                m_Initialized = true;
                m_Logger.LogDebug("Starting MapImage service");

                var serviceConfig = config.GetSection("MapImageService");
                if (serviceConfig.Exists())
                {
                    m_TilesStoragePath = serviceConfig.GetValue("TilesStoragePath", m_TilesStoragePath);
                    if (!Directory.Exists(m_TilesStoragePath))
                        Directory.CreateDirectory(m_TilesStoragePath);

                    m_WaterTileFile = Path.Combine(m_TilesStoragePath, "water.jpg");
                    if (!File.Exists(m_WaterTileFile))
                    {
                        Bitmap waterTile = new Bitmap(IMAGE_WIDTH, IMAGE_WIDTH);
                        FillImage(waterTile, m_Watercolor);
                        waterTile.Save(m_WaterTileFile, ImageFormat.Jpeg);
                        m_WaterBitmap = waterTile;
                    }

                    if (File.Exists(m_WaterTileFile))
                    {
                        m_WaterBitmap = new Bitmap(m_WaterTileFile);
                        using (MemoryStream ms = new MemoryStream())
                        {
                            m_WaterBitmap.Save(ms,ImageFormat.Jpeg);
                            ms.Seek(0, SeekOrigin.Begin);
                            m_WaterBytes = ms.ToArray();
                        }
                    }
                }
            }
        }

        #region IMapImageService

        public bool AddMapTile(int x, int y, byte[] imageData, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            string fileName = GetFileName(1, x, y, scopeID);

            lock (m_Sync)
            {
                try
                {
                    using (FileStream f = File.Open(fileName, FileMode.OpenOrCreate, FileAccess.Write))
                        f.Write(imageData, 0, imageData.Length);
                }
                catch (Exception e)
                {
                    m_Logger.LogError(e, $"[MAP IMAGE SERVICE]: Unable to save image file {fileName}");
                    reason = e.Message;
                    return false;
                }
            }

            return UpdateMultiResolutionFiles(x, y, scopeID, out reason);
        }

        public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
        {
            reason = String.Empty;
            string fileName = GetFileName(1, x, y, scopeID);

            lock (m_Sync)
            {
                try
                {
                    File.Delete(fileName);
                }
                catch (Exception e)
                {
                    m_Logger.LogWarning(e, $"Unable to save delete file {fileName}");
                    reason = e.Message;
                    return false;
                }
            }
            return UpdateMultiResolutionFiles(x, y, scopeID, out reason);
        }

        // When large varregions start up, they can send piles of new map tiles. This causes
        //    this multi-resolution routine to be called a zillion times an causes much CPU
        //    time to be spent creating multi-resolution tiles that will be replaced when
        //    the next maptile arrives.
        private class mapToMultiRez
        {
            public int xx;
            public int yy;
            public UUID scopeID;
            public mapToMultiRez(int pX, int pY, UUID pscopeID)
            {
                xx = pX;
                yy = pY;
                scopeID = pscopeID;
            }
        };
        private Queue<mapToMultiRez> multiRezToBuild = new Queue<mapToMultiRez>();

        private bool UpdateMultiResolutionFiles(int x, int y, UUID scopeID, out string reason)
        {
            reason = String.Empty;

            lock (multiRezToBuild)
            {
                multiRezToBuild.Enqueue(new mapToMultiRez(x, y, scopeID));
                if (multiRezToBuild.Count == 1)
                    Util.FireAndForget(
                        DoUpdateMultiResolutionFilesAsync);
            }

            return true;
        }

        private void DoUpdateMultiResolutionFilesAsync(object o)
        {
            // let acumulate large region tiles
            Thread.Sleep(60 * 1000); // large regions take time to upload tiles

            while (multiRezToBuild.Count > 0)
            {
                mapToMultiRez toMultiRez = null;
                lock (multiRezToBuild)
                {
                    if (multiRezToBuild.Count > 0)
                        toMultiRez = multiRezToBuild.Dequeue();
                }
                if (toMultiRez != null)
                {
                    int x = toMultiRez.xx;
                    int y = toMultiRez.yy;
                    UUID scopeID = toMultiRez.scopeID;
                    // m_log.DebugFormat("{0} DoUpdateMultiResolutionFilesAsync: doing build for <{1},{2}>", LogHeader, x, y);

                    int width = 1;

                    // Stitch seven more aggregate tiles together
                    for (uint zoomLevel = 2; zoomLevel <= ZOOM_LEVELS; zoomLevel++)
                    {
                        // Calculate the width (in full resolution tiles) and bottom-left
                        // corner of the current zoom level
                        width *= 2;
                        int x1 = x - (x % width);
                        int y1 = y - (y % width);

                        lock (m_Sync)   // must lock the reading and writing of the maptile files
                        {
                            if (!CreateTile(zoomLevel, x1, y1, scopeID))
                            {
                                m_Logger.LogWarning($"[MAP IMAGE SERVICE]: Unable to create tile for {x},{y} at zoom level {zoomLevel}");
                                return;
                            }
                        }
                    }
                }
            }
            return;
        }

        public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
        {
            string fullName = Path.Combine(m_TilesStoragePath, scopeID.ToString());
            fullName = Path.Combine(fullName, fileName);
            try
            {
                format = Path.GetExtension(fileName).ToLower();
                return File.ReadAllBytes(fullName);
            }
            catch
            {
                format = ".jpg";
                if (m_WaterBytes != null)
                {
                    return (byte[])m_WaterBytes.Clone();
                }
                else
                {
                    return Array.Empty<byte>();
                }
            }
        }

        #endregion


        private string GetFileName(uint zoomLevel, int x, int y, UUID scopeID)
        {
            string extension = "jpg";
            string path = Path.Combine(m_TilesStoragePath, scopeID.ToString());
            Directory.CreateDirectory(path);
            return Path.Combine(path, string.Format("map-{0}-{1}-{2}-objects.{3}", zoomLevel, x, y, extension));
        }

        private Bitmap GetInputTileImage(string fileName)
        {
            try
            {
                if (File.Exists(fileName))
                    return new Bitmap(fileName);
            }
            catch (Exception e)
            {
                m_Logger.LogWarning(e, $"Unable to read image data from {fileName}");
            }

            return null;
        }

        private Bitmap GetOutputTileImage(string fileName)
        {
            try
            {
                if (File.Exists(fileName))
                    return new Bitmap(fileName);

                else
                {
                    // Create a new output tile with a transparent background
                    Bitmap bm = new Bitmap(IMAGE_WIDTH, IMAGE_WIDTH, PixelFormat.Format24bppRgb);
                    //bm.MakeTransparent(); // 24bpp does not have transparency, this would make it 32bpp
                    return bm;
                }
            }
            catch (Exception e)
            {
                m_Logger.LogWarning(e, $"Unable to read image data from {fileName}");
            }

            return null;
        }

        private bool CreateTile(uint zoomLevel, int x, int y, UUID scopeID)
        {
            // m_log.DebugFormat("[MAP IMAGE SERVICE]: Create tile for {0} {1}, zoom {2}", x, y, zoomLevel);
            zoomLevel--;
            int thisWidth = 1 << ((int)zoomLevel);
            int prevWidth = thisWidth >> 1;

            // Convert x and y to the bottom left tile for this zoom level
            int xIn = x - (x % prevWidth);
            int yIn = y - (y % prevWidth);

            // Convert x and y to the bottom left tile for the next zoom level
            int xOut = x - (x % thisWidth);
            int yOut = y - (y % thisWidth);

            // Try to open the four input tiles from the previous zoom level
            Bitmap inputBL = GetInputTileImage(GetFileName(zoomLevel, xIn, yIn, scopeID));
            Bitmap inputBR = GetInputTileImage(GetFileName(zoomLevel, xIn + prevWidth, yIn, scopeID));
            Bitmap inputTL = GetInputTileImage(GetFileName(zoomLevel, xIn, yIn + prevWidth, scopeID));
            Bitmap inputTR = GetInputTileImage(GetFileName(zoomLevel, xIn + prevWidth, yIn + prevWidth, scopeID));

            // Open the output tile (current zoom level)
            string outputFile = GetFileName(++zoomLevel, xOut, yOut, scopeID);

            int ntiles = 0;
            Bitmap output = (Bitmap)m_WaterBitmap.Clone();

            if (inputBL != null)
            {
                ImageCopyResampled(output, inputBL, 0, HALF_WIDTH, 0, 0);
                inputBL.Dispose();
                ntiles++;
            }
            if (inputBR != null)
            {
                ImageCopyResampled(output, inputBR, HALF_WIDTH, HALF_WIDTH, 0, 0);
                inputBR.Dispose();
                ntiles++;
            }
            if (inputTL != null)
            {
                ImageCopyResampled(output, inputTL, 0, 0, 0, 0);
                inputTL.Dispose();
                ntiles++;
            }
            if (inputTR != null)
            {
                ImageCopyResampled(output, inputTR, HALF_WIDTH, 0, 0, 0);
                inputTR.Dispose();
                ntiles++;
            }

            try
            {
                File.Delete(outputFile);
                if (ntiles > 0)
                    output.Save(outputFile, ImageFormat.Jpeg);
            }
            catch (Exception e)
            {
                m_Logger.LogWarning(e, $"Oops on saving {outputFile}");
            }

            output.Dispose();
            return true;
        }

        #region Image utilities

        private void FillImage(Bitmap bm, Color c)
        {
            for (int x = 0; x < bm.Width; x++)
                for (int y = 0; y < bm.Height; y++)
                    bm.SetPixel(x, y, c);
        }

        private void ImageCopyResampled(Bitmap output, Bitmap input, int destX, int destY, int srcX, int srcY)
        {
            int resamplingRateX = 2; // (input.Width - srcX) / (output.Width - destX);
            int resamplingRateY = 2; //  (input.Height - srcY) / (output.Height - destY);

            for (int x = destX; x < destX + HALF_WIDTH; x++)
                for (int y = destY; y < destY + HALF_WIDTH; y++)
                {
                    Color p = input.GetPixel(srcX + (x - destX) * resamplingRateX, srcY + (y - destY) * resamplingRateY);
                    output.SetPixel(x, y, p);
                }
        }

        #endregion
    }
}
