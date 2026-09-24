using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Expression conformance matrix. Every case compiles and RUNS a script and compares what it
/// said with the value LSL gives. Each case is run on its own, and all of them together in
/// one script (<see cref="AllCasesTogetherInOneScript"/>).
///
/// <para>Sources for the expected values (each case names one):</para>
/// <list type="bullet">
/// <item><b>OPS</b>: wiki.secondlife.com/wiki/LSL_Operators. Its precedence table, highest first:
///   () [] (type); ! ~ ++ --; * / %; -; +; &lt;&lt; &gt;&gt;; &lt; &lt;= &gt; &gt;=; == !=; &amp;; ^; |; &amp;&amp; ||;
///   = += -= *= /= %=. "&amp;&amp; and || have the same precedence"; "both operands are always
///   evaluated" (no short-circuit); "the order of evaluation is from right to left"; % "has
///   the same sign as the first operand". The table lists - and + on separate rows; for the
///   integer cases below either reading gives the same value.</item>
/// <item><b>INT</b>: wiki.secondlife.com/wiki/Integer: "a signed 32 bit value between
///   -2,147,483,648 and +2,147,483,647 (0x80000000 to 0x7FFFFFFF)". Arithmetic wraps in two's
///   complement.</item>
/// <item><b>CAST</b>: wiki.secondlife.com/wiki/Typecast: (string)float has 6 decimal places;
///   (integer)"0x12A" is 298.</item>
/// <item><b>FOR</b>: wiki.secondlife.com/wiki/For: the initializer runs once before the loop and
///   the increment after each pass. Both are ordinary expressions, so an assignment there
///   assigns.</item>
/// <item><b>ASSIGN</b>: OPS lists = and the compound operators as operators, the lowest tier
///   and right-associative: an assignment is an expression whose value is the value stored.</item>
/// </list>
///
/// <para>
/// OPS says operands are evaluated right to left; Phlox evaluates left to right. No case here
/// depends on that order: every case with side effects gives the same value either way, and
/// says so.
/// </para>
/// </summary>
public class ExpressionConformanceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ExpressionConformanceTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

    public sealed record Case(string Id, string Cause, string Source, string Globals, string Body,
        string[] Expect, string Skip = null)
    {
        public string Name => Cause + "/" + Id;
    }

    /// <summary>Formatting helpers every case may use.</summary>
    private const string Fmt =
        "string fmtv(vector v) { return (string)v.x + \",\" + (string)v.y + \",\" + (string)v.z; }\n" +
        "string fmtr(rotation r) { return (string)r.x + \",\" + (string)r.y + \",\" + (string)r.z + \",\" + (string)r.s; }\n";

    // Cause groups (they match the defects the fixes name).
    private const string AssignExpr = "assign-expr";
    private const string AssignStmtTypes = "assign-stmt-types";
    private const string Additive = "additive-op";
    private const string BooleanChain = "boolean-chain";
    private const string BitwiseChain = "bitwise-chain";
    private const string ChainTypes = "chain-types";
    private const string NegLiteral = "neg-literal";
    private const string RotNeg = "rotation-neg";
    private const string ConstLoad = "const-load";
    private const string Control = "control";

    private const string BitwisePrecedenceDecision =
        "DECISION: SL gives & > ^ > | (OPS); Phlox's grammar, like Halcyon's, has | & ^ on one level " +
        "evaluated left to right. The values differ only when a lower-precedence bitwise operator " +
        "comes first. Kept as the SL value until the precedence is decided.";

    private static Case C(string id, string cause, string source, string body, params string[] expect)
        => new(id, cause, source, "", body, expect);

    private static Case CG(string id, string cause, string source, string globals, string body, params string[] expect)
        => new(id, cause, source, globals, body, expect);

    public static readonly IReadOnlyList<Case> Cases = new List<Case>
    {
        // ── The six live probes, verbatim ────────────────────────────────────
        C("P1 for-init on a reused counter", AssignExpr, "FOR",
            "integer i = 5; for (i = 0; i < 3; i++) {} llOwnerSay((string)i);", "3"),
        C("P2 a = b = 7", AssignExpr, "ASSIGN",
            "integer a = 1; integer b = 1; a = b = 7; llOwnerSay((string)a + \" \" + (string)b);", "7 7"),
        C("P3 p + q - r", Additive, "OPS",
            "integer p = 10; integer q = 5; integer r = 3; llOwnerSay((string)(p + q - r));", "12"),
        C("P4 x && y && z", BooleanChain, "OPS",
            "integer x = 1; integer y = 1; integer z = 0; llOwnerSay((string)(x && y && z));", "0"),
        C("P5 for j += 2 with a guard", AssignExpr, "FOR",
            "integer j; integer guard; for (j = 0; j < 10; j += 2) { if (++guard > 50) jump out; } @out; " +
            "llOwnerSay(\"j=\" + (string)j + \" guard=\" + (string)guard);", "j=10 guard=5"),
        C("P6 (string)DEBUG_CHANNEL", ConstLoad, "INT; wiki DEBUG_CHANNEL = 0x7FFFFFFF",
            "llOwnerSay((string)DEBUG_CHANNEL);", "2147483647"),

        // ── Assignment as an expression: for-loop init and step ──────────────
        C("A01 for-init fresh counter", Control, "FOR",
            "integer i; integer n; for (i = 0; i < 3; i++) n += 10; llOwnerSay((string)i + \" \" + (string)n);", "3 30"),
        C("A02 for-init non-zero start", AssignExpr, "FOR",
            "integer i; integer n; for (i = 2; i <= 4; i++) n += i; llOwnerSay((string)i + \" \" + (string)n);", "5 9"),
        C("A03 two loops over one counter", AssignExpr, "FOR",
            "integer i; integer n; for (i = 0; i < 3; i++) n++; for (i = 0; i < 3; i++) n++; " +
            "llOwnerSay((string)i + \" \" + (string)n);", "3 6"),
        C("A04 for-step i = i + 1", AssignExpr, "FOR",
            "integer i; integer n; for (i = 0; i < 4; i = i + 1) { if (++n > 50) jump out; } @out; " +
            "llOwnerSay((string)i + \" \" + (string)n);", "4 4"),
        C("A05 for-step *=", AssignExpr, "FOR",
            "integer i; integer n; for (i = 1; i < 100; i *= 3) { if (++n > 50) jump out; } @out; " +
            "llOwnerSay((string)i + \" \" + (string)n);", "243 5"),
        C("A06 for-step -=", AssignExpr, "FOR",
            "integer i; integer n; for (i = 10; i > 0; i -= 3) { if (++n > 50) jump out; } @out; " +
            "llOwnerSay((string)i + \" \" + (string)n);", "-2 4"),
        C("A07 for-step /=", AssignExpr, "FOR",
            "integer i; integer n; for (i = 100; i > 0; i /= 10) { if (++n > 50) jump out; } @out; " +
            "llOwnerSay((string)i + \" \" + (string)n);", "0 3"),
        C("A08 for-step += 3", AssignExpr, "FOR",
            "integer i; integer n; for (i = 0; i < 10; i += 3) { if (++n > 50) jump out; } @out; " +
            "llOwnerSay((string)i + \" \" + (string)n);", "12 4"),
        C("A09 float for-step", AssignExpr, "FOR",
            "float f; integer n; for (f = 0.5; f < 2.0; f += 0.5) { if (++n > 50) jump out; } @out; " +
            "llOwnerSay((string)f + \" \" + (string)n);", "2.000000 3"),

        // ── Chained assignment ───────────────────────────────────────────────
        C("A10 a = b = c = 3", AssignExpr, "ASSIGN",
            "integer a; integer b; integer c; a = b = c = 3; " +
            "llOwnerSay((string)a + \" \" + (string)b + \" \" + (string)c);", "3 3 3"),
        C("A11 chain in a declaration", AssignExpr, "ASSIGN",
            "integer b; integer a = b = 4; llOwnerSay((string)a + \" \" + (string)b);", "4 4"),
        C("A12 a = b += 5", AssignExpr, "ASSIGN",
            "integer a; integer b = 2; a = b += 5; llOwnerSay((string)a + \" \" + (string)b);", "7 7"),
        C("A13 global chain", AssignExpr, "ASSIGN",
            "ga = gb = 11; llOwnerSay((string)ga + \" \" + (string)gb);", "11 11") with { Globals = "integer ga; integer gb;\n" },

        // ── Assignment inside conditions and arguments ───────────────────────
        C("A14 assignment in if", AssignExpr, "ASSIGN",
            "integer k; if ((k = 5) == 5) llOwnerSay(\"t\"); else llOwnerSay(\"f\"); llOwnerSay((string)k);", "t", "5"),
        C("A15 assignment in while", AssignExpr, "ASSIGN",
            "integer n = 3; integer s; while ((n = n - 1) >= 0) s += n; " +
            "llOwnerSay((string)s + \" \" + (string)n);", "3 -1"),
        CG("A16 assignment as a user-function argument", AssignExpr, "ASSIGN",
            "integer twice(integer x) { return x * 2; }\n",
            "integer v; integer r = twice(v = 21); llOwnerSay((string)r + \" \" + (string)v);", "42 21"),
        C("A17 assignment as a syscall argument", AssignExpr, "ASSIGN",
            "integer v; llOwnerSay((string)(v = 9)); llOwnerSay((string)v);", "9", "9"),
        C("A18 assignment as an operand", AssignExpr, "ASSIGN",
            "integer x; integer y = (x = 3) + 1; llOwnerSay((string)y + \" \" + (string)x);", "4 3"),

        // ── Compound operators ───────────────────────────────────────────────
        C("A19 integer compound statements", Control, "ASSIGN",
            "integer x = 10; x += 5; llOwnerSay((string)x); x -= 3; llOwnerSay((string)x); " +
            "x *= 2; llOwnerSay((string)x); x /= 5; llOwnerSay((string)x); x %= 3; llOwnerSay((string)x);",
            "15", "12", "24", "4", "1"),
        C("A20 integer compound expressions", AssignExpr, "ASSIGN",
            "integer x = 10; integer y; " +
            "y = (x += 5); llOwnerSay((string)x + \" \" + (string)y); " +
            "y = (x -= 3); llOwnerSay((string)x + \" \" + (string)y); " +
            "y = (x *= 2); llOwnerSay((string)x + \" \" + (string)y); " +
            "y = (x /= 5); llOwnerSay((string)x + \" \" + (string)y); " +
            "y = (x %= 3); llOwnerSay((string)x + \" \" + (string)y);",
            "15 15", "12 12", "24 24", "4 4", "1 1"),
        C("A21 float compound statements", Control, "ASSIGN; CAST",
            "float f = 1.5; f += 1; llOwnerSay((string)f); f -= 0.5; llOwnerSay((string)f); " +
            "f *= 3; llOwnerSay((string)f); f /= 4; llOwnerSay((string)f);",
            "2.500000", "2.000000", "6.000000", "1.500000"),
        C("A22 float compound expression", AssignExpr, "ASSIGN; CAST",
            "float f = 1.5; float g = (f += 0.25); llOwnerSay((string)f + \" \" + (string)g);", "1.750000 1.750000"),
        C("A23 float = integer statement", AssignStmtTypes, "ASSIGN; CAST (integer promotes to float)",
            "float f; f = 1; llOwnerSay((string)f);", "1.000000"),
        C("A24 float = integer expression", AssignExpr, "ASSIGN; CAST (integer promotes to float)",
            "float f; float g = (f = 2); llOwnerSay((string)f + \" \" + (string)g);", "2.000000 2.000000"),
        C("A25 string targets", AssignExpr, "ASSIGN",
            "string s = \"a\"; s += \"b\"; llOwnerSay(s); string t = (s += \"c\"); llOwnerSay(s + \" \" + t); " +
            "string u; string w = u = \"x\"; llOwnerSay(u + w);", "ab", "abc abc", "xx"),
        C("A26 key targets", AssignExpr, "ASSIGN",
            "key k; key j = k = NULL_KEY; llOwnerSay((string)j); llOwnerSay((string)k);",
            "00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000000"),
        C("A27 list targets", AssignExpr, "ASSIGN",
            "list l = [1]; l += [2]; l += 3; llOwnerSay((string)l); list m = (l += [\"x\"]); " +
            "llOwnerSay((string)m + \" \" + (string)l);", "123", "123x 123x"),
        CG("A28 vector targets", AssignExpr, "ASSIGN", Fmt,
            "vector v = <1,2,3>; v += <1,1,1>; llOwnerSay(fmtv(v)); v -= <1,1,1>; llOwnerSay(fmtv(v)); " +
            "v *= 2.0; llOwnerSay(fmtv(v)); v /= 2.0; llOwnerSay(fmtv(v)); v *= 2; llOwnerSay(fmtv(v)); " +
            "vector w = (v -= <1,1,1>); llOwnerSay(fmtv(w));",
            "2.000000,3.000000,4.000000", "1.000000,2.000000,3.000000", "2.000000,4.000000,6.000000",
            "1.000000,2.000000,3.000000", "2.000000,4.000000,6.000000", "1.000000,3.000000,5.000000"),
        // Rotation * and / are left out: the VM's rotation multiply negates its result, a VM
        // matter separate from the compiler (ZERO_ROTATION * ZERO_ROTATION is <0,0,0,-1>).
        CG("A29 rotation targets", AssignExpr, "ASSIGN", Fmt,
            "rotation r = <1,2,3,4>; r += <1,1,1,1>; r -= <0,0,0,1>; rotation q; rotation p = q = r; " +
            "rotation s = (r += <0,0,0,1>); llOwnerSay(fmtr(p)); llOwnerSay(fmtr(q)); llOwnerSay(fmtr(s));",
            "2.000000,3.000000,4.000000,4.000000", "2.000000,3.000000,4.000000,4.000000",
            "2.000000,3.000000,4.000000,5.000000"),

        // ── Vector and rotation components ───────────────────────────────────
        CG("A30 component = float statement", Control, "ASSIGN", Fmt,
            "vector v; v.x = 1.5; llOwnerSay(fmtv(v));", "1.500000,0.000000,0.000000"),
        CG("A31 component = integer statement", AssignStmtTypes, "ASSIGN; CAST (integer promotes to float)", Fmt,
            "vector v; v.z = 25; llOwnerSay(fmtv(v));", "0.000000,0.000000,25.000000"),
        CG("A32 component = in an expression", AssignExpr, "ASSIGN", Fmt,
            "vector v; float f = (v.y = 2.5); llOwnerSay((string)f); llOwnerSay(fmtv(v));",
            "2.500000", "0.000000,2.500000,0.000000"),
        CG("A33 component compound", AssignExpr, "ASSIGN", Fmt,
            "vector v = <1,1,1>; v.z += 1.0; float g = (v.x *= 3.0); llOwnerSay(fmtv(v)); llOwnerSay((string)g);",
            "3.000000,1.000000,2.000000", "3.000000"),
        CG("A34 rotation components", AssignExpr, "ASSIGN", Fmt,
            "rotation r; r.s = 0.5; float f = (r.x = 0.25); llOwnerSay(fmtr(r)); llOwnerSay((string)f);",
            "0.250000,0.000000,0.000000,0.500000", "0.250000"),

        // ── ++ and -- ────────────────────────────────────────────────────────
        C("A35 ++/-- statements", Control, "OPS",
            "integer i = 5; i++; ++i; i--; --i; i++; llOwnerSay((string)i);", "6"),
        C("A36 ++/-- expressions", Control, "OPS",
            "integer i = 5; integer a = i++; integer b = ++i; integer c = i--; integer d = --i; " +
            "llOwnerSay((string)a + \" \" + (string)b + \" \" + (string)c + \" \" + (string)d + \" \" + (string)i);",
            "5 7 7 5 5"),
        C("A37 i++ + ++i (same either order)", Control, "OPS (left-to-right and right-to-left both give 2)",
            "integer i; integer z = i++ + ++i; llOwnerSay((string)z + \" \" + (string)i);", "2 2"),
        C("A38 float ++", Control, "OPS",
            "float f = 1.5; f++; float g = f++; llOwnerSay((string)f + \" \" + (string)g);", "3.500000 2.500000"),
        CG("A39 component ++/--", Control, "OPS", Fmt,
            "vector v; v.x++; ++v.y; float q = v.z--; llOwnerSay(fmtv(v)); llOwnerSay((string)q);",
            "1.000000,1.000000,-1.000000", "0.000000"),

        // ── + and - chains ───────────────────────────────────────────────────
        C("B01 a - b + c", Control, "OPS",
            "integer a = 10; integer b = 5; integer c = 3; llOwnerSay((string)(a - b + c));", "8"),
        C("B02 a + b + c - d", Additive, "OPS",
            "integer a = 1; integer b = 2; integer c = 3; integer d = 4; llOwnerSay((string)(a + b + c - d));", "2"),
        C("B03 a + b - c + d - e", Additive, "OPS",
            "integer a = 20; integer b = 1; integer c = 2; integer d = 3; integer e = 4; " +
            "llOwnerSay((string)(a + b - c + d - e));", "18"),
        C("B04 a - b - c + d + e", Control, "OPS",
            "integer a = 20; integer b = 1; integer c = 2; integer d = 3; integer e = 4; " +
            "llOwnerSay((string)(a - b - c + d + e));", "24"),
        C("B05 float x + 2 - 0.25", Additive, "OPS; CAST",
            "float x = 1.5; llOwnerSay((string)(x + 2 - 0.25));", "3.250000"),
        C("B06 string chain", Control, "OPS",
            "string a = \"a\"; llOwnerSay(a + \"b\" + \"c\");", "abc"),
        CG("B07 vector a + b - c", Additive, "OPS", Fmt,
            "vector a = <1,1,1>; vector b = <2,2,2>; vector c = <3,3,3>; llOwnerSay(fmtv(a + b - c));",
            "0.000000,0.000000,0.000000"),

        // ── * / % chains and mixed levels ────────────────────────────────────
        C("B08 a * b / c", Control, "OPS",
            "integer a = 6; integer b = 4; integer c = 3; llOwnerSay((string)(a * b / c));", "8"),
        C("B09 a / b * c", Control, "OPS",
            "integer a = 7; integer b = 2; integer c = 3; llOwnerSay((string)(a / b * c));", "9"),
        C("B10 a % b * c", Control, "OPS",
            "integer a = 7; integer b = 4; integer c = 3; llOwnerSay((string)(a % b * c));", "9"),
        C("B11 a * b % c", Control, "OPS",
            "integer a = 7; integer b = 4; integer c = 5; llOwnerSay((string)(a * b % c));", "3"),
        C("B12 a / b % c * d", Control, "OPS",
            "integer a = 100; integer b = 7; integer c = 5; integer d = 2; llOwnerSay((string)(a / b % c * d));", "8"),
        C("B13 a + b * c - d / e", Additive, "OPS",
            "integer a = 1; integer b = 2; integer c = 3; integer d = 8; integer e = 4; " +
            "llOwnerSay((string)(a + b * c - d / e));", "5"),
        C("B14 a - b * c + d % e", Control, "OPS",
            "integer a = 10; integer b = 2; integer c = 3; integer d = 7; integer e = 4; " +
            "llOwnerSay((string)(a - b * c + d % e));", "7"),
        C("B15 1 + 2 + [3]", ChainTypes, "OPS (1 + 2 is 3; integer + list prepends)",
            "llOwnerSay((string)(1 + 2 + [3]));", "33"),
        CG("B16 0.5 * 2 * vector", ChainTypes, "OPS (0.5 * 2 is 1.0; float * vector scales)", Fmt,
            "llOwnerSay(fmtv(0.5 * 2 * <1,1,1>));", "1.000000,1.000000,1.000000"),
        C("B17 i + j + 0.5", ChainTypes, "OPS; CAST",
            "integer i = 1; integer j = 2; llOwnerSay((string)(i + j + 0.5));", "3.500000"),
        C("B18 [1] + 2 + 3", Control, "OPS",
            "llOwnerSay((string)([1] + 2 + 3));", "123"),

        // ── Shifts ───────────────────────────────────────────────────────────
        C("B19 1 << 4 >> 2", Control, "OPS", "integer a = 1; llOwnerSay((string)(a << 4 >> 2));", "4"),
        C("B20 a << b << c", Control, "OPS",
            "integer a = 1; integer b = 2; integer c = 3; llOwnerSay((string)(a << b << c));", "32"),
        C("B21 -16 >> 2", Control, "OPS; INT (UNCERTAIN: the wiki does not say >> sign-extends; SL's Mono int >> does)",
            "integer a = -16; llOwnerSay((string)(a >> 2));", "-4"),
        C("B22 + binds tighter than << and >>", Control, "OPS",
            "integer a = 1; integer b = 256; llOwnerSay((string)(a + 1 << 2)); llOwnerSay((string)(b >> 1 + 1));", "8", "64"),

        // ── & | ^ ────────────────────────────────────────────────────────────
        C("B23 a | b | c", Control, "OPS", "integer a = 1; integer b = 2; integer c = 4; llOwnerSay((string)(a | b | c));", "7"),
        C("B24 a & b & c", Control, "OPS", "integer a = 7; integer b = 6; integer c = 4; llOwnerSay((string)(a & b & c));", "4"),
        C("B25 a ^ b ^ c", Control, "OPS", "integer a = 1; integer b = 3; integer c = 7; llOwnerSay((string)(a ^ b ^ c));", "5"),
        C("B26 a & b | c", BitwiseChain, "OPS (& before |)",
            "integer a = 6; integer b = 3; integer c = 8; llOwnerSay((string)(a & b | c));", "10"),
        C("B27 a ^ b | c", BitwiseChain, "OPS (^ before |)",
            "integer a = 12; integer b = 5; integer c = 1; llOwnerSay((string)(a ^ b | c));", "9"),
        C("B28 a & b ^ c", BitwiseChain, "OPS (& before ^)",
            "integer a = 3; integer b = 5; integer c = 6; llOwnerSay((string)(a & b ^ c));", "7"),
        new Case("B29 a | b & c (SL precedence)", BitwiseChain, "OPS (& before |: 1 | (2 & 0))", "",
            "integer a = 1; integer b = 2; integer c = 0; llOwnerSay((string)(a | b & c));", new[] { "1" },
            BitwisePrecedenceDecision),
        new Case("B30 a | b ^ c (SL precedence)", BitwiseChain, "OPS (^ before |: 4 | (1 ^ 5))", "",
            "integer a = 4; integer b = 1; integer c = 5; llOwnerSay((string)(a | b ^ c));", new[] { "4" },
            BitwisePrecedenceDecision),
        C("B31 a & b == c", Control, "OPS (== before &)",
            "integer a = 6; integer b = 1; integer c = 1; llOwnerSay((string)(a & b == c));", "0"),

        // ── Equality and relational ──────────────────────────────────────────
        C("B32 equality and relational chains", Control, "OPS (left-associative; comparisons give 1 or 0)",
            "integer a = 1; integer b = 2; integer c = 3; integer d = 5; " +
            "llOwnerSay((string)(a == a == a)); llOwnerSay((string)(b == b == b)); llOwnerSay((string)(a != b == a)); " +
            "llOwnerSay((string)(a < b == a)); llOwnerSay((string)(c > b > a)); llOwnerSay((string)(a < b < c)); " +
            "llOwnerSay((string)(d >= d <= 0)); llOwnerSay((string)(a < 1.5)); llOwnerSay((string)(\"a\" == \"a\" != 0));",
            "1", "0", "1", "1", "0", "1", "0", "1", "1"),

        // ── && and || ────────────────────────────────────────────────────────
        C("B33 1 && 1 && 1", Control, "OPS", "integer a = 1; llOwnerSay((string)(a && a && a));", "1"),
        C("B34 0 || 0 || 1", BooleanChain, "OPS",
            "integer a = 0; integer b = 1; llOwnerSay((string)(a || a || b));", "1"),
        C("B35 1 || 0 && 0 (same precedence)", BooleanChain, "OPS (&& and || share a level: (1 || 0) && 0)",
            "integer a = 1; integer b = 0; llOwnerSay((string)(a || b && b));", "0"),
        C("B36 0 && 1 || 1", BooleanChain, "OPS ((0 && 1) || 1)",
            "integer a = 0; integer b = 1; llOwnerSay((string)(a && b || b));", "1"),
        C("B37 four-operand chains", BooleanChain, "OPS",
            "integer a = 1; integer z = 0; integer f = 5; llOwnerSay((string)(a && 2 && 3 && z)); " +
            "llOwnerSay((string)(z || z || z || f));", "0", "1"),
        C("B38 2 && 3 is 1", Control, "OPS (&& gives TRUE, which is 1)",
            "integer a = 2; integer b = 3; llOwnerSay((string)(a && b));", "1"),
        CG("B39 no short-circuit", BooleanChain, "OPS (both operands always evaluated; the count is the same in either order)",
            "integer calls;\ninteger hit() { calls++; return 1; }\n",
            "integer z = 0; integer r = z && hit() && hit(); llOwnerSay((string)r + \" \" + (string)calls); " +
            "calls = 0; integer o = 1; r = o || hit(); llOwnerSay((string)r + \" \" + (string)calls);",
            "0 2", "1 1"),

        // ── Unary and casts ──────────────────────────────────────────────────
        C("B40 unary operators in chains", Control, "OPS",
            "integer a = 3; integer b = 5; integer z = 0; integer t = 2; " +
            "llOwnerSay((string)(-a + b)); llOwnerSay((string)(a - -b)); llOwnerSay((string)(-t * a)); " +
            "llOwnerSay((string)(!z + 1)); llOwnerSay((string)(~z & 0xFF)); llOwnerSay((string)(!a || !z));",
            "2", "8", "-6", "2", "255", "1"),
        C("B41 -2147483648", NegLiteral, "INT (the minimum integer)",
            "llOwnerSay((string)(-2147483648));", "-2147483648"),
        C("B42 unary minus on a rotation", RotNeg, "OPS (- negates integer, float, vector and rotation)",
            "rotation r = -ZERO_ROTATION; llOwnerSay((string)r.s);", "-1.000000"),
        C("B43 casts in chains", Control, "CAST; OPS (a cast binds tighter than * /)",
            "integer a = 7; integer b = 2; integer one = 1; integer two = 2; " +
            "llOwnerSay((string)((float)a / b)); llOwnerSay((string)((integer)1.9 + 1)); " +
            "llOwnerSay((string)one + (string)two); llOwnerSay((string)((integer)\"0x10\" + 1)); " +
            "llOwnerSay((string)((integer)2.5 * 2));",
            "3.500000", "2", "12", "17", "4"),

        // ── 32-bit wrap, division, modulus ───────────────────────────────────
        C("B44 32-bit wrap", Control, "INT",
            "integer m = 2147483647; integer k = 65536; llOwnerSay((string)(m + 1)); llOwnerSay((string)(k * k)); " +
            "llOwnerSay((string)(m + 1 - 1));", "-2147483648", "0", "2147483647"),
        C("B45 division and modulus signs", Control, "OPS (% takes the sign of the first operand); INT (division truncates)",
            "integer a = -7; integer b = 7; llOwnerSay((string)(a / 2)); llOwnerSay((string)(a % 3)); " +
            "llOwnerSay((string)(b % -3));", "-3", "-1", "1"),

        // ── Assignment and chains together ───────────────────────────────────
        C("B46 (a = 2) + (b = 3) - 1", AssignExpr, "ASSIGN; OPS (same value in either order)",
            "integer a; integer b; integer c = (a = 2) + (b = 3) - 1; " +
            "llOwnerSay((string)a + \" \" + (string)b + \" \" + (string)c);", "2 3 4"),
        C("B47 for with -= step and && condition", AssignExpr, "FOR; OPS",
            "integer n; integer g; for (n = 10; n > 0 && n != 4; n -= 3) { if (++g > 50) jump out; } @out; " +
            "llOwnerSay((string)n);", "4"),
        C("B48 for with a + - * body", AssignExpr, "FOR; OPS",
            "integer i; integer s; for (i = 0; i < 5; i += 1) s = s + i * 2 - 1; " +
            "llOwnerSay((string)s + \" \" + (string)i);", "15 5"),
    };

    public static IEnumerable<object[]> CaseNames()
    {
        foreach (var c in Cases.Where(c => c.Skip == null)) yield return new object[] { c.Name };
    }

    private static Case Find(string name) => Cases.Single(c => c.Name == name);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case_(string name)
    {
        var c = Find(name);
        var r = ExprRunner.RunInDefault(c.Body, c.Globals);
        var want = "[" + string.Join(" | ", c.Expect) + "]";
        Assert.True(r.Ok && r.Said.SequenceEqual(c.Expect),
            $"{c.Id}: got {r.Describe()} (expect {want}) [source: {c.Source}]");
    }

    /// <summary>
    /// The cases whose SL value is not what this compiler gives by design, pending a decision.
    /// Each is run and its current value printed, so the report quotes it.
    /// </summary>
    [Fact]
    public void SkippedCasesAreDecisions()
    {
        foreach (var c in Cases.Where(c => c.Skip != null))
        {
            var r = ExprRunner.RunInDefault(c.Body, c.Globals);
            Assert.True(r.Ok, $"{c.Id}: {r.Describe()}");
            _out.WriteLine($"DECISION {c.Id}: got {r.Describe()} (SL [{string.Join(" | ", c.Expect)}])");
        }
    }

    /// <summary>Every active case in one script, each in its own function, called in order.</summary>
    [Fact]
    public void AllCasesTogetherInOneScript()
    {
        var active = Cases.Where(c => c.Skip == null).ToList();
        var globals = new List<string>();
        foreach (var c in active)
            foreach (var g in SplitGlobals(c.Globals))
                if (!globals.Contains(g)) globals.Add(g);
        var sb = new System.Text.StringBuilder();
        foreach (var g in globals) sb.AppendLine(g);
        for (int i = 0; i < active.Count; i++)
            sb.Append("case").Append(i).Append("()\n{\n").Append(active[i].Body).Append("\n}\n");
        sb.Append("default\n{\n    state_entry()\n    {\n");
        for (int i = 0; i < active.Count; i++) sb.Append("        case").Append(i).Append("();\n");
        sb.Append("    }\n}\n");

        var r = ExprRunner.RunLsl(sb.ToString());
        var expect = active.SelectMany(c => c.Expect).ToList();
        Assert.True(r.Ok && r.Said.SequenceEqual(expect),
            $"combined: got {r.Describe()}\nexpect [{string.Join(" | ", expect)}]");
    }

    private static IEnumerable<string> SplitGlobals(string globals)
        => globals.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Statement assignments (x = e; and x op= e;) are type-checked like declarations: LSL rejects
/// a value that cannot be assigned, and a constant is not a variable.
/// </summary>
public class AssignmentStatementTypeTests
{
    [Theory]
    [InlineData("integer i; i = \"abc\";")]
    [InlineData("integer i; i = 2.5;")]
    [InlineData("string s; s = 5;")]
    [InlineData("list l; l = <1,2,3>;")]
    [InlineData("string s; s -= \"a\";")]
    [InlineData("PI = 3.0;")]
    [InlineData("TRUE = 0;")]
    public void RejectedAssignment_(string body)
    {
        var c = PhloxCompiler.CompileInDefault(body);
        Assert.True(c.HasErrors(), $"'{body}' compiled; LSL rejects it");
    }

    [Theory]
    [InlineData("key k; k = \"abc\"; string s; s = k; llOwnerSay(s);", "abc")]
    [InlineData("float f; f = 3; f += 1; llOwnerSay((string)f);", "4.000000")]
    [InlineData("vector v; v.y = 2; v.y *= 3; llOwnerSay((string)v.y);", "6.000000")]
    [InlineData("list l; l = []; l += 1; llOwnerSay((string)l);", "1")]
    public void AcceptedAssignment_(string body, string expect)
    {
        var r = ExprRunner.RunInDefault(body);
        Assert.True(r.Ok && r.Said.SequenceEqual(new[] { expect }), $"'{body}': got {r.Describe()} (expect [{expect}])");
    }
}

/// <summary>A for-loop condition of any type, and vector literals with hex components.</summary>
public class ForConditionAndVectorLiteralTests
{
    [Theory]
    // OPS/FOR: a float condition is true when non-zero: 0.5 and 0.25 run, 0.0 stops.
    [InlineData("float f = 0.5; integer n; for (; f; f -= 0.25) { if (++n > 50) jump out; } @out; llOwnerSay((string)n);", "2")]
    // A string condition is true when non-empty.
    [InlineData("string s = \"a\"; integer n; for (; s; s = \"\") { if (++n > 50) jump out; } @out; llOwnerSay((string)n);", "1")]
    // A key condition is true when it is a valid, non-null key.
    [InlineData("key k = NULL_KEY; integer n; for (; k; ) { if (++n > 50) jump out; } @out; llOwnerSay((string)n);", "0")]
    public void ForCondition_(string body, string expect)
    {
        var r = ExprRunner.RunInDefault(body);
        Assert.True(r.Ok && r.Said.SequenceEqual(new[] { expect }), $"'{body}': got {r.Describe()} (expect [{expect}])");
    }

    [Theory]
    [InlineData("vector v = <0x10, 0, 0>; llOwnerSay((string)v.x);", "16.000000")]
    [InlineData("rotation r = <0, 0, 0, 0x1>; llOwnerSay((string)r.s);", "1.000000")]
    [InlineData("vector v = <1, 2.5, 3>; llOwnerSay((string)v.z);", "3.000000")]
    public void VectorLiteral_(string body, string expect)
    {
        var r = ExprRunner.RunInDefault(body);
        Assert.True(r.Ok && r.Said.SequenceEqual(new[] { expect }), $"'{body}': got {r.Describe()} (expect [{expect}])");
    }
}
