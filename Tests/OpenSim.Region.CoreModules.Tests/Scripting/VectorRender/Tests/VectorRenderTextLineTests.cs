/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using CoreJ2K;
using Nini.Config;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.VectorRender.Tests;

/// <summary>
/// A line feed in a Text command (osDrawText's "\n") starts a new line one line spacing lower, as upstream's
/// System.Drawing DrawString did, and osGetDrawStringSize measures the text the same way.
/// </summary>
public class VectorRenderTextLineTests
{
    private const string FontName = "Arial";
    private const int FontSize = 20;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ALineFeedStartsANewLineOneLineSpacingLower(string lineBreak)
    {
        using SKBitmap bitmap = Render($"MoveTo 10,10; PenColour Red; FontSize {FontSize}; Text HHHH{lineBreak}HHHH;");

        // The rows with red in them form one band per line of text.
        List<(int Top, int Bottom)> bands = new();
        for (int y = 0; y < bitmap.Height; y++)
        {
            bool red = false;
            for (int x = 0; x < bitmap.Width && !red; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                red = c.Red > 200 && c.Green < 100 && c.Blue < 100;
            }
            if (!red)
                continue;
            if (bands.Count > 0 && bands[^1].Bottom == y - 1)
                bands[^1] = (bands[^1].Top, y);
            else
                bands.Add((y, y));
        }

        Assert.True(bands.Count == 2, $"expected two lines of text, found bands {string.Join(", ", bands)}");
        using SKFont font = new(SKTypeface.FromFamilyName(FontName, SKFontStyle.Normal), FontSize);
        Assert.InRange(bands[1].Top - bands[0].Top, font.Spacing - 2, font.Spacing + 2);
    }

    [Fact]
    public void TheSizeOfTextOnTwoLinesIsItsWidestLineAndBothLines()
    {
        VectorRenderModule module = new();
        module.GetDrawStringSize("HHHH", FontName, FontSize, out double oneLineWidth, out double oneLineHeight);
        module.GetDrawStringSize("HHHH\nHH", FontName, FontSize, out double width, out double height);

        using SKFont font = new(SKTypeface.FromFamilyName(FontName, SKFontStyle.Normal), FontSize);
        Assert.Equal(oneLineWidth, width);
        Assert.InRange(height, oneLineHeight + font.Spacing - 1, oneLineHeight + font.Spacing + 1);
    }

    private static SKBitmap Render(string drawList)
    {
        var module = new VectorRenderModule();
        module.Initialise(new IniConfigSource());
        try
        {
            var texture = module.ConvertData(drawList, "width:128,height:128");
            Assert.NotNull(texture);
            SKBitmap bitmap = J2kImage.FromBytes(texture.Data).As<SKBitmap>();
            Assert.NotNull(bitmap);
            return bitmap;
        }
        finally
        {
            module.Close();
        }
    }
}
