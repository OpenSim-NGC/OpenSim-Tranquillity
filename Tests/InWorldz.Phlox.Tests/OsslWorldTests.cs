using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL parcel, estate, terrain, wind and sun family, each ported from OSSL_Api.cs under its upstream
/// threat level through OsslGate. One dispatch test per group with an assertion on the scene: the heightmap read back,
/// the parcel renamed, the restart scheduled through the region's restart module.
/// </summary>
public class OsslWorldTests
{
    private readonly ITestOutputHelper _out;
    public OsslWorldTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    /// <summary>A real land channel (LandManagementModule), so a parcel edit persists and can be read back.</summary>
    private static SchedulerHarness Scene(string threat = "Severe")
    {
        var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat));
        var lmm = new LandManagementModule();
        SceneHelpers.SetupSceneModules(h.Scene, h.Config, lmm);
        lmm.EventManagerOnNoLandDataFromStorage();   // the one default parcel a fresh region gets
        return h;
    }

    private string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    /// <summary>Records what a script asks the restart module for; never restarts anything.</summary>
    private sealed class RecordingRestart : IRestartModule
    {
        public readonly List<(string Op, int Seconds, string Msg)> Calls = new();
        public TimeSpan TimeUntilRestart => TimeSpan.Zero;
        public void ScheduleRestart(UUID initiator, int seconds) => Calls.Add(("schedule", seconds, ""));
        public void AbortRestart(string message) => Calls.Add(("abort", 0, message));
        public void DelayRestart(int seconds, string message) => Calls.Add(("delay", seconds, message));
    }

    // ------------------------------------------------------------------ terrain

    [Fact]
    public void TerrainHeightReadsBackWhatWasSetAndIwSetGroundIsRealNow()
    {
        using var h = Scene();
        float before = h.Scene.Heightmap[10, 10];
        h.RezScript(@"default { state_entry() {
            integer ok = osSetTerrainHeight(10, 10, osGetTerrainHeight(10, 10) + 1.0);
            osTerrainFlush();
            llSay(0, ""ok="" + (string)ok + ""|h="" + (string)osTerrainGetHeight(10, 10));
            iwSetGround(20, 20, 21, 21, 15.0);
            llSay(0, ""done"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("done"));
        _out.WriteLine("before=" + before + " after=" + h.Scene.Heightmap[10, 10] + " said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("done", h.Said);
        Assert.Contains("ok=1|h=" + (before + 1f).ToString("0.000000"), h.Said);
        Assert.Equal(before + 1f, h.Scene.Heightmap[10, 10]);
        Assert.Equal(15f, h.Scene.Heightmap[20, 20]);
        Assert.Equal(15f, h.Scene.Heightmap[21, 21]);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ parcels

    [Fact]
    public void SetParcelDetailsRenamesThePrimsParcelAndGetParcelDetailsReadsItBackById()
    {
        using var h = Scene();
        var land = h.Scene.LandChannel.GetLandObject(h.Prim.AbsolutePosition.X, h.Prim.AbsolutePosition.Y);
        Assert.NotNull(land);
        land.LandData.OwnerID = h.Prim.OwnerID;   // the script owner owns the land it edits
        UUID parcelId = land.LandData.GlobalID;

        h.RezScript(@"default { state_entry() {
            osSetParcelDetails(llGetPos(), [PARCEL_DETAILS_NAME, ""Renamed by script"", PARCEL_DETAILS_DESC, ""desc""]);
            osSetParcelMusicURL(""http://music.example/stream"");
            llSay(0, ""name="" + llList2String(osGetParcelDetails(""" + parcelId + @""", [PARCEL_DETAILS_NAME]), 0));
            llSay(0, ""done"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("done"));
        var after = h.Scene.LandChannel.GetLandObject(h.Prim.AbsolutePosition.X, h.Prim.AbsolutePosition.Y);
        _out.WriteLine("name=" + after.LandData.Name + " said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("done", h.Said);
        Assert.Equal("Renamed by script", after.LandData.Name);
        Assert.Equal("desc", after.LandData.Description);
        Assert.Equal("http://music.example/stream", after.LandData.MusicURL);
        Assert.Contains("name=Renamed by script", h.Said);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ restart and sun

    [Fact]
    public void RegionRestartSchedulesThroughTheRestartModuleAndUnderFifteenSecondsAborts()
    {
        using var h = Scene();
        var restart = new RecordingRestart();
        h.Scene.RegisterModuleInterface<IRestartModule>(restart);
        h.RezScript(@"default { state_entry() {
            llSay(0, ""r1="" + (string)osRegionRestart(120.0));
            llSay(0, ""r2="" + (string)osRegionRestart(5.0, ""going down""));
            llSay(0, ""sun="" + (string)osGetSunParam(""year_length"") + ""|wind="" + osWindActiveModelPluginName() + ""|"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("sun=")));
        _out.WriteLine("calls=[" + string.Join(" | ", restart.Calls) + "] said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("r1=1", h.Said);
        Assert.Contains("r2=1", h.Said);
        Assert.Equal(2, restart.Calls.Count);
        Assert.Equal(("schedule", 120, ""), restart.Calls[0]);
        Assert.Equal("abort", restart.Calls[1].Op);
        Assert.Contains("sun=365.000000|wind=|", h.Said);   // no wind module in the harness: the empty name, not a stop
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void TheGateHoldsAtItsUpstreamLevel()
    {
        using var h = Scene("VeryLow");
        var restart = new RecordingRestart();
        h.Scene.RegisterModuleInterface<IRestartModule>(restart);
        float before = h.Scene.Heightmap[10, 10];
        h.RezScript(@"default { state_entry() { osRegionRestart(120.0); llSay(0, ""after""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osRegionRestart permission denied")));

        Assert.DoesNotContain("after", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osRegionRestart permission denied"));
        Assert.Empty(restart.Calls);

        h.RezScript(@"default { state_entry() { osSetTerrainHeight(10, 10, 99.0); llSay(0, ""after2""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("after2", h.Said);
        Assert.Equal(before, h.Scene.Heightmap[10, 10]);
    }
}
