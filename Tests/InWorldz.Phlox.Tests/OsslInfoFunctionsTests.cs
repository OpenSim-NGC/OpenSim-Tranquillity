using System;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The first OSSL family on Phlox: seventeen read-only information functions, each
/// ported from OSSL_Api.cs with the same threat level, behind OsslGate - the same [OSSL] keys YEngine
/// reads (AllowOSFunctions, OSFunctionThreatLevel, Allow_&lt;fn&gt;, Creators_&lt;fn&gt;,
/// PermissionErrorToOwner). Dispatch is proven through the harness (a script calls each one and says
/// the result); the gate is proven by the same script under four configurations.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class OsslInfoFunctionsTests
{
    private readonly ITestOutputHelper _out;
    public OsslInfoFunctionsTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static Action<IConfigSource> Ossl(params (string key, string value)[] kv) => cfg =>
    {
        var s = cfg.AddConfig("OSSL");
        foreach (var (k, v) in kv) s.Set(k, v);
    };

    private static string Denials(SchedulerHarness h) =>
        string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    [Fact]
    public void TheUngatedAndLowFunctionsDispatchUnderTheDefaultConfig()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        h.RezScript(@"default { state_entry() {
            llSay(0, ""name="" + osGetGridName() + ""|nick="" + osGetGridNick());
            vector s = osGetRegionSize(); llSay(0, ""size="" + (string)((integer)s.x) + ""x"" + (string)((integer)s.y));
            llSay(0, ""agents="" + (string)llGetListLength(osGetAgents()));
            llSay(0, ""map="" + osGetMapTexture());
            llSay(0, ""phys="" + osGetPhysicsEngineName() + ""|ptype="" + osGetPhysicsEngineType());
            llSay(0, ""health="" + (string)osGetHealth(""" + sp.UUID + @"""));
            llSay(0, ""done"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("done"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] denials=[" + Denials(h) + "]");

        Assert.Contains("done", h.Said);
        Assert.Contains(h.Said, s => s.StartsWith("name="));
        Assert.Contains("size=256x256", h.Said);
        Assert.Contains("agents=1", h.Said);
        Assert.Contains("map=" + h.Scene.RegionInfo.RegionSettings.TerrainImageID, h.Said);
        Assert.Contains(h.Said, s => s.StartsWith("phys=") && s.EndsWith("|ptype="));   // High: empty, not an error
        Assert.Contains("health=100.000000", h.Said);
        Assert.Empty(Denials(h));
    }

    private const string HomeUriScript = @"default { state_entry() { llSay(0, ""home="" + osGetGridHomeURI()); llSay(0, ""after""); } }";

    [Fact]
    public void AModerateFunctionIsDeniedAtTheDefaultVeryLowWithYEnginesMessage()
    {
        using var h = new SchedulerHarness();
        h.RezScript(HomeUriScript);
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("OSSL Permission Error: osGetGridHomeURI permission denied.  Allowed threat level is VeryLow but function threat level is Moderate")));
        _out.WriteLine("denials=[" + Denials(h) + "]");

        Assert.DoesNotContain(h.Said, s => s.StartsWith("home="));
        Assert.DoesNotContain("after", h.Said);                     // the script stopped, as on YEngine
        Assert.Contains(h.SaidOn, s => s.Channel == DebugChannel
            && s.Message.Contains("OSSL Permission Error: osGetGridHomeURI permission denied.  Allowed threat level is VeryLow but function threat level is Moderate"));
    }

    [Fact]
    public void RaisingOSFunctionThreatLevelAllowsIt()
    {
        using var h = new SchedulerHarness(Ossl(("OSFunctionThreatLevel", "Moderate")));
        h.RezScript(HomeUriScript);
        h.PumpUntil(() => h.Said.Contains("after"));
        Assert.Contains(h.Said, s => s.StartsWith("home="));
        Assert.Contains("after", h.Said);
    }

    [Fact]
    public void AllowFunctionTrueAndAnOwnerUuidListAreHonoured()
    {
        using (var h = new SchedulerHarness(Ossl(("Allow_osGetGridHomeURI", "true"))))
        {
            h.RezScript(HomeUriScript);
            h.PumpUntil(() => h.Said.Any(s => s.StartsWith("home=")));
            Assert.Contains(h.Said, s => s.StartsWith("home="));
        }
        var listed = UUID.Random();
        using (var h = new SchedulerHarness(Ossl(("Allow_osGetGridHomeURI", listed + ", " + UUID.Random()))))
        {
            // the prim's owner is not on the list: denied with the list message, script stopped.
            // (PARCEL_OWNER is not exercised here: the test parcel and the test script both carry a zero
            // owner, so that keyword matches trivially in this scene.)
            h.RezScript(HomeUriScript);
            h.PumpFor(TimeSpan.FromSeconds(1));
            h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osGetGridHomeURI permission denied")));
            _out.WriteLine("denials=[" + Denials(h) + "]");
            Assert.DoesNotContain(h.Said, s => s.StartsWith("home="));
            Assert.Contains(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osGetGridHomeURI permission denied"));
        }
        using (var h = new SchedulerHarness(Ossl(("Allow_osGetGridHomeURI", listed.ToString()))))
        {
            h.Prim.OwnerID = listed;                                     // now the prim's owner IS on the list
            h.RezScript(HomeUriScript);
            h.PumpUntil(() => h.Said.Any(s => s.StartsWith("home=")));
            Assert.Contains(h.Said, s => s.StartsWith("home="));
        }
        using (var h = new SchedulerHarness(Ossl(("Allow_osGetGridHomeURI", "false"))))
        {
            h.RezScript(HomeUriScript);
            h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osGetGridHomeURI disabled in region configuration")));
            Assert.Contains(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osGetGridHomeURI disabled in region configuration"));
        }
    }

    [Fact]
    public void AllowOSFunctionsFalseStopsEvenTheBareCheck()
    {
        using var h = new SchedulerHarness(Ossl(("AllowOSFunctions", "false"), ("PermissionErrorToOwner", "true")));
        h.RezScript(@"default { state_entry() { vector s = osGetRegionSize(); llSay(0, ""size""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("(OWNER)OSSL Permission Error: All unsafe OSSL funtions disabled")));
        Assert.DoesNotContain("size", h.Said);
        Assert.Contains(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("(OWNER)OSSL Permission Error: All unsafe OSSL funtions disabled"));
    }
}
