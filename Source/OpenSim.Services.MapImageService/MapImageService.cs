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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using System.Reflection;
using SkiaSharp;
using System.Timers;

using Microsoft.Extensions.Logging;

namespace OpenSim.Services.MapImageService;

public class MapImageService : IMapImageService
{
    // Logging.
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[MAP IMAGE SERVICE]";

    // Image standards.
    private const int ZOOM_LEVELS = 8;
    private const int IMAGE_WIDTH = 256;
    private const int JPEG_QUALITY = 80;

    private static string m_TilesStoragePath = "maptiles";

    private static readonly object m_FileAccessLock = new();
    private static bool m_Initialized = false;
    private static readonly SKColor m_Watercolor = new(29, 72, 96);
    private static byte[] m_WaterJPEGBytes = null;

    // Return this to callers, so they can't modify m_WaterJPEGBytes.
    public static byte[] WaterJPEG
    {
        get => [.. m_WaterJPEGBytes];
    }

    public MapImageService(IConfigSource config)
    {
        lock (m_FileAccessLock)
        {
            if (!m_Initialized)
            {
                using var tempWaterBitmap = new SKBitmap(IMAGE_WIDTH, IMAGE_WIDTH, SKColorType.Bgra8888, SKAlphaType.Opaque);
                tempWaterBitmap.Erase(m_Watercolor);

                using var tempWaterData = tempWaterBitmap.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);
                m_WaterJPEGBytes = tempWaterData.ToArray();

                IConfig serviceConfig = config.Configs["MapImageService"];
                if (serviceConfig is not null)
                {
                    m_TilesStoragePath = serviceConfig.GetString("TilesStoragePath", m_TilesStoragePath);
                }

                m_Initialized = true;
            }
        }
    }

    #region Module API

    /// <remarks>
    /// This implementation <b>checks</b> the incoming byte array. If it is not a valid image (don't care what kind), and isn't
    /// 256x256, it is rejected. Then it is recoded to JPEG at 80% quality, and that recode is what is written out as a file.
    /// </remarks>
    public bool AddMapTile(int x, int y, byte[] imageData, UUID tenantScopeUUID, out string reason)
    {
        reason = string.Empty;

        byte[] jpegBytes;

        // Don't trust unknown bytes from the Internet. You don't know where they've been!
        // First, are they a valid image? We'll take anything SkiaSharp can decode, or a JPEG2000.
        if (!SkiaImageUtils.TryDecodeFromBytes(imageData, out SKBitmap inputImage)
            && !SkiaImageUtils.TryDecodeFromJ2K(imageData, out inputImage))
        {
            reason = $"The submitted data is not an image file";
            m_log.LogWarning($"{LogHeader}: Add map tile at {x},{y} failed: {reason}");
            return false;
        }

        using (inputImage)
        {
            // Ok, the image is valid. Is it the right size? It has to be 256x256.
            if (inputImage.Width != IMAGE_WIDTH || inputImage.Height != IMAGE_WIDTH)
            {
                reason = $"The image is not 256x256. It is {inputImage.Width}x{inputImage.Height}";
                m_log.LogWarning($"{LogHeader}: Add map tile at {x},{y} failed: {reason}");
                return false;
            }

            // And does it cleanly encode back to regular JPEG? Note, we also normalize all level-1 tiles to the same JPEG quality.
            if (!SkiaImageUtils.TryEncodeToJpeg(inputImage, JPEG_QUALITY, out jpegBytes))
            {
                reason = $"Failed to re-encode the submitted data as JPEG.";
                m_log.LogWarning($"{LogHeader}: Add map tile at {x},{y} failed: {reason}");
                return false;
            }
        }

        // We have a valid byte[] with a normalized JPEG in it. Now we can write it to disk.
        string fileName = GetTileFileName(1, x, y, tenantScopeUUID);
        try
        {
            lock (m_FileAccessLock)
            {
                CreateScopeFolder(tenantScopeUUID);
                File.WriteAllBytes(fileName, jpegBytes);
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            m_log.LogWarning($"{LogHeader}: Unable to save incoming image to {fileName}. Message: {reason}");
            return false;
        }

        // If the write succeeded, we can queue this tile up for producing the relevant zoomed map tiles.
        ZoomTileWorkQueue.Enqueue(x, y, tenantScopeUUID);
        return true;
    }

    public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
    {
        reason = string.Empty;
        string fileName = GetTileFileName(1, x, y, scopeID);

        try
        {
            lock (m_FileAccessLock)
            {
                File.Delete(fileName);
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            m_log.LogWarning($"{LogHeader}: Unable to delete file {fileName}. Reason: {reason}");
            return false;
        }

        // Queue up the deletion for regenerating zoomed map tiles.
        ZoomTileWorkQueue.Enqueue(x, y, scopeID);
        return true;
    }

    public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
    {
        string fullName = Path.Combine(GetScopeFolder(scopeID), fileName);

        if (File.Exists(fullName))
        {
            format = Path.GetExtension(fullName).ToLower();
            try
            {
                lock (m_FileAccessLock)
                {
                    if (IsJpegMaptile(fullName))
                    {
                        using var fs = File.OpenRead(fullName);
                        using var ms = new MemoryStream();
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                // Intentionally left blank.
                // The code in the try clause should not throw anything, as the server is known to have access to the file when
                // File.Exists() returns true. But, if the file read fails for any reason, we need to drop down below and return an
                // ocean tile.
            }
        }

        // The file either didn't exist, or was not what we expected, so return an empty ocean tile instead.
        format = ".jpg";
        return WaterJPEG;
    }

    #endregion

    #region File and filesystem Utils

    /// <summary>
    /// Get the map tiles directory for the given scope UUID
    /// </summary>
    /// <param name="scopeID"></param>
    /// <returns>the folder pathname</returns>
    private static string GetScopeFolder(UUID scopeID)
    {
        return Path.Combine(m_TilesStoragePath, scopeID.ToString());
    }

    /// <summary>
    /// Get the map tiles directory for the given scope UUID, creating it if needed
    /// </summary>
    /// <param name="scopeID"></param>
    /// <returns>the folder pathname</returns>
    private static string CreateScopeFolder(UUID scopeID)
    {
        string path = GetScopeFolder(scopeID);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Tack the filename onto the scope directory
    /// </summary>
    /// <param name="zoomLevel"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="scopeID"></param>
    /// <returns>the file full pathname</returns>
    private static string GetTileFileName(int zoomLevel, int x, int y, UUID scopeID)
    {
        return Path.Combine(GetScopeFolder(scopeID), $"map-{zoomLevel}-{x}-{y}-objects.jpg");
    }

    /// <summary>
    /// Tack the filename onto the given path
    /// </summary>
    /// <param name="zoomLevel"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="path"></param>
    /// <returns>the file full pathname</returns>
    private static string GetTileFileName(int zoomLevel, int x, int y, string path)
    {
        return Path.Combine(path, $"map-{zoomLevel}-{x}-{y}-objects.jpg");
    }

    /// <summary>
    /// Check if a given map tile file is likely a JPEG and is 256x256.
    /// </summary>
    /// <remarks>
    /// <b>Note: This does not guarantee the file is a fully-well-formed JPEG!</b> It only checks it is likely to be JPEG, and has
    /// enough metadata to correctly get the size, and the size is IMAGE_WIDTH.
    /// </remarks>
    /// <param name="fileName">the file</param>
    /// <returns>true if the tile is likely a 256x256 JPEG</returns>
    private static bool IsJpegMaptile(string fileName)
    {
        if (File.Exists(fileName))
        {
            using var fs = File.OpenRead(fileName);
            byte[] sig = new byte[3];

            fs.Read(sig, 0, 3);
            if (SkiaImageUtils.IsNotJpeg(sig)) return false;

            // Rewind the stream for DecodeBounds
            fs.Seek(0, SeekOrigin.Begin);

            var info = SKBitmap.DecodeBounds(fs);

            return info.Width == IMAGE_WIDTH && info.Height == IMAGE_WIDTH;
        }

        return false;
    }

    #endregion

    #region Zoom tile creation

    private sealed class ZoomTileWorkQueue
    {
        private static bool hasWork = false;
        private static bool triggered = false;
        private static bool isWorking = false;
        private static Dictionary<UUID, HashSet<TileInfo>> pendingWork = [];
        private static Dictionary<UUID, HashSet<TileInfo>> currentWork = [];
        private static System.Timers.Timer timer = null;
        private static readonly Object m_queueLock = new();
        private readonly record struct TileInfo
        {
            public readonly int x;
            public readonly int y;
            public TileInfo(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        /// <summary>
        /// Public entrypoint to the Zoom Tile Work Queue.
        /// </summary>
        /// <remarks>
        /// Enqueues a new incoming map tile, and starts a fuse. Every incoming tile resets the fuse, and when it runs out, the
        /// gathered work batch is started.
        /// </remarks>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="tenantScopeID"></param>
        public static void Enqueue(int x, int y, UUID tenantScopeID)
        {
            lock (m_queueLock)
            {
                HashSet<TileInfo> tiles;
                if (!pendingWork.TryGetValue(tenantScopeID, out tiles))
                {
                    tiles = [];
                    pendingWork[tenantScopeID] = tiles;
                }

                tiles.Add(new TileInfo(x, y));
                hasWork = true;

                if (timer is null)
                {
                    timer = new(5000) { AutoReset = false };
                    timer.Elapsed += TriggerFired;
                }
                timer.Start();
            }
        }

        /// <summary>
        /// We've had a couple seconds of silence since the last add/remove tile call, let's process any work.
        /// </summary>
        /// <param name="o"></param>
        /// <param name="e"></param>
        private static void TriggerFired(Object o, ElapsedEventArgs e)
        {
            bool shouldFire = false;
            lock (m_queueLock)
            {
                triggered = hasWork;
                shouldFire = hasWork && (!isWorking);
            }

            if (shouldFire)
            {
                Util.FireAndForget(ZoomsWorker);
            }
        }

        /// <summary>
        /// Worker to process the current work queue. If there's been more incoming work, and the fuse has run out again (there's
        /// another batch to do) we continue on.
        /// </summary>
        /// <param name="o"></param>
        private static void ZoomsWorker(Object o)
        {
            do
            {
                lock (m_queueLock)
                {
                    // Snag the pending batch, set up to collect more.
                    (currentWork, pendingWork) = (pendingWork, currentWork);
                    pendingWork.Clear();
                    hasWork = false;
                    isWorking = true;
                    triggered = false;
                }
                // Go do The Thing
                DoScopes();
            } while (triggered);
            isWorking = false;
        }

        /// <summary>
        /// Thin wrapper so we don't muddy the water with scopeIDs while zooming.
        /// </summary>
        private static void DoScopes()
        {
            foreach (var key in currentWork.Keys)
            {
                DoZooms(currentWork[key], GetScopeFolder(key));
            }
        }

        /// <summary>
        /// Generate map tiles for zoom levels 2 and up.
        /// </summary>
        /// <param name="levelOneWorkSet"></param>
        /// <param name="scopePath"></param>
        private static void DoZooms(HashSet<TileInfo> levelOneWorkSet, string scopePath)
        {
            // A little commentary about "parent" and "child." The zoomed-in tiles are the children, the next zoomed-out tiles are
            // the parents. Potentially four children per parent. Same as a tree data structure.

            HashSet<TileInfo> childSet = levelOneWorkSet;
            HashSet<TileInfo> parentSet;

            // The processing is breadth-first, starting at level 1 and going down one level at a time. All tiles marked for
            // consideration at a level are considered, potentially regenerating or deleting tiles at the next level. These are
            // added to the set for the next level.

            for (int childLevel = 1; childLevel < ZOOM_LEVELS; childLevel++)
            {
                parentSet = [];

                while (childSet.Count != 0)
                {
                    // Pick a tile, any tile! Don't care which.
                    HashSet<TileInfo>.Enumerator enumerator = childSet.GetEnumerator();
                    if (!enumerator.MoveNext())
                        continue;
                    TileInfo childTile = enumerator.Current;

                    uint parentSize = 1u << (childLevel - 1);
                    uint mask = ~((parentSize << 1) - 1u);
                    int stride = (int)parentSize;

                    // Get the parent tile's grid position.
                    int px = (int)(((uint)childTile.x) & mask);
                    int py = (int)(((uint)childTile.y) & mask);

                    // Remove all four child tiles from consideration, we are considering all four now.
                    childSet.Remove(new TileInfo(px, py));
                    childSet.Remove(new TileInfo(px + stride, py));
                    childSet.Remove(new TileInfo(px, py + stride));
                    childSet.Remove(new TileInfo(px + stride, py + stride));

                    // We are doing something to this file here. At least one child was either updated or removed, so this
                    // parent tile will be changed or deleted as a result.
                    parentSet.Add(new TileInfo(px, py));
                    string parentFile = GetTileFileName(childLevel + 1, px, py, scopePath);

                    // Snag all child tiles that exist.
                    using SKBitmap bottomLeft = GetExistingTileImage(childLevel, px, py, scopePath);
                    using SKBitmap bottomRight = GetExistingTileImage(childLevel, px + stride, py, scopePath);
                    using SKBitmap topLeft = GetExistingTileImage(childLevel, px, py + stride, scopePath);
                    using SKBitmap topRight = GetExistingTileImage(childLevel, px + stride, py + stride, scopePath);

                    // If any child tile exists, we are updating this parent tile. If none exist, this parent is only ocean, so we
                    // delete it.
                    if (bottomLeft is not null || bottomRight is not null || topLeft is not null || topRight is not null)
                    {
                        // We plop our potential four children onto a 512x512 of ocean, and resize it to 256x256, then save as
                        // JPEG to file.
                        using SKBitmap tempBitmap = new(512, 512, SKColorType.Bgra8888, SKAlphaType.Opaque);
                        using SKCanvas tempCanvas = new(tempBitmap);
                        tempCanvas.Clear(m_Watercolor);

                        if (bottomLeft is not null) tempCanvas.DrawBitmap(bottomLeft, 0, IMAGE_WIDTH);
                        if (bottomRight is not null) tempCanvas.DrawBitmap(bottomRight, IMAGE_WIDTH, IMAGE_WIDTH);
                        if (topLeft is not null) tempCanvas.DrawBitmap(topLeft, 0, 0);
                        if (topRight is not null) tempCanvas.DrawBitmap(topRight, IMAGE_WIDTH, 0);

                        using SKBitmap newTile = SkiaImageUtils.OpaqueResize(tempBitmap, IMAGE_WIDTH, IMAGE_WIDTH);
                        using SKData newTileData = newTile.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);

                        try
                        {
                            lock (m_FileAccessLock)
                            {
                                using FileStream fs = File.Create(parentFile);
                                newTileData.SaveTo(fs);
                            }
                        }
                        catch (Exception e)
                        {
                            m_log.LogWarning($"{LogHeader}: Unable to save new zoom map tile {parentFile}. Reason: {e.Message}");
                        }
                    }
                    else
                    {
                        // If there were no children, this parent tile is going to be all ocean, so we may as well delete it. We
                        // know the file did exist. If there's no child tiles it's because at least one descendant was deleted.
                        try
                        {
                            lock (m_FileAccessLock)
                            {
                                File.Delete(parentFile);
                            }
                        }
                        catch (Exception e)
                        {
                            m_log.LogWarning($"{LogHeader}: Unable to delete all-water tile {parentFile}. Reason: {e.Message}");
                        }
                    }
                }

                childSet = parentSet;
            }
        }

        /// <summary>
        /// Get a bitmap from an existing file. This <b>should</b> be a 256x256 JPEG, unless someone's tampering with our maptiles
        /// directory. because this module wrote them all.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="path"></param>
        /// <returns>An <b>immmutable</b> Skia bitmap, or null on failure</returns>
        private static SKBitmap GetExistingTileImage(int level, int x, int y, string path)
        {
            string fileName = GetTileFileName(level, x, y, path);

            if (File.Exists(fileName))
            {
                try
                {
                    lock (m_FileAccessLock)
                    {
                        // The tiles we saved should be 256x256 JPEG files. Reject if they are not.
                        if (IsJpegMaptile(fileName))
                        {
                            using var fs = File.OpenRead(fileName);
                            SKBitmap output = SKBitmap.Decode(fs);
                            if (output is null)
                            {
                                m_log.LogError($"{LogHeader}: Failed to decode map tile {fileName}");
                                return null;
                            }

                            output.SetImmutable();

                            return output;
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.LogError(e, $"{LogHeader}: Unable to read map tile from {fileName}");
                }
            }

            // The file did not exist, or we couldn't access it, or it wasn't a well-formed 256x256 JPEG.
            return null;
        }
    }
}

#endregion
