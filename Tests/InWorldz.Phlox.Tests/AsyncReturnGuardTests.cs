using System.Reflection;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An async syscall (<c>SyscallShim.RunAsync</c>) returns only what its body hands to
/// <c>SysReturn</c>; a body that hands nothing leaves a value-returning call with nothing on the
/// operand stack, and the script "was killed with Stack empty". Every table entry that returns a
/// value and runs async is exercised here twice - as a statement and as an assignment - and the
/// list is checked against the dispatch table itself, so a new async entry cannot land uncovered.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class AsyncReturnGuardTests
{
    private readonly ITestOutputHelper _out;
    public AsyncReturnGuardTests(ITestOutputHelper o) => _out = o;

    private const string AKey = "\"a1b2c3d4-0000-4000-8000-000000000001\"";
    private const string NullKey = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Every value-returning async entry, with dummy arguments and what the assignment must print in
    /// the harness (no object inventory, no bot manager, no estate rights), or null for "any value".
    /// </summary>
    public static readonly (string Fn, VarType Ret, string Args, string Expect)[] Entries =
    {
        ("llRequestAgentData",     VarType.Key,     AKey + ", DATA_NAME",                          null),
        ("llRequestUsername",      VarType.Key,     AKey,                                          null),
        ("llRequestDisplayName",   VarType.Key,     AKey,                                          null),
        // Halcyon: the avatar's key is the call's value, NULL_KEY for an unknown name (AvatarName2KeyTests).
        ("iwAvatarName2Key",       VarType.Key,     "\"Nobody\", \"Here\"",                        NullKey),
        ("iwRezObject",            VarType.Key,     "\"nothing\", ZERO_VECTOR, ZERO_VECTOR, ZERO_ROTATION, 0", NullKey),
        ("iwRezAtRoot",            VarType.Key,     "\"nothing\", ZERO_VECTOR, ZERO_VECTOR, ZERO_ROTATION, 0", NullKey),
        ("iwRezAt",                VarType.Key,     "\"nothing\", 0, ZERO_VECTOR, ZERO_VECTOR, ZERO_ROTATION, 0", NullKey),
        // SL wiki LlRezObjectWithParams: "On failure, returns (key)"" (in LSL)" - empty, not NULL_KEY.
        ("llRezObjectWithParams",  VarType.Key,     "\"nothing\", []",                             ""),
        // The harness has no permissions module, so everybody is a god and may manage the estate; a NULL_KEY
        // target is FALSE on every path (SL: "FALSE if ... invalid or null id").
        ("llManageEstateAccess",   VarType.Integer, "0, NULL_KEY",                                 "0"),
        ("botCreateBot",           VarType.Key,     "\"A\", \"Bot\", \"\", ZERO_VECTOR, 0",        null),
        ("botGetBotOutfits",       VarType.List,    "",                                            null),
        ("botSearchBotOutfits",    VarType.List,    "\"x\", 0, 0, -1",                             null),
        // Halcyon: an item missing from the link's contents is IW_DELIVER_NONE (7) for one item and
        // IW_DELIVER_ITEM (3) for a list (LSLSystemAPI.cs _GiveInventory / _GiveLinkInventoryList).
        ("iwDeliverInventory",     VarType.Integer, "LINK_THIS, " + AKey + ", \"nothing\"",         "7"),
        ("iwDeliverInventoryList", VarType.Integer, "LINK_THIS, " + AKey + ", \"f\", [\"nothing\"]", "3"),
    };

    public static IEnumerable<object[]> Names() => Entries.Select(e => new object[] { e.Fn });

    private static string LslType(VarType t) => t switch
    {
        VarType.Key => "key", VarType.Integer => "integer", VarType.List => "list",
        VarType.String => "string", VarType.Float => "float", VarType.Vector => "vector",
        VarType.Rotation => "rotation", _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>The table's own view: every non-void entry whose shim calls RunAsync.</summary>
    internal static SortedSet<string> AsyncValueEntriesInTable()
    {
        var shimMap = (Array)typeof(SyscallShim).GetField("_shimMap", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var runAsync = typeof(SyscallShim).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var found = new SortedSet<string>();
        foreach (var sig in DispatchIndexGuardTests.AllSigs())
        {
            if (sig.ReturnType == VarType.Void) continue;
            var shim = ((Delegate)shimMap.GetValue(sig.TableIndex)!).Method;
            if (Calls(shim, runAsync)) found.Add(sig.FunctionName);
        }
        return found;
    }

    /// <summary>Does <paramref name="m"/>'s IL contain a call (0x28) to <paramref name="target"/>?</summary>
    private static bool Calls(MethodInfo m, MethodInfo target)
    {
        var il = m.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28) continue;
            int token = BitConverter.ToInt32(il, i + 1);
            try { if (m.Module.ResolveMethod(token) == target) return true; } catch { }
        }
        return false;
    }

    [Fact]
    public void TheListIsEveryValueReturningAsyncEntryInTheTable()
    {
        var table = AsyncValueEntriesInTable();
        _out.WriteLine("table: " + string.Join(", ", table));
        Assert.Equal(table, new SortedSet<string>(Entries.Select(e => e.Fn)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReturnsAValueOnEveryPath(string fn)
    {
        var e = Entries.Single(x => x.Fn == fn);
        using var h = new SchedulerHarness();
        var call = $"{e.Fn}({e.Args})";
        var stmt = h.RezScript("default { state_entry() { " + call + "; llSay(0, \"S:done\"); } }");
        var asgn = h.RezScript("default { state_entry() { " + LslType(e.Ret) + " v = " + call + "; " +
                               "llSay(0, \"A:v=\" + (string)v); llSay(0, \"A:done\"); } }");

        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !(h.Said.Contains("S:done") && h.Said.Contains("A:done")))
        { if (!h.PumpOnceBusy()) Thread.Sleep(1); }
        h.Pump(20);   // let each handler finish and the script go idle
        h.PumpUntil(() => h.RunStateOf(stmt) == "Waiting" && h.RunStateOf(asgn) == "Waiting");

        var debug = h.SaidOn.Where(s => s.Channel == 0x7FFFFFFF).Select(s => s.Message).ToList();
        _out.WriteLine($"{fn}: said=[{string.Join(" | ", h.Said)}] debug=[{string.Join(" | ", debug)}]");
        _out.WriteLine("stmt " + h.StatusOf(stmt) + " " + h.DumpFrame(stmt));
        _out.WriteLine("asgn " + h.StatusOf(asgn) + " " + h.DumpFrame(asgn));

        foreach (var (id, tag) in new[] { (stmt, "statement"), (asgn, "assignment") })
        {
            Assert.True(h.StatusOf(id).Contains("terminated=-"), $"{fn} as {tag} stopped the script: {h.StatusOf(id)} debug=[{string.Join(" | ", debug)}]");
            Assert.True(OperandCount(h, id) == 0, $"{fn} as {tag} left the operand stack non-empty: {h.DumpFrame(id)}");
        }
        Assert.DoesNotContain(debug, d => d.Contains("stopped"));
        Assert.Contains("S:done", h.Said);
        Assert.Contains("A:done", h.Said);
        if (e.Expect != null) Assert.Contains("A:v=" + e.Expect, h.Said);
    }

    private static int OperandCount(SchedulerHarness h, OpenMetaverse.UUID id)
    {
        var st = h.StateOf(id);
        var ops = st?.GetType().GetProperty("Operands")?.GetValue(st) ?? st?.GetType().GetField("Operands")?.GetValue(st);
        return (ops as System.Collections.ICollection)?.Count ?? -1;
    }
}
