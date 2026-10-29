/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text.Json;
using OpenMetaverse;
using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

/// <summary>
/// Runs every reference result in Vectors/ (see Vectors/README.md) through <see cref="AvatarBakeCompositor"/> and
/// the pixel arithmetic it uses. Each bake vector is the compositor's input (wearables, textures, size, channel)
/// and its exact output (canvas, morph mask, flags); the raw vectors j01 to j03 cover the mask ramp, the byte
/// product and resampling on their own. Outputs must match byte for byte; the README states no tolerance.
/// </summary>
public class BakeVectorTests
{
    private static readonly string VectorRoot = Path.Combine(AppContext.BaseDirectory, "Vectors");

    private static readonly AvatarBakeCompositor Compositor = new();

    public static IEnumerable<object[]> BakeVectors()
        => Directory.GetDirectories(VectorRoot)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.StartsWith('j'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n! });

    [Fact]
    public void VectorFolderHoldsTheSeventyOneVectors()
        => Assert.Equal(71, Directory.GetDirectories(VectorRoot).Length);

    [Theory]
    [MemberData(nameof(BakeVectors))]
    public void BakeMatchesReference(string id)
    {
        var dir = Path.Combine(VectorRoot, id);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        var root = doc.RootElement;

        var channel = Enum.Parse<BakeChannel>(root.GetProperty("channel").GetString()!);
        var size = root.GetProperty("size").GetInt32();
        Dictionary<int, float>? overlay = null;
        if (root.GetProperty("overlayParams") is { ValueKind: JsonValueKind.Object } ov)
            overlay = ReadParams(ov);

        var worn = new List<BakeWearable>();
        foreach (var w in root.GetProperty("wearables").EnumerateArray())
        {
            var ids = new Dictionary<TextureSlot, UUID>();
            var pixels = new Dictionary<TextureSlot, BakeBitmap>();
            foreach (var t in w.GetProperty("textures").EnumerateArray())
            {
                var slot = Enum.Parse<TextureSlot>(t.GetProperty("slot").GetString()!);
                ids[slot] = t.GetProperty("textureId").GetString() switch
                {
                    "invisible-texture-id" => BakeConstants.InvisibleTexture,
                    _ => OrdinaryTextureId,
                };
                if (t.GetProperty("file") is { ValueKind: JsonValueKind.String } file)
                    pixels[slot] = ReadRgba(Path.Combine(dir, file.GetString()!),
                        t.GetProperty("width").GetInt32(), t.GetProperty("height").GetInt32(), t.GetProperty("hasAlpha").GetBoolean());
            }
            worn.Add(new BakeWearable
            {
                Kind = Enum.Parse<WearableKind>(w.GetProperty("kind").GetString()!),
                Label = w.GetProperty("label").GetString() ?? "",
                Params = ReadParams(w.GetProperty("params")),
                TextureIds = ids,
                Textures = pixels,
            });
        }

        var result = Compositor.Bake(channel, worn, size, overlay);

        var expected = root.GetProperty("result");
        Assert.Equal(expected.GetProperty("invisible").GetBoolean(), result.Invisible);
        Assert.Equal(expected.GetProperty("nothingDrawn").GetBoolean(), result.NothingDrawn);

        var rgba = Interleave(result);
        Assert.Equal(expected.GetProperty("rgbaSha256").GetString(), Hex(SHA256.HashData(rgba)));
        Assert.Equal(expected.GetProperty("morphSha256").GetString(), Hex(SHA256.HashData(result.MorphMask)));

        // Where the vector keeps its output files, compare the bytes themselves too: a failure then names a pixel.
        if (File.Exists(Path.Combine(dir, "out.rgba")))
            AssertSameBytes(File.ReadAllBytes(Path.Combine(dir, "out.rgba")), rgba, "canvas");
        if (File.Exists(Path.Combine(dir, "out.morph")))
            AssertSameBytes(File.ReadAllBytes(Path.Combine(dir, "out.morph")), result.MorphMask, "morph mask");
    }

    [Fact]
    public void MaskRampMatchesReference()
    {
        var dir = Path.Combine(VectorRoot, "j01-processalpha-ramps");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        var domains = doc.RootElement.GetProperty("domains").EnumerateArray().Select(e => (float)e.GetDouble()).ToList();
        var weights = doc.RootElement.GetProperty("weights").EnumerateArray().Select(e => (float)e.GetDouble()).ToList();
        var expected = File.ReadAllBytes(Path.Combine(dir, "out.bin"));
        Assert.Equal(domains.Count * weights.Count * 256, expected.Length);

        var actual = new byte[expected.Length];
        var o = 0;
        foreach (var d in domains)
            foreach (var w in weights)
                for (var g = 0; g < 256; g++) actual[o++] = BakePixelMath.Ramp((byte)g, d, w);
        AssertSameBytes(expected, actual, "ramp table");
    }

    [Fact]
    public void ByteProductMatchesReference()
    {
        var expected = File.ReadAllBytes(Path.Combine(VectorRoot, "j02-multiply-table", "out.bin"));
        Assert.Equal(256 * 256, expected.Length);
        var actual = new byte[expected.Length];
        for (var a = 0; a < 256; a++)
            for (var b = 0; b < 256; b++) actual[a * 256 + b] = BakePixelMath.ByteProduct(a, b);
        AssertSameBytes(expected, actual, "product table");
    }

    [Fact]
    public void ResamplingMatchesReference()
    {
        var dir = Path.Combine(VectorRoot, "j03-resample");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(16, cases.Count);
        foreach (var c in cases)
        {
            int sw = c.GetProperty("srcW").GetInt32(), sh = c.GetProperty("srcH").GetInt32();
            int dw = c.GetProperty("dstW").GetInt32(), dh = c.GetProperty("dstH").GetInt32();
            var input = File.ReadAllBytes(Path.Combine(dir, c.GetProperty("input").GetString()!));
            var expected = File.ReadAllBytes(Path.Combine(dir, c.GetProperty("output").GetString()!));
            var actual = BakePixelMath.Resample(input, sw, sh, dw, dh);
            AssertSameBytes(expected, actual, $"resample case {c.GetProperty("n").GetInt32()} ({sw}x{sh} to {dw}x{dh})");
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>Any texture id other than the invisible one; the compositor only compares ids with the invisible id.</summary>
    private static readonly UUID OrdinaryTextureId = new("5b1c2d3e-4f50-4a61-8b72-93a4b5c6d7e8");

    private static Dictionary<int, float> ReadParams(JsonElement obj)
    {
        var d = new Dictionary<int, float>();
        foreach (var p in obj.EnumerateObject()) d[int.Parse(p.Name)] = (float)p.Value.GetDouble();
        return d;
    }

    private static BakeBitmap ReadRgba(string path, int w, int h, bool hasAlpha)
    {
        var bytes = File.ReadAllBytes(path);
        var n = w * h;
        Assert.Equal(n * 4, bytes.Length);
        var r = new byte[n]; var g = new byte[n]; var b = new byte[n]; var a = new byte[n];
        for (var i = 0; i < n; i++) { r[i] = bytes[i * 4]; g[i] = bytes[i * 4 + 1]; b[i] = bytes[i * 4 + 2]; a[i] = bytes[i * 4 + 3]; }
        return new BakeBitmap(w, h, r, g, b, hasAlpha ? a : null);
    }

    private static byte[] Interleave(BakeComposite c)
    {
        var o = new byte[c.R.Length * 4];
        for (var i = 0; i < c.R.Length; i++) { o[i * 4] = c.R[i]; o[i * 4 + 1] = c.G[i]; o[i * 4 + 2] = c.B[i]; o[i * 4 + 3] = c.A[i]; }
        return o;
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private static void AssertSameBytes(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i])
                Assert.Fail($"{what}: first difference at byte {i}: expected {expected[i]}, got {actual[i]}");
    }
}
