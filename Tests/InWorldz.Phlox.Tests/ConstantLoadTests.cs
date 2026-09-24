using System.Globalization;
using InWorldz.Phlox.Compiler;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Every constant in <see cref="DefaultConstants"/>, loaded in a running script as its declared
/// type. The expected value is the table's own (hex and decimal parsed as a 32-bit integer),
/// and the value on the VM stack must have the declared type: an integer constant is an
/// <c>int</c>, never the string of its digits.
/// </summary>
public class ConstantLoadTests
{
    private const string Fmt =
        "string fmtv(vector v) { return (string)v.x + \",\" + (string)v.y + \",\" + (string)v.z; }\n" +
        "string fmtr(rotation r) { return (string)r.x + \",\" + (string)r.y + \",\" + (string)r.z + \",\" + (string)r.s; }\n";

    public static IEnumerable<object[]> AllConstants()
        => DefaultConstants.Constants.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    /// <summary>
    /// A table integer value, decimal or hex, as the 32-bit integer LSL gives it. Hex is the bit
    /// pattern (0xFFFFFFFF is -1). A decimal value outside the 32-bit range is -1, as an
    /// oversized integer literal is in LSL (wiki.secondlife.com/wiki/Integer: "an undocumented
    /// way to say -1") and in the assembler; the IW_POWER_* entries are 64-bit group powers and
    /// all load as -1.
    /// </summary>
    public static int ParseTableInt(string text)
    {
        bool neg = text.StartsWith("-");
        string t = neg ? text.Substring(1) : text;
        int v;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = unchecked((int)Convert.ToUInt32(t.Substring(2), 16));
        else if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v))
            return -1;
        return neg ? unchecked(-v) : v;
    }

    /// <summary>A table string value is written with the assembler's escapes (EOF is \n\n\n).</summary>
    private static string Unescape(string text)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i == text.Length - 1) { sb.Append(text[i]); continue; }
            char n = text[++i];
            sb.Append(n switch { 'n' => "\n", 't' => "    ", '"' => "\"", '\\' => "\\", _ => "\\" + n });
        }
        return sb.ToString();
    }

    private static string F(float f) => f.ToString("0.000000", CultureInfo.InvariantCulture);

    private static float[] ParseComponents(string text)
        => text.Trim('<', '>').Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    [Theory]
    [MemberData(nameof(AllConstants))]
    public void Constant_(string name)
    {
        var sym = DefaultConstants.Constants[name];
        string decl, print, expect;
        Type clr;
        if (sym.Type == SymbolTable.INT)
        {
            decl = "integer"; print = "(string)v"; clr = typeof(int);
            expect = ParseTableInt(sym.ConstValue).ToString(CultureInfo.InvariantCulture);
        }
        else if (sym.Type == SymbolTable.FLOAT)
        {
            decl = "float"; print = "(string)v"; clr = typeof(float);
            expect = F(float.Parse(sym.ConstValue, CultureInfo.InvariantCulture));
        }
        else if (sym.Type == SymbolTable.STRING || sym.Type == SymbolTable.KEY)
        {
            // The VM has no separate key value: a key is a string at run time, as a key literal is.
            decl = sym.Type == SymbolTable.KEY ? "key" : "string"; print = "(string)v"; clr = typeof(string);
            expect = Unescape(sym.ConstValue);
        }
        else if (sym.Type == SymbolTable.VECTOR)
        {
            decl = "vector"; print = "fmtv(v)"; clr = typeof(Vector3);
            expect = string.Join(",", ParseComponents(sym.ConstValue).Select(F));
        }
        else if (sym.Type == SymbolTable.ROTATION)
        {
            decl = "rotation"; print = "fmtr(v)"; clr = typeof(Quaternion);
            expect = string.Join(",", ParseComponents(sym.ConstValue).Select(F));
        }
        else
        {
            throw new InvalidOperationException($"{name}: unexpected constant type {sym.Type?.Name}");
        }

        // The constant used directly, stored in a variable of its type, and put in a list.
        var r = ExprRunner.RunInDefault(
            $"llOwnerSay((string){name}); {decl} v = {name}; llOwnerSay({print}); list l = [{name}]; llSetPrimitiveParams(l);",
            Fmt);
        string direct = sym.Type == SymbolTable.VECTOR || sym.Type == SymbolTable.ROTATION ? null : expect;
        Assert.True(r.Ok, $"{name} ({decl} {sym.ConstValue}): {r.Describe()}");
        if (direct != null)
            Assert.True(r.Said[0] == direct, $"{name} ({decl} {sym.ConstValue}): (string){name} is \"{r.Said[0]}\" (expect \"{direct}\")");
        Assert.True(r.Said[1] == expect, $"{name} ({decl} {sym.ConstValue}): {decl} v = {name} prints \"{r.Said[1]}\" (expect \"{expect}\")");
        var call = r.Calls.Last(c => c.Name == "llSetPrimitiveParams");
        var element = ((LSLList)call.Args[0]).Data[0];
        Assert.True(element?.GetType() == clr,
            $"{name} ({decl} {sym.ConstValue}): [{name}] holds a {element?.GetType().Name ?? "null"} (expect {clr.Name})");
    }

    [Theory]
    [MemberData(nameof(AllConstants))]
    public void ConstantIsNotAssignableToAnUnrelatedType_(string name)
    {
        var sym = DefaultConstants.Constants[name];
        // integer, float, vector, rotation -> string needs a cast; string and key -> integer too.
        string target = sym.Type == SymbolTable.STRING || sym.Type == SymbolTable.KEY ? "integer" : "string";
        var c = PhloxCompiler.CompileInDefault($"        {target} x = {name};");
        Assert.True(c.HasErrors(), $"{target} x = {name}; compiled, so {name} is not typed {sym.Type?.Name}");
    }

    [Fact]
    public void DebugChannelReachesLlSayAsAnInteger()
    {
        var r = ExprRunner.RunInDefault("llSay(DEBUG_CHANNEL, \"x\"); llSay(PUBLIC_CHANNEL, \"y\");");
        Assert.True(r.Ok, r.Describe());
        var says = r.Calls.Where(c => c.Name == "llSay").ToList();
        Assert.Equal(2147483647, Assert.IsType<int>(says[0].Args[0]));
        Assert.Equal(0, Assert.IsType<int>(says[1].Args[0]));
    }

    [Fact]
    public void HexFlagConstantsWorkInBitwiseExpressions()
    {
        var r = ExprRunner.RunInDefault(
            "integer f = PARCEL_FLAG_ALLOW_SCRIPTS | PARCEL_FLAG_USE_ACCESS_GROUP; " +
            "llOwnerSay((string)((f & PARCEL_FLAG_ALLOW_SCRIPTS) != 0)); llOwnerSay((string)(f & REGION_FLAG_SANDBOX));");
        int f = ParseTableInt(DefaultConstants.Constants["PARCEL_FLAG_ALLOW_SCRIPTS"].ConstValue)
            | ParseTableInt(DefaultConstants.Constants["PARCEL_FLAG_USE_ACCESS_GROUP"].ConstValue);
        int sandbox = ParseTableInt(DefaultConstants.Constants["REGION_FLAG_SANDBOX"].ConstValue);
        var expect = new[] { "1", (f & sandbox).ToString(CultureInfo.InvariantCulture) };
        Assert.True(r.Ok && r.Said.SequenceEqual(expect), $"got {r.Describe()} (expect [{string.Join(" | ", expect)}])");
    }

    /// <summary>wiki.secondlife.com/wiki/TOUCH_INVALID_FACE: "integer TOUCH_INVALID_FACE = 0xFFFFFFFF", which is -1.</summary>
    [Fact]
    public void TouchInvalidFaceIsMinusOne()
    {
        var r = ExprRunner.RunInDefault("llOwnerSay((string)TOUCH_INVALID_FACE);");
        Assert.True(r.Ok && r.Said.SequenceEqual(new[] { "-1" }), $"got {r.Describe()} (expect [-1])");
    }

    [Fact]
    public void TrueAndPiInAListKeepTheirTypes()
    {
        var r = ExprRunner.RunInDefault("llSetPrimitiveParams([TRUE, PI]); llOwnerSay((string)PI);");
        Assert.True(r.Ok, r.Describe());
        var data = ((LSLList)r.Calls.Single(c => c.Name == "llSetPrimitiveParams").Args[0]).Data;
        Assert.IsType<int>(data[0]);
        Assert.IsType<float>(data[1]);
        Assert.Equal("3.141593", r.Said[0]); // CAST: (string)PI has 6 decimals
    }
}
