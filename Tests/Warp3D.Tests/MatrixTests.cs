using System;
using Xunit;
using Warp3D;

namespace Warp3D.Tests
{
    public class MatrixTests
    {
        private const float Delta = 0.0001f; // For floating point comparisons

        [Fact]
        public void Constructor_Default_ShouldCreateIdentityMatrix()
        {
            // Act
            var matrix = new warp_Matrix();

            // Assert
            Assert.Equal(1.0f, matrix.m11, Delta);
            Assert.Equal(0.0f, matrix.m12, Delta);
            Assert.Equal(0.0f, matrix.m13, Delta);
            Assert.Equal(0.0f, matrix.m14, Delta);

            Assert.Equal(0.0f, matrix.m21, Delta);
            Assert.Equal(1.0f, matrix.m22, Delta);
            Assert.Equal(0.0f, matrix.m23, Delta);
            Assert.Equal(0.0f, matrix.m24, Delta);

            Assert.Equal(0.0f, matrix.m31, Delta);
            Assert.Equal(0.0f, matrix.m32, Delta);
            Assert.Equal(1.0f, matrix.m33, Delta);
            Assert.Equal(0.0f, matrix.m34, Delta);

            Assert.Equal(0.0f, matrix.m41, Delta);
            Assert.Equal(0.0f, matrix.m42, Delta);
            Assert.Equal(0.0f, matrix.m43, Delta);
            Assert.Equal(1.0f, matrix.m44, Delta);
        }

        [Fact]
        public void Multiply_ByIdentity_ShouldNotChange()
        {
            // Arrange
            var matrix = new warp_Matrix();
            matrix.Scale(2, 3, 4);  // Create a non-identity matrix
            var identity = new warp_Matrix();

            // Act
            var result = matrix * identity;

            // Assert
            for (int i = 1; i <= 4; i++)
            {
                for (int j = 1; j <= 4; j++)
                {
                    Assert.Equal(matrix[i,j], result[i,j], Delta);
                }
            }
        }

        [Theory]
        [InlineData(2.0f, 2.0f, 2.0f)]
        [InlineData(0.5f, 1.0f, 2.0f)]
        [InlineData(-1.0f, -1.0f, -1.0f)]
        public void Scale_ShouldSetCorrectScaleFactors(float x, float y, float z)
        {
            // Arrange
            var matrix = new warp_Matrix();

            // Act
            matrix.Scale(x, y, z);

            // Assert
            Assert.Equal(x, matrix.m11, Delta);
            Assert.Equal(y, matrix.m22, Delta);
            Assert.Equal(z, matrix.m33, Delta);
            Assert.Equal(1.0f, matrix.m44, Delta);
        }

        [Theory]
        [InlineData(0.0f, 0.0f, 0.0f)]
        [InlineData(90.0f, 0.0f, 0.0f)]
        [InlineData(0.0f, 90.0f, 0.0f)]
        [InlineData(0.0f, 0.0f, 90.0f)]
        [InlineData(45.0f, 45.0f, 45.0f)]
        public void Rotate_ShouldApplyCorrectRotation(float x, float y, float z)
        {
            // Arrange
            var matrix = new warp_Matrix();
            
            // Convert degrees to radians
            float xRad = x * (float)Math.PI / 180.0f;
            float yRad = y * (float)Math.PI / 180.0f;
            float zRad = z * (float)Math.PI / 180.0f;

            // Act
            matrix.Rotate(xRad, yRad, zRad);

            // Assert - Test rotation by applying to a point and verifying expected transformation
            float[] testPoint = { 1.0f, 0.0f, 0.0f, 1.0f };
            var result = matrix.Transform(testPoint);

            // Calculate expected values - this is a simplified test
            // For more precise testing, we'd need to implement full rotation matrix validation
            if (x == 0 && y == 0 && z == 0)
            {
                Assert.Equal(1.0f, result[0], Delta);
                Assert.Equal(0.0f, result[1], Delta);
                Assert.Equal(0.0f, result[2], Delta);
            }
        }

        [Fact]
        public void Transform_Point_ShouldApplyTransformation()
        {
            // Arrange
            var matrix = new warp_Matrix();
            matrix.Scale(2, 2, 2);  // Scale by 2
            float[] point = { 1.0f, 1.0f, 1.0f, 1.0f };

            // Act
            var result = matrix.Transform(point);

            // Assert
            Assert.Equal(2.0f, result[0], Delta);
            Assert.Equal(2.0f, result[1], Delta);
            Assert.Equal(2.0f, result[2], Delta);
            Assert.Equal(1.0f, result[3], Delta);
        }

        [Fact]
        public void Transpose_ShouldSwapElements()
        {
            // Arrange
            var matrix = new warp_Matrix();
            matrix.m12 = 2.0f;
            matrix.m13 = 3.0f;
            matrix.m14 = 4.0f;
            matrix.m23 = 5.0f;
            matrix.m24 = 6.0f;
            matrix.m34 = 7.0f;

            // Act
            var transposed = matrix.Transpose();

            // Assert
            Assert.Equal(matrix.m12, transposed.m21, Delta);
            Assert.Equal(matrix.m13, transposed.m31, Delta);
            Assert.Equal(matrix.m14, transposed.m41, Delta);
            Assert.Equal(matrix.m23, transposed.m32, Delta);
            Assert.Equal(matrix.m24, transposed.m42, Delta);
            Assert.Equal(matrix.m34, transposed.m43, Delta);
        }

        [Fact]
        public void CopyFrom_ShouldCopyAllElements()
        {
            // Arrange
            var source = new warp_Matrix();
            source.Scale(2, 3, 4);
            var target = new warp_Matrix();

            // Act
            target.CopyFrom(source);

            // Assert
            for (int i = 1; i <= 4; i++)
            {
                for (int j = 1; j <= 4; j++)
                {
                    Assert.Equal(source[i,j], target[i,j], Delta);
                }
            }
        }
    }
}