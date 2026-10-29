using System;
using System.Linq;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.DynamicTexture;
using OpenSim.Region.CoreModules.Scripting.VectorRender;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL draw-list helpers and the dynamic-texture calls, ported from OSSL_Api.cs. The helpers
/// are pinned by the command string they build; the texture call end to end - DynamicTextureModule plus the Skia
/// VectorRenderModule in the scene - by the texture id that lands on the prim's faces.
/// </summary>
public class OsslDrawTests
{
    private readonly ITestOutputHelper _out;
    public OsslDrawTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    /// <summary>
    /// A dynamic texture is a LOCAL asset: DynamicTextureUpdater.DataReceived caches it and refuses to work without
    /// an IAssetCache ("this are local assets and will not work without cache"). The harness scene has none, so a
    /// memory cache stands in - which is also where the test reads the rendered asset back from.
    /// </summary>
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

    private static SchedulerHarness Scene(string threat = "Severe", bool renderer = true, MemoryAssetCache cache = null)
    {
        var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat));
        if (renderer)
        {
            h.Scene.RegisterModuleInterface<IAssetCache>(cache ?? new MemoryAssetCache());
            SceneHelpers.SetupSceneModules(h.Scene, h.Config, new DynamicTextureModule(), new VectorRenderModule());
        }
        return h;
    }

    private string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    [Fact]
    public void TheDrawHelpersBuildTheCommandStringTheRendererParses()
    {
        using var h = Scene(renderer: false);
        h.RezScript(@"default { state_entry() {
            string d = """";
            d = osMovePen(d, 20, 20);
            d = osSetPenColor(d, ""Red"");
            d = osSetPenColor(d, <1.0, 0.0, 0.0>, 0.5);
            d = osSetFontSize(d, 24);
            d = osSetFontName(d, ""Arial"");
            d = osSetPenSize(d, 3);
            d = osSetPenCap(d, ""both"", ""round"");
            d = osDrawText(d, ""Hello"");
            d = osDrawLine(d, 1, 2, 3, 4);
            d = osDrawLine(d, 5, 6);
            d = osDrawEllipse(d, 7, 8); d = osDrawFilledEllipse(d, 9, 10);
            d = osDrawRectangle(d, 11, 12); d = osDrawFilledRectangle(d, 13, 14);
            d = osDrawPolygon(d, [1, 2, 3], [4, 5, 6]);
            d = osDrawFilledPolygon(d, [1, 2, 3], [4, 5, 6]);
            d = osDrawImage(d, 15, 16, ""http://example/x.png"");
            d = osDrawResetTransform(d); d = osDrawRotationTransform(d, 90.0);
            d = osDrawScaleTransform(d, 2.0, 3.0); d = osDrawTranslationTransform(d, 4.0, 5.0);
            llSay(0, ""d="" + d);
            llSay(0, ""bad="" + osDrawPolygon("""", [1, 2], [3, 4]) + ""|"");
            llSay(0, ""sz="" + (string)osGetDrawStringSize(""vector"", ""Hello"", ""Arial"", 24));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("sz=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        string d = h.Said.First(s => s.StartsWith("d=")).Substring(2);
        Assert.Equal(
            "MoveTo 20,20;PenColor Red; PenColor 7FFF0000; FontSize 24; FontName Arial; PenSize 3; PenCap both,round; Text Hello; " +
            "MoveTo 1,2; LineTo 3,4; LineTo 5,6; Ellipse 7,8; FillEllipse 9,10; Rectangle 11,12; FillRectangle 13,14; " +
            "Polygon 1,4,2,5,3,6; FillPolygon 1,4,2,5,3,6; Image 15,16,http://example/x.png; ResetTransf;RotTransf 90;ScaleTransf 2,3;TransTransf 4,5;", d);
        Assert.Contains("bad=|", h.Said);                         // fewer than three points: "" as upstream
        Assert.Contains("sz=<0.00000, 0.00000, 0.00000>", h.Said);   // no texture manager in this scene
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void SetDynamicTextureDataRendersTheDrawListOntoTheFaces()
    {
        var cache = new MemoryAssetCache();
        using var h = Scene(cache: cache);
        Assert.NotNull(h.Scene.RequestModuleInterface<IDynamicTextureManager>());
        UUID before = h.Prim.Shape.Textures.DefaultTexture.TextureID;

        h.RezScript(@"default { state_entry() {
            string id = osSetDynamicTextureData("""", ""vector"", ""MoveTo 20,20; PenColour RED; FontSize 24; Text Hello;"", """", 0);
            llSay(0, ""id="" + id);
            llSay(0, ""sz="" + (string)osGetDrawStringSize(""vector"", ""Hello"", ""Arial"", 24));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("sz=")) && h.Prim.Shape.Textures.DefaultTexture.TextureID != before
            && cache.Get(h.Prim.Shape.Textures.DefaultTexture.TextureID.ToString(), out _));
        UUID after = h.Prim.Shape.Textures.DefaultTexture.TextureID;
        _out.WriteLine("before=" + before + " after=" + after + " said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        string id = h.Said.First(s => s.StartsWith("id=")).Substring(3);
        Assert.NotEqual(UUID.Zero.ToString(), id);                 // this tree returns the new texture's id
        Assert.NotEqual(before, after);
        Assert.NotEqual(UUID.Zero, after);
        Assert.Equal(id, after.ToString());
        Assert.True(cache.Get(after.ToString(), out var asset));    // a local asset: cached, never sent to the asset server
        Assert.True(asset.Local);
        Assert.True(asset.Data.Length > 100);
        var sz = h.Said.First(s => s.StartsWith("sz="));
        Assert.DoesNotContain(h.Said, x => x.StartsWith("sz=<0.00000"));   // measured by the renderer this time
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void TheGateHoldsAtItsUpstreamLevel()
    {
        using var h = Scene("VeryLow");
        UUID before = h.Prim.Shape.Textures.DefaultTexture.TextureID;
        h.RezScript(@"default { state_entry() { osSetDynamicTextureURL("""", ""image"", ""http://example/x.png"", """", 0); llSay(0, ""after""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osSetDynamicTextureURL permission denied")));

        Assert.DoesNotContain("after", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osSetDynamicTextureURL permission denied"));
        Assert.Equal(before, h.Prim.Shape.Textures.DefaultTexture.TextureID);
    }
}
