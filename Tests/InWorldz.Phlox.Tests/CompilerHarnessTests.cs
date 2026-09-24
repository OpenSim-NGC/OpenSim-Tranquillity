using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>The compile harness itself: a valid script compiles clean, a broken one reports.</summary>
public class CompilerHarnessTests
{
    [Fact]
    public void AValidScriptCompilesWithoutErrors()
    {
        var c = PhloxCompiler.CompileInDefault("        llOwnerSay(\"hello\");");
        Assert.False(c.HasErrors(), c.Report);
    }

    [Fact]
    public void AnUndeclaredVariableIsReported()
    {
        var c = PhloxCompiler.CompileInDefault("        llOwnerSay((string)nosuchvariable);");
        Assert.True(c.HasErrors());
    }
}
