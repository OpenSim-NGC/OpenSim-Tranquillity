using System.Text.RegularExpressions;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The "phlox status" console lines operators read: they say what the state means and what to do, in words, and carry no
/// internal tracking ids.
/// </summary>
public class StatusTextTests
{
    private static readonly Regex TrackingId = new(@"[A-Z]+-\d+");

    [Fact]
    public void AParcelHoldSaysThePausedScriptWaitsForTheParcel()
    {
        string text = PhloxEngine.HeldText("Parcel");
        Assert.Equal("  HELD: Parcel (the parcel does not allow this script; paused until it does, not stopped)", text);
        Assert.DoesNotMatch(TrackingId, text);
    }

    [Fact]
    public void ATerminatedScriptSaysItStaysStoppedAndHowToStartIt()
    {
        string text = PhloxEngine.TerminatedLine("llDie");
        Assert.Equal("  terminated    : llDie  (stays stopped; reset it, or tick Running, to start it fresh)", text);
        Assert.DoesNotMatch(TrackingId, text);
    }

    [Fact]
    public void NoHoldSaysNothingAndAStateLoadHoldKeepsItsNote()
    {
        Assert.Equal("", PhloxEngine.HeldText(null));
        Assert.Equal("  HELD: StateLoadFailed (state load failed - row kept, never run or saved this process; restart to retry)",
            PhloxEngine.HeldText("StateLoadFailed"));
    }
}
