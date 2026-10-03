using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The remaining stubs: sit flags, llMinEventDelay, the memory profiler, and
/// llRequestSimulatorData beyond the local region. Same method as 7a - wiki page first, upstream
/// body where one exists, Phlox conventions kept - and every test here is red on the tree before
/// the port.
/// </summary>
[Collection("phlox-state")]
public class RemainingStubTests
{
    private readonly ITestOutputHelper _out;
    public RemainingStubTests(ITestOutputHelper o) => _out = o;

    private static string Only(IReadOnlyList<string> said, string prefix)
        => said.FirstOrDefault(s => s.StartsWith(prefix)) ?? "(not said)";

    // ------------------------------------------------------------------ sit flags

    /// <summary>
    /// wiki: llSetLinkSitFlags "sets flags on the link's sittarget", llGetLinkSitFlags reads them.
    /// ALLOW_UNSIT and SCRIPTED_ONLY land on the part properties the sit path honours; NO_COLLIDE and
    /// NO_DAMAGE are stored and read back; SIT_TARGET is read-only and comes from the sit target.
    /// </summary>
    [Fact]
    public void SitFlagsSetOnTheLinkReadBackAndReachThePart()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default { state_entry() {
            llSetLinkSitFlags(LINK_THIS, SIT_FLAG_ALLOW_UNSIT | SIT_FLAG_NO_DAMAGE);
            llSay(0, ""flags="" + (string)llGetLinkSitFlags(LINK_THIS));
            llSitTarget(<0,0,0.5>, ZERO_ROTATION);
            llSay(0, ""withtarget="" + (string)llGetLinkSitFlags(LINK_THIS));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("withtarget=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        Assert.Contains("flags=34", h.Said);             // 0x02 | 0x20
        Assert.Contains("withtarget=35", h.Said);         // ... | 0x01 once a sit target exists
        // The bits the region honours today went where the sit path looks for them.
        Assert.True(h.Prim.AllowUnsit);
        Assert.False(h.Prim.ScriptedSitOnly);
    }

    // ------------------------------------------------------------------ llMinEventDelay

    /// <summary>
    /// wiki: "Set the minimum time between events being handled" - a floor between handler starts,
    /// events inside the window queued. Two touch_starts posted 10 ms apart with delay=1.0 must be
    /// handled at least 1 s apart, and BOTH must be handled - nothing is dropped.
    /// <para>
    /// The engine holds the floor on its own clock (Clock.Now, Environment.TickCount64 by default), which
    /// on Windows advances in 15-16 ms steps; the script reads llGetTime from DateTime.UtcNow. Measured
    /// that way, a floor the engine kept exactly could read up to one step short of 1.0 s (0.987 s was
    /// seen). So for this test the engine's clock is DateTime.UtcNow in whole milliseconds - the same
    /// clock llGetTime reads - and the gap is then short of the floor by at most 1 ms of rounding.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoTouchesTenMillisecondsApartAreHandledAtLeastOneSecondApart()
    {
        InWorldz.Phlox.Util.Clock.SetSourceForTesting(() => (ulong)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond));
        try
        {
            using var h = new SchedulerHarness();
            var item = h.RezScript(@"default {
                state_entry() { llMinEventDelay(1.0); llResetTime(); llSay(0, ""armed""); }
                touch_start(integer n) { llSay(0, ""t="" + (string)llGetTime()); }
            }");
            h.PumpUntil(() => h.Said.Contains("armed"));
            Assert.Contains("armed", h.Said);

            h.PostTouch(item);
            h.PumpFor(TimeSpan.FromMilliseconds(10));
            h.PostTouch(item);
            h.PumpFor(TimeSpan.FromSeconds(2.5));
            h.PumpUntil(() => h.Said.Count(s => s.StartsWith("t=")) >= 2);

            var times = h.Said.Where(s => s.StartsWith("t=")).Select(s => float.Parse(s[2..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            _out.WriteLine("touch handler starts at: " + string.Join(", ", times));
            Assert.Equal(2, times.Count);                     // queued, not dropped
            Assert.True(times[1] - times[0] >= 0.99f, $"handled {times[1] - times[0]:F3}s apart; the floor is 1.0s");
        }
        finally
        {
            InWorldz.Phlox.Util.Clock.SetSourceForTesting(null);
        }
    }

    // ------------------------------------------------------------------ profiler

    /// <summary>
    /// wiki: PROFILE_SCRIPT_MEMORY starts recording; after PROFILE_NONE, llGetSPMaxMemory "will return
    /// the most memory used at any one time". Allocating a large list inside the window must raise the
    /// peak above what was used before it; before this the answer was a constant 16384.
    /// </summary>
    [Fact]
    public void TheProfilerReportsThePeakOfMemoryUsedInsideTheWindow()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default { state_entry() {
            llSay(0, ""before="" + (string)llGetSPMaxMemory());
            llScriptProfiler(PROFILE_SCRIPT_MEMORY);
            integer base = llGetSPMaxMemory();
            list big; integer i;
            for (i = 0; i < 400; i++) big += [""0123456789abcdef0123456789abcdef""];
            llScriptProfiler(PROFILE_NONE);
            integer peak = llGetSPMaxMemory();
            llSay(0, ""base="" + (string)base);
            llSay(0, ""peak="" + (string)peak);
            llSay(0, ""grew="" + (string)(peak > base + 4000));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("grew=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        Assert.Contains("before=0", h.Said);              // never profiled: no peak, not 16384
        Assert.Contains("grew=1", h.Said);
    }

    // ------------------------------------------------------------------ llRequestSimulatorData

    /// <summary>wiki: the local region answers "up", a rating string, and a global-position vector.</summary>
    [Fact]
    public void TheLocalRegionAnswersWithTheWikisStrings()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default {
            state_entry() {
                llRequestSimulatorData(llGetRegionName(), DATA_SIM_STATUS);
                llRequestSimulatorData(llGetRegionName(), DATA_SIM_RATING);
                llRequestSimulatorData(llGetRegionName(), DATA_SIM_POS);
            }
            dataserver(key q, string d) { llSay(0, ""ds="" + d); }
        }");
        h.PumpUntil(() => h.Said.Count(s => s.StartsWith("ds=")) >= 3);   // three calls, 1 s sleep each
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        Assert.Contains("ds=up", h.Said);
        Assert.Contains(h.Said, s => s == "ds=PG" || s == "ds=MATURE" || s == "ds=ADULT" || s == "ds=UNKNOWN");
        // A vector, in metres: the test region sits at 1000,1000 region units.
        Assert.Contains(h.Said, s => s.StartsWith("ds=<256000") );
    }

    /// <summary>
    /// wiki: an unknown region answers DATA_SIM_STATUS "unknown" and DATA_SIM_RATING "UNKNOWN", the values in the
    /// wiki's tables (as YEngine answers). Before this the call returned NULL_KEY and raised nothing at all.
    /// </summary>
    [Fact]
    public void AnUnknownRegionStillRaisesTheDataserverEvent()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default {
            state_entry() {
                key q = llRequestSimulatorData(""No Such Region"", DATA_SIM_STATUS);
                llSay(0, ""key="" + (string)(q != NULL_KEY));
                llRequestSimulatorData(""No Such Region"", DATA_SIM_RATING);
            }
            dataserver(key q, string d) { llSay(0, ""ds="" + d); }
        }");
        h.PumpUntil(() => h.Said.Contains("key=1") && h.Said.Contains("ds=unknown") && h.Said.Contains("ds=UNKNOWN"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        Assert.Contains("key=1", h.Said);
        Assert.Contains("ds=unknown", h.Said);
        Assert.Contains("ds=UNKNOWN", h.Said);
    }
}
