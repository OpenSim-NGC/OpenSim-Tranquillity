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

    [Fact]
    public void DuplicateDefinitionsKeepTheIdentifierLineAndColumn()
    {
        var c = PhloxCompiler.Compile(
            "integer first;\n  integer first;\ndefault { state_entry() {} }");

        Assert.True(c.HasErrors());
        Assert.Contains(c.Errors, error => error.StartsWith("line 2:10 ") && error.Contains("first"));
    }

    [Fact]
    public void MissingReturnsKeepTheFunctionNameLineAndColumn()
    {
        var c = PhloxCompiler.Compile(
            "\n  integer missing(integer x) { if (x) return 1; }\ndefault { state_entry() {} }");

        Assert.True(c.HasErrors());
        Assert.Contains("line: 2:10 missing(): Not all control paths return a value", c.Errors);
    }

    [Fact]
    public void CompleteReturnBranchesStillCompile()
    {
        var c = PhloxCompiler.Compile(
            "integer complete(integer x) { if (x) return 1; else return 2; }\n" +
            "default { state_entry() { llOwnerSay((string)complete(1)); } }");

        Assert.False(c.HasErrors(), c.Report);
    }
}
