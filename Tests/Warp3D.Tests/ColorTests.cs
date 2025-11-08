using System;
using Xunit;
using Warp3D;

namespace Warp3D.Tests
{
    public class ColorTests
    {
        [Fact]
        public void Constructor_Default_ShouldInitializeToZero()
        {
            // Act
            var color = new warp_Color();

            // Assert
            Assert.Equal(0, color.r);
            Assert.Equal(0, color.g);
            Assert.Equal(0, color.b);
            Assert.Equal(0, color.a);
        }

        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(255, 255, 255, 255)]
        [InlineData(128, 64, 32, 255)]
        public void Constructor_WithComponents_ShouldInitializeCorrectly(byte r, byte g, byte b, byte a)
        {
            // Act
            var color = new warp_Color(r, g, b, a);

            // Assert
            Assert.Equal(r, color.r);
            Assert.Equal(g, color.g);
            Assert.Equal(b, color.b);
            Assert.Equal(a, color.a);
        }

        [Theory]
        [InlineData(0x00000000)]
        [InlineData(0xFF000000)]
        [InlineData(0x00FF0000)]
        [InlineData(0x0000FF00)]
        [InlineData(0x000000FF)]
        [InlineData(0xFFFFFFFF)]
        public void Constructor_FromARGB_ShouldUnpackCorrectly(uint argb)
        {
            // Arrange
            byte expectedA = (byte)((argb >> 24) & 0xFF);
            byte expectedR = (byte)((argb >> 16) & 0xFF);
            byte expectedG = (byte)((argb >> 8) & 0xFF);
            byte expectedB = (byte)(argb & 0xFF);

            // Act
            var color = new warp_Color(argb);

            // Assert
            Assert.Equal(expectedR, color.r);
            Assert.Equal(expectedG, color.g);
            Assert.Equal(expectedB, color.b);
            Assert.Equal(expectedA, color.a);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, 0, 0, 0, 0)]
        [InlineData(255, 128, 64, 255, 128, 64, 32, 255)]
        [InlineData(100, 150, 200, 255, 200, 150, 100, 255)]
        public void Add_Colors_ShouldClamp(byte r1, byte g1, byte b1, byte a1,
                                         byte r2, byte g2, byte b2, byte a2)
        {
            // Arrange
            var color1 = new warp_Color(r1, g1, b1, a1);
            var color2 = new warp_Color(r2, g2, b2, a2);

            // Act
            var result = color1 + color2;

            // Assert
            Assert.Equal((byte)Math.Min(255, r1 + r2), result.r);
            Assert.Equal((byte)Math.Min(255, g1 + g2), result.g);
            Assert.Equal((byte)Math.Min(255, b1 + b2), result.b);
            Assert.Equal((byte)Math.Min(255, a1 + a2), result.a);
        }

        [Theory]
        [InlineData(255, 128, 64, 255, 128, 64, 32, 255)]
        [InlineData(100, 150, 200, 255, 200, 150, 100, 255)]
        public void Subtract_Colors_ShouldClampToZero(byte r1, byte g1, byte b1, byte a1,
                                                     byte r2, byte g2, byte b2, byte a2)
        {
            // Arrange
            var color1 = new warp_Color(r1, g1, b1, a1);
            var color2 = new warp_Color(r2, g2, b2, a2);

            // Act
            var result = color1 - color2;

            // Assert
            Assert.Equal((byte)Math.Max(0, r1 - r2), result.r);
            Assert.Equal((byte)Math.Max(0, g1 - g2), result.g);
            Assert.Equal((byte)Math.Max(0, b1 - b2), result.b);
            Assert.Equal((byte)Math.Max(0, a1 - a2), result.a);
        }

        [Theory]
        [InlineData(128, 64, 32, 255, 0.5f)]
        [InlineData(255, 255, 255, 255, 0.25f)]
        public void Multiply_Color_ByScalar_ShouldScale(byte r, byte g, byte b, byte a, float scalar)
        {
            // Arrange
            var color = new warp_Color(r, g, b, a);

            // Act
            var result = color * scalar;

            // Assert
            Assert.Equal((byte)(r * scalar), result.r);
            Assert.Equal((byte)(g * scalar), result.g);
            Assert.Equal((byte)(b * scalar), result.b);
            Assert.Equal((byte)(a * scalar), result.a);
        }

        [Fact]
        public void EqualityOperators_ShouldWorkCorrectly()
        {
            // Arrange
            var color1 = new warp_Color(128, 64, 32, 255);
            var color2 = new warp_Color(128, 64, 32, 255);
            var color3 = new warp_Color(255, 255, 255, 255);

            // Assert
            Assert.True(color1 == color2);
            Assert.False(color1 == color3);
            Assert.False(color1 != color2);
            Assert.True(color1 != color3);
        }

        [Fact]
        public void CopyFrom_ShouldCopyAllComponents()
        {
            // Arrange
            var source = new warp_Color(128, 64, 32, 255);
            var target = new warp_Color();

            // Act
            target.CopyFrom(source);

            // Assert
            Assert.Equal(source.r, target.r);
            Assert.Equal(source.g, target.g);
            Assert.Equal(source.b, target.b);
            Assert.Equal(source.a, target.a);
        }
    }
}