using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Numbers, formatting and small leftovers, each against its source:
/// 1. Rotation multiply with SL's operand order and formula, no identity shortcut, no sign flip; r/r, v*r, v/r;
/// 2. -0 prints as 0, infinities and NaN as Halcyon printed them, (integer) of NaN through llList2Integer,
///    Halcyon's string -> vector / rotation parser; division by zero and NaN comparisons pinned;
/// 3. (string) of a float shows its own value with 6 decimals, vectors and rotations with 5;
/// 4. llDumpList2String, llList2CSV and llList2String write elements as (string)list does;
/// 5. llBreakAllLinks needs PERMISSION_CHANGE_LINKS (AutomaticLinkPermission honoured); osForceBreakAllLinks does not;
/// 6. !, && and || on a key are compile errors;
/// 7. STATUS_RETURN_AT_EDGE is set and read;
/// 8. a notecard whose asset cannot be fetched: Halcyon's error shouted and no dataserver answer;
/// 9. llHTTPRequest's text for a flag that is not an integer;
/// 10. llList2ListSlice on the SL wiki's examples.
/// Each test builds its own interpreter or harness and touches no process-wide state (no test clock, no static
/// hook), so the class runs in parallel. No network: the harness scene is in-process and no HTTP module sends.
/// </summary>
public class NumbersFormattingTests
{
    private readonly ITestOutputHelper _out;
    public NumbersFormattingTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const int PERMISSION_CHANGE_LINKS = 0x80;
    private const int STATUS_RETURN_AT_EDGE = 256;

    private string[] Say(string body, string globals = "")
    {
        var r = ExprRunner.RunInDefault(body, globals);
        _out.WriteLine(r.Describe());
        Assert.True(r.Ok, r.Describe());
        return r.Said.ToArray();
    }

    private string Said1(string expr) => Assert.Single(Say("llOwnerSay(" + expr + ");"));

    private static string F5(float f) => f.ToString("0.00000", CultureInfo.InvariantCulture);
    private static string Rot5(Quaternion q) => "<" + F5(q.X) + ", " + F5(q.Y) + ", " + F5(q.Z) + ", " + F5(q.W) + ">";
    private static string Lsl(Quaternion q) => "<" + q.X.ToString("R", CultureInfo.InvariantCulture) + "," + q.Y.ToString("R", CultureInfo.InvariantCulture)
        + "," + q.Z.ToString("R", CultureInfo.InvariantCulture) + "," + q.W.ToString("R", CultureInfo.InvariantCulture) + ">";

    /// <summary>LL's operator*(a, b) (indra llquaternion.cpp), written out: LSL's a * b.</summary>
    private static Quaternion LlMul(Quaternion a, Quaternion b) => new(
        b.W * a.X + b.X * a.W + b.Y * a.Z - b.Z * a.Y,
        b.W * a.Y + b.Y * a.W + b.Z * a.X - b.X * a.Z,
        b.W * a.Z + b.Z * a.W + b.X * a.Y - b.Y * a.X,
        b.W * a.W - b.X * a.X - b.Y * a.Y - b.Z * a.Z);

    // ── 1. Rotation arithmetic ───────────────────────────────────────────────────────────────────

    [Fact]
    public void IdentityTimesIdentityIsTheIdentityNotItsNegative()
    {
        // before: the package's shortcut returned one operand and Phlox negated it: <-0, -0, -0, -1>
        Assert.Equal("<0.00000, 0.00000, 0.00000, 1.00000>", Said1("(string)(ZERO_ROTATION * ZERO_ROTATION)"));
        Assert.Equal("<0.00000, 0.00000, 0.00000, 1.00000>", Said1("(string)(ZERO_ROTATION / ZERO_ROTATION)"));
    }

    [Fact]
    public void RotationProductFollowsSlsOperandOrderAndFormula()
    {
        var a = new Quaternion(0.1f, 0.2f, 0.3f, 0.927362f);
        var b = new Quaternion(-0.4f, 0.5f, 0.1f, 0.760263f);
        string[] said = Say("rotation a = " + Lsl(a) + "; rotation b = " + Lsl(b) + ";" +
                            "llOwnerSay((string)(a * b)); llOwnerSay((string)(b * a));");
        Assert.Equal(Rot5(LlMul(a, b)), said[0]);
        Assert.Equal(Rot5(LlMul(b, a)), said[1]);
        Assert.NotEqual(said[0], said[1]);
    }

    [Fact]
    public void RotationQuotientIsTheProductWithTheConjugate()
    {
        var a = new Quaternion(0.1f, 0.2f, 0.3f, 0.927362f);
        var b = new Quaternion(-0.4f, 0.5f, 0.1f, 0.760263f);
        string[] said = Say("rotation a = " + Lsl(a) + "; rotation b = " + Lsl(b) + ";" +
                            "llOwnerSay((string)(a / b)); llOwnerSay((string)(ZERO_ROTATION / b));");
        Assert.Equal(Rot5(LlMul(a, new Quaternion(-b.X, -b.Y, -b.Z, b.W))), said[0]);
        Assert.Equal(Rot5(new Quaternion(-b.X, -b.Y, -b.Z, b.W)), said[1]);   // SL wiki: invert R by ZERO_ROTATION / R
    }

    [Fact]
    public void UnnormalisedRotationsAreMultipliedNotShortCut()
    {
        // |W| > 0.999999: the package returned the other operand unchanged
        Assert.Equal("<0.00000, 0.00000, 0.00000, 4.00000>", Said1("(string)(<0.0, 0.0, 0.0, 2.0> * <0.0, 0.0, 0.0, 2.0>)"));
    }

    [Fact]
    public void ATenthOfADegreeTurnsAVector()
    {
        // 0.1 degrees about Z: W = cos(0.05 deg) = 0.99999962, under the package's 0.999999 shortcut (it stayed <100,0,0>)
        double half = 0.05 * Math.PI / 180;
        string r = "<0.0, 0.0, " + Math.Sin(half).ToString("R", CultureInfo.InvariantCulture) + ", " + Math.Cos(half).ToString("R", CultureInfo.InvariantCulture) + ">";
        string[] said = Say("rotation r = " + r + "; vector v = <100.0, 0.0, 0.0> * r; llOwnerSay((string)v);" +
                            "llOwnerSay((string)(v / r)); rotation s = r; integer i; for (i = 0; i < 899; ++i) s = s * r;" +
                            "llOwnerSay((string)(<1.0, 0.0, 0.0> * s));");
        Assert.Equal("<99.99985, 0.17453, 0.00000>", said[0]);
        Assert.Equal("<100.00000, 0.00000, 0.00000>", said[1]);
        // 900 x 0.1 deg accumulate to 90 deg (float rounding over 900 products leaves the length a little over 1)
        double[] v = said[2].Trim('<', '>').Split(',').Select(c => double.Parse(c, CultureInfo.InvariantCulture)).ToArray();
        Assert.True(Math.Abs(v[0]) < 0.001 && Math.Abs(v[1] - 1) < 0.001 && v[2] == 0, said[2]);
    }

    [Fact]
    public void VectorRotateAndInverseRotate()
    {
        string[] said = Say("rotation r = <0.0, 0.0, 0.70710678, 0.70710678>; llOwnerSay((string)(<1.0, 0.0, 0.0> * r));" +
                            "llOwnerSay((string)(<1.0, 0.0, 0.0> / r));");
        Assert.Equal("<0.00000, 1.00000, 0.00000>", said[0]);
        Assert.Equal("<0.00000, -1.00000, 0.00000>", said[1]);
    }

    // ── 2. Float edge rules ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void NegativeZeroPrintsAsZero()
    {
        string[] said = Say("float z = -0.0; llOwnerSay((string)z); llOwnerSay((string)(-0.0000001));" +
                            "llOwnerSay((string)<-0.0, -0.000001, 0.0>); llOwnerSay((string)<-0.0, 0.0, -0.0, -0.0>);" +
                            "llOwnerSay((string)[z, <-0.0, 0.0, 0.0>]); llOwnerSay((string)(-1.5)); llOwnerSay((string)<-0.000006, 0.0, 0.0>);");
        Assert.Equal(new[]
        {
            "0.000000", "0.000000", "<0.00000, 0.00000, 0.00000>", "<0.00000, 0.00000, 0.00000, 0.00000>",
            "0.000000<0.000000, 0.000000, 0.000000>", "-1.500000", "<-0.00001, 0.00000, 0.00000>",
        }, said);
    }

    [Fact]
    public void InfinityAndNaNPrintAsHalcyonPrintedThem()
    {
        string[] said = Say("float big = 1e38; float inf = big * 10.0; float nan = inf - inf;" +
                            "llOwnerSay((string)inf); llOwnerSay((string)(-inf)); llOwnerSay((string)nan);" +
                            "llOwnerSay((string)<inf, 0.0, 0.0>); llOwnerSay((string)[inf]);");
        Assert.Equal(new[] { "Infinity", "-Infinity", "NaN", "<Infinity, 0.00000, 0.00000>", "Infinity" }, said);
    }

    [Fact]
    public void FloatDivisionByZeroAndNaNComparisonsAreHalcyons()
    {
        // pinned, unchanged (the same opcodes as Halcyon): float / 0 is an infinity, NaN equals nothing
        string[] said = Say("float zero = 0.0; float inf = 1.0 / zero; float nan = inf - inf;" +
                            "llOwnerSay((string)inf); llOwnerSay((string)(-1.0 / zero));" +
                            "llOwnerSay((string)(nan == nan) + (string)(nan != nan) + (string)(nan < 1.0) + (string)(nan > 1.0) + (string)(inf > 3e38));" +
                            "llOwnerSay((string)((integer)nan) + \" \" + (string)((integer)inf));");
        Assert.Equal(new[] { "Infinity", "-Infinity", "01001", "-2147483648 -2147483648" }, said);
    }

    [Fact]
    public void IntegerDivisionByZeroStopsTheScript()
    {
        // pinned, unchanged: Halcyon's opcode raises; the script stops with a run-time error
        var r = ExprRunner.RunInDefault("integer zero = 0; llOwnerSay((string)(1 / zero)); llOwnerSay(\"after\");");
        _out.WriteLine(r.Describe());
        Assert.NotNull(r.RuntimeError);
        Assert.DoesNotContain("after", r.Said);
    }

    private static LSLSystemAPI Plain(SchedulerHarness h) => new(null, h.Prim, h.Prim.LocalId, UUID.Random());

    [Fact]
    public void List2IntegerOfAFloatTruncatesAndGivesMinValueOutOfRange()
    {
        using var h = new SchedulerHarness();
        var api = Plain(h);
        var l = new LSLList(new object[] { float.NaN, 1e10f, -1e10f, float.PositiveInfinity, 1.7f, -1.7f, "0x1F", "12abc", 5 });
        Assert.Equal(new[] { int.MinValue, int.MinValue, int.MinValue, int.MinValue, 1, -1, 31, 12, 5 },
            Enumerable.Range(0, l.Length).Select(i => api.llList2Integer(l, i)).ToArray());
        Assert.Equal(5, api.llList2Integer(l, -1));
        Assert.Equal(0, api.llList2Integer(l, 99));
        Assert.Equal(1.7f, api.llList2Float(l, 4));
        Assert.Equal(12f, api.llList2Float(l, 7));
    }

    /// <summary>(text, (string)(vector)text) - Halcyon's split-and-trim parser.</summary>
    public static TheoryData<string, string> VectorTexts => new()
    {
        { "<1,2,3>", "<1.00000, 2.00000, 3.00000>" },
        { "1,2,3", "<1.00000, 2.00000, 3.00000>" },
        { "<1,2,3", "<1.00000, 2.00000, 3.00000>" },
        { "<1, 2,  3>", "<1.00000, 2.00000, 3.00000>" },
        { "  < 1 , 2 , 3 >  ", "<1.00000, 2.00000, 3.00000>" },
        { "<1,2,3,4>", "<1.00000, 2.00000, 3.00000>" },
        { "<1,2,3>xyz", "<0.00000, 0.00000, 0.00000>" },
        { "<1e39,0,0>", "<0.00000, 0.00000, 0.00000>" },
        { "<1,2>", "<0.00000, 0.00000, 0.00000>" },
        { "", "<0.00000, 0.00000, 0.00000>" },
        { "<1.5e2, -2.25, .5>", "<150.00000, -2.25000, 0.50000>" },
        { "<Infinity, 0, 0>", "<Infinity, 0.00000, 0.00000>" },
        { "<infinity, 0, 0>", "<0.00000, 0.00000, 0.00000>" },
    };

    [Theory]
    [MemberData(nameof(VectorTexts))]
    public void StringToVectorIsHalcyonsParser(string text, string want)
    {
        Assert.Equal(want, Said1("(string)((vector)\"" + text + "\")"));
    }

    /// <summary>(text, (string)(rotation)text): three parts make Quaternion(x, y, z), W = sqrt(1 - x2 - y2 - z2) or 0.</summary>
    public static TheoryData<string, string> RotationTexts => new()
    {
        { "<1,2,3,4>", "<1.00000, 2.00000, 3.00000, 4.00000>" },
        { "<1, 2, 3,  4>", "<1.00000, 2.00000, 3.00000, 4.00000>" },
        { "<1,2,3,4,5>", "<1.00000, 2.00000, 3.00000, 4.00000>" },
        { "<1,2,3,4>junk", "<0.00000, 0.00000, 0.00000, 1.00000>" },
        { "<0.6,0,0>", "<0.60000, 0.00000, 0.00000, 0.80000>" },
        { "<1,2,3>", "<1.00000, 2.00000, 3.00000, 0.00000>" },
        { "<1,2>", "<0.00000, 0.00000, 0.00000, 1.00000>" },
    };

    [Theory]
    [MemberData(nameof(RotationTexts))]
    public void StringToRotationIsHalcyonsParser(string text, string want)
    {
        Assert.Equal(want, Said1("(string)((rotation)\"" + text + "\")"));
    }

    [Fact]
    public void List2VectorAndList2RotOfAStringUseTheSameParser()
    {
        using var h = new SchedulerHarness();
        var api = Plain(h);
        var l = new LSLList(new object[] { "<1, 2,  3>", "<1,2,3>xyz", "<1, 2, 3,  4>", "<0.6,0,0>" });
        Assert.Equal(new Vector3(1, 2, 3), api.llList2Vector(l, 0));
        Assert.Equal(Vector3.Zero, api.llList2Vector(l, 1));
        Assert.Equal(new Quaternion(1, 2, 3, 4), api.llList2Rot(l, -2));
        Assert.Equal(0.8f, api.llList2Rot(l, 3).W, 5);
        Assert.Equal(Quaternion.Identity, api.llList2Rot(l, 1));
    }

    // ── 3. (string) of a float ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AFloatPrintsItsOwnValueWithSixDecimals()
    {
        string[] said = Say("llOwnerSay((string)2147483520.0); llOwnerSay((string)123456.7); llOwnerSay((string)16777217.0);" +
                            "llOwnerSay((string)(-PI)); llOwnerSay((string)0.1); llOwnerSay((string)1e30);");
        Assert.Equal(new[]
        {
            "2147483520.000000", "123456.703125", "16777216.000000", "-3.141593", "0.100000", "1000000015047466219876688855040.000000",
        }, said);
    }

    [Fact]
    public void VectorsAndRotationsPrintTheirOwnValuesWithFiveDecimals()
    {
        string[] said = Say("llOwnerSay((string)<1.0, 2.3, 4.56>); llOwnerSay((string)<2147483520.0, 1234.567, 0.0>);" +
                            "llOwnerSay((string)<2147483520.0, 0.0, 0.0, 1.0>); llOwnerSay((string)[<123456.7, 0.0, 0.0>, 123456.7]);");
        Assert.Equal(new[]
        {
            "<1.00000, 2.30000, 4.56000>", "<2147483520.00000, 1234.56702, 0.00000>",
            "<2147483520.00000, 0.00000, 0.00000, 1.00000>", "<123456.703125, 0.000000, 0.000000>123456.703125",
        }, said);
    }

    // ── 4. list dumps ───────────────────────────────────────────────────────────────────────────

    private static readonly UUID K = new("a822ff2b-ff02-461d-b45d-dcd10a2de0c2");
    private static LSLList Mixed() => new(new object[] { 1, 2.0f, "a", new Vector3(1, 2, 3), Quaternion.Identity, K.ToString(), 2147483520f });

    [Fact]
    public void DumpList2StringWritesElementsAsStringListDoes()
    {
        using var h = new SchedulerHarness();
        Assert.Equal("1|2.000000|a|<1.000000, 2.000000, 3.000000>|<0.000000, 0.000000, 0.000000, 1.000000>|" + K + "|2147483520.000000",
            Plain(h).llDumpList2String(Mixed(), "|"));
    }

    [Fact]
    public void List2CsvWritesElementsAsStringListDoes()
    {
        using var h = new SchedulerHarness();
        Assert.Equal("1, 2.000000, a, <1.000000, 2.000000, 3.000000>, <0.000000, 0.000000, 0.000000, 1.000000>, " + K + ", 2147483520.000000",
            Plain(h).llList2CSV(Mixed()));
    }

    [Fact]
    public void List2StringWritesTheElementAsStringListDoes()
    {
        using var h = new SchedulerHarness();
        var api = Plain(h);
        Assert.Equal(new[] { "1", "2.000000", "a", "<1.000000, 2.000000, 3.000000>", "<0.000000, 0.000000, 0.000000, 1.000000>", K.ToString(), "2147483520.000000" },
            Enumerable.Range(0, 7).Select(i => api.llList2String(Mixed(), i)).ToArray());
        Assert.Equal("2147483520.000000", api.llList2String(Mixed(), -1));
        Assert.Equal("", api.llList2String(Mixed(), 7));
    }

    [Fact]
    public void ListDumpsThroughTheRealEngine()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { list l = [1, 2.0, \"a\", <1.0, 2.0, 3.0>, ZERO_ROTATION];" +
                    "llSay(0, llDumpList2String(l, \"|\")); llSay(0, llList2CSV(l)); llSay(0, llList2String(l, 1)); llSay(0, \"done\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("done")), string.Join(" | ", h.Said));
        Assert.Contains("1|2.000000|a|<1.000000, 2.000000, 3.000000>|<0.000000, 0.000000, 0.000000, 1.000000>", h.Said);
        Assert.Contains("1, 2.000000, a, <1.000000, 2.000000, 3.000000>, <0.000000, 0.000000, 0.000000, 1.000000>", h.Said);
        Assert.Contains("2.000000", h.Said);
    }

    [Fact]
    public void ListSortInPlaceIsSeenByEveryReaderOfTheList()
    {
        // osListSortInPlace wrote only LSLList's Data array; (string)list and the dumps read its member list
        using var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));
        h.RezScript("default { state_entry() { list src = [3, 1, 2]; osListSortInPlace(src, 1, TRUE);" +
                    "llSay(0, \"cast=\" + (string)src); llSay(0, \"dump=\" + llDumpList2String(src, \",\")); llSay(0, \"first=\" + llList2String(src, 0));" +
                    "list p = [\"b\", 2, \"a\", 1]; osListSortInPlaceStrided(p, 2, 0, TRUE); llSay(0, \"strided=\" + (string)p); } }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("strided="))), string.Join(" | ", h.Said));
        Assert.Contains("cast=123", h.Said);
        Assert.Contains("dump=1,2,3", h.Said);
        Assert.Contains("first=1", h.Said);
        Assert.Contains("strided=a1b2", h.Said);
    }

    // ── 5, 7, 8: calls on a loaded script's own API ─────────────────────────────────────────────

    private const string Idle = "default { state_entry() { } }";

    private sealed class Loaded : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly UUID Item;

        public Loaded(Action<Nini.Config.IConfigSource> configure = null, string source = Idle)
        {
            H = new SchedulerHarness(configure);
            Item = H.RezScript(source);
            Assert.True(H.PumpUntil(() => H.RunStateOf(Item) == "Waiting" || H.RunStateOf(Item) == "Sleeping"),
                "the script never loaded: " + H.RunStateOf(Item));
        }

        public LSLSystemAPI Api
        {
            get
            {
                var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
                return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[Item];
            }
        }

        public TaskInventoryItem Self { get { lock (H.Prim.TaskInventory) return H.Prim.TaskInventory[Item]; } }

        public List<string> Errors => H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

        public void Dispose() => H.Dispose();
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    private static void LinkASecondPrim(Loaded r)
    {
        r.H.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(r.H.Scene, "p2", r.H.Prim.OwnerID));
        Assert.Equal(2, r.H.Prim.ParentGroup.PrimCount);
    }

    [Fact]
    public void BreakAllLinksWithoutChangeLinksIsRefusedWithAnError()
    {
        using var r = new Loaded();
        LinkASecondPrim(r);
        r.Self.PermsMask = 0;
        r.Api.llBreakAllLinks();
        Assert.Equal(2, r.H.Prim.ParentGroup.PrimCount);
        Assert.Equal(new[] { "Script error: llBreakAllLinks: PERMISSION_CHANGE_LINKS not set" }, r.Errors);
    }

    [Fact]
    public void BreakAllLinksGrantedByTheOwnerBreaksEveryLink()
    {
        using var r = new Loaded();
        LinkASecondPrim(r);
        r.Self.PermsGranter = r.H.Prim.OwnerID;
        r.Self.PermsMask = PERMISSION_CHANGE_LINKS;
        r.Api.llBreakAllLinks();
        Assert.Equal(1, r.H.Prim.ParentGroup.PrimCount);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void BreakAllLinksGrantedBySomeoneElseIsRefusedWithAnError()
    {
        using var r = new Loaded();
        LinkASecondPrim(r);
        r.Self.PermsGranter = UUID.Random();
        r.Self.PermsMask = PERMISSION_CHANGE_LINKS;
        r.Api.llBreakAllLinks();
        Assert.Equal(2, r.H.Prim.ParentGroup.PrimCount);
        Assert.Equal(new[] { "Script error: llBreakAllLinks: PERMISSION_CHANGE_LINKS not set by script owner" }, r.Errors);
    }

    [Fact]
    public void BreakAllLinksWithAutomaticLinkPermissionNeedsNoGrant()
    {
        using var r = new Loaded(c => c.AddConfig("YEngine").Set("AutomaticLinkPermission", "true"));
        LinkASecondPrim(r);
        r.Self.PermsMask = 0;
        r.Api.llBreakAllLinks();
        Assert.Equal(1, r.H.Prim.ParentGroup.PrimCount);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void ForceBreakAllLinksNeedsNoChangeLinksPermission()
    {
        using var r = new Loaded(c => c.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));
        LinkASecondPrim(r);
        r.Self.PermsMask = 0;
        r.Api.osForceBreakAllLinks();
        Assert.Equal(1, r.H.Prim.ParentGroup.PrimCount);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void ReturnAtEdgeIsSetAndReadWithoutAnError()
    {
        using var r = new Loaded();
        Assert.Equal(0, r.Api.llGetStatus(STATUS_RETURN_AT_EDGE));
        r.Api.llSetStatus(STATUS_RETURN_AT_EDGE, 1);
        Assert.True(r.H.Prim.ParentGroup.RootPart.RETURN_AT_EDGE);
        Assert.Equal(1, r.Api.llGetStatus(STATUS_RETURN_AT_EDGE));
        r.Api.llSetStatus(STATUS_RETURN_AT_EDGE, 0);
        Assert.False(r.H.Prim.ParentGroup.RootPart.RETURN_AT_EDGE);
        Assert.Equal(0, r.Api.llGetStatus(STATUS_RETURN_AT_EDGE));
        Assert.Empty(r.Errors);
    }

    private const string Reader = @"
        default {
            touch_start(integer n) { }
            link_message(integer s, integer n, string str, key id) {
                if (n == 1) llGetNotecardLine(str, 0);
                else if (n == 2) llGetNumberOfNotecardLines(str);
                else if (n == 3) iwGetNotecardSegment(str, 0, 0, 5);
                else if (n == 4) iwGetLinkNotecardLine(LINK_THIS, str, 0);
                else if (n == 5) iwGetLinkNumberOfNotecardLines(LINK_THIS, str);
                else if (n == 6) iwGetLinkNotecardSegment(LINK_THIS, str, 0, 0, 5);
                else llSay(0, ""done"");
            }
            dataserver(key q, string d) { llSay(0, ""answered""); }
        }";

    /// <summary>A notecard item whose asset is not in the asset service, or (wrongType) is an asset of another type.</summary>
    private static void AddGhostCard(Loaded r, string name, bool wrongType)
    {
        UUID asset = UUID.Random();
        if (wrongType)
            r.H.Scene.AssetService.Store(new AssetBase(asset, name, (sbyte)AssetType.Texture, UUID.Zero.ToString()) { Data = new byte[] { 1, 2, 3 } });
        var item = new TaskInventoryItem
        {
            Name = name, ItemID = UUID.Random(), AssetID = asset, Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard,
            OwnerID = r.H.Prim.OwnerID, CurrentPermissions = (uint)PermissionMask.All, BasePermissions = (uint)PermissionMask.All,
            EveryonePermissions = (uint)PermissionMask.All, NextPermissions = (uint)PermissionMask.All,
        };
        r.H.Prim.Inventory.AddInventoryItem(item, true);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)] [InlineData(5, false)] [InlineData(6, false)]
    [InlineData(1, true)] [InlineData(2, true)]
    public void ANotecardThatCannotBeFetchedShoutsHalcyonsErrorAndAnswersNothing(int call, bool wrongType)
    {
        using var r = new Loaded(source: Reader);
        AddGhostCard(r, "ghost", wrongType);
        r.H.Engine.PostScriptEvent(r.Item, "link_message", new object[] { 0, call, "ghost", UUID.Zero.ToString() });
        string want = "Script error: Notecard 'ghost' could not be found.";
        Assert.True(r.H.PumpUntil(() => r.Errors.Contains(want) || r.H.Said.Contains("answered")),
            "neither an error nor an answer: errors [" + string.Join(" | ", r.Errors) + "] said [" + string.Join(" | ", r.H.Said) + "]");
        Assert.Equal(new[] { want }, r.Errors);
        // the error is shouted where the answer was posted, so no answer can follow it; one more event proves the
        // script still runs and that nothing was queued ahead of it
        r.H.Engine.PostScriptEvent(r.Item, "link_message", new object[] { 0, 0, "", UUID.Zero.ToString() });
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains("done")), string.Join(" | ", r.H.Said));
        Assert.DoesNotContain("answered", r.H.Said);
    }

    [Fact]
    public void ANotecardThatIsThereStillAnswers()
    {
        using var r = new Loaded(source: Reader);
        TaskInventoryHelpers.AddNotecard(r.H.Scene.AssetService, r.H.Prim, "card", UUID.Random(), UUID.Random(), "line one");
        r.H.Engine.PostScriptEvent(r.Item, "link_message", new object[] { 0, 1, "card", UUID.Zero.ToString() });
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains("answered")), string.Join(" | ", r.H.Said));
        Assert.Empty(r.Errors);
    }

    // ── 9. llHTTPRequest's flag ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("x")]
    [InlineData(1.0f)]
    public void HttpRequestFlagThatIsNotAnIntegerHasHalcyonsText(object flag)
    {
        using var r = new Loaded();
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        string key = r.Api.llHTTPRequest("http://example.invalid/", new LSLList(new[] { flag, "GET" }), "");
        Assert.Equal(UUID.Zero.ToString(), key);
        Assert.Equal(new[] { "Script error: Invalid flag passed in parameters list of llHTTPRequest." }, r.Errors);
        Assert.Equal(0, http.Started);   // no request made
    }

    [Fact]
    public void HttpRequestWithAnIntegerFlagIsMade()
    {
        using var r = new Loaded();
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        string key = r.Api.llHTTPRequest("http://example.invalid/", new LSLList(new object[] { 0, "GET" }), "");
        Assert.NotEqual(UUID.Zero.ToString(), key);
        Assert.Equal(1, http.Started);
        Assert.Empty(r.Errors);
    }

    /// <summary>Takes a request and sends nothing (no network).</summary>
    private sealed class HoldingHttp : IHttpRequestModule
    {
        public int Started;
        public UUID MakeHttpRequest(string url, string parameters, string body) => UUID.Zero;
        public UUID StartHttpRequest(uint localID, UUID itemID, string url, List<string> parameters, Dictionary<string, string> headers, string body)
        {
            System.Threading.Interlocked.Increment(ref Started);
            return UUID.Random();
        }
        public void StopHttpRequest(uint localID, UUID itemID) { }
        public IHttpServiceRequest GetNextCompletedRequest() => null;
        public void RemoveCompletedRequest(UUID id) { }
        public bool CheckThrottle(uint localID, UUID onerID) => true;
        public bool CheckAllowed(Uri url) => true;
    }

    // ── 6. ! && || on a key ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("if (!k) llOwnerSay(\"x\");", "'!' cannot be applied to a key")]
    [InlineData("integer i = !llGetOwner();", "'!' cannot be applied to a key")]
    [InlineData("if (k && TRUE) llOwnerSay(\"x\");", "'&&' cannot be applied to a key")]
    [InlineData("if (TRUE || k) llOwnerSay(\"x\");", "'||' cannot be applied to a key")]
    [InlineData("integer i = (TRUE && 1) || (key)\"x\";", "'||' cannot be applied to a key")]
    public void BooleanOperatorsOnAKeyAreCompileErrors(string body, string message)
    {
        var r = ExprRunner.RunInDefault("key k = NULL_KEY; " + body);
        _out.WriteLine(r.Describe());
        Assert.NotNull(r.CompileError);
        Assert.Contains("Type mismatch: " + message, r.CompileError);
    }

    [Fact]
    public void AKeyAsAConditionAndBooleanOperatorsOnIntegersStillCompile()
    {
        string[] said = Say("key k = NULL_KEY; key j = \"a822ff2b-ff02-461d-b45d-dcd10a2de0c2\"; if (k) llOwnerSay(\"k\"); if (j) llOwnerSay(\"j\");" +
                            "llOwnerSay((string)(!0) + (string)(!5) + (string)(1 && 2) + (string)(0 || 0) + (string)(k == NULL_KEY));");
        Assert.Equal(new[] { "j", "10101" }, said);
    }

    // ── 10. llList2ListSlice ────────────────────────────────────────────────────────────────────

    /// <summary>(start, end, stride, slice_index, result) on [0,1,2,3,4,5,6]: the SL wiki's five examples, then the edges.</summary>
    public static TheoryData<int, int, int, int, string> Slices => new()
    {
        { 0, -1, 3, 0, "[0,3,6]" },
        { 0, -1, 3, 1, "[1,4]" },
        { 1, -1, 3, 1, "[2,5]" },
        { 2, -1, 3, -1, "[4]" },
        { 4, 2, 1, 0, "[0,1,2,4,5,6]" },
        { 5, 1, 2, 0, "[0,5]" },          // exclusion range [0,1] + [5,6], strides of it
        { 5, 1, 2, -1, "[1,6]" },
        { 0, -1, 0, 0, "[0,1,2,3,4,5,6]" }, // stride < 1 is 1
        { 0, -1, 3, 3, "[]" },            // slice_index outside the stride
        { 0, -1, 3, -4, "[]" },
        { 10, 20, 1, 0, "[]" },           // out of range: nothing
        { -10, -1, 3, 0, "[0,3,6]" },
        { 10, 2, 2, 0, "[0,2]" },
    };

    [Theory]
    [MemberData(nameof(Slices))]
    public void List2ListSliceFollowsTheWiki(int start, int end, int stride, int slice, string want)
    {
        using var h = new SchedulerHarness();
        var src = new LSLList(Enumerable.Range(0, 7).Select(i => (object)i).ToArray());
        Assert.Equal(want, "[" + string.Join(",", Plain(h).llList2ListSlice(src, start, end, stride, slice).Data) + "]");
    }
}
