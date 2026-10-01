using InWorldz.Phlox.Glue;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script in an in-world prim crashes the compiler.
///
/// <para>
/// Every region start logged
/// <c>ERROR [PhloxCompile]: 4e51f068-...: Object reference not set to an instance of an object.</c>
/// followed by <c>ERROR [PhloxLoader]: Compilation failed for 1ee3b9b1-...</c> — twice, at two
/// separate starts, so it reproduces from the stored asset rather than from anything about that start.
/// </para>
///
/// <para>
/// The script is <c>Fixtures/lmap4.lsl</c>, a four-state lamp from a resident's prim. There is
/// nothing exotic in it, which is the point: whatever the compiler trips over is something ordinary.
/// </para>
///
/// <para>
/// <b>These tests call <see cref="CompilerFrontend"/> directly rather than through
/// <see cref="PhloxCompiler"/>.</b> That helper catches the exception and files it as an error message,
/// which is right for tests about what the compiler <i>says</i> — but it would hide the very thing this
/// file exists to show. A crash must arrive here as a crash, with its stack.
/// </para>
/// </summary>
public class CompilerCrashTests
{
    private readonly ITestOutputHelper _out;
    public CompilerCrashTests(ITestOutputHelper o) => _out = o;

    private static string FixturePath(string name) => Path.Combine(
        Path.GetDirectoryName(typeof(CompilerCrashTests).Assembly.Location)!, "Fixtures", name);

    private static string Lmap4 => File.ReadAllText(FixturePath("lmap4.lsl"));

    /// <summary>
    /// The whole requirement, and it is deliberately loose about which way it is met: a compiler faced
    /// with a script either compiles it or explains what is wrong with it **and where**. What it may not
    /// do is throw. If the construct turns out to be invalid LSL, this test still passes — as long as the
    /// owner is told a line number instead of being handed a null dereference.
    /// </summary>
    [Fact]
    public void TheLampScriptEitherCompilesOrFailsWithALineNumber()
    {
        var listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);

        // No try/catch: a NullReferenceException out of here IS the defect, and the stack trace xunit
        // prints is the finding.
        var compiled = frontend.Compile(Lmap4);

        if (!listener.HasErrors())
        {
            Assert.NotNull(compiled);
            return;
        }

        _out.WriteLine(listener.Report);
        Assert.All(listener.Errors, e => Assert.Matches(@"\d+:\d+|line \d+", e));
    }

    /// <summary>
    /// The construct on its own, reduced from the fixture: four states where the last one returns to
    /// <c>default</c>. If the fixture crashes and this does not, the cause is elsewhere in the script and
    /// this test says so by passing.
    /// </summary>
    [Fact]
    public void ReturningToTheDefaultStateFromAnotherStateCompiles()
    {
        const string src = @"
default
{
    touch_start(integer n) { state onHigh; }
}

state onHigh
{
    touch_start(integer n) { state default; }
}
";
        var listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);
        var compiled = frontend.Compile(src);

        Assert.False(listener.HasErrors(), listener.Report);
        Assert.NotNull(compiled);
    }
}

/// <summary>
/// The owner-visible path has to tell a compiler crash apart from a fault in the
/// script. Before this, both arrived as "script failed to compile" followed by whatever string the
/// compiler produced — so a NullReferenceException read to the resident as a verdict on their code.
/// </summary>
public class CompilerCrashOwnerAlertTests
{
    /// <summary>What a throwing compile actually puts in the listener, produced the real way.</summary>
    private static IReadOnlyList<string> ErrorsFromAThrowingCompile()
    {
        var listener = new PhloxCompiler();
        var frontend = new InWorldz.Phlox.Glue.CompilerFrontend(listener, templatePath: null);
        // A null input stream throws inside Compile; the route to the blanket catch is what matters
        // here, not which exception takes it.
        frontend.Compile((Antlr4.Runtime.ICharStream)null);
        Assert.True(listener.HasErrors(), "the compile was expected to fail");
        return listener.Errors;
    }

    [Fact]
    public void AThrowingCompileIsMarkedAsACrashAndCarriesItsStack()
    {
        var errors = ErrorsFromAThrowingCompile();

        Assert.Contains(errors, InWorldz.Phlox.Types.CompilerCrash.IsCrash);
        // The stack is what makes the region log useful; LogOutputListener writes this at ERROR.
        Assert.Contains(errors, e => e.Contains("CompilerFrontend"));
    }

    [Fact]
    public void TheOwnerIsToldItIsTheCompilerAndNotTheirScript()
    {
        var msg = global::Phlox.ScriptEngine.PhloxCompileErrorReport.Build(
            "Lamp Prim", "lmap4", ErrorsFromAThrowingCompile());

        Assert.Contains("Script lmap4:", msg);
        Assert.Contains("compiler error (not a script syntax error)", msg);
        Assert.Contains("reported to the grid operator", msg);

        // And it must NOT read as a verdict on the script, nor leak a stack to a resident.
        Assert.DoesNotContain("failed to compile", msg);
        Assert.DoesNotContain("   at ", msg);
    }

    [Fact]
    public void AnOrdinaryScriptErrorStillReportsInTheLineNumberForm()
    {
        // The distinction has to cut both ways, or it is just a second wording.
        var msg = global::Phlox.ScriptEngine.PhloxCompileErrorReport.Build(
            "Lamp Prim", "lmap4", new[] { "line 16:12 Unknown state 'onHigh'" });

        Assert.Contains("failed to compile", msg);
        Assert.Contains("line 16:12", msg);
        Assert.DoesNotContain("compiler error (not a script syntax error)", msg);
    }
}
