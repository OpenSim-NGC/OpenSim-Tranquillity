using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Avatars seated on an object take the link numbers after its last prim, in the order they sat
/// (Halcyon SceneObjectGroup.AddSeatedAvatar and RecalcSeatedAvatarLinks). llGetNumberOfPrims counts them,
/// llGetLinkKey and llGetLinkName name them, llGetLinkPrimitiveParams reads them and
/// llSetLinkPrimitiveParams(Fast) moves them. LINK_SET, LINK_ALL_OTHERS and LINK_ALL_CHILDREN take them in for
/// the prim-params functions; llMessageLinked never reaches them.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SeatedAvatarLinkTests
{
    private readonly ITestOutputHelper _out;
    public SeatedAvatarLinkTests(ITestOutputHelper o) => _out = o;

    private const int LINK_SET = -1, LINK_ALL_OTHERS = -2, LINK_ALL_CHILDREN = -3, LINK_THIS = -4, LINK_ROOT = 1;

    /// <summary>The harness prim as the root "p1" plus a child "p2" at link 2.</summary>
    private static SceneObjectPart[] TwoPrimLinkset(SchedulerHarness h)
    {
        h.Prim.Name = "p1";
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        var group = h.Prim.ParentGroup;
        Assert.Equal(2, group.PrimCount);
        var parts = new[] { group.GetLinkNumPart(1), group.GetLinkNumPart(2) };
        Assert.Same(h.Prim, parts[0]);
        parts[1].Name = "p2";
        return parts;
    }

    private static ScenePresence Avatar(SchedulerHarness h, string first)
    {
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        acd.firstname = first;
        acd.lastname = "Sitter";
        return SceneHelpers.AddScenePresence(h.Scene, acd);
    }

    /// <summary>Seat an avatar on a part through the scene's own sit request.</summary>
    private static void Sit(ScenePresence sp, SceneObjectPart part)
    {
        sp.AbsolutePosition = part.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, part.UUID, Vector3.Zero);
        Assert.True(sp.ParentID != 0, sp.Name + " did not sit");
    }

    /// <summary>A two-prim object with "Ann Sitter" seated first (on the child) and "Bob Sitter" second (on the
    /// root). Bob is added to the scene first, so presence order and sit order differ.</summary>
    private static (SceneObjectPart[] parts, ScenePresence ann, ScenePresence bob) Seated(SchedulerHarness h)
    {
        var parts = TwoPrimLinkset(h);
        var bob = Avatar(h, "Bob");
        var ann = Avatar(h, "Ann");
        Sit(ann, parts[1]);
        Sit(bob, parts[0]);
        return (parts, ann, bob);
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private static void Receivers(SchedulerHarness h, SceneObjectPart[] parts)
    {
        foreach (var p in parts)
            h.RezScriptInto(p, "default { state_entry() { llSay(0, \"ready " + p.Name + "\"); } " +
                               "link_message(integer s, integer n, string m, key k) { llSay(0, \"" + p.Name + " got \" + m); } }");
        WaitFor(h, said => parts.All(p => said.Contains("ready " + p.Name)), 30);
        foreach (var p in parts)
            Assert.True(h.Said.Contains("ready " + p.Name), $"receiver in {p.Name} never started: [{string.Join(" | ", h.Said)}]");
    }

    private void Run(SchedulerHarness h, SceneObjectPart sender, string body)
    {
        h.RezScriptInto(sender, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the sender never ran: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        // Under load a delivery can still be queued when the 500 ms window ends; finish what is queued
        // (the window above is unchanged, so "never reaches" still means nothing arrived in it or after).
        h.PumpUntilIdle(TimeSpan.FromSeconds(20));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    private static float[] Nums(string lsl)
        => lsl.Trim('<', '>').Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();

    private static void Near(Vector3 expected, string lsl)
    {
        var n = Nums(lsl);
        Assert.True(Vector3.Distance(expected, new Vector3(n[0], n[1], n[2])) < 0.001f, $"expected {expected}, got {lsl}");
    }

    private static void Near(Quaternion expected, Quaternion actual)
        => Assert.True(Math.Abs(Quaternion.Dot(expected, actual)) > 0.9999f, $"expected {expected}, got {actual}");

    private static void Near(Quaternion expected, string lsl)
    {
        var n = Nums(lsl);
        Near(expected, new Quaternion(n[0], n[1], n[2], n[3]));
    }

    [Fact]
    public void SittersAreTheLinksAfterThePrimsInTheOrderTheySat()
    {
        using var h = new SchedulerHarness();
        var (parts, ann, bob) = Seated(h);
        Run(h, parts[0], "llSay(0, \"n=\" + (string)llGetNumberOfPrims()); " +
                         "llSay(0, \"k3=\" + (string)llGetLinkKey(3)); llSay(0, \"k4=\" + (string)llGetLinkKey(4)); " +
                         "llSay(0, \"k5=\" + (string)llGetLinkKey(5)); " +
                         "llSay(0, \"n3=\" + llGetLinkName(3)); llSay(0, \"n4=\" + llGetLinkName(4)); " +
                         "llSay(0, \"k2=\" + (string)llGetLinkKey(2));");
        Assert.Equal("4", Line(h, "n="));
        Assert.Equal(ann.UUID.ToString(), Line(h, "k3="));
        Assert.Equal(bob.UUID.ToString(), Line(h, "k4="));
        Assert.Equal(UUID.Zero.ToString(), Line(h, "k5="));
        Assert.Equal("Ann Sitter", Line(h, "n3="));
        Assert.Equal("Bob Sitter", Line(h, "n4="));
        Assert.Equal(parts[1].UUID.ToString(), Line(h, "k2="));
    }

    [Fact]
    public void PosLocalAndRotLocalMoveTheSitterAndTellTheViewers()
    {
        using var h = new SchedulerHarness();
        var (parts, ann, bob) = Seated(h);
        var bobOffset = bob.OffsetPosition;
        var bobRot = bob.Rotation;
        var p1 = parts[0].AbsolutePosition;
        var p2 = parts[1].OffsetPosition;
        var target = new Vector3(0.5f, -1.25f, 0.75f);
        var targetRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);

        // A viewer (Bob's) sees Ann's terse update carry the new offset and rotation.
        var seen = new List<(Vector3 pos, Quaternion rot)>();
        ((TestClient)bob.ControllingClient).OnReceivedEntityUpdate += (e, flags) =>
        {
            if (e is ScenePresence sp && sp.UUID == ann.UUID && (flags & PrimUpdateFlags.Position) != 0)
                lock (seen) seen.Add((sp.OffsetPosition, sp.Rotation));
        };

        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(3, [PRIM_POS_LOCAL, <0.5, -1.25, 0.75>, " +
                         "PRIM_ROT_LOCAL, llEuler2Rot(<0, 0, PI_BY_TWO>)]);");

        Assert.Equal(target, ann.OffsetPosition);
        Near(targetRot, ann.Rotation);
        lock (seen)
            Assert.Contains(seen, s => s.pos == target && Math.Abs(Quaternion.Dot(s.rot, targetRot)) > 0.9999f);
        Assert.Equal(bobOffset, bob.OffsetPosition);
        Assert.Equal(bobRot, bob.Rotation);
        Assert.Equal(p1, parts[0].AbsolutePosition);
        Assert.Equal(p2, parts[1].OffsetPosition);
        Assert.NotEqual(0u, ann.ParentID);
    }

    [Fact]
    public void PrimRotationOnASitterIsTheRootRotationTimesTheGivenOne()
    {
        using var h = new SchedulerHarness();
        var (parts, ann, _) = Seated(h);
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 4);
        parts[0].UpdateRotation(rootRot);
        var q = Quaternion.CreateFromEulers((float)Math.PI / 2, 0, 0);
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(3, [PRIM_ROTATION, llEuler2Rot(<PI_BY_TWO, 0, 0>)]);");
        Near(parts[0].RotationOffset * q, ann.Rotation);
    }

    [Fact]
    public void PrimPositionFartherThan54MetresDoesNotMoveTheSitter()
    {
        using var h = new SchedulerHarness();
        var (parts, ann, _) = Seated(h);
        var before = ann.OffsetPosition;
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(3, [PRIM_POS_LOCAL, <60, 0, 0>]);");
        Assert.Equal(before, ann.OffsetPosition);
    }

    [Fact]
    public void GetLinkPrimitiveParamsOnASitterReturnsHalcyonsAvatarValues()
    {
        using var h = new SchedulerHarness();
        var (parts, ann, _) = Seated(h);
        string P(string tag, string rules) =>
            $"llSay(0, \"{tag}=\" + llDumpList2String(llGetLinkPrimitiveParams(3, [{rules}]), \"|\")); " +
            $"llSay(0, \"{tag}#=\" + (string)llGetListLength(llGetLinkPrimitiveParams(3, [{rules}]))); ";
        Run(h, parts[0],
            P("name", "PRIM_NAME") + P("misc", "PRIM_DESC, PRIM_MATERIAL, PRIM_TEMP_ON_REZ, PRIM_PHANTOM") +
            P("size", "PRIM_SIZE") + P("pos", "PRIM_POSITION") + P("posl", "PRIM_POS_LOCAL") +
            P("rot", "PRIM_ROTATION") + P("type", "PRIM_TYPE") + P("slice", "PRIM_SLICE") + P("text", "PRIM_TEXT") +
            P("light", "PRIM_POINT_LIGHT") + P("flex", "PRIM_FLEXIBLE") + P("sit", "PRIM_SIT_TARGET") +
            P("color", "PRIM_COLOR, ALL_SIDES, PRIM_NAME") + P("rotl", "PRIM_ROT_LOCAL") + P("phys", "PRIM_PHYSICS") +
            "llSay(0, \"agentsize=\" + (string)llGetAgentSize(llGetLinkKey(3)));");

        Assert.Equal("Ann Sitter", Line(h, "name="));
        Assert.Equal("|4|0|0", Line(h, "misc="));
        var agentSize = Nums(Line(h, "agentsize="));
        Near(new Vector3(agentSize[0], agentSize[1], agentSize[2]), Line(h, "size="));
        Near(ann.AbsolutePosition, Line(h, "pos="));
        Near(ann.AbsolutePosition - parts[0].AbsolutePosition, Line(h, "posl="));
        Near(ann.GetWorldRotation(), Line(h, "rot="));
        Assert.Equal("7", Line(h, "type#="));
        Assert.StartsWith("0|0|", Line(h, "type="));
        Assert.Equal("1", Line(h, "slice#="));
        Assert.Equal("3", Line(h, "text#="));
        Assert.Equal("5", Line(h, "light#="));
        Assert.Equal("7", Line(h, "flex#="));
        Assert.Equal("3", Line(h, "sit#="));
        Assert.StartsWith("0|", Line(h, "sit="));
        // Texture rules consume their face and return nothing for an avatar; the next rule still reads.
        Assert.Equal("Ann Sitter", Line(h, "color="));
        // PRIM_ROT_LOCAL: the sitter's rotation relative to the root (the root is not turned here).
        Assert.Equal("1", Line(h, "rotl#="));
        Near(ann.Rotation, Line(h, "rotl="));
        Assert.Equal("0", Line(h, "phys#="));
        Assert.Contains(h.Said, s => s.Contains("texture info cannot be accessed for avatars"));
    }

    // sender link, selector, the prims it must select, the sitters it must select (the prim-params functions)
    public static TheoryData<int, int, string[], string[]> Selectors => new()
    {
        { 1, LINK_SET,          new[] { "p1", "p2" }, new[] { "Ann", "Bob" } },
        { 1, LINK_ALL_OTHERS,   new[] { "p2" },       new[] { "Ann", "Bob" } },
        { 2, LINK_ALL_OTHERS,   new[] { "p1" },       new[] { "Ann", "Bob" } },
        { 1, LINK_ALL_CHILDREN, new[] { "p2" },       new[] { "Ann", "Bob" } },
        { 1, LINK_THIS,         new[] { "p1" },       new string[0] },
        { 1, LINK_ROOT,         new[] { "p1" },       new string[0] },
        { 1, 0,                 new[] { "p1" },       new string[0] },
        { 1, 2,                 new[] { "p2" },       new string[0] },
        { 1, 3,                 new string[0],        new[] { "Ann" } },
        { 2, 4,                 new string[0],        new[] { "Bob" } },
        { 1, 5,                 new string[0],        new string[0] },
    };

    [Theory]
    [MemberData(nameof(Selectors))]
    public void SetLinkPrimitiveParamsTakesInSittersPerTheSelector(int senderLink, int link, string[] prims, string[] sitters)
    {
        using var h = new SchedulerHarness();
        var (parts, ann, bob) = Seated(h);
        foreach (var p in parts) p.Description = "before";
        var target = new Vector3(0.25f, 0.5f, 0.75f);
        Run(h, parts[senderLink - 1], $"llSetLinkPrimitiveParamsFast({link}, [PRIM_DESC, \"after\", PRIM_POS_LOCAL, <0.25, 0.5, 0.75>]);");
        Assert.Equal(prims, parts.Where(p => p.Description == "after").Select(p => p.Name).OrderBy(s => s).ToArray());
        Assert.Equal(sitters, new[] { ann, bob }.Where(a => a.OffsetPosition == target).Select(a => a.Firstname).OrderBy(s => s).ToArray());
    }

    [Theory]
    [MemberData(nameof(Selectors))]
    public void GetLinkPrimitiveParamsTakesInSittersPerTheSelector(int senderLink, int link, string[] prims, string[] sitters)
    {
        using var h = new SchedulerHarness();
        var (parts, _, _) = Seated(h);
        Run(h, parts[senderLink - 1], $"llSay(0, \"names=\" + llDumpList2String(llGetLinkPrimitiveParams({link}, [PRIM_NAME]), \",\"));");
        var read = Line(h, "names=").Split(',', StringSplitOptions.RemoveEmptyEntries);
        // Prims come first in link order, then the sitters in theirs.
        Assert.Equal(prims.Concat(sitters.Select(s => s + " Sitter")).ToArray(), read);
    }

    [Theory]
    [MemberData(nameof(Selectors))]
    public void MessageLinkedNeverReachesSitters(int senderLink, int link, string[] prims, string[] sitters)
    {
        _ = sitters;
        using var h = new SchedulerHarness();
        var (parts, _, _) = Seated(h);
        Receivers(h, parts);
        Run(h, parts[senderLink - 1], $"llMessageLinked({link}, 0, \"m\", NULL_KEY);");
        var got = h.Said.Where(s => s.EndsWith(" got m")).Select(s => s.Split(' ')[0]).OrderBy(s => s).ToArray();
        Assert.Equal(prims, got);
    }

    [Fact]
    public void WhenASitterStandsTheLaterSittersMoveUpAndThePrimsKeepTheirNumbers()
    {
        using var h = new SchedulerHarness();
        var (parts, ann, bob) = Seated(h);
        ann.StandUp();
        Assert.Equal(0u, ann.ParentID);
        var cid = Avatar(h, "Cid");
        Sit(cid, parts[1]);
        Run(h, parts[0], "llSay(0, \"n=\" + (string)llGetNumberOfPrims()); " +
                         "llSay(0, \"k1=\" + (string)llGetLinkKey(1)); llSay(0, \"k2=\" + (string)llGetLinkKey(2)); " +
                         "llSay(0, \"k3=\" + (string)llGetLinkKey(3)); llSay(0, \"k4=\" + (string)llGetLinkKey(4)); " +
                         "llSay(0, \"k5=\" + (string)llGetLinkKey(5));");
        Assert.Equal("4", Line(h, "n="));
        Assert.Equal(parts[0].UUID.ToString(), Line(h, "k1="));
        Assert.Equal(parts[1].UUID.ToString(), Line(h, "k2="));
        Assert.Equal(bob.UUID.ToString(), Line(h, "k3="));
        Assert.Equal(cid.UUID.ToString(), Line(h, "k4="));
        Assert.Equal(UUID.Zero.ToString(), Line(h, "k5="));
        Assert.Equal(1, parts[0].LinkNum);
        Assert.Equal(2, parts[1].LinkNum);
    }

    [Fact]
    public void WithNoSittersNothingPastThePrimsIsALink()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Run(h, parts[0], "llSay(0, \"n=\" + (string)llGetNumberOfPrims()); " +
                         "llSay(0, \"k3=\" + (string)llGetLinkKey(3)); llSay(0, \"n3=\" + llGetLinkName(3)); " +
                         "llSay(0, \"set=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_NAME]), \",\")); " +
                         "llSay(0, \"k=\" + (string)llGetLinkKey(LINK_SET));");
        Assert.Equal("2", Line(h, "n="));
        Assert.Equal(UUID.Zero.ToString(), Line(h, "k3="));
        Assert.Equal(UUID.Zero.ToString(), Line(h, "n3="));   // SL: "If link is out of bounds, NULL_KEY is returned."
        Assert.Equal("p1,p2", Line(h, "set="));
        Assert.Equal(UUID.Zero.ToString(), Line(h, "k="));
    }
}
