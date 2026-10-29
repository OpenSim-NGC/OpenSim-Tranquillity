using System.Globalization;
using InWorldz.Phlox.Compiler;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A float literal keeps its exact 32-bit value. SL wiki Float: "The LSL "float" type is a floating point data
/// type that uses 32 bit in IEEE-754 form." "The valid range is 1.401298464E-45 to 3.402823466E+38". "Floats can be
/// specified in scientific notation such as 2.6E-5." Before, GenVisitor.FormatFloat wrote the literal for the assembler
/// with a custom format that keeps 7 significant digits: 2147483520.0 became 2147484000.0 (read back as 2147483904) and
/// 1.17549435E-38 became 0.0.
///
/// The oracle is the run-time cast (float)"text", which parses the same text exactly (float.Parse); run-time
/// (string) of a float still prints 7 significant digits (Util/Encoding.cs, not changed here), so the exact value is shown
/// through arithmetic, comparison and (integer). Compiler and VM only, parallel.
/// </summary>
public class FloatLiteralTests
{
    private readonly ITestOutputHelper _out;
    public FloatLiteralTests(ITestOutputHelper o) => _out = o;

    /// <summary>GenVisitor.FormatFloat, the literal as the compiler writes it for the assembler (private; by reflection).</summary>
    private static string FormatFloat(string literal)
        => (string)typeof(GenVisitor).GetMethod("FormatFloat", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { literal })!;

    private ExprRunner.Result Run(string body, string globals = "")
    {
        var r = ExprRunner.RunInDefault(body, globals);
        _out.WriteLine(r.Describe());
        Assert.True(r.Ok, r.Describe());
        return r;
    }

    /// <summary>The literal equals the exact parse of its own text, as a local, a global, a vector and a rotation part,
    /// and a negated literal. (List syscalls are stubs in ExprRunner, so lists are not checked here.)</summary>
    [Theory]
    [InlineData("2147483520.0")]
    [InlineData("2147483647.0")]
    [InlineData("16777215.0")]
    [InlineData("16777216.0")]
    [InlineData("16777217.0")]
    [InlineData("16777218.0")]
    [InlineData("33554434.0")]
    [InlineData("3.40282347E+38")]
    [InlineData("340282346638528859811704183484516925440.0")]
    [InlineData("1.17549435E-38")]
    [InlineData("1.401298464E-45")]
    [InlineData("0.1")]
    [InlineData("0.123456789")]
    [InlineData("123456.789")]
    [InlineData("2.6E-5")]
    [InlineData("1e10")]
    [InlineData("1.5E+3")]
    [InlineData("123456789e-3")]
    [InlineData(".000000000000000001")]
    [InlineData("7.")]
    public void TheLiteralIsTheExactFloat(string lit)
    {
        var r = Run($@"
        float f = {lit};
        float t = (float)""{lit}"";
        llOwnerSay(""local "" + (string)(f == t));
        llOwnerSay(""global "" + (string)(g == t));
        vector v = <{lit}, 0.0, {lit}>;
        llOwnerSay(""vector "" + (string)(v.x == t && v.z == t));
        rotation q = <0.0, {lit}, 0.0, {lit}>;
        llOwnerSay(""rotation "" + (string)(q.y == t && q.s == t));
        llOwnerSay(""negated "" + (string)(-{lit} == -t));", $"float g = {lit};");
        Assert.Equal(new[] { "local 1", "global 1", "vector 1", "rotation 1", "negated 1" }, r.Said);
    }

    [Fact]
    public void TwoToTheThirtyOneMinus128ThroughArithmeticAndInteger()
    {
        // 2147483520 = 2^31 - 128 is a float; the old text 2147484000 read back as 2147483904.
        var r = Run(@"
        llOwnerSay((string)(integer)2147483520.0);
        llOwnerSay((string)(2147483520.0 - 2147483000.0));
        llOwnerSay((string)(2147483520.0 - 2147483392.0));
        llOwnerSay((string)(integer)(2147483520.0 / 128.0));");
        Assert.Equal(new[] { "2147483520", "512.000000", "128.000000", "16777215" }, r.Said);
    }

    [Fact]
    public void AroundTwoToTheTwentyFour()
    {
        // Floats are exact to 16777216 (wiki: "Only the values between -16,777,216 and 16,777,216 are precise"); above
        // it they step by 2. 16777217.0 rounds to 16777216 (ties to even); 16777215.0 was 16777220 before.
        var r = Run(@"
        llOwnerSay((string)(integer)16777215.0);
        llOwnerSay((string)(integer)16777216.0);
        llOwnerSay((string)(integer)16777217.0);
        llOwnerSay((string)(integer)16777218.0);
        llOwnerSay((string)(integer)16777219.0);
        llOwnerSay((string)(16777215.0 + 1.0 == 16777216.0));
        llOwnerSay((string)(16777217.0 == 16777216.0));");
        Assert.Equal(new[] { "16777215", "16777216", "16777216", "16777218", "16777220", "1", "1" }, r.Said);
    }

    [Fact]
    public void LargestAndSmallestNormalFloats()
    {
        var r = Run(@"
        float big = 3.40282347E+38;
        float small = 1.17549435E-38;
        llOwnerSay((string)(big > 3.4028233E+38));          // it is not the next float down
        llOwnerSay((string)(big / 2.0 * 2.0 == big));
        llOwnerSay((string)(small > 0.0));                    // was 0.0
        llOwnerSay((string)(small * 1.0E38 > 1.17 && small * 1.0E38 < 1.18));
        llOwnerSay((string)(integer)(small * 1.0E38 * 1000000.0));");
        Assert.Equal(new[] { "1", "1", "1", "1", "1175494" }, r.Said);
    }

    [Fact]
    public void NegativeZero()
    {
        // A minus sign on a literal is part of the literal (GenVisitor.VisitUnaryMinus). -0.0 is a zero: equal to 0.0,
        // and adding it changes nothing; multiplying keeps the sign rule (-0 * -1 = +0).
        var r = Run(@"
        float nz = -0.0;
        llOwnerSay((string)(nz == 0.0));
        llOwnerSay((string)(nz + 5.0));
        llOwnerSay((string)(nz * -1.0 == 0.0));
        llOwnerSay((string)(integer)nz);");
        Assert.Equal(new[] { "1", "5.000000", "1", "0" }, r.Said);
    }

    [Fact]
    public void LiteralsWithExponents()
    {
        var r = Run(@"
        llOwnerSay((string)2.6E-5);
        llOwnerSay((string)1.5E+3);
        llOwnerSay((string)(integer)1e9);
        llOwnerSay((string)(integer)123456789e-1);
        llOwnerSay((string)(2.5e2 + 0.5E1));
        llOwnerSay((string)(integer)(1.0E-3 * 1.0E3));");
        Assert.Equal(new[] { "0.000026", "1500.000000", "1000000000", "12345679", "255.000000", "1" }, r.Said);
    }

    /// <summary>The assembler text itself: plain decimal (Assembler.g4 FLOAT has no exponent), always with a point, and it
    /// parses back to the same float bits.</summary>
    [Theory]
    [InlineData("2147483520.0", "2147483500.0")]   // shortest digits; the same float
    [InlineData("0.5", "0.5")]
    [InlineData("10", "10.0")]
    [InlineData("1e10", "10000000000.0")]
    [InlineData("2.6E-5", "0.000026")]
    [InlineData("1.17549435E-38", "0.000000000000000000000000000000000000011754944")]
    [InlineData("3.40282347E+38", "340282350000000000000000000000000000000.0")]
    [InlineData("-0.0", "-0.0")]
    [InlineData("-16777217.0", "-16777216.0")]
    public void AssemblerTextIsPlainDecimalAndRoundTrips(string literal, string expected)
    {
        string text = FormatFloat(literal);
        Assert.Equal(expected, text);
        float want = float.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
        float got = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        Assert.Equal(BitConverter.SingleToInt32Bits(want), BitConverter.SingleToInt32Bits(got));
    }

    /// <summary>Every float bit pattern class the assembler can meet, round tripped: subnormals, powers of two, the
    /// neighbours of 2^24 and 2^31, and a spread of random finite values.</summary>
    [Fact]
    public void EveryFiniteFloatRoundTripsThroughTheAssemblerText()
    {
        var values = new List<float> { float.Epsilon, float.MaxValue, 1.17549435E-38f, 16777216f, 16777218f, 2147483520f, 2147483648f };
        for (int e = -149; e < 128; e++) values.Add(MathF.Pow(2, e));
        var rnd = new Random(63);
        while (values.Count < 20000)
        {
            float f = BitConverter.Int32BitsToSingle(rnd.Next());
            if (float.IsFinite(f)) values.Add(f);
        }
        foreach (float f in values)
        {
            string text = FormatFloat(f.ToString("R", CultureInfo.InvariantCulture));
            Assert.DoesNotContain("E", text);
            Assert.Contains(".", text);
            float back = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.True(BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits(back), $"{f:R} -> {text} -> {back:R}");
        }
    }
}
