using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A local variable is in scope from the end of its declaration onward (SL's rule). A use before that point, or inside
/// the declaration's own initialiser, means the same name one scope out: an outer block's local, a parameter, a global.
/// With nothing of that name it is a compile error, worded as Halcyon worded it
/// (InWorldz.Phlox.Compiler.SymbolTable.EnsureResolve in Halcyon's compiler).
/// Compile-and-run only; no process-wide state, so the class runs in parallel.
/// </summary>
public class UseBeforeDefineTests
{
    private static void AssertSays(ExprRunner.Result r, params string[] expect)
        => Assert.True(r.Ok && r.Said.SequenceEqual(expect),
            $"got {r.Describe()} (expect [{string.Join(" | ", expect)}])");

    [Fact]
    public void AReadBeforeTheLocalIsDeclaredReadsTheGlobal()
        => AssertSays(ExprRunner.RunInDefault(
            "llOwnerSay((string)count); integer count = 1; llOwnerSay((string)count);",
            "integer count = 5;"), "5", "1");

    [Fact]
    public void AnInitialiserThatNamesItsOwnVariableReadsTheGlobal()
        => AssertSays(ExprRunner.RunInDefault(
            "integer x = x + 1; llOwnerSay((string)x);",
            "integer x = 3;"), "4");

    [Fact]
    public void AnInitialiserThatNamesItsOwnVariableReadsTheParameter()
        => AssertSays(ExprRunner.RunLsl(
            "twice(integer x) { { integer x = x * 2; llOwnerSay((string)x); } llOwnerSay((string)x); }\n" +
            "default { state_entry() { twice(21); } }\n"), "42", "21");

    [Fact]
    public void AReadBeforeAnInnerLocalReadsTheOuterLocal()
        => AssertSays(ExprRunner.RunInDefault(
            "integer a = 1; { llOwnerSay((string)a); integer a = 2; llOwnerSay((string)a); } llOwnerSay((string)a);"),
            "1", "2", "1");

    [Fact]
    public void AStoreBeforeTheLocalIsDeclaredStoresTheGlobal()
        => AssertSays(ExprRunner.RunLsl(
            "integer g = 0;\n" +
            "show() { llOwnerSay((string)g); }\n" +
            "default { state_entry() { g = 7; show(); g++; show(); g += 2; show(); integer g = 1; llOwnerSay((string)g); show(); } }\n"),
            "7", "8", "10", "1", "10");

    [Theory]
    [InlineData("llOwnerSay((string)y); integer y = 1;")]
    [InlineData("integer z = z;")]
    [InlineData("w = 2; integer w;")]
    [InlineData("{ llOwnerSay((string)v); } integer v = 1;")]
    public void AUseWithNothingOfThatNameInScopeIsACompileError(string body)
    {
        var c = PhloxCompiler.CompileInDefault(body);
        Assert.True(c.HasErrors(), $"'{body}' compiled; SL rejects it");
        Assert.Contains(c.Errors, e => e.Contains("can not be used before it is defined") && e.StartsWith("line "));
    }

    [Fact]
    public void AnOrdinaryLocalIsUnchanged()
        => AssertSays(ExprRunner.RunInDefault(
            "integer i = 2; integer j = i * 3; i = j + i; llOwnerSay((string)i); llOwnerSay((string)j);",
            "integer i = 100;"), "8", "6");
}
