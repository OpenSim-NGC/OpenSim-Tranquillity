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
/// The colour names a draw list takes in PenColour and in the bgcolour parameter: the named .NET colours, in any
/// case, as upstream's Color.FromName took them.
/// </summary>
public class VectorRenderColourNameTests
{
    [Theory]
    [InlineData("Orange", 255, 165, 0)]
    [InlineData("orange", 255, 165, 0)]
    [InlineData("DarkViolet", 148, 0, 211)]
    [InlineData("ForestGreen", 34, 139, 34)]
    [InlineData("Red", 255, 0, 0)]
    public void PenColourTakesANamedColour(string name, int red, int green, int blue)
    {
        using SKBitmap bitmap = Render($"PenColour {name}; MoveTo 0,0; FillRectangle 64,64;", "width:64,height:64");

        AssertNear(red, green, blue, bitmap.GetPixel(32, 32));
    }

    [Theory]
    [InlineData("SkyBlue", 135, 206, 235)]
    [InlineData("Gold", 255, 215, 0)]
    [InlineData("Black", 0, 0, 0)]
    public void BgColourTakesANamedColour(string name, int red, int green, int blue)
    {
        using SKBitmap bitmap = Render("MoveTo 0,0;", $"width:64,height:64,bgcolour:{name}");

        AssertNear(red, green, blue, bitmap.GetPixel(32, 32));
    }

    [Fact]
    public void AnUnknownNameKeepsTheDefaultColours()
    {
        // The pen stays black and the background white, as before.
        using SKBitmap bitmap = Render("PenColour NoSuchColour; MoveTo 0,0; FillRectangle 32,64;",
            "width:64,height:64,bgcolour:NoSuchColour");

        AssertNear(0, 0, 0, bitmap.GetPixel(16, 32));
        AssertNear(255, 255, 255, bitmap.GetPixel(48, 32));
    }

    private static SKBitmap Render(string drawList, string extraParams)
    {
        var module = new VectorRenderModule();
        module.Initialise(new IniConfigSource());
        try
        {
            var texture = module.ConvertData(drawList, extraParams);
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

    // The texture is lossy JPEG 2000, so a flat colour comes back within a few levels of what was drawn.
    private static void AssertNear(int red, int green, int blue, SKColor actual)
    {
        const int Tolerance = 6;
        Assert.True(
            Math.Abs(actual.Red - red) <= Tolerance &&
            Math.Abs(actual.Green - green) <= Tolerance &&
            Math.Abs(actual.Blue - blue) <= Tolerance,
            $"expected about ({red},{green},{blue}), got {actual}");
    }
}
