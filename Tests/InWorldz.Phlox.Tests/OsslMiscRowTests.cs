using System;
using System.Linq;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL sit target, misc and list functions: readers that answer from the prim, the region
/// or the list itself, plus the two OSSL functions with value semantics - osListSortInPlace and its strided
/// twin sort the caller's own list rather than returning a new one.
/// </summary>
public class OsslMiscRowTests
{
    private readonly ITestOutputHelper _out;
    public OsslMiscRowTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene() => new SchedulerHarness(
        cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));

    [Fact]
    public void TheListFamilyAnswersAndSortsTheCallersOwnList()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            list src = [3, 1, 2];
            osListSortInPlace(src, 1, TRUE);
            llSay(0, ""sorted="" + llDumpList2String(src, "",""));

            list pairs = [""b"", 2, ""a"", 1];
            osListSortInPlaceStrided(pairs, 2, 0, TRUE);
            llSay(0, ""strided="" + llDumpList2String(pairs, "",""));

            llSay(0, ""old="" + llDumpList2String(osOldList2ListStrided([0,1,2,3,4,5], 0, 5, 2), "",""));
            llSay(0, ""next0="" + (string)osListFindListNext([1,2,1,2,9], [1,2], 0, -1, 0));
            llSay(0, ""next1="" + (string)osListFindListNext([1,2,1,2,9], [1,2], 0, -1, 1));
            llSay(0, ""nextlast="" + (string)osListFindListNext([1,2,1,2,9], [1,2], 0, -1, -1));
            llSay(0, ""nextmiss="" + (string)osListFindListNext([1,2,1,2,9], [7], 0, -1, 0));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("nextmiss=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        // the variable itself changed - the whole point of the in-place forms
        Assert.Contains("sorted=1,2,3", h.Said);
        Assert.Contains("strided=a,1,b,2", h.Said);
        Assert.Contains("old=0,2,4", h.Said);
        Assert.Contains("next0=0", h.Said);
        Assert.Contains("next1=2", h.Said);
        Assert.Contains("nextlast=2", h.Said);
        Assert.Contains("nextmiss=-1", h.Said);
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == DebugChannel);
    }

    [Fact]
    public void TheMiscReadersAnswerFromThePrimAndTheRegion()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            llSitTarget(<0,0,1>, <0,0,0,1>);
            llSay(0, ""sitpos="" + (string)osGetSitTargetPos());
            llSay(0, ""sitrot="" + (string)osGetSitTargetRot());
            llSay(0, ""date="" + (string)llStringLength(osLoadedCreationDate()) + ""|"" +
                     (string)llStringLength(osLoadedCreationTime()) + ""|"" +
                     (string)llStringLength(osLoadedCreationID()));
            llSay(0, ""cold="" + (string)osTemperature2sRGB(900.0));
            llSay(0, ""warm="" + (string)osTemperature2sRGB(45000.0));
            llSay(0, ""noise="" + (string)(osPerlinNoise2D(0.25, 0.75, 4, 1.0) == osPerlinNoise2D(0.25, 0.75, 4, 1.0)));
            llSay(0, ""npcs="" + (string)llGetListLength(osGetNPCList()));
            llSay(0, ""inertia="" + (string)llGetListLength(osGetInertiaData()));
            osParticleSystem([PSYS_PART_FLAGS, 0]);
            osLinkParticleSystem(LINK_THIS, [PSYS_PART_FLAGS, 0]);
            osPreloadSound(LINK_THIS, ""nosuchsound"");
            osRemoveLinkInventory(LINK_THIS, ""nosuchitem"");
            llSay(0, ""done"");
        } }");
        h.PumpUntil(() => h.Said.Contains("done"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        Assert.Contains("sitpos=<0.00000, 0.00000, 1.00000>", h.Said);
        Assert.Contains("sitrot=<0.00000, 0.00000, 0.00000, 1.00000>", h.Said);
        Assert.Single(h.Said, s => s.StartsWith("cold=<1.00000, 0.04010, 0.00000>"));   // below 1000 K, the fixed end of the fit
        Assert.Single(h.Said, s => s.StartsWith("warm=<0.32770, 0.50220, 1.00000>"));   // above 40000 K, the other end
        Assert.Contains("noise=1", h.Said);
        Assert.Contains("npcs=0", h.Said);          // no bots in the harness
        Assert.Contains("inertia=4", h.Said);       // mass, centre, inertia, aux
        Assert.Contains("done", h.Said);            // the side-effect calls did not throw
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == DebugChannel);
    }

    /// <summary>osAgentSaveAppearance is VeryHigh and needs the agent here; the gate and the absence both answer.</summary>
    [Fact]
    public void OsAgentSaveAppearanceNeedsAnAgentInTheRegion()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            llSay(0, ""saved="" + (string)osAgentSaveAppearance(""" + UUID.Random() + @""", ""outfit""));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("saved=")) && h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("no such agent")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] err=[" +
            string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message)) + "]");

        Assert.Contains("saved=" + UUID.Zero, h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("no such agent"));
    }
}
