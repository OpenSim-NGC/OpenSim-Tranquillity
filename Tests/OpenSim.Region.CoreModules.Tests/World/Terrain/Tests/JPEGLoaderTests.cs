/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using System;
using System.IO;
using Xunit;
using OpenSim.Tests.Common;
using OpenSim.Region.CoreModules.World.Terrain.FileLoaders;
using OpenSim.Region.Framework.Scenes;
using SkiaSharp;

namespace OpenSim.Region.CoreModules.World.Terrain.Tests
{
    public class JPEGLoaderTests : OpenSimTestCase
    {
        private string _origCwd = null;

        public void SetUp()
        {
            _origCwd = Environment.CurrentDirectory;
        }

        public void TearDown()
        {
            if (_origCwd != null)
                Environment.CurrentDirectory = _origCwd;
        }

        private string CreateDefaultStripe()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "opensim_jpeg_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string path = Path.Combine(tempDir, "defaultstripe.png");

            // Create a 1x256 gradient palette (height = palette)
            int palette = 256;
            using (var bmp = new SKBitmap(1, palette, SKColorType.Bgra8888, SKAlphaType.Premul))
            {
                for (int i = 0; i < palette; i++)
                {
                    // simple gradient from black to white
                    byte v = (byte)i;
                    bmp.SetPixel(0, i, new SKColor(v, v, v));
                }

                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var fs = File.OpenWrite(path))
                {
                    data.SaveTo(fs);
                }
            }

            return tempDir;
        }

        private static bool ColorApproximatelyEqual(SKColor a, SKColor b, byte tolerance = 10)
        {
            return Math.Abs(a.Red - b.Red) <= tolerance
                   && Math.Abs(a.Green - b.Green) <= tolerance
                   && Math.Abs(a.Blue - b.Blue) <= tolerance;
        }

        [Fact]
        public void SaveFileSimpleAndTiledTest()
        {
            string tempDir = CreateDefaultStripe();
            string prevCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tempDir;

            try
            {
                // Simple save
                TerrainChannel map = new TerrainChannel(2, 2);
                // set one distinct value
                map[0, 0] = 512.0f; // max value used by CreateBitmapFromMap to pick last palette entry

                string file1 = Path.Combine(tempDir, "simple.jpg");

                JPEG loader = new JPEG();
                loader.SaveFile(file1, map);

                Assert.That(File.Exists(file1), "Simple JPEG file should be created");

                using (var decoded = SKBitmap.Decode(file1))
                {
                    Assert.NotNull();
                    // TODO: Assert.Equal(,); - incomplete assertion
                    // TODO: Assert.Equal(,); - incomplete assertion
                }

                // Tiled save
                int regionSizeX = 2, regionSizeY = 2;
                int fileWidth = 2, fileHeight = 2;
                int offsetX = 1, offsetY = 0;

                TerrainChannel tile = new TerrainChannel(regionSizeX, regionSizeY);
                // zero all
                for (int x = 0; x < regionSizeX; x++)
                    for (int y = 0; y < regionSizeY; y++)
                        tile[x, y] = 0.0f;

                // set single pixel to max to identify placement
                tile[0, 0] = 512.0f;

                string tiledFile = Path.Combine(tempDir, "tiled.jpg");

                loader.SaveFile(tile, tiledFile, offsetX, offsetY, fileWidth, fileHeight, regionSizeX, regionSizeY);

                Assert.That(File.Exists(tiledFile), "Tiled JPEG file should be created");

                using (var decoded = SKBitmap.Decode(tiledFile))
                {
                    Assert.NotNull();
                    int targetX = 0 + offsetX * regionSizeX; // 2
                    int targetY = 0 + (fileHeight - 1 - offsetY) * regionSizeY; // 2

                    Assert.True(targetX < decoded.Width && targetY < decoded.Height);

                    SKColor pixel = decoded.GetPixel(targetX, targetY);

                    // Palette last entry is white in our generated stripe (255,255,255)
                    SKColor expected = new SKColor(255, 255, 255);

                    Assert.That(ColorApproximatelyEqual(pixel, expected, 20), "Saved pixel should approximately match expected palette color");
                }
            }
            finally
            {
                // cleanup
                Environment.CurrentDirectory = prevCwd;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
