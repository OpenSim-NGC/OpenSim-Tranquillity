using System;
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Script data fixes, each against its source:
/// 1. a whole number stored into a vector or rotation part (from an earlier tree, its two tests ported);
/// 2. SL's list and string range rule for llList2List, llDeleteSubList, llGetSubString, llDeleteSubString,
///    llListReplaceList, llList2ListStrided, including start past the end (it threw);
/// 3. (integer), llFloor, llCeil, llRound of NaN, infinities and out-of-range floats give -2147483648 (SL);
/// 4. PRIM_TEXTURE by the name of a texture in the script's prim;
/// 5. PRIM_BUMP_SHINY's shininess, set and read.
/// Each test builds its own harness or interpreter and touches no process-wide state, so the class runs in parallel.
/// No network: the harness scene is in-process.
/// </summary>
public class ScriptDataFixesTests
{
    private readonly ITestOutputHelper _out;
    public ScriptDataFixesTests(ITestOutputHelper o) => _out = o;

    // ── 1. whole numbers into vector / rotation parts ────────────────────────────────────────────

    private static Interpreter RunAssembly(string asm)
    {
        CompiledScript script = new CompilerFrontend(new PhloxCompiler(), ".").AssembleText(asm);
        Assert.NotNull(script);
        var i = new Interpreter(script, null);
        int ticks = 0;
        while (i.ScriptState.RunState == RuntimeState.Status.Running && ticks++ < 10_000)
            i.Tick();   // before the fix: InvalidCastException on the int -> component store
        return i;
    }

    /// <summary>Ported from an earlier tree's Subscript.cs TestSubscriptIntCoercion.</summary>
    [Fact]
    public void SubscriptIntCoercion()
    {
        // vec.z = 10; assigning an INT to a vector component must coerce to float, not throw.
        var i = RunAssembly(@"
                            .globals 1
                            .statedef default
                            vconst <1.1,1.2,1.3>
                            gstore 0
                            iconst 10
                            gstore.sub 0,2
                            halt

                            .evt default/state_entry: args=0, locals=0
                            ret
                            ");
        Assert.True(i.ScriptState.Operands.Count == 0);
        Vector3 v = (Vector3)i.ScriptState.Globals[0];
        Assert.Equal(10.0f, v.Z);
        Assert.Equal(1.1f, v.X, 4);
        Assert.Equal(1.2f, v.Y, 4);
    }

    /// <summary>Ported from an earlier tree's Subscript.cs TestRotationSubscriptIntCoercion.</summary>
    [Fact]
    public void RotationSubscriptIntCoercion()
    {
        // rot.s = 5; the same coercion on a rotation component.
        var i = RunAssembly(@"
                            .globals 1
                            .statedef default
                            rconst <1.1,1.2,1.3,1.4>
                            gstore 0
                            iconst 5
                            gstore.sub 0,3
                            halt

                            .evt default/state_entry: args=0, locals=0
                            ret
                            ");
        Assert.True(i.ScriptState.Operands.Count == 0);
        Quaternion q = (Quaternion)i.ScriptState.Globals[0];
        Assert.Equal(5.0f, q.W);
        Assert.Equal(1.1f, q.X, 4);
    }

    /// <summary>Every one of the 7 sites (x, y, z; x, y, z, s). Locals reach the same _AssignSubscript; the LSL test
    /// below stores into locals and globals.</summary>
    [Theory]
    [InlineData("x", 0), InlineData("y", 1), InlineData("z", 2)]
    public void EveryVectorPartTakesAnInteger(string part, int index)
    {
        var i = RunAssembly(".globals 1\n.statedef default\nvconst <1.5,2.5,3.5>\ngstore 0\niconst 7\ngstore.sub 0," + index + "\nhalt\n\n.evt default/state_entry: args=0, locals=0\nret\n");
        Vector3 v = (Vector3)i.ScriptState.Globals[0];
        float[] got = { v.X, v.Y, v.Z };
        float[] want = { 1.5f, 2.5f, 3.5f };
        want[index] = 7f;
        Assert.True(want.SequenceEqual(got), part + ": " + v);
    }

    [Theory]
    [InlineData("x", 0), InlineData("y", 1), InlineData("z", 2), InlineData("s", 3)]
    public void EveryRotationPartTakesAnInteger(string part, int index)
    {
        var i = RunAssembly(".globals 1\n.statedef default\nrconst <1.5,2.5,3.5,4.5>\ngstore 0\niconst -3\ngstore.sub 0," + index + "\nhalt\n\n.evt default/state_entry: args=0, locals=0\nret\n");
        Quaternion q = (Quaternion)i.ScriptState.Globals[0];
        float[] got = { q.X, q.Y, q.Z, q.W };
        float[] want = { 1.5f, 2.5f, 3.5f, 4.5f };
        want[index] = -3f;
        Assert.True(want.SequenceEqual(got), part + ": " + q);
    }

    /// <summary>The LSL shapes a script writes, through the compiler (the red run shows which reached the throw).</summary>
    [Fact]
    public void LslPartAssignmentsOfWholeNumbers()
    {
        var r = ExprRunner.RunInDefault(@"
        vector v = <1.5, 2.5, 3.5>;
        rotation q = <1.5, 2.5, 3.5, 4.5>;
        v.z = 10; llOwnerSay((string)v.z);
        v.x = -3; llOwnerSay((string)v.x);
        q.s = 5; llOwnerSay((string)q.s);
        q.y = 2; llOwnerSay((string)q.y);
        v.y += 1; llOwnerSay((string)v.y);
        g.z = 4; llOwnerSay((string)g.z);
        gr.x = 6; llOwnerSay((string)gr.x);",
            "vector g = <0.5, 0.5, 0.5>;\nrotation gr = <0.5, 0.5, 0.5, 0.5>;\n");
        _out.WriteLine(r.Describe());
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "10.000000", "-3.000000", "5.000000", "2.000000", "3.500000", "4.000000", "6.000000" }, r.Said);
    }

    // ── 2. ranges ────────────────────────────────────────────────────────────────────────────────

    private static LSLSystemAPI Api(SchedulerHarness h) => new(null, h.Prim, h.Prim.LocalId, UUID.Random());

    private static LSLList Numbers(int n) => new(Enumerable.Range(0, n).Select(i => (object)i).ToArray());
    private static string Show(LSLList l) => "[" + string.Join(",", l.Data.Select(o => o.ToString())) + "]";

    /// <summary>(start, end, llList2List of NUMBERS [0..9]). llDeleteSubList must give the rest.</summary>
    public static TheoryData<int, int, string> TenItemRanges => new()
    {
        { 2, 4, "[2,3,4]" },
        { 5, 5, "[5]" },
        { 0, -1, "[0,1,2,3,4,5,6,7,8,9]" },
        { -3, -1, "[7,8,9]" },
        // SL wiki llList2List examples:
        { 8, 1, "[0,1,8,9]" }, { 8, -9, "[0,1,8,9]" }, { -2, -9, "[0,1,8,9]" }, { -2, 1, "[0,1,8,9]" },
        // one index outside the list
        { -15, 2, "[0,1,2]" }, { 7, 15, "[7,8,9]" }, { 12, 3, "[0,1,2,3]" }, { 3, -15, "[3,4,5,6,7,8,9]" },
        // both outside: past the end, before the start, inverted either side, straddling
        { 12, 15, "[]" }, { -15, -12, "[]" }, { 15, 12, "[0,1,2,3,4,5,6,7,8,9]" }, { -12, -15, "[0,1,2,3,4,5,6,7,8,9]" },
        { 15, -15, "[]" }, { -15, 15, "[0,1,2,3,4,5,6,7,8,9]" },
        { int.MinValue, int.MaxValue, "[0,1,2,3,4,5,6,7,8,9]" }, { int.MaxValue, int.MinValue, "[]" },
    };

    [Theory]
    [MemberData(nameof(TenItemRanges))]
    public void List2ListAndDeleteSubListFollowSlRanges(int start, int end, string want)
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        var ten = Numbers(10);
        Assert.Equal(want, Show(api.llList2List(ten, start, end)));

        var kept = want.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        string rest = "[" + string.Join(",", Enumerable.Range(0, 10).Where(i => !kept.Contains(i))) + "]";
        Assert.Equal(rest, Show(api.llDeleteSubList(ten, start, end)));

        // The same rule on strings, "0123456789".
        string s = "0123456789";
        Assert.Equal(want.Trim('[', ']').Replace(",", ""), api.llGetSubString(s, start, end));
        Assert.Equal(rest.Trim('[', ']').Replace(",", ""), api.llDeleteSubString(s, start, end));
    }

    /// <summary>The crash: a start past the end of a short list (3 items, 5, 10) threw in llList2List and llDeleteSubString.</summary>
    [Fact]
    public void StartPastTheEndDoesNotThrow()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        var abc = new LSLList(new object[] { "a", "b", "c" });
        Assert.Equal("[]", Show(api.llList2List(abc, 5, 10)));
        Assert.Equal("[a,b,c]", Show(api.llDeleteSubList(abc, 5, 10)));
        Assert.Equal("", api.llGetSubString("abc", 5, 10));
        Assert.Equal("abc", api.llDeleteSubString("abc", 5, 10));
        Assert.Equal("[a,b]", Show(api.llList2List(abc, 5, 1)));
        Assert.Equal("[]", Show(api.llList2List(new LSLList(), 0, -1)));
        Assert.Equal("[]", Show(api.llDeleteSubList(new LSLList(), 2, 1)));
        Assert.Equal("", api.llGetSubString("", 2, 1));
        Assert.Equal("", api.llDeleteSubString("", 2, 1));
    }

    [Fact]
    public void WikiExamplesForDeleteFunctions()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        var names = new LSLList(new object[] { "Anthony", "Bob", "Charlie", "Diane", "Edgar", "Gabriela" });
        names = api.llDeleteSubList(names, 1, 2);
        Assert.Equal("[Anthony,Diane,Edgar,Gabriela]", Show(names));
        names = api.llDeleteSubList(names, 3, 1);
        Assert.Equal("[Edgar]", Show(names));
        Assert.Equal("abcdi", api.llDeleteSubString("abcdefghi", 4, 7));
        Assert.Equal("H", api.llGetSubString("Hello!", 0, 0));
        Assert.Equal("!", api.llGetSubString("Hello!", -1, -1));
        Assert.Equal("ll", api.llGetSubString("Hello!", 2, 3));
        // Halcyon's llDeleteSubString kept char 0 here ("if (end > 0)"): SL's rule deletes [0, 0] and [3, -1].
        Assert.Equal("bc", api.llDeleteSubString("abcde", 3, 0));
    }

    public static TheoryData<int, int, string> ReplaceRanges => new()
    {
        { 2, 4, "[0,1,x,5,6,7,8,9]" },
        { 0, -1, "[x]" },
        { 5, 5, "[0,1,2,3,4,x,6,7,8,9]" },
        { -3, -2, "[0,1,2,3,4,5,6,x,9]" },
        { 12, 15, "[0,1,2,3,4,5,6,7,8,9,x]" },          // start past the end: appended
        { -15, -12, "[x,0,1,2,3,4,5,6,7,8,9]" },        // both before the start: nothing replaced, src first
        { -15, 2, "[x,3,4,5,6,7,8,9]" },
        { 7, 15, "[0,1,2,3,4,5,6,x]" },
        { 0, int.MaxValue, "[x]" },
        { 8, 1, "[2,3,4,5,6,7,x]" },                     // inverted: [0,1] and [8,9] replaced, src after the rest
        { 3, -15, "[0,1,2,x]" },                         // wiki: end before the beginning -> range [start, -1]
        { 15, 3, "[4,5,6,7,8,9,x]" },                    // start past the end -> range [0, end]
        { 15, 12, "[x]" },                               // wiki: end past the end -> [0, end]: everything
        { -1, -15, "[0,1,2,3,4,5,6,7,8,x]" },
    };

    [Theory]
    [MemberData(nameof(ReplaceRanges))]
    public void ListReplaceListFollowsSlRanges(int start, int end, string want)
    {
        using var h = new SchedulerHarness();
        Assert.Equal(want, Show(Api(h).llListReplaceList(Numbers(10), new LSLList(new object[] { "x" }), start, end)));
    }

    public static TheoryData<int, int, int, string> StridedRanges => new()
    {
        // SL wiki examples on [0,1,2,3,4,5,6]
        { 0, -1, 2, "[0,2,4,6]" }, { 1, -1, 2, "[2,4,6]" }, { 2, -1, 2, "[2,4,6]" },
        { 2, 2, 2, "[2]" }, { 3, 3, 2, "[]" }, { 0, 3, 3, "[0,3]" }, { 0, 10, 3, "[0,3,6]" },
        { -3, -1, 2, "[4,6]" }, { -20, 3, 2, "[0,2]" },
        { 5, 1, 2, "[0,2,4,6]" },                 // wiki: not an exclusion range, "as if start was zero & end was -1"
        { 0, -1, 0, "[0,1,2,3,4,5,6]" }, { 0, -1, -2, "[0,1,2,3,4,5,6]" },   // stride < 1 is 1
        { 10, 20, 2, "[]" }, { -20, -10, 2, "[]" },
        { 0, int.MaxValue, int.MaxValue, "[0]" },
    };

    [Theory]
    [MemberData(nameof(StridedRanges))]
    public void List2ListStridedFollowsTheWiki(int start, int end, int stride, string want)
    {
        using var h = new SchedulerHarness();
        Assert.Equal(want, Show(Api(h).llList2ListStrided(Numbers(7), start, end, stride)));
    }

    /// <summary>The crash through a running script in the real engine: the script goes on after the call.</summary>
    [Fact]
    public void RangesThroughTheRealEngine()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { list l = [1, 2, 3]; " +
                    "llSay(0, \"a=\" + llList2CSV(llList2List(l, 5, 10))); " +
                    "llSay(0, \"b=\" + llList2CSV(llList2List([0,1,2,3,4,5,6,7,8,9], 8, 1))); " +
                    "llSay(0, \"c=\" + llList2CSV(llDeleteSubList([0,1,2,3,4,5,6,7,8,9], 8, 1))); " +
                    "llSay(0, \"d=\" + llGetSubString(\"0123456789\", 8, 1)); " +
                    "llSay(0, \"e=\" + llDeleteSubString(\"abc\", 5, 10)); " +
                    "llSay(0, \"done\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("done")), string.Join(" | ", h.Said));
        Assert.Contains("a=", h.Said);
        Assert.Contains("b=0, 1, 8, 9", h.Said);
        Assert.Contains("c=2, 3, 4, 5, 6, 7", h.Said);
        Assert.Contains("d=0189", h.Said);
        Assert.Contains("e=abc", h.Said);
    }

    // ── 3. float to integer ──────────────────────────────────────────────────────────────────────

    public static TheoryData<float, int> Casts => new()
    {
        { float.NaN, int.MinValue },
        { float.PositiveInfinity, int.MinValue },
        { float.NegativeInfinity, int.MinValue },
        { 2147483648f, int.MinValue },            // just past the top (the next float above 2147483647)
        { -2147483904f, int.MinValue },           // just past the bottom (the next float below -2147483648)
        { 3e9f, int.MinValue }, { -3e9f, int.MinValue },
        { 2147483520f, 2147483520 },              // the largest float in range
        { -2147483648f, int.MinValue },           // in range, exactly the bottom
        { 1.9f, 1 }, { -1.9f, -1 }, { 0f, 0 },
    };

    [Theory]
    [MemberData(nameof(Casts))]
    public void IntegerCastFollowsSl(float f, int want)
    {
        Assert.Equal(want, InWorldz.Phlox.Util.LslConvert.FloatToInteger(f));
    }

    [Fact]
    public void IntegerCastOfSpecialValuesInAScript()
    {
        var r = ExprRunner.RunInDefault(@"
        float big = 3.0e38;
        float inf = big * 10.0;
        float ninf = -inf;
        float nan = inf - inf;
        llOwnerSay((string)(integer)nan);
        llOwnerSay((string)(integer)inf);
        llOwnerSay((string)(integer)ninf);
        float top = 65535.0 * 32768.0;
        float past = 65536.0 * 32768.0;
        float below = -65536.0 * 32768.0 - 256.0;
        llOwnerSay((string)(integer)past);
        llOwnerSay((string)(integer)below);
        llOwnerSay((string)(integer)top);
        llOwnerSay((string)(integer)(-top));
        llOwnerSay((string)(integer)-1.9);");
        // Factors of at most 7 digits: a longer float literal loses digits at compile time (a known limit, not
        // changed here), which would test the constant rather than the cast.
        _out.WriteLine(r.Describe());
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "-2147483648", "-2147483648", "-2147483648", "-2147483648", "-2147483648", "2147450880", "-2147450880", "-1" }, r.Said);
    }

    [Fact]
    public void FloorCeilRoundOutOfRangeGiveMinInt()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        foreach (float f in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2147483648f, -2147483904f, 3e9f })
        {
            Assert.Equal(int.MinValue, api.llFloor(f));
            Assert.Equal(int.MinValue, api.llCeil(f));
            Assert.Equal(int.MinValue, api.llRound(f));
        }
        Assert.Equal(2, api.llFloor(2.7f));
        Assert.Equal(3, api.llCeil(2.2f));
        Assert.Equal(3, api.llRound(2.5f));
        Assert.Equal(-3, api.llFloor(-2.2f));
        Assert.Equal(2147483520, api.llFloor(2147483520f));
    }

    // ── 4 and 5. PRIM_TEXTURE by name, PRIM_BUMP_SHINY ───────────────────────────────────────────

    private static readonly UUID TexAsset = new("61616161-0000-0000-0000-000000000001");
    private static readonly UUID NoteAsset = new("61616161-0000-0000-0000-000000000002");
    private static readonly UUID OldTex = new("61616161-0000-0000-0000-000000000003");

    private static void AddItem(SceneObjectPart part, string name, UUID asset, AssetType type)
    {
        const uint full = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move);
        var item = new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)type, InvType = (int)(type == AssetType.Notecard ? InventoryType.Notecard : InventoryType.Texture),
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, EveryonePermissions = 0,
            OwnerID = part.OwnerID, CreatorID = part.OwnerID,
        };
        part.Inventory.AddInventoryItem(item, true);
    }

    private static SceneObjectPart TwoPrims(SchedulerHarness h)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        var child = h.Prim.ParentGroup.GetLinkNumPart(2);
        Assert.NotNull(child);
        return child;
    }

    private static void Dress(SceneObjectPart part)
    {
        var te = new Primitive.TextureEntry(UUID.Zero);
        te.CreateFace(0).TextureID = OldTex;
        part.Shape.Textures = te;
    }

    private void Run(SchedulerHarness h, string body)
    {
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("done")), "the script never finished: " + string.Join(" | ", h.Said));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    private static string Line(SchedulerHarness h, string tag) => h.Said.Single(s => s.StartsWith(tag + "=")).Substring(tag.Length + 1);

    [Fact]
    public void PrimTextureByInventoryNameSetsTheTextureAndReadsBackAsItsName()
    {
        using var h = new SchedulerHarness();
        var child = TwoPrims(h);
        Dress(h.Prim); Dress(child);
        AddItem(h.Prim, "brick", TexAsset, AssetType.Texture);
        Run(h,
            "llSetPrimitiveParams([PRIM_TEXTURE, 0, \"brick\", <2, 3, 0>, <0.25, 0.5, 0>, 1.0]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_TEXTURE, 0, \"brick\", <4, 5, 0>, ZERO_VECTOR, 0.0]); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_TEXTURE, 0]), \"|\")); " +
            "llSay(0, \"link=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_TEXTURE, 0]), \"|\")); ");
        Assert.Equal(TexAsset, h.Prim.Shape.Textures.GetFace(0).TextureID);
        Assert.Equal(TexAsset, child.Shape.Textures.GetFace(0).TextureID);
        string[] own = Line(h, "own").Split('|');
        string[] link = Line(h, "link").Split('|');
        // The read rule: a texture in the script's prim reads back as its name.
        Assert.Equal("brick", own[0]);
        Assert.Equal("brick", link[0]);
        Assert.StartsWith("<2.000000, 3.000000,", own[1]);   // llDumpList2String writes SL's 6 decimals
        Assert.StartsWith("<4.000000, 5.000000,", link[1]);
    }

    [Fact]
    public void PrimTextureWithAnUnknownNameOrANonTextureLeavesTheTexture()
    {
        using var h = new SchedulerHarness();
        Dress(h.Prim);
        AddItem(h.Prim, "notes", NoteAsset, AssetType.Notecard);
        Run(h,
            "llSetPrimitiveParams([PRIM_TEXTURE, 0, \"no such texture\", <2, 2, 0>, ZERO_VECTOR, 0.0]); " +
            "llSay(0, \"a=\" + llDumpList2String(llGetPrimitiveParams([PRIM_TEXTURE, 0]), \"|\")); " +
            "llSetPrimitiveParams([PRIM_TEXTURE, 0, \"notes\", <3, 3, 0>, ZERO_VECTOR, 0.0]); " +
            "llSay(0, \"b=\" + llDumpList2String(llGetPrimitiveParams([PRIM_TEXTURE, 0]), \"|\")); ");
        var f = h.Prim.Shape.Textures.GetFace(0);
        Assert.Equal(OldTex, f.TextureID);   // not blanked, not the notecard's asset
        Assert.Equal(3f, f.RepeatU);          // the rest of the rule still applied
        Assert.StartsWith("<2.000000, 2.000000,", Line(h, "a").Split('|')[1]);   // SL's 6 decimals
    }

    [Fact]
    public void PrimTextureByKeyStillWorks()
    {
        using var h = new SchedulerHarness();
        Dress(h.Prim);
        Run(h, "llSetPrimitiveParams([PRIM_TEXTURE, 0, \"" + TexAsset + "\", <1, 1, 0>, ZERO_VECTOR, 0.0]); ");
        Assert.Equal(TexAsset, h.Prim.Shape.Textures.GetFace(0).TextureID);
    }

    [Fact]
    public void BumpShinyRoundTripsEveryShininess()
    {
        using var h = new SchedulerHarness();
        var child = TwoPrims(h);
        var body = new System.Text.StringBuilder("llSetPrimitiveParams([PRIM_FULLBRIGHT, 0, TRUE]); ");
        int[] bumps = { 0, 5, 17 };
        for (int shiny = 0; shiny <= 3; shiny++)
            foreach (int bump in bumps)
            {
                string tag = shiny + "_" + bump;
                body.Append("llSetPrimitiveParams([PRIM_BUMP_SHINY, 0, " + shiny + ", " + bump + "]); ");
                body.Append("llSetLinkPrimitiveParamsFast(2, [PRIM_BUMP_SHINY, 1, " + shiny + ", " + bump + "]); ");
                body.Append("llSay(0, \"own" + tag + "=\" + llList2CSV(llGetPrimitiveParams([PRIM_BUMP_SHINY, 0])) + \"|\" + llList2CSV(llGetPrimitiveParams([PRIM_FULLBRIGHT, 0]))); ");
                body.Append("llSay(0, \"link" + tag + "=\" + llList2CSV(llGetLinkPrimitiveParams(2, [PRIM_BUMP_SHINY, 1]))); ");
            }
        body.Append("llSetPrimitiveParams([PRIM_BUMP_SHINY, 0, PRIM_SHINY_MEDIUM, PRIM_BUMP_BRICKS]); ");
        Run(h, body.ToString());
        for (int shiny = 0; shiny <= 3; shiny++)
            foreach (int bump in bumps)
            {
                string tag = shiny + "_" + bump;
                Assert.Equal(shiny + ", " + bump + "|1", Line(h, "own" + tag));   // fullbright kept
                Assert.Equal(shiny + ", " + bump, Line(h, "link" + tag));
            }
        var f = h.Prim.Shape.Textures.GetFace(0);
        Assert.Equal(Shininess.Medium, f.Shiny);
        Assert.Equal(Bumpiness.Bricks, f.Bump);
        Assert.True(f.Fullbright);
    }

    [Fact]
    public void BumpShinyOutOfRangeValuesDoNotSpillIntoOtherBits()
    {
        using var h = new SchedulerHarness();
        Run(h, "llSetPrimitiveParams([PRIM_FULLBRIGHT, 0, FALSE, PRIM_BUMP_SHINY, 0, 7, 63]); " +
               "llSay(0, \"x=\" + llList2CSV(llGetPrimitiveParams([PRIM_BUMP_SHINY, 0, PRIM_FULLBRIGHT, 0]))); ");
        // shiny 7 is none (Halcyon, YEngine); bump 63 keeps its five bits (31) and leaves fullbright off.
        Assert.Equal("0, 31, 0", Line(h, "x"));
    }
}
