using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Link numbers select the prims SL defines, the same way for llMessageLinked, the link setters and the link
/// getters: LINK_SET (-1) every prim, the script's own included; LINK_ALL_OTHERS (-2) every prim but the
/// script's own; LINK_ALL_CHILDREN (-3) every prim but the root; LINK_THIS (-4) the script's prim; LINK_ROOT (1)
/// and link 0 the root; a positive number that link. An unknown negative number or one past the last link
/// selects nothing.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class LinkSelectorTests
{
    private readonly ITestOutputHelper _out;
    public LinkSelectorTests(ITestOutputHelper o) => _out = o;

    private const int LINK_SET = -1, LINK_ALL_OTHERS = -2, LINK_ALL_CHILDREN = -3, LINK_THIS = -4, LINK_ROOT = 1;

    /// <summary>The harness prim as the root "p1", plus children "p2" and "p3" at links 2 and 3.</summary>
    private static SceneObjectPart[] ThreePrimLinkset(SchedulerHarness h)
    {
        h.Prim.Name = "p1";
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p3", h.Prim.OwnerID));
        var group = h.Prim.ParentGroup;
        Assert.Equal(3, group.PrimCount);
        var parts = new[] { group.GetLinkNumPart(1), group.GetLinkNumPart(2), group.GetLinkNumPart(3) };
        Assert.Same(h.Prim, parts[0]);
        parts[1].Name = "p2";
        parts[2].Name = "p3";
        return parts;
    }

    /// <summary>A receiver in each prim, reporting every link message it gets once.</summary>
    private static void Receivers(SchedulerHarness h, SceneObjectPart[] parts)
    {
        foreach (var p in parts)
            h.RezScriptInto(p, "default { state_entry() { llSay(0, \"ready " + p.Name + "\"); } " +
                               "link_message(integer s, integer n, string m, key k) { llSay(0, \"" + p.Name + " got \" + m); } }");
        WaitFor(h, said => parts.All(p => said.Contains("ready " + p.Name)), 30);
        foreach (var p in parts)
            Assert.True(h.Said.Contains("ready " + p.Name), $"receiver in {p.Name} never started: [{string.Join(" | ", h.Said)}]");
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    /// <summary>Run one call from a script in the sender prim, then report "done" and let deliveries settle.</summary>
    private void Run(SchedulerHarness h, SceneObjectPart sender, string body)
    {
        h.RezScriptInto(sender, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the sender never ran: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        // Under load a delivery can still be queued when the 500 ms window ends; finish what is queued
        // (the window above is unchanged, so "not selected" still means nothing arrived in it or after).
        h.PumpUntilIdle(TimeSpan.FromSeconds(20));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    private static string[] Got(SchedulerHarness h, string msg)
        => h.Said.Where(s => s.EndsWith(" got " + msg)).Select(s => s.Split(' ')[0]).OrderBy(s => s).ToArray();

    private static string[] Names(SceneObjectPart[] parts, int[] links) => links.Select(l => parts[l - 1].Name).OrderBy(s => s).ToArray();

    // sender link, selector, the links it must select
    public static TheoryData<int, int, int[]> Selectors => new()
    {
        { 1, LINK_SET,          new[] { 1, 2, 3 } },
        { 2, LINK_SET,          new[] { 1, 2, 3 } },
        { 1, LINK_ALL_OTHERS,   new[] { 2, 3 } },
        { 2, LINK_ALL_OTHERS,   new[] { 1, 3 } },
        { 1, LINK_ALL_CHILDREN, new[] { 2, 3 } },
        { 2, LINK_ALL_CHILDREN, new[] { 2, 3 } },
        { 1, LINK_THIS,         new[] { 1 } },
        { 2, LINK_THIS,         new[] { 2 } },
        { 1, LINK_ROOT,         new[] { 1 } },
        { 2, LINK_ROOT,         new[] { 1 } },
        { 1, 3,                 new[] { 3 } },
        { 2, 3,                 new[] { 3 } },
        { 3, 2,                 new[] { 2 } },
        // Link 0 is the root, as Halcyon's resolver has it.
        { 1, 0,                 new[] { 1 } },
        { 2, 0,                 new[] { 1 } },
        // An unknown negative number and one past the last link select nothing.
        { 1, -7,                new int[0] },
        { 2, -7,                new int[0] },
        { 1, 4,                 new int[0] },
        { 2, 4,                 new int[0] },
    };

    [Theory]
    [MemberData(nameof(Selectors))]
    public void MessageLinkedReachesTheSelectedPrims(int senderLink, int link, int[] expected)
    {
        using var h = new SchedulerHarness();
        var parts = ThreePrimLinkset(h);
        Receivers(h, parts);
        Run(h, parts[senderLink - 1], $"llMessageLinked({link}, 0, \"m\", NULL_KEY);");
        Assert.Equal(Names(parts, expected), Got(h, "m"));
    }

    [Theory]
    [MemberData(nameof(Selectors))]
    public void SetLinkPrimitiveParamsChangesTheSelectedPrims(int senderLink, int link, int[] expected)
    {
        using var h = new SchedulerHarness();
        var parts = ThreePrimLinkset(h);
        foreach (var p in parts) p.Description = "before";
        Run(h, parts[senderLink - 1], $"llSetLinkPrimitiveParamsFast({link}, [PRIM_DESC, \"after\"]);");
        var changed = parts.Where(p => p.Description == "after").Select(p => p.Name).OrderBy(s => s).ToArray();
        Assert.Equal(Names(parts, expected), changed);
    }

    [Theory]
    [MemberData(nameof(Selectors))]
    public void GetLinkPrimitiveParamsReadsTheSelectedPrims(int senderLink, int link, int[] expected)
    {
        using var h = new SchedulerHarness();
        var parts = ThreePrimLinkset(h);
        Run(h, parts[senderLink - 1], $"llSay(0, \"names=\" + llDumpList2String(llGetLinkPrimitiveParams({link}, [PRIM_NAME]), \",\"));");
        var line = h.Said.Single(s => s.StartsWith("names="));
        var read = line.Substring(6).Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s).ToArray();
        Assert.Equal(Names(parts, expected), read);
    }

    /// <summary>A single-prim getter names the selected prim when there is exactly one, and nothing otherwise.</summary>
    [Theory]
    [InlineData(1, LINK_THIS, "p1")]
    [InlineData(2, LINK_THIS, "p2")]
    [InlineData(2, LINK_ROOT, "p1")]
    [InlineData(2, 0, "p1")]
    [InlineData(1, 3, "p3")]
    [InlineData(1, LINK_SET, "")]
    [InlineData(1, -7, "")]
    [InlineData(1, 4, "")]
    public void GetLinkKeyResolvesTheSamePrim(int senderLink, int link, string expected)
    {
        using var h = new SchedulerHarness();
        var parts = ThreePrimLinkset(h);
        Run(h, parts[senderLink - 1], $"llSay(0, \"key=\" + (string)llGetLinkKey({link}));");
        var key = h.Said.Single(s => s.StartsWith("key=")).Substring(4);
        var named = parts.SingleOrDefault(p => p.UUID.ToString() == key)?.Name ?? "";
        Assert.Equal(expected, named);
        if (expected == "") Assert.Equal(UUID.Zero.ToString(), key);
    }

    // A single unlinked prim: LINK_SET, LINK_THIS, LINK_ROOT and 0 are the prim itself; LINK_ALL_OTHERS and
    // LINK_ALL_CHILDREN select nothing, and so does link 2.
    [Theory]
    [InlineData(LINK_SET, true)]
    [InlineData(LINK_ALL_OTHERS, false)]
    [InlineData(LINK_ALL_CHILDREN, false)]
    [InlineData(LINK_THIS, true)]
    [InlineData(LINK_ROOT, true)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(-7, false)]
    public void ASingleUnlinkedPrimSelectsItselfOrNothing(int link, bool selected)
    {
        using var h = new SchedulerHarness();
        h.Prim.Name = "p1";
        h.Prim.Description = "before";
        Receivers(h, new[] { h.Prim });
        Run(h, h.Prim, $"llMessageLinked({link}, 0, \"m\", NULL_KEY); " +
                       $"llSetLinkPrimitiveParamsFast({link}, [PRIM_DESC, \"after\"]); " +
                       $"llSay(0, \"names=\" + llDumpList2String(llGetLinkPrimitiveParams({link}, [PRIM_NAME]), \",\"));");
        Assert.Equal(selected ? new[] { "p1" } : new string[0], Got(h, "m"));
        Assert.Equal(selected ? "after" : "before", h.Prim.Description);
        Assert.Equal(selected ? "names=p1" : "names=", h.Said.Single(s => s.StartsWith("names=")));
    }
}
