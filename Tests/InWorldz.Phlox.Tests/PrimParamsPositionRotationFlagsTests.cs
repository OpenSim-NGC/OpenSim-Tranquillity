using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PRIM_POS_LOCAL and PRIM_ROTATION read back from a prim (Halcyon GetPartLocalPos and GetPartRot); PRIM_ROT_LOCAL
/// reads a root's and a child's rotation; PRIM_POSITION sets as PRIM_POS_LOCAL does, within the same move caps, once
/// per rule; PRIM_PHYSICS, PRIM_PHANTOM and PRIM_TEMP_ON_REZ set the whole object's flags, even through a child
/// link, and read them back.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class PrimParamsPositionRotationFlagsTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsPositionRotationFlagsTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

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

    /// <summary>A viewer in the scene, so changed prims queue their updates for it.</summary>
    private static void Viewer(SchedulerHarness h) => SceneHelpers.AddScenePresence(h.Scene, UUID.Random());

    private static void ClearUpdates(SceneObjectPart[] parts)
    {
        foreach (var p in parts) p.UpdateFlag = PrimUpdateFlags.None;
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private void Run(SchedulerHarness h, SceneObjectPart sender, string body)
    {
        h.RezScriptInto(sender, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    private static string[] Fields(SchedulerHarness h, string prefix, int count)
    {
        var f = Line(h, prefix).Split('|');
        Assert.True(f.Length == count, $"{prefix} expected {count} values, got {f.Length}: {Line(h, prefix)}");
        return f;
    }

    private static IReadOnlyList<string> Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToList();

    private static float[] Nums(string lsl)
        => lsl.Trim('<', '>').Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static void Near(Vector3 expected, string lsl, float tolerance = 0.001f)
    {
        var n = Nums(lsl);
        Near(expected, new Vector3(n[0], n[1], n[2]), tolerance);
    }

    private static void Near(Quaternion expected, Quaternion actual)
        => Assert.True(Math.Abs(Quaternion.Dot(expected, actual)) > 0.9999f, $"expected {expected}, got {actual}");

    private static void Near(Quaternion expected, string lsl)
    {
        var n = Nums(lsl);
        Near(expected, new Quaternion(n[0], n[1], n[2], n[3]));
    }

    private static bool Has(SceneObjectPart p, PrimFlags flag) => (p.GetEffectiveObjectFlags() & (uint)flag) != 0;

    // ── PRIM_POS_LOCAL, PRIM_ROTATION and PRIM_ROT_LOCAL read ─────────────────

    [Fact]
    public void PrimPosLocalAndPrimRotationReadFromTheRootAndAChildOfATurnedRoot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        var childRot = Quaternion.CreateFromEulers((float)Math.PI / 4, 0, 0);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(100, 110, 30));
        parts[0].UpdateRotation(rootRot);
        parts[1].UpdateOffSet(new Vector3(1.5f, -0.5f, 2f));
        parts[1].UpdateRotation(childRot);

        Run(h, parts[0],
            "llSay(0, \"r=\" + llDumpList2String(llGetLinkPrimitiveParams(1, [PRIM_POS_LOCAL, PRIM_ROTATION]), \"|\")); " +
            "llSay(0, \"c=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_POS_LOCAL, PRIM_ROTATION]), \"|\")); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_POS_LOCAL, PRIM_ROTATION]), \"|\")); " +
            "llSay(0, \"ll=\" + (string)llGetLocalPos() + \"|\" + (string)llGetRot());");

        // The root: its region position (SL llGetLocalPos, Halcyon GetPartLocalPos) and the object's rotation.
        var r = Fields(h, "r=", 2);
        Near(parts[0].AbsolutePosition, r[0]);
        Near(new Vector3(100, 110, 30), r[0]);
        Near(parts[0].ParentGroup.GroupRotation, r[1]);
        Near(rootRot, r[1]);
        Assert.Equal(Line(h, "r="), Line(h, "own="));
        // llGetLocalPos and llGetRot on the root agree.
        var ll = Fields(h, "ll=", 2);
        Near(new Vector3(100, 110, 30), ll[0]);
        Near(rootRot, ll[1]);

        // The child: its offset from the root, in the root's frame, and its region rotation (root's times its own).
        var c = Fields(h, "c=", 2);
        Near(new Vector3(1.5f, -0.5f, 2f), c[0]);
        Near(parts[1].OffsetPosition, c[0]);
        Near(parts[1].GetWorldRotation(), c[1]);
        Near(rootRot * childRot, c[1]);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimPosLocalAndPrimRotationReadInAChildMatchItsOwnLlGetLocalPosAndLlGetRot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].UpdateRotation(Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 3));
        parts[1].UpdateOffSet(new Vector3(0.25f, 1f, -0.75f));
        parts[1].UpdateRotation(Quaternion.CreateFromEulers(0, (float)Math.PI / 6, 0));

        Run(h, parts[1],
            "llSay(0, \"pp=\" + llDumpList2String(llGetPrimitiveParams([PRIM_POS_LOCAL, PRIM_ROTATION]), \"|\")); " +
            "llSay(0, \"ll=\" + (string)llGetLocalPos() + \"|\" + (string)llGetRot());");

        var pp = Fields(h, "pp=", 2);
        var ll = Fields(h, "ll=", 2);
        var p = Nums(ll[0]);
        var q = Nums(ll[1]);
        Near(new Vector3(p[0], p[1], p[2]), pp[0]);
        Near(new Quaternion(q[0], q[1], q[2], q[3]), pp[1]);
        Near(parts[1].OffsetPosition, pp[0]);
        Near(parts[1].GetWorldRotation(), pp[1]);
    }

    [Fact]
    public void PrimRotLocalReadsTheRootsRotationAndAChildsRotationRelativeToTheRoot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        var childRot = Quaternion.CreateFromEulers((float)Math.PI / 4, 0, 0);
        parts[0].UpdateRotation(rootRot);
        parts[1].UpdateRotation(childRot);

        Run(h, parts[0],
            "llSay(0, \"r=\" + llDumpList2String(llGetLinkPrimitiveParams(1, [PRIM_ROT_LOCAL]), \"|\")); " +
            "llSay(0, \"c=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_ROT_LOCAL]), \"|\"));");

        var r = Fields(h, "r=", 1);
        Near(rootRot, r[0]);
        Near(parts[0].RotationOffset, r[0]);
        var c = Fields(h, "c=", 1);
        Near(childRot, c[0]);
        Near(parts[1].RotationOffset, c[0]);
    }

    // ── PRIM_POSITION set ─────────────────────────────────────────────────────

    [Fact]
    public void PrimPositionOnAnUnattachedRootMovesAtMostTenMetres()
    {
        using var h = new SchedulerHarness();
        var start = h.Prim.AbsolutePosition;
        Run(h, h.Prim, "llSetPrimitiveParams([PRIM_POSITION, llGetPos() + <30, 0, 0>]);");
        Near(start + new Vector3(10, 0, 0), h.Prim.AbsolutePosition);
    }

    [Fact]
    public void PrimPositionRepeatedInOneListMovesTenMetresPerRule()
    {
        using var h = new SchedulerHarness();
        var start = h.Prim.AbsolutePosition;
        Run(h, h.Prim,
            "vector far = llGetPos() + <0, 45, 0>; " +
            "llSetPrimitiveParams([PRIM_POSITION, far, PRIM_POSITION, far, PRIM_POSITION, far]);");
        Near(start + new Vector3(0, 30, 0), h.Prim.AbsolutePosition);
    }

    [Fact]
    public void PrimPositionOnAChildSetsItsOffsetFromATurnedRoot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Viewer(h);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(120, 90, 40));
        parts[0].UpdateRotation(Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2));
        var rootPos = parts[0].AbsolutePosition;
        ClearUpdates(parts);

        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(2, [PRIM_POSITION, <0.5, -0.25, 1>, PRIM_NAME, \"moved\"]);");

        var offset = new Vector3(0.5f, -0.25f, 1f);
        Near(offset, parts[1].OffsetPosition);
        Near(rootPos + offset * parts[0].RotationOffset, parts[1].AbsolutePosition);
        Near(rootPos, parts[0].AbsolutePosition);
        Assert.Equal("moved", parts[1].Name);
        Assert.NotEqual(PrimUpdateFlags.None, parts[1].UpdateFlag);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    // ── PRIM_PHYSICS, PRIM_PHANTOM, PRIM_TEMP_ON_REZ ─────────────────────────

    [Fact]
    public void PhysicsPhantomAndTempOnRezSetThroughAChildApplyToTheWholeObjectAndReadBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var group = parts[0].ParentGroup;
        Viewer(h);
        ClearUpdates(parts);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS, TRUE, PRIM_PHANTOM, TRUE, PRIM_TEMP_ON_REZ, TRUE]); " +
            "llSay(0, \"on1=\" + llDumpList2String(llGetLinkPrimitiveParams(1, [PRIM_PHYSICS, PRIM_PHANTOM, PRIM_TEMP_ON_REZ]), \"|\")); " +
            "llSay(0, \"on2=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_PHYSICS, PRIM_PHANTOM, PRIM_TEMP_ON_REZ]), \"|\")); " +
            "llSay(0, \"status=\" + (string)llGetStatus(STATUS_PHYSICS) + \"|\" + (string)llGetStatus(STATUS_PHANTOM));");

        Assert.True(group.UsesPhysics, "physics was not set on the object");
        Assert.True(group.IsPhantom, "phantom was not set on the object");
        Assert.True(group.IsTemporary, "temp-on-rez was not set on the object");
        foreach (var p in parts)
        {
            Assert.True(Has(p, PrimFlags.Physics), $"{p.Name} is not physical");
            Assert.True(Has(p, PrimFlags.Phantom), $"{p.Name} is not phantom");
            Assert.True(Has(p, PrimFlags.TemporaryOnRez), $"{p.Name} is not temporary");
            Assert.NotEqual(PrimUpdateFlags.None, p.UpdateFlag);
        }
        Assert.Equal("1|1|1", Line(h, "on1="));
        Assert.Equal("1|1|1", Line(h, "on2="));
        Assert.Equal("1|1", Line(h, "status="));
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PhysicsPhantomAndTempOnRezTurnOffThroughTheRootAndReadBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var group = parts[0].ParentGroup;
        group.UpdateFlags(true, true, true, false);
        Assert.True(group.UsesPhysics && group.IsPhantom && group.IsTemporary);
        Viewer(h);
        ClearUpdates(parts);

        Run(h, parts[0],
            "llSay(0, \"before=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_PHYSICS, PRIM_PHANTOM, PRIM_TEMP_ON_REZ]), \"|\")); " +
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_TEMP_ON_REZ, FALSE, PRIM_PHANTOM, FALSE, PRIM_PHYSICS, FALSE]); " +
            "llSay(0, \"after=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_PHYSICS, PRIM_PHANTOM, PRIM_TEMP_ON_REZ]), \"|\"));");

        Assert.Equal("1|1|1", Line(h, "before="));
        Assert.False(group.UsesPhysics);
        Assert.False(group.IsPhantom);
        Assert.False(group.IsTemporary);
        foreach (var p in parts)
        {
            Assert.False(Has(p, PrimFlags.Physics) || Has(p, PrimFlags.Phantom) || Has(p, PrimFlags.TemporaryOnRez),
                $"{p.Name} still carries a flag");
            Assert.NotEqual(PrimUpdateFlags.None, p.UpdateFlag);
        }
        Assert.Equal("0|0|0|0|0|0", Line(h, "after="));
    }

    // ── A long list mixing them with other rules ───────────────────────────────

    [Fact]
    public void ALongListMixingTheseRulesWithOthersAppliesEveryRule()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var group = parts[0].ParentGroup;
        Viewer(h);
        var start = parts[0].AbsolutePosition;
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        ClearUpdates(parts);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [" +
            "PRIM_TEXT, \"root\", <1, 1, 1>, 1.0, " +
            "PRIM_PHANTOM, TRUE, " +
            "PRIM_POSITION, llGetPos() + <2, 3, 4>, " +
            "PRIM_ROTATION, llEuler2Rot(<0, 0, PI_BY_TWO>), " +
            "PRIM_TEMP_ON_REZ, TRUE, " +
            "PRIM_LINK_TARGET, 2, " +
            "PRIM_POS_LOCAL, <0, 0, 1.5>, " +
            "PRIM_PHYSICS, TRUE, " +
            "PRIM_TEXT, \"child\", <0, 1, 0>, 1.0, " +
            "PRIM_ROT_LOCAL, llEuler2Rot(<PI_BY_TWO, 0, 0>), " +
            "PRIM_NAME, \"last\"]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_POS_LOCAL, PRIM_PHANTOM, PRIM_TEXT, " +
            "PRIM_TEMP_ON_REZ, PRIM_PHYSICS, PRIM_NAME, PRIM_LINK_TARGET, 1, PRIM_POS_LOCAL, PRIM_ROTATION]), \"|\"));");

        Assert.Equal("root", parts[0].Text);
        Near(start + new Vector3(2, 3, 4), parts[0].AbsolutePosition);
        Near(rootRot, group.GroupRotation);
        Near(new Vector3(0, 0, 1.5f), parts[1].OffsetPosition);
        Assert.Equal("child", parts[1].Text);
        Near(Quaternion.CreateFromEulers((float)Math.PI / 2, 0, 0), parts[1].RotationOffset);
        Assert.Equal("last", parts[1].Name);
        Assert.True(group.IsPhantom, "phantom");
        Assert.True(group.IsTemporary, "temp-on-rez");
        Assert.True(group.UsesPhysics, "physics");

        // Link 2: pos, phantom, text (3), temp, physics, name; then link 1: pos, rotation.
        var got = Fields(h, "got=", 10);
        Near(new Vector3(0, 0, 1.5f), got[0]);
        Assert.Equal("1", got[1]);
        Assert.Equal("child", got[2]);
        Assert.Equal("1", got[5]);
        Assert.Equal("1", got[6]);
        Assert.Equal("last", got[7]);
        Near(start + new Vector3(2, 3, 4), got[8]);
        Near(rootRot, got[9]);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }
}
