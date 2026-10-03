using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script that will not compile must reach its owner, not only the region log.
/// Before this, <c>LogOutputListener.Error</c> wrote one line to the log and that was the whole of
/// it — the resident whose object was broken was never told and the object gave no sign. Both in-world
/// failures that started this were found by reading the startup log.
/// </summary>
public class CompileErrorToOwnerTests
{
    [Fact]
    public void TheMessageNamesTheObjectTheScriptAndEveryError()
    {
        var msg = PhloxCompileErrorReport.Build(
            "ManholeAccess Prim", "ManholeAccess",
            new[] { "line 16:12 Function 'osTeleportAgent' expects 4 arguments, got 3" });

        Assert.Contains("ManholeAccess Prim", msg);
        Assert.Contains("[ManholeAccess]", msg);
        Assert.Contains("failed to compile", msg);
        Assert.Contains("line 16:12", msg);
        Assert.Contains("osTeleportAgent", msg);
    }

    [Fact]
    public void ManyErrorsAreStillOneMessage()
    {
        // A script with twenty errors must not be twenty dialogs.
        var errors = new List<string>();
        for (var i = 1; i <= 20; i++) errors.Add($"line {i}:0 something wrong");

        var msg = PhloxCompileErrorReport.Build("Prim", "Script", errors);

        Assert.Equal(2, msg.Split("failed to compile").Length);   // the phrase appears exactly once
        Assert.Contains("... and 10 more", msg);
        Assert.Contains("region log", msg);
        Assert.Contains("line 10:0", msg);
        Assert.DoesNotContain("line 11:0", msg);
    }

    [Fact]
    public void AnEmptyErrorListSaysOnlyThatItFailed()
    {
        var msg = PhloxCompileErrorReport.Build("Prim", "Script", new string[0]);
        Assert.Equal("Prim [Script]: script failed to compile", msg);
    }

    [Fact]
    public void AMissingNameDoesNotProduceABlankMessage()
    {
        var msg = PhloxCompileErrorReport.Build(null, null, new[] { "boom" });
        Assert.Contains("Object", msg);
        Assert.Contains("[Script]", msg);
        Assert.Contains("boom", msg);
    }

    [Fact]
    public void ANullPartIsSilentRatherThanThrowing()
    {
        // A missing notification must never take down a script load.
        PhloxCompileErrorReport.ToOwner(null, "Script", new[] { "boom" });
    }
}
