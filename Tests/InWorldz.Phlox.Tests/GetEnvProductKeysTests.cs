using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetEnv's product and channel keys answer what YEngine answers in the same scene (LSL_Api.llGetEnv:
/// region_product_name => RegionInfo.RegionType, region_product_sku and sim_channel => "OpenSim"),
/// not a fixed product name of one grid.
/// </summary>
[Collection("phlox-yengine")]
public class GetEnvProductKeysTests
{
    private static readonly string[] Keys = { "region_product_name", "region_product_sku", "sim_channel" };

    private readonly ITestOutputHelper _out;
    public GetEnvProductKeysTests(ITestOutputHelper o) => _out = o;

    private static void SetRegionType(RegionInfo ri, string type)
        => typeof(RegionInfo).GetField("m_regionType", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ri, type);

    [Theory]
    [InlineData("")]
    [InlineData("Mainland")]
    public void PhloxAnswersWhatYEngineAnswers(string regionType)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        SetRegionType(h.Scene.RegionInfo, regionType);

        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var yengine = new LSL_Api();
        yengine.Initialize(h.YEngine, h.Prim, item);

        h.RezScript("default { state_entry() { list k = [" + string.Join(", ", Keys.Select(k => "\"" + k + "\""))
            + "]; integer i; for (i = 0; i < llGetListLength(k); ++i) llSay(0, llList2String(k, i) + \"=\" + llGetEnv(llList2String(k, i))); } }");
        h.PumpUntil(() => Keys.All(k => h.Said.Any(s => s.StartsWith(k + "="))));
        _out.WriteLine(string.Join(" | ", h.Said));

        foreach (var key in Keys)
        {
            string expected = key + "=" + (string)yengine.llGetEnv(key);
            _out.WriteLine("YEngine " + expected);
            Assert.Contains(expected, h.Said);
        }
    }

    /// <summary>
    /// llGetEnv("grid"): the grid name osGetGridName reads (Scene.SceneGridInfo.GridName), the same in both
    /// engines through LSL_Api.EnvGridName. A grid with no name configured, and a scene with no grid info, answer "",
    /// as an unknown key does.
    /// </summary>
    [Theory]
    [InlineData("configured", "Example Test Grid")]
    [InlineData("unconfigured", "")]
    [InlineData("no grid info", "")]
    public void GridAnswersWhatYEngineAnswers(string grid, string expected)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        // The test scene's grid info is built from a config with no grid name, so it holds GridInfo's stand-in; a
        // configured name reaches it through the GridName setter, as SimulatorFeaturesModule sets it from config.
        Assert.Equal("Another bad configured grid", h.Scene.SceneGridInfo.GridName);
        if (grid == "configured") h.Scene.SceneGridInfo.GridName = "Example Test Grid";
        if (grid == "no grid info") h.Scene.SceneGridInfo = null;

        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var yengine = new LSL_Api();
        yengine.Initialize(h.YEngine, h.Prim, item);

        h.RezScript("default { state_entry() { llSay(0, \"grid=\" + llGetEnv(\"grid\") + \"|unknown=\" + llGetEnv(\"no such key\")); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("grid=")));
        _out.WriteLine(string.Join(" | ", h.Said));

        Assert.Equal(expected, (string)yengine.llGetEnv("grid"));
        Assert.Contains("grid=" + expected + "|unknown=", h.Said);
    }
}
