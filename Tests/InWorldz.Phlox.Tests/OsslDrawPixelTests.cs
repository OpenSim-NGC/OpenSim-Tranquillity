using System;
using System.Collections.Generic;
using System.Linq;
using CoreJ2K;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.DynamicTexture;
using OpenSim.Region.CoreModules.Scripting.VectorRender;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What a dynamic texture actually contains. The earlier tests pinned a new texture id on the face; in world, the
/// same draw list gave a flat grey prim. This renders that draw list through the real modules, takes the
/// J2K bytes the face now points at, decodes them the way the sim itself decodes textures (CoreJ2K), and
/// asserts pixels: red where the text is, the upstream default white background elsewhere.
/// </summary>
public class OsslDrawPixelTests
{
    private readonly ITestOutputHelper _out;
    public OsslDrawPixelTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;
    private const string LiveDrawList = "MoveTo 20,20; PenColour RED; FontSize 24; Text Hello;";

    private sealed class MemoryAssetCache : IAssetCache
    {
        private readonly Dictionary<string, AssetBase> m_assets = new();
        public void Cache(AssetBase asset, bool replace = false) { lock (m_assets) m_assets[asset.ID] = asset; }
        public void CacheNegative(string id) { }
        public bool Get(string id, out AssetBase asset) { lock (m_assets) return m_assets.TryGetValue(id, out asset); }
        public AssetBase GetCached(string id) { lock (m_assets) return m_assets.TryGetValue(id, out var a) ? a : null; }
        public bool GetFromMemory(string id, out AssetBase asset) => Get(id, out asset);
        public bool Check(string id) { lock (m_assets) return m_assets.ContainsKey(id); }
        public void Expire(string id) { lock (m_assets) m_assets.Remove(id); }
        public void Clear() { lock (m_assets) m_assets.Clear(); }
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 80 && c.Blue < 80;
    /// <summary>Red within 3 px of the point: "Hello" at 24 px has gaps between strokes, and the brief says ~(30,30).</summary>
    private static bool RedNear(SKBitmap bmp, int px, int py)
    {
        for (int y = py - 3; y <= py + 3; y++) for (int x = px - 3; x <= px + 3; x++) if (IsRed(bmp.GetPixel(x, y))) return true;
        return false;
    }
    private static bool IsWhite(SKColor c) => c.Red > 240 && c.Green > 240 && c.Blue > 240;

    private (SKBitmap bmp, string magic, int redCount, SKRectI redBox) Decode(byte[] j2k)
    {
        string magic = BitConverter.ToString(j2k.Take(8).ToArray());
        var img = J2kImage.FromBytes(j2k);
        var bmp = img.As<SKBitmap>();
        int reds = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsRed(bmp.GetPixel(x, y))) { reds++; minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        return (bmp, magic, reds, reds == 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX, maxY));
    }

    [Fact]
    public void TheRendererAloneDrawsRedHelloOnWhiteWhereTheScriptAskedFor()
    {
        var vr = new VectorRenderModule();
        vr.Initialise(new Nini.Config.IniConfigSource());
        var tex = ((IDynamicTextureRender)vr).ConvertData(LiveDrawList, "256");
        Assert.NotNull(tex?.Data);
        var (bmp, magic, reds, box) = Decode(tex.Data);
        _out.WriteLine($"j2k bytes={tex.Data.Length} magic={magic} decoded={bmp.Width}x{bmp.Height} colorType={bmp.ColorType} alpha={bmp.AlphaType} " +
                       $"p(30,30)={bmp.GetPixel(30, 30)} p(200,200)={bmp.GetPixel(200, 200)} redPixels={reds} redBox={box}");

        Assert.Equal(256, bmp.Width);
        Assert.StartsWith("FF-4F-FF-51", magic);   // A raw J2K codestream (SOC, SIZ), not a JP2 signature box
        Assert.True(IsWhite(bmp.GetPixel(200, 200)), "background at (200,200) is not white: " + bmp.GetPixel(200, 200));
        Assert.True(reds > 50, "no red text pixels at all: the font or the pen never drew");
        Assert.True(box.Top >= 18, "the text sits above the pen position (Skia baseline semantics), red box " + box);   // Pen = top-left, as GDI
        Assert.True(RedNear(bmp, 30, 30), "no red within 3 px of (30,30): " + bmp.GetPixel(30, 30) + " (red box " + box + ")");
    }

    [Fact]
    public void TheTextureOnTheFaceDecodesToRedHelloOnWhite()
    {
        var cache = new MemoryAssetCache();
        var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));
        using (h)
        {
            h.Scene.RegisterModuleInterface<IAssetCache>(cache);
            SceneHelpers.SetupSceneModules(h.Scene, h.Config, new DynamicTextureModule(), new VectorRenderModule());
            UUID before = h.Prim.Shape.Textures.DefaultTexture.TextureID;
            h.RezScript("default { state_entry() { llSay(0, \"id=\" + osSetDynamicTextureData(\"\", \"vector\", \"" + LiveDrawList + "\", \"\", 0)); } }");
            h.PumpUntil(() =>
            {
                UUID now = h.Prim.Shape.Textures.DefaultTexture.TextureID;
                return h.Said.Any(s => s.StartsWith("id=")) && now != before && cache.Get(now.ToString(), out _);
            });
            UUID face = h.Prim.Shape.Textures.DefaultTexture.TextureID;
            Assert.True(cache.Get(face.ToString(), out var asset), "the face's texture is not in the cache; said=[" + string.Join(" | ", h.Said) + "]");
            var (bmp, magic, reds, box) = Decode(asset.Data);
            _out.WriteLine($"asset type={asset.Type} local={asset.Local} bytes={asset.Data.Length} magic={magic} decoded={bmp.Width}x{bmp.Height} " +
                           $"p(30,30)={bmp.GetPixel(30, 30)} p(200,200)={bmp.GetPixel(200, 200)} redPixels={reds} redBox={box}");

            Assert.Equal((sbyte)AssetType.Texture, asset.Type);
            Assert.StartsWith("FF-4F-FF-51", magic);   // A raw J2K codestream, which is what the viewer's OPJ_CODEC_J2K decoder reads
            Assert.True(IsWhite(bmp.GetPixel(200, 200)), "background at (200,200) is not white: " + bmp.GetPixel(200, 200));
            Assert.True(box.Top >= 18, "the text sits above the pen position, red box " + box);
            Assert.True(RedNear(bmp, 30, 30), "no red within 3 px of (30,30): " + bmp.GetPixel(30, 30) + " (red box " + box + ")");
        }
    }
}
