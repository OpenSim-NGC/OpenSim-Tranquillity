using System;
using Xunit;
using Warp3D;
using SkiaSharp;

namespace Warp3D.Tests
{
    public class TextureTests : IDisposable
    {
        public void Dispose()
        {
            // Cleanup any resources used in tests
        }

        [Fact]
        public void CreateTexture_WithDimensions_ShouldMatchSize()
        {
            // Arrange
            const int width = 256;
            const int height = 256;

            // Act
            using var texture = new warp_Texture(width, height);

            // Assert
            Assert.Equal(width, texture.width);
            Assert.Equal(height, texture.height);
            Assert.NotNull(texture.color);
            Assert.Equal(width * height, texture.color.Length);
        }

        [Fact]
        public void CreateTexture_FromSKBitmap_ShouldMatchContent()
        {
            // Arrange
            const int width = 64;
            const int height = 64;
            using var bitmap = new SKBitmap(width, height);
            
            // Fill with a test pattern
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor((byte)x, (byte)y, 128, 255));
                }
            }

            // Act
            using var texture = new warp_Texture(bitmap);

            // Assert
            Assert.Equal(width, texture.width);
            Assert.Equal(height, texture.height);
            
            // Verify content matches (check a few sample points)
            for (int y = 0; y < height; y += 16)
            {
                for (int x = 0; x < width; x += 16)
                {
                    SKColor sourcePixel = bitmap.GetPixel(x, y);
                    int texIdx = y * width + x;
                    warp_Color texPixel = texture.color[texIdx];
                    
                    Assert.Equal(sourcePixel.Red, texPixel.r);
                    Assert.Equal(sourcePixel.Green, texPixel.g);
                    Assert.Equal(sourcePixel.Blue, texPixel.b);
                    Assert.Equal(sourcePixel.Alpha, texPixel.a);
                }
            }
        }

        [Fact]
        public void CreateTexture_WithReduction_ShouldHaveCorrectSize()
        {
            // Arrange
            const int width = 256;
            const int height = 256;
            const int reduction = 1; // Should reduce dimensions by half
            using var bitmap = new SKBitmap(width, height);

            // Fill with test pattern
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor((byte)x, (byte)y, 128, 255));
                }
            }

            // Act
            using var texture = new warp_Texture(bitmap, reduction);

            // Assert
            Assert.Equal(width >> reduction, texture.width);
            Assert.Equal(height >> reduction, texture.height);
            Assert.Equal((width >> reduction) * (height >> reduction), texture.color.Length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void CreateTexture_WithInvalidDimensions_ShouldThrow(int invalidSize)
        {
            Assert.Throws<ArgumentException>(() => new warp_Texture(invalidSize, 64));
            Assert.Throws<ArgumentException>(() => new warp_Texture(64, invalidSize));
        }

        [Fact]
        public void CreateTexture_FromNull_ShouldThrow()
        {
            Assert.Throws<ArgumentNullException>(() => new warp_Texture((SKBitmap)null!));
        }

        [Fact]
        public void Clear_ShouldResetAllPixels()
        {
            // Arrange
            const int width = 64;
            const int height = 64;
            using var texture = new warp_Texture(width, height);
            
            // Fill with non-zero values first
            for (int i = 0; i < texture.color.Length; i++)
            {
                texture.color[i] = new warp_Color(255, 255, 255, 255);
            }

            // Act
            texture.Clear();

            // Assert
            foreach (var pixel in texture.color)
            {
                Assert.Equal(0, pixel.r);
                Assert.Equal(0, pixel.g);
                Assert.Equal(0, pixel.b);
                Assert.Equal(0, pixel.a);
            }
        }
    }
}