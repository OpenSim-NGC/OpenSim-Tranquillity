using System;
using OpenSim.Region.CoreModules.World.Warp3DMap;
using OpenMetaverse.Imaging;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Warp3DMap
{
    public class Warp3DBitmapShimTests
    {
        [Fact]
        public void TestSKBitmapToJpeg2000Encode()
        {
            // Create a test SKBitmap with a simple gradient
            int width = 256;
            int height = 256;
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);

            // Draw a red-to-blue gradient
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(width, height),
                    new[] { SKColors.Red, SKColors.Blue },
                    new[] { 0.0f, 1.0f },
                    SKShaderTileMode.Clamp)
            };

            canvas.DrawRect(0, 0, width, height, paint);

            // Encode to JPEG2000 using our new direct SKBitmap path
            byte[] j2kData = Warp3DBitmapShim.EncodeSKBitmapToJpeg2000(bitmap);
            Assert.NotNull(j2kData);
            Assert.True(j2kData.Length > 0, "JPEG2000 data should not be empty");

            // Decode back to verify contents
            ManagedImage decoded;
            bool success = OpenJPEG.DecodeToImage(j2kData, out decoded);
            Assert.True(success, "Failed to decode JPEG2000 data");
            Assert.NotNull(decoded);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);

            // Quick spot-check of some pixels
            // Top-left should be reddish, bottom-right should be bluish
            int redIndex = 0;  // top-left
            Assert.True(decoded.Red[redIndex] > 200);   // High red
            Assert.True(decoded.Blue[redIndex] < 50);   // Low blue

            int blueIndex = (height - 1) * width + (width - 1);  // bottom-right
            Assert.True(decoded.Red[blueIndex] < 50);   // Low red
            Assert.True(decoded.Blue[blueIndex] > 200); // High blue
        }
    }
}