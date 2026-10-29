using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llRezObjectWithParams returns a key. SL wiki (LlRezObjectWithParams): "Returns a key which will
/// be the key of the object when it is successfully rezzed in the world. On failure, returns
/// (key)"" (in LSL)". The key is the rezzed root's, the same one object_rez reports, as upstream
/// LSL_Api also returns the rezzed group's id. A value-returning async call hands its value back
/// through SysReturn on every path (AsyncReturnGuardTests), so the plain statement form still runs.
/// </summary>
// Touches no process-wide state, so it runs in parallel.
public class RezObjectWithParamsKeyTests
{
    private readonly ITestOutputHelper _out;
    public RezObjectWithParamsKeyTests(ITestOutputHelper o) => _out = o;

    private const string Rez = "llRezObjectWithParams(\"child\", [REZ_POS, llGetPos() + <0,0,1>, FALSE, FALSE])";

    private static SchedulerHarness WithChild()
    {
        var h = new SchedulerHarness();
        TaskInventoryHelpers.AddSceneObject(h.Scene.AssetService, h.Prim, "child", UUID.Random(), h.Prim.OwnerID);
        return h;
    }

    private SceneObjectGroupInfo[] Rezzed(SchedulerHarness h) =>
        h.Scene.GetSceneObjectGroups()
            .Where(g => g.UUID != h.Prim.ParentGroup.UUID)
            .Select(g => new SceneObjectGroupInfo(g.UUID, g.RootPart.UUID))
            .ToArray();

    private record SceneObjectGroupInfo(UUID Group, UUID Root);

    [Fact]
    public void TheReturnedKeyIsTheKeyObjectRezReports()
    {
        using var h = WithChild();
        h.RezScript("default { state_entry() { key k = " + Rez + "; llSay(0, \"k=\" + (string)k); } " +
                    "object_rez(key id) { llSay(0, \"rez=\" + (string)id); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("k=")) && h.Said.Any(s => s.StartsWith("rez=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        var k = h.Said.Single(s => s.StartsWith("k="))["k=".Length..];
        var rez = h.Said.Single(s => s.StartsWith("rez="))["rez=".Length..];
        Assert.NotEqual(UUID.Zero.ToString(), k);
        Assert.Equal(rez, k);
        var child = Assert.Single(Rezzed(h));
        Assert.Equal(child.Root.ToString(), k);
    }

    [Fact]
    public void AFailedRezReturnsTheEmptyKey()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { key k = llRezObjectWithParams(\"nothing\", []); " +
                    "llSay(0, \"k=[\" + (string)k + \"] len=\" + (string)llStringLength((string)k)); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("k=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Contains("k=[] len=0", h.Said);
    }

    [Fact]
    public void AnEmptyInventoryNameReturnsTheEmptyKey()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { key k = llRezObjectWithParams(\"\", []); llSay(0, \"k=[\" + (string)k + \"]\"); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("k=")));
        Assert.Contains("k=[]", h.Said);
    }

    [Fact]
    public void AsAStatementItStillRezzesAndTheScriptCarriesOn()
    {
        using var h = WithChild();
        var id = h.RezScript("default { state_entry() { " + Rez + "; llSay(0, \"after\"); } " +
                             "object_rez(key id) { llSay(0, \"rez=\" + (string)id); } }");
        h.PumpUntil(() => h.Said.Contains("after") && h.Said.Any(s => s.StartsWith("rez=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] " + h.StatusOf(id));
        Assert.Contains("after", h.Said);
        var child = Assert.Single(Rezzed(h));
        Assert.Contains("rez=" + child.Root, h.Said);
        Assert.Contains("terminated=-", h.StatusOf(id));
    }
}
