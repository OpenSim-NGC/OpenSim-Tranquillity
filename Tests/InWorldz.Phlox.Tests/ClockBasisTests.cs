using System.Reflection;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// One clock for the engine.
///
/// <para>
/// The scheduler used to compare its queues against
/// <c>(ulong)OpenSim.Framework.Util.EnvironmentTickCount()</c> — system uptime <b>masked to 30 bits</b>
/// (<c>Util.cs:3618-3623</c>). At every <c>0x40000000</c> boundary, which is a shade over 12.4 days of
/// uptime, that value drops back to nearly zero: below every queued <c>ReadyOn</c>. Every timer and
/// every sleep on the region then stalls until it climbs back past them — up to another 12 days.
/// </para>
///
/// <para>
/// These tests step the clock across exactly that boundary. Without a settable source they would need
/// a machine to be up for twelve days to fail, which is why the defect survived.
/// </para>
/// </summary>
[Collection("phlox-state")]
public class ClockBasisTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public ClockBasisTests(ITestOutputHelper o) => _out = o;

    /// <summary>Just under the 30-bit mask boundary — where a masked clock is about to fall over.</summary>
    private const ulong JustBelowTheBoundary = 0x3FFFFF00;

    /// <summary>Past it: a real clock reads this as later, a masked one reads it as ~0.</summary>
    private const ulong JustAfterTheBoundary = 0x40000C00;

    private ulong _now = JustBelowTheBoundary;

    public void Dispose() => InWorldz.Phlox.Util.Clock.SetSourceForTesting(null);

    private void UseTestClock() => InWorldz.Phlox.Util.Clock.SetSourceForTesting(() => _now);

    [Fact]
    public void TheClockIsMonotonicAcrossTheThirtyBitBoundary()
    {
        UseTestClock();
        var before = InWorldz.Phlox.Util.Clock.Now;
        _now = JustAfterTheBoundary;
        var after = InWorldz.Phlox.Util.Clock.Now;

        _out.WriteLine($"before={before} after={after} delta={(long)after - (long)before}");
        Assert.True(after > before,
            "the engine clock must not go backwards at the 30-bit boundary; the masked one did");
        Assert.Equal(0xD00UL, after - before);
    }

    /// <summary>
    /// A timer armed just below the boundary with a 3000 ms interval must fire once the clock passes
    /// its wake-up time, and not before. On the masked clock the wake-up (0x40000AC0) was unreachable:
    /// "now" wrapped to nearly zero and stayed below it for another twelve days.
    /// </summary>
    [Fact]
    public void ATimerArmedBelowTheBoundaryFiresAfterItAndNotBefore()
    {
        UseTestClock();

        ulong armedAt = InWorldz.Phlox.Util.Clock.Now;
        ulong readyOn = armedAt + 3000;

        // Not yet: the clock has not reached the wake-up.
        _now = armedAt + 2999;
        Assert.False(InWorldz.Phlox.Util.Clock.Now >= readyOn, "fired early");

        // Stepped past the boundary AND past the interval: it is due.
        _now = JustAfterTheBoundary;
        _out.WriteLine($"armedAt={armedAt} readyOn={readyOn} now={InWorldz.Phlox.Util.Clock.Now}");
        Assert.True(InWorldz.Phlox.Util.Clock.Now >= readyOn,
            "a timer armed before the 30-bit boundary must still be due after it");
    }

    /// <summary>
    /// The master loop's wait: <c>waitMs = wakeAt - now</c>. With one clock this stays sane across the
    /// boundary. With the masked one it became enormous — a wait of up to twelve days.
    /// </summary>
    [Fact]
    public void TheMasterLoopWaitStaysSaneAcrossTheBoundary()
    {
        UseTestClock();
        ulong wakeAt = InWorldz.Phlox.Util.Clock.Now + 3000;

        long waitBefore = (long)wakeAt - (long)InWorldz.Phlox.Util.Clock.Now;
        _now = JustAfterTheBoundary;
        long waitAfter = (long)wakeAt - (long)InWorldz.Phlox.Util.Clock.Now;

        _out.WriteLine($"waitBefore={waitBefore}ms waitAfter={waitAfter}ms");
        Assert.Equal(3000, waitBefore);
        // Past the wake-up, so non-positive: the loop runs immediately rather than sleeping.
        Assert.True(waitAfter <= 0, $"waitMs went to {waitAfter}ms across the boundary");
        // The specific disaster: a masked clock made this ~12 days.
        Assert.True(waitAfter > -1_000_000, $"waitMs is implausible: {waitAfter}ms");
    }

    /// <summary>
    /// The other half of the old bug, and the reason the two clocks disagreed: this class used to
    /// return <c>(UInt64)Environment.TickCount</c> off Windows. That counter is signed 32-bit and goes
    /// negative at 24.9 days, and the cast turns it into ~1.8e19 — a wake-up time eighteen quintillion
    /// milliseconds away.
    /// </summary>
    [Fact]
    public void TheRealClockIsNowhereNearTheThirtyTwoBitDangerZone()
    {
        InWorldz.Phlox.Util.Clock.SetSourceForTesting(null);
        ulong real = InWorldz.Phlox.Util.Clock.Now;

        // TickCount64 is milliseconds since boot: always representable, never the 1.8e19 the old cast
        // produced from a negative Int32.
        Assert.True(real < (ulong)long.MaxValue, $"clock returned {real}, which is the old cast bug");
        Assert.Equal((ulong)Environment.TickCount64 / 1000, real / 1000);
    }
}
