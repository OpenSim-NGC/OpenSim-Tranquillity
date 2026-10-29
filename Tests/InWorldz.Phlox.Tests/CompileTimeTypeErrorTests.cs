using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Expressions SL refuses at compile time that Phlox compiled (and then failed on, or ran wrongly). Each is now a
/// line-numbered compile error with the message Halcyon's compiler gave (InWorldz.Phlox.Compiler.SymbolTable: EqOp,
/// RelOp, SubScript, CheckListLiteral, CheckVectorLiteral, CheckRotationLiteral, MethodCall). The comparison rule is
/// Halcyon's: the two sides have the same type after integer-to-float and key/string conversion, and < > do not take
/// strings. The valid neighbours of every case keep compiling and running.
/// Compile-and-run only; no process-wide state, so the class runs in parallel.
/// </summary>
public class CompileTimeTypeErrorTests
{
    private static void AssertRejected(string body, string message, string globals = "")
    {
        var c = PhloxCompiler.Compile(globals + "\ndefault\n{\n    state_entry()\n    {\n" + body + "\n    }\n}\n");
        Assert.True(c.HasErrors(), $"'{body}' compiled; SL rejects it");
        Assert.True(c.Errors.Any(e => e.StartsWith("line ") && e.Contains(message)),
            $"'{body}': expected a line-numbered '{message}', got {c.Report}");
    }

    private static void AssertSays(string body, params string[] expect)
    {
        var r = ExprRunner.RunInDefault(body);
        Assert.True(r.Ok && r.Said.SequenceEqual(expect), $"'{body}': got {r.Describe()} (expect [{string.Join(" | ", expect)}])");
    }

    // ── == and != ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("list l = []; integer r = (l == \"a\");")]
    [InlineData("integer r = (\"a\" != 1);")]
    [InlineData("integer r = (<0,0,0> == 1);")]
    [InlineData("integer r = (<0,0,0> == <0,0,0,1>);")]
    [InlineData("key k; integer r = (k == 1);")]
    public void AnEqualityBetweenDifferentTypesIsRejected(string body)
        => AssertRejected(body, "Type mismatch, equality operators == and != require arguments of the same type");

    [Theory]
    [InlineData("llOwnerSay((string)(1 == 1.0));", "1")]
    [InlineData("llOwnerSay((string)(2.0 != 2));", "0")]
    [InlineData("key k = \"a\"; llOwnerSay((string)(k == \"a\"));", "1")]
    [InlineData("key k = \"a\"; llOwnerSay((string)(\"b\" != k));", "1")]
    [InlineData("llOwnerSay((string)(<1,2,3> == <1,2,3>));", "1")]
    [InlineData("llOwnerSay((string)(\"x\" == \"x\"));", "1")]
    public void AnEqualityBetweenCompatibleTypesStillRuns(string body, string expect) => AssertSays(body, expect);

    [Fact]
    public void AListEqualityStillCompiles()
    {
        var r = ExprRunner.RunInDefault("list a = [1]; list b = [2]; integer r = (a == b); llOwnerSay(\"ran\");");
        Assert.True(r.Ok, r.Describe());
    }

    // ── < > <= >= ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("integer r = (\"a\" < \"b\");", "strings can not be compared with < or >")]
    [InlineData("string s; integer r = (s >= s);", "strings can not be compared with < or >")]
    [InlineData("integer r = (1 < \"b\");", "relational operators require arguments of the same type")]
    [InlineData("integer r = (1.0 > <0,0,0>);", "relational operators require arguments of the same type")]
    public void ARelationalOnStringsOrMixedTypesIsRejected(string body, string message) => AssertRejected(body, message);

    [Theory]
    [InlineData("llOwnerSay((string)(2 < 3.5));", "1")]
    [InlineData("llOwnerSay((string)(2.5 >= 3));", "0")]
    [InlineData("llOwnerSay((string)(4 > 3));", "1")]
    public void ARelationalOnNumbersStillRuns(string body, string expect) => AssertSays(body, expect);

    // ── Subscripts ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("float z = llGetPos().z;", "Use of subscript .z requires a vector or rotation variable")]
    [InlineData("integer i; float f = i.x;", "Use of subscript .x requires a vector or rotation variable")]
    [InlineData("vector v; float f = v.s;", "Invalid subscript .s")]
    [InlineData("vector v; float f = v.w;", "Invalid subscript .w")]
    [InlineData("rotation q; float f = q.w;", "Invalid subscript .w")]
    [InlineData("vector v; v.s = 1.0;", "Invalid subscript .s")]
    [InlineData("vector v; v.w += 1.0;", "Invalid subscript .w")]
    [InlineData("integer i; i.x = 1.0;", "Use of subscript .x requires a vector or rotation variable")]
    public void ABadSubscriptIsRejected(string body, string message) => AssertRejected(body, message);

    [Fact]
    public void GoodSubscriptsStillRun()
        => AssertSays("vector v = <1,2,3>; rotation r = <0,0,0,1>; v.y = 5; r.s += 1; llOwnerSay((string)(v.x + v.y + v.z + r.s));",
            "11.000000");

    // ── List, vector and rotation literals ───────────────────────────────

    [Theory]
    [InlineData("list l = [1, [2]];", "A list can not contain another list")]
    [InlineData("list a = [1]; list l = [a, 2];", "A list can not contain another list")]
    [InlineData("vector v = <\"a\", 0, 0>;", "Vector components must be float or implicitly convertable to float")]
    [InlineData("vector v = <0, 0, [1]>;", "Vector components must be float or implicitly convertable to float")]
    [InlineData("rotation q = <0, 0, 0, \"a\">;", "Rotation components must be float or implicitly convertable to float")]
    [InlineData("rotation q = <0, <1,2,3>, 0, 1>;", "Rotation components must be float or implicitly convertable to float")]
    public void ABadLiteralIsRejected(string body, string message) => AssertRejected(body, message);

    [Fact]
    public void GoodLiteralsStillRun()
        => AssertSays("integer i = 2; vector v = <1, 2.0, i>; rotation r = <0, 0, i, 1.0>; list l = [1, \"a\", v, (key)\"k\"]; " +
                      "llOwnerSay((string)v); llOwnerSay((string)r); llOwnerSay((string)l);",
            "<1.00000, 2.00000, 2.00000>", "<0.00000, 0.00000, 2.00000, 1.00000>", "1a<1.000000, 2.000000, 2.000000>k");

    // ── A function with no value, used as a value; an undefined function ─

    [Theory]
    [InlineData("list l = [llSay(0, \"a\")];")]
    [InlineData("integer i = 1 + llSay(0, \"a\");")]
    [InlineData("integer r = (llSay(0, \"a\") == 1);")]
    [InlineData("integer r = !llSay(0, \"a\");")]
    [InlineData("integer r = llSay(0, \"a\") && 1;")]
    [InlineData("string s = (string)llSay(0, \"a\");")]
    [InlineData("vector v = <llSay(0, \"a\"), 0, 0>;")]
    public void AFunctionWithNoValueUsedAsAValueIsRejected(string body)
    {
        var c = PhloxCompiler.CompileInDefault(body);
        Assert.True(c.HasErrors(), $"'{body}' compiled; SL rejects it");
        Assert.True(c.Errors.Any(e => e.StartsWith("line ")), $"'{body}': no line-numbered error: {c.Report}");
    }

    [Theory]
    [InlineData("integer i = nosuch(1);")]
    [InlineData("nosuch(1);")]
    [InlineData("llOwnerSay((string)nosuch());")]
    public void ACallToAnUndefinedFunctionIsRejectedWithItsLine(string body)
        => AssertRejected(body, "Call to undefined function nosuch()");
}
