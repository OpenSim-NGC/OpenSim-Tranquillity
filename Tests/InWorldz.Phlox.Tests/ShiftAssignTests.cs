using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The rule: accept &lt;&lt;= and &gt;&gt;= as an extension; quaternion stays a type.
/// Halcyon compiled them for integers only (templates iilsa / iirsa): x &lt;&lt;= n is x = x &lt;&lt; n, and the result is
/// an integer. Both the statement form and the assignment expression, at the assignment operators' level (right
/// associative, below every other operator). Every other operand type stays a compile error with the type-mismatch
/// message. Compiler and VM only (ExprRunner), no process-wide state, so the class runs in parallel.
/// </summary>
public class ShiftAssignTests
{
    private readonly ITestOutputHelper _out;
    public ShiftAssignTests(ITestOutputHelper o) => _out = o;

    private ExprRunner.Result Run(string body, string globals = "")
    {
        var r = ExprRunner.RunInDefault(body, globals);
        _out.WriteLine(r.Describe());
        Assert.True(r.Ok, r.Describe());
        return r;
    }

    [Fact]
    public void StatementFormShiftsALocalInteger()
    {
        var r = Run(@"
        integer x = 3;
        x <<= 2; llOwnerSay((string)x);
        x >>= 1; llOwnerSay((string)x);");
        Assert.Equal(new[] { "12", "6" }, r.Said);
    }

    [Fact]
    public void StatementFormShiftsAGlobalInteger()
    {
        var r = Run(@"
        g <<= 4; llOwnerSay((string)g);
        g >>= 3; llOwnerSay((string)g);", "integer g = 5;");
        Assert.Equal(new[] { "80", "10" }, r.Said);
    }

    /// <summary>Same answer as x = x &lt;&lt; n and x = x &gt;&gt; n for every count, including 0, 31, 32, 33 and -1,
    /// and for negative values (&gt;&gt; is arithmetic).</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 31)]
    [InlineData(1, 32)]
    [InlineData(1, 33)]
    [InlineData(1, -1)]
    [InlineData(-8, 1)]
    [InlineData(-2147483648, 31)]
    [InlineData(2147483647, 4)]
    public void MatchesTheLongForm(int value, int count)
    {
        var r = Run($@"
        integer a = {value}; integer b = {value}; integer n = {count};
        a <<= n; b = b << n; llOwnerSay((string)a + "" "" + (string)b);
        a = {value}; b = {value};
        a >>= n; b = b >> n; llOwnerSay((string)a + "" "" + (string)b);");
        foreach (string line in r.Said)
        {
            string[] ab = line.Split(' ');
            Assert.Equal(ab[1], ab[0]);
        }
    }

    [Fact]
    public void AssignmentExpressionYieldsTheShiftedIntegerAndStoresIt()
    {
        var r = Run(@"
        integer x = 1;
        integer y = (x <<= 3);
        llOwnerSay((string)x + "","" + (string)y);
        llOwnerSay((string)((x >>= 2) + 100) + "","" + (string)x);
        x = 4;
        integer z = x <<= 1;
        llOwnerSay((string)z + "","" + (string)x);");
        Assert.Equal(new[] { "8,8", "102,2", "8,8" }, r.Said);
    }

    [Fact]
    public void RightAssociativeAndBelowEveryOtherOperator()
    {
        var r = Run(@"
        integer a = 1; integer b = 1;
        a <<= b <<= 2;                 // b becomes 4 first, then a = 1 << 4
        llOwnerSay((string)a + "","" + (string)b);
        integer c = 1;
        c <<= 1 + 2;                   // + binds tighter: shift by 3
        llOwnerSay((string)c);
        integer d = 64;
        d >>= 2 * 2;                   // * binds tighter: shift by 4
        llOwnerSay((string)d);
        integer e = 2; integer f = 3;
        e <<= f += 1;                  // f becomes 4, e = 2 << 4
        llOwnerSay((string)e + "","" + (string)f);
        integer g = 1;
        g <<= 2 << 1;                  // the plain shift operator on the right: shift by 4
        llOwnerSay((string)g);");
        Assert.Equal(new[] { "16,4", "8", "4", "32,4", "16" }, r.Said);
    }

    [Fact]
    public void WorksInForLoopsAndConditions()
    {
        var r = Run(@"
        integer bits = 0;
        integer m;
        for (m = 1; m < 256; m <<= 1) bits++;
        llOwnerSay((string)bits);
        integer v = 1024; integer steps = 0;
        while ((v >>= 1) > 0) steps++;
        llOwnerSay((string)steps);");
        Assert.Equal(new[] { "8", "10" }, r.Said);
    }

    /// <summary>What stays an error: anything but integer &lt;&lt;= integer is still a compile error, saying why.</summary>
    [Theory]
    [InlineData("float f = 1.0; f <<= 1;", "<<=", "float and integer")]
    [InlineData("float f = 8.0; f >>= 1;", ">>=", "float and integer")]
    [InlineData("integer i = 8; i <<= 1.0;", "<<=", "integer and float")]
    [InlineData("integer i = 8; i >>= 2.5;", ">>=", "integer and float")]
    [InlineData("vector v = <1,2,3>; v.x <<= 1;", "<<=", "float and integer")]
    [InlineData("vector v = <1,2,3>; v <<= 1;", "<<=", "vector and integer")]
    [InlineData("rotation q = ZERO_ROTATION; q >>= 1;", ">>=", "rotation and integer")]
    [InlineData("string s = \"a\"; s <<= 1;", "<<=", "string and integer")]
    [InlineData("key k = NULL_KEY; k >>= 1;", ">>=", "key and integer")]
    [InlineData("list l = []; l <<= 1;", "<<=", "list and integer")]
    [InlineData("integer i = 1; i <<= \"2\";", "<<=", "integer and string")]
    [InlineData("integer i = 1; integer j = (i <<= [1]);", "<<=", "integer and list")]
    public void OtherOperandTypesAreACompileErrorWithTheTypes(string body, string op, string types)
    {
        var r = ExprRunner.RunInDefault(body);
        _out.WriteLine(r.Describe());
        Assert.NotNull(r.CompileError);
        Assert.Contains($"Type mismatch: cannot apply '{op}' to {types}", r.CompileError);
    }

    [Theory]
    [InlineData("<<=")]
    [InlineData(">>=")]
    public void AConstantOrALiteralIsNotAssignable(string op)
    {
        Assert.NotNull(ExprRunner.RunInDefault($"PI_BY_TWO {op} 1;").CompileError);
        Assert.NotNull(ExprRunner.RunInDefault($"integer x = (1 {op} 1);").CompileError);
    }

    [Fact]
    public void QuaternionStaysAType()
    {
        var r = Run("quaternion q = <0, 0, 0, 1>; llOwnerSay((string)q);");
        Assert.Equal(new[] { "<0.00000, 0.00000, 0.00000, 1.00000>" }, r.Said);
    }
}
