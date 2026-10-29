using CoreJ2K;
using Nini.Config;
using OpenSim.Region.CoreModules.Scripting.DynamicTexture;
using OpenSim.Region.CoreModules.Scripting.VectorRender;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Scripting;

public class ModernSkiaRenderingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public void BitmapCompositionPreservesForegroundOpacity(byte alpha)
    {
        using var front = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var back = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        front.Erase(SKColors.Red);
        back.Erase(SKColors.Blue);
        using var merged = new DynamicTextureModule.DynamicTextureUpdater().MergeBitMaps(front, back, alpha);

        SKColor pixel = merged.GetPixel(2, 2);
        Assert.Equal(255, pixel.Alpha);
        Assert.InRange((int)pixel.Red, Math.Max(0, alpha - 1), Math.Min(255, alpha + 1));
        Assert.Equal(0, pixel.Green);
        Assert.InRange((int)pixel.Blue, Math.Max(0, 254 - alpha), Math.Min(255, 256 - alpha));
    }

    [Theory]
    [InlineData("FillPolygon", false)]
    [InlineData("Polygon", true)]
    public void PolygonPathsRemainClosedAndKeepTheirFillOrStroke(string command, bool stroked)
    {
        var module = new VectorRenderModule();
        module.Initialise(new IniConfigSource());
        try
        {
            var texture = module.ConvertData(
                $"PenColour Red; PenSize 2; {command} 8,8,48,8,48,48;",
                "width:64,height:64,bgcolor:White");
            Assert.NotNull(texture);
            using var bitmap = J2kImage.FromBytes(texture.Data).As<SKBitmap>();
            Assert.NotNull(bitmap);

            SKColor interior = bitmap.GetPixel(36, 20);
            Assert.True(stroked ? interior.Green > 200 : interior.Green < 40,
                $"Unexpected polygon interior: {interior}");
            SKColor closingEdge = bitmap.GetPixel(stroked ? 28 : 30, 28);
            Assert.True(closingEdge.Red > 180 && closingEdge.Green < 80,
                $"The final edge was not drawn: {closingEdge}");
            Assert.True(bitmap.GetPixel(4, 55).Green > 200);
        }
        finally
        {
            module.Close();
        }
    }
}
