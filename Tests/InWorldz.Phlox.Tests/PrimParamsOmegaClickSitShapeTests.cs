using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PRIM_OMEGA, PRIM_CLICK_ACTION, PRIM_SIT_TARGET and PRIM_PHYSICS_SHAPE_TYPE set and read back on a root and on a
/// child, each against the scene and against the ll function that does the same job (llTargetOmega,
/// llSetClickAction, llSitTarget / llLinkSitTarget); SL's PRIM_PHYSICS_SHAPE_TYPE rule that a root may not be NONE;
/// llGetLocalPos in an unattached root, an attachment's root and a child; llGetRot, PRIM_ROTATION and PRIM_POS_LOCAL on
/// an attachment's root; and a long list mixing these rules with earlier ones.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class PrimParamsOmegaClickSitShapeTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsOmegaClickSitShapeTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const float TwoPi = 6.28318548f;

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

    /// <summary>
    /// The harness object worn on a new avatar's chest: its group position and AttachedPos are the offset from the
    /// attach point, its root turned by <paramref name="objectRot"/> and the wearer by <paramref name="wearerRot"/>.
    /// </summary>
    private static ScenePresence Wear(SchedulerHarness h, Vector3 offset, Quaternion objectRot, Quaternion wearerRot)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = (uint)AttachmentPoint.Chest;
        sp.AddAttachment(sog);
        sog.AbsolutePosition = offset;
        sog.RootPart.AttachedPos = offset;
        sog.RootPart.UpdateRotation(objectRot);
        sp.Rotation = wearerRot;
        return sp;
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

    private static float Num(string lsl) => float.Parse(lsl.Trim(), CultureInfo.InvariantCulture);

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

    private static void Near(float expected, string lsl) =>
        Assert.True(Math.Abs(expected - Num(lsl)) < 0.001f, $"expected {expected}, got {lsl}");

    // ── PRIM_OMEGA ────────────────────────────────────────────────────────────

    [Fact]
    public void PrimOmegaSetsTheRootsAndAChildsSpinAndReadsBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Viewer(h);
        ClearUpdates(parts);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_OMEGA, <0, 0, 1>, TWO_PI, 1.0]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_OMEGA, <1, 0, 0>, PI, 1.0]); " +
            "llSay(0, \"r=\" + llDumpList2String(llGetLinkPrimitiveParams(1, [PRIM_OMEGA]), \"|\")); " +
            "llSay(0, \"c=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_OMEGA]), \"|\"));");

        // Each prim spins by itself: axis * spinrate on that prim (Halcyon PrimTargetOmega), sent as a terse update.
        Near(new Vector3(0, 0, TwoPi), parts[0].AngularVelocity);
        Near(new Vector3((float)Math.PI, 0, 0), parts[1].AngularVelocity);
        Assert.NotEqual(PrimUpdateFlags.None, parts[0].UpdateFlag);
        Assert.NotEqual(PrimUpdateFlags.None, parts[1].UpdateFlag);

        // Read back in SL's form: the normalised axis, the spinrate times the axis's length, the gain.
        var r = Fields(h, "r=", 3);
        Near(new Vector3(0, 0, 1), r[0]);
        Near(TwoPi, r[1]);
        Near(1f, r[2]);
        var c = Fields(h, "c=", 3);
        Near(new Vector3(1, 0, 0), c[0]);
        Near((float)Math.PI, c[1]);
        Near(1f, c[2]);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void LlTargetOmegaInAChildSpinsThatChildAsPrimOmegaDoes()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        // An axis that is not a unit vector keeps its length (SL: normalise it yourself); gain scales nothing on a
        // non-physical prim.
        Run(h, parts[1],
            "llTargetOmega(<0, 2, 0>, 1.5, 0.5); " +
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_OMEGA, <0, 2, 0>, 1.5, 0.5]); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_OMEGA]), \"|\")); " +
            "llSay(0, \"root=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_ROOT, [PRIM_OMEGA]), \"|\"));");

        Near(new Vector3(0, 3, 0), parts[1].AngularVelocity);
        Near(parts[1].AngularVelocity, parts[0].AngularVelocity);
        Assert.Equal(Line(h, "own="), Line(h, "root="));
        var own = Fields(h, "own=", 3);
        Near(new Vector3(0, 1, 0), own[0]);
        Near(3f, own[1]);
        Near(0.5f, own[2]);
    }

    [Fact]
    public void AGainOfZeroStopsTheSpinThroughTheRuleAndLlTargetOmega()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].UpdateAngularVelocity(new Vector3(0, 0, 2));
        parts[1].UpdateAngularVelocity(new Vector3(1, 0, 0));

        Run(h, parts[1],
            "llTargetOmega(<1, 0, 0>, 1.0, 0.0); " +
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_OMEGA, <0, 0, 1>, 2.0, 0.0]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_OMEGA]), \"|\"));");

        Near(Vector3.Zero, parts[0].AngularVelocity);
        Near(Vector3.Zero, parts[1].AngularVelocity);
        // The values read are the ones set, gain 0 included.
        var got = Fields(h, "got=", 6);
        Near(new Vector3(0, 0, 1), got[0]);
        Near(2f, got[1]);
        Near(0f, got[2]);
        Near(new Vector3(1, 0, 0), got[3]);
        Near(1f, got[4]);
        Near(0f, got[5]);
    }

    // ── PRIM_CLICK_ACTION ─────────────────────────────────────────────────────

    [Fact]
    public void PrimClickActionSetsTheRootsAndAChildsActionAndReadsBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Viewer(h);
        ClearUpdates(parts);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_CLICK_ACTION, CLICK_ACTION_SIT]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_CLICK_ACTION, CLICK_ACTION_OPEN]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_CLICK_ACTION]), \"|\"));");

        Assert.Equal(1, parts[0].ClickAction);
        Assert.Equal(4, parts[1].ClickAction);
        Assert.NotEqual(PrimUpdateFlags.None, parts[0].UpdateFlag);
        Assert.NotEqual(PrimUpdateFlags.None, parts[1].UpdateFlag);
        Assert.Equal("1|4", Line(h, "got="));
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimClickActionReadsWhatLlSetClickActionSetInAChild()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h, parts[1],
            "llSetClickAction(CLICK_ACTION_OPEN); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_CLICK_ACTION]), \"|\")); " +
            "llSetPrimitiveParams([PRIM_CLICK_ACTION, CLICK_ACTION_TOUCH]); " +
            "llSay(0, \"after=\" + llDumpList2String(llGetPrimitiveParams([PRIM_CLICK_ACTION]), \"|\"));");

        Assert.Equal("4", Line(h, "own="));
        Assert.Equal("0", Line(h, "after="));
        Assert.Equal(0, parts[1].ClickAction);
        Assert.Equal(0, parts[0].ClickAction);
    }

    // ── PRIM_SIT_TARGET ───────────────────────────────────────────────────────

    [Fact]
    public void PrimSitTargetSetsTheRootsAndAChildsTargetAndReadsBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var quarter = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);

        // A zero offset may be set explicitly through the rule (SL), here with a turned rotation.
        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_SIT_TARGET, TRUE, <0, 0, 0.5>, ZERO_ROTATION]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_SIT_TARGET, TRUE, ZERO_VECTOR, llEuler2Rot(<0, 0, PI_BY_TWO>)]); " +
            "llSay(0, \"r=\" + llDumpList2String(llGetLinkPrimitiveParams(1, [PRIM_SIT_TARGET]), \"|\")); " +
            "llSay(0, \"c=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_SIT_TARGET]), \"|\"));");

        Assert.True(parts[0].IsSitTargetSet, "the root has no sit target");
        Near(new Vector3(0, 0, 0.5f), parts[0].SitTargetPosition);
        Near(Quaternion.Identity, parts[0].SitTargetOrientation);
        Assert.True(parts[1].IsSitTargetSet, "the child has no sit target");
        Near(Vector3.Zero, parts[1].SitTargetPosition);
        Near(quarter, parts[1].SitTargetOrientation);

        var r = Fields(h, "r=", 3);
        Assert.Equal("1", r[0]);
        Near(new Vector3(0, 0, 0.5f), r[1]);
        Near(Quaternion.Identity, r[2]);
        var c = Fields(h, "c=", 3);
        Assert.Equal("1", c[0]);
        Near(Vector3.Zero, c[1]);
        Near(quarter, c[2]);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimSitTargetInactiveRemovesTheTarget()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[1].SitTargetPosition = new Vector3(1, 2, 3);
        parts[1].SitTargetOrientation = Quaternion.CreateFromEulers(0, 0, 1);

        Run(h, parts[0],
            "llSay(0, \"before=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_SIT_TARGET]), \"|\")); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_SIT_TARGET, FALSE, <1, 2, 3>, llEuler2Rot(<0, 0, 1>)]); " +
            "llSay(0, \"after=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_SIT_TARGET]), \"|\"));");

        var before = Fields(h, "before=", 3);
        Assert.Equal("1", before[0]);
        Near(new Vector3(1, 2, 3), before[1]);
        Assert.False(parts[1].IsSitTargetSet, "the sit target is still set");
        var after = Fields(h, "after=", 3);
        Assert.Equal("0", after[0]);
        Near(Vector3.Zero, after[1]);
        Near(Quaternion.Identity, after[2]);
    }

    [Fact]
    public void LlSitTargetAndLlLinkSitTargetReadBackThroughPrimSitTarget()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var quarter = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);

        Run(h, parts[1],
            "llSitTarget(<0.25, 0, 0.75>, llEuler2Rot(<0, 0, PI_BY_TWO>)); " +
            "llLinkSitTarget(LINK_ROOT, <0, 0, 1>, ZERO_ROTATION); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_SIT_TARGET]), \"|\")); " +
            "llSay(0, \"root=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_ROOT, [PRIM_SIT_TARGET]), \"|\"));");

        var own = Fields(h, "own=", 3);
        Assert.Equal("1", own[0]);
        Near(new Vector3(0.25f, 0, 0.75f), own[1]);
        Near(quarter, own[2]);
        Near(parts[1].SitTargetPosition, own[1]);
        var root = Fields(h, "root=", 3);
        Assert.Equal("1", root[0]);
        Near(new Vector3(0, 0, 1), root[1]);
        Near(parts[0].SitTargetPosition, root[1]);
    }

    [Fact]
    public void LlSitTargetWithAZeroOffsetRemovesTheTargetEvenWhenTurned()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].SitTargetPosition = new Vector3(0, 0, 1);
        parts[1].SitTargetPosition = new Vector3(0, 0, 1);

        // SL: "If offset == <0.0, 0.0, 0.0> then the sit target is removed", for llSitTarget and llLinkSitTarget.
        Run(h, parts[1],
            "llSitTarget(ZERO_VECTOR, llEuler2Rot(<0, 0, PI_BY_TWO>)); " +
            "llLinkSitTarget(LINK_ROOT, ZERO_VECTOR, llEuler2Rot(<PI_BY_TWO, 0, 0>)); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_SIT_TARGET]), \"|\"));");

        Assert.False(parts[0].IsSitTargetSet, "the root still has a sit target");
        Assert.False(parts[1].IsSitTargetSet, "the child still has a sit target");
        var got = Fields(h, "got=", 6);
        Assert.Equal("0", got[0]);
        Assert.Equal("0", got[3]);
    }

    [Fact]
    public void PrimSitTargetActiveAtAZeroOffsetAndZeroRotationIsSetAndReadsBackExactly()
    {
        // SL PRIM_SIT_TARGET: "If it is nonzero the prim's sit target is set to the indicated offset and rotation" and
        // "Unlike llLinkSitTarget(), an offset of <0.0, 0.0, 0.0> may be explicitly set". The core keeps the on/off
        // state apart from the offset (SceneObjectPart.SetSitTarget), so nothing is added to the offset.
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(2, [PRIM_SIT_TARGET, TRUE, ZERO_VECTOR, ZERO_ROTATION]); " +
            "list got = llGetLinkPrimitiveParams(2, [PRIM_SIT_TARGET]); " +
            "llSay(0, \"active=\" + (string)llList2Integer(got, 0)); " +
            "llSay(0, \"same=\" + (string)(llList2Vector(got, 1) == ZERO_VECTOR && llList2Rot(got, 2) == ZERO_ROTATION));");

        Assert.True(parts[1].IsSitTargetSet, "the child has no sit target");
        Assert.Equal(Vector3.Zero, parts[1].SitTargetPosition);
        Assert.Equal(Quaternion.Identity, parts[1].SitTargetOrientation);
        Assert.Equal("1", Line(h, "active="));
        Assert.Equal("1", Line(h, "same="));
        Assert.False(parts[0].IsSitTargetSet, "the root got a sit target");
    }

    [Fact]
    public void AnyNonzeroActiveValueSetsTheTarget()
    {
        using var h = new SchedulerHarness();
        Run(h, h.Prim,
            "llSetPrimitiveParams([PRIM_SIT_TARGET, 7, ZERO_VECTOR, ZERO_ROTATION]); " +
            "llSay(0, \"seven=\" + (string)llList2Integer(llGetPrimitiveParams([PRIM_SIT_TARGET]), 0)); " +
            "llSetPrimitiveParams([PRIM_SIT_TARGET, -1, <0, 0, 1>, ZERO_ROTATION]); " +
            "llSay(0, \"minus=\" + (string)llList2Integer(llGetPrimitiveParams([PRIM_SIT_TARGET]), 0));");

        Assert.Equal("1", Line(h, "seven="));
        Assert.Equal("1", Line(h, "minus="));
        Assert.True(h.Prim.IsSitTargetSet);
    }

    [Fact]
    public void AZeroOffsetTargetFromTheRuleSeatsAnAvatarAndSurvivesTheObjectsXml()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(2, [PRIM_SIT_TARGET, TRUE, ZERO_VECTOR, ZERO_ROTATION]);");

        // The XML form is what a crossing, a take and rez and an archive carry (SceneObjectSerializer).
        var copy = OpenSim.Region.Framework.Scenes.Serialization.SceneObjectSerializer.FromXml2Format(
            OpenSim.Region.Framework.Scenes.Serialization.SceneObjectSerializer.ToXml2Format(parts[0].ParentGroup));
        Assert.True(copy.GetLinkNumPart(2).IsSitTargetSet, "the copy lost the sit target");
        Assert.Equal(Vector3.Zero, copy.GetLinkNumPart(2).SitTargetPosition);

        var sp = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        sp.AbsolutePosition = parts[0].AbsolutePosition + new Vector3(1, 0, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, parts[0].UUID, Vector3.Zero);
        Assert.Equal(parts[1].LocalId, sp.ParentID);
        Assert.Equal(sp.UUID, parts[1].SitTargetAvatar);

        h.ClearSaid(UUID.Zero);
        Run(h, parts[0], "llSay(0, \"on=\" + (string)llAvatarOnLinkSitTarget(2));");
        Assert.Equal(sp.UUID.ToString(), Line(h, "on="));
    }

    [Fact]
    public void LlSitTargetStillRemovesAZeroOffsetTargetTheRuleSet()
    {
        using var h = new SchedulerHarness();
        Run(h, h.Prim,
            "llSetPrimitiveParams([PRIM_SIT_TARGET, TRUE, ZERO_VECTOR, ZERO_ROTATION]); " +
            "llSitTarget(ZERO_VECTOR, ZERO_ROTATION); " +
            "llSay(0, \"after=\" + (string)llList2Integer(llGetPrimitiveParams([PRIM_SIT_TARGET]), 0));");

        Assert.Equal("0", Line(h, "after="));
        Assert.False(h.Prim.IsSitTargetSet);
    }

    [Fact]
    public void ASitTargetOffsetIsHeldTo300MetresOnEachAxis()
    {
        using var h = new SchedulerHarness();
        Run(h, h.Prim,
            "llSitTarget(<500, -400, 10>, ZERO_ROTATION); " +
            "llSay(0, \"ll=\" + llDumpList2String(llGetPrimitiveParams([PRIM_SIT_TARGET]), \"|\")); " +
            "llSetPrimitiveParams([PRIM_SIT_TARGET, TRUE, <-301, 5, 1000>, ZERO_ROTATION]); " +
            "llSay(0, \"rule=\" + llDumpList2String(llGetPrimitiveParams([PRIM_SIT_TARGET]), \"|\"));");

        Near(new Vector3(300, -300, 10), Fields(h, "ll=", 3)[1]);
        Near(new Vector3(-300, 5, 300), Fields(h, "rule=", 3)[1]);
        Near(new Vector3(-300, 5, 300), h.Prim.SitTargetPosition);
    }

    // ── PRIM_PHYSICS_SHAPE_TYPE ───────────────────────────────────────────────

    [Fact]
    public void PrimPhysicsShapeTypeSetsTheRootsAndAChildsTypeAndReadsBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Assert.Equal((byte)PhysShapeType.prim, parts[0].PhysicsShapeType);
        Assert.Equal((byte)PhysShapeType.prim, parts[1].PhysicsShapeType);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_CONVEX]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_NONE]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_PHYSICS_SHAPE_TYPE]), \"|\")); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_PRIM]); " +
            "llSay(0, \"back=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_PHYSICS_SHAPE_TYPE]), \"|\"));");

        Assert.Equal("2|1", Line(h, "got="));
        Assert.Equal("0", Line(h, "back="));
        Assert.Equal((byte)PhysShapeType.convex, parts[0].PhysicsShapeType);
        Assert.Equal((byte)PhysShapeType.prim, parts[1].PhysicsShapeType);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void ARootIsRefusedPhysicsShapeNoneAndKeepsItsType()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        // SL: PRIM_PHYSICS_SHAPE_NONE "cannot be applied to the root prim or avatars".
        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_CONVEX, " +
            "PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_NONE]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_NONE]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(LINK_SET, [PRIM_PHYSICS_SHAPE_TYPE]), \"|\"));");

        Assert.Equal((byte)PhysShapeType.convex, parts[0].PhysicsShapeType);
        Assert.Equal((byte)PhysShapeType.none, parts[1].PhysicsShapeType);
        Assert.Equal("2|1", Line(h, "got="));
    }

    [Fact]
    public void AnUndefinedPhysicsShapeTypeChangesNothing()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_CONVEX, " +
            "PRIM_PHYSICS_SHAPE_TYPE, 7, PRIM_PHYSICS_SHAPE_TYPE, -1, PRIM_NAME, \"after\"]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_PHYSICS_SHAPE_TYPE]), \"|\"));");

        Assert.Equal((byte)PhysShapeType.convex, parts[1].PhysicsShapeType);
        Assert.Equal("2", Line(h, "got="));
        Assert.Equal("after", parts[1].Name);
    }

    // ── llGetLocalPos, llGetRot, PRIM_POS_LOCAL and PRIM_ROTATION on roots and attachments ──

    [Fact]
    public void LlGetLocalPosInAnUnattachedRootIsTheRegionPositionAsPrimPosLocal()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(100, 110, 30));
        parts[1].UpdateOffSet(new Vector3(0.5f, 1f, -0.25f));

        Run(h, parts[0],
            "llSay(0, \"ll=\" + (string)llGetLocalPos()); " +
            "llSay(0, \"pp=\" + llDumpList2String(llGetPrimitiveParams([PRIM_POS_LOCAL]), \"|\"));");

        Near(new Vector3(100, 110, 30), Line(h, "ll="));
        Near(new Vector3(100, 110, 30), Line(h, "pp="));
    }

    [Fact]
    public void LlGetLocalPosInAChildIsItsOffsetFromTheRoot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(100, 110, 30));
        parts[1].UpdateOffSet(new Vector3(0.5f, 1f, -0.25f));

        Run(h, parts[1],
            "llSay(0, \"ll=\" + (string)llGetLocalPos()); " +
            "llSay(0, \"pp=\" + llDumpList2String(llGetPrimitiveParams([PRIM_POS_LOCAL]), \"|\"));");

        Near(new Vector3(0.5f, 1f, -0.25f), Line(h, "ll="));
        Near(new Vector3(0.5f, 1f, -0.25f), Line(h, "pp="));
    }

    [Fact]
    public void AnAttachmentsRootReadsItsOffsetFromTheAttachPointAndTheWearersRotation()
    {
        using var h = new SchedulerHarness();
        var offset = new Vector3(0.1f, -0.2f, 0.3f);
        var objectRot = Quaternion.CreateFromEulers((float)Math.PI / 4, 0, 0);
        var wearerRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        Wear(h, offset, objectRot, wearerRot);

        Run(h, h.Prim,
            "llSay(0, \"ll=\" + (string)llGetLocalPos() + \"|\" + (string)llGetRot()); " +
            "llSay(0, \"pp=\" + llDumpList2String(llGetPrimitiveParams([PRIM_POS_LOCAL, PRIM_ROTATION, PRIM_ROT_LOCAL]), \"|\"));");

        // SL llGetLocalPos: "the position relative to the attach point"; Halcyon GetPartLocalPos: AttachedPos.
        var ll = Fields(h, "ll=", 2);
        Near(offset, ll[0]);
        // SL llGetRot and PRIM_ROTATION: the wearer's rotation on an attachment's root; Halcyon GetPartRot, llGetRot.
        Near(wearerRot, ll[1]);

        var pp = Fields(h, "pp=", 3);
        Near(offset, pp[0]);
        Near(wearerRot, pp[1]);
        // PRIM_ROT_LOCAL stays the attachment's own rotation relative to the attach point.
        Near(objectRot, pp[2]);
    }

    [Fact]
    public void AChildOfAnAttachmentReadsItsOffsetAndItsOwnRegionRotation()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var objectRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 3);
        Wear(h, new Vector3(0, 0, 0.2f), objectRot, Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2));
        parts[1].UpdateOffSet(new Vector3(0.05f, 0, 0.1f));

        Run(h, parts[1],
            "llSay(0, \"ll=\" + (string)llGetLocalPos() + \"|\" + (string)llGetRot()); " +
            "llSay(0, \"pp=\" + llDumpList2String(llGetPrimitiveParams([PRIM_POS_LOCAL, PRIM_ROTATION]), \"|\"));");

        var ll = Fields(h, "ll=", 2);
        var pp = Fields(h, "pp=", 2);
        Near(new Vector3(0.05f, 0, 0.1f), ll[0]);
        Near(new Vector3(0.05f, 0, 0.1f), pp[0]);
        Near(parts[1].GetWorldRotation(), ll[1]);
        Near(parts[1].GetWorldRotation(), pp[1]);
    }

    // ── A long list mixing them with earlier rules ────────────────────────────

    [Fact]
    public void ALongListMixingTheseRulesWithOthersAppliesEveryRule()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var group = parts[0].ParentGroup;
        Viewer(h);
        var start = parts[0].AbsolutePosition;
        var quarter = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        ClearUpdates(parts);

        Run(h, parts[0],
            // PRIM_OMEGA comes after PRIM_PHANTOM: the scene stops a prim (its spin too) when it turns phantom.
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [" +
            "PRIM_TEXT, \"root\", <1, 1, 1>, 1.0, " +
            "PRIM_CLICK_ACTION, CLICK_ACTION_SIT, " +
            "PRIM_POSITION, llGetPos() + <1, 2, 3>, " +
            "PRIM_PHANTOM, TRUE, " +
            "PRIM_OMEGA, <0, 0, 1>, TWO_PI, 1.0, " +
            "PRIM_SIT_TARGET, TRUE, <0, 0, 0.5>, ZERO_ROTATION, " +
            "PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_CONVEX, " +
            "PRIM_LINK_TARGET, 2, " +
            "PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_NONE, " +
            "PRIM_POS_LOCAL, <0, 0, 1.5>, " +
            "PRIM_SIT_TARGET, TRUE, <0.25, 0, 0>, llEuler2Rot(<0, 0, PI_BY_TWO>), " +
            "PRIM_CLICK_ACTION, CLICK_ACTION_OPEN, " +
            "PRIM_OMEGA, <1, 0, 0>, TWO_PI, 1.0, " +
            "PRIM_NAME, \"last\"]); " +
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_OMEGA, PRIM_CLICK_ACTION, " +
            "PRIM_SIT_TARGET, PRIM_PHYSICS_SHAPE_TYPE, PRIM_POS_LOCAL, PRIM_NAME, " +
            "PRIM_LINK_TARGET, 1, PRIM_PHYSICS_SHAPE_TYPE, PRIM_CLICK_ACTION, PRIM_SIT_TARGET, PRIM_OMEGA, PRIM_PHANTOM]), \"|\"));");

        // The root.
        Near(new Vector3(0, 0, TwoPi), parts[0].AngularVelocity);
        Assert.Equal("root", parts[0].Text);
        Assert.Equal(1, parts[0].ClickAction);
        Near(start + new Vector3(1, 2, 3), parts[0].AbsolutePosition);
        Near(new Vector3(0, 0, 0.5f), parts[0].SitTargetPosition);
        Assert.True(group.IsPhantom, "phantom");
        Assert.Equal((byte)PhysShapeType.convex, parts[0].PhysicsShapeType);
        // The child.
        Assert.Equal((byte)PhysShapeType.none, parts[1].PhysicsShapeType);
        Near(new Vector3(0, 0, 1.5f), parts[1].OffsetPosition);
        Near(new Vector3(0.25f, 0, 0), parts[1].SitTargetPosition);
        Near(quarter, parts[1].SitTargetOrientation);
        Assert.Equal(4, parts[1].ClickAction);
        Near(new Vector3(TwoPi, 0, 0), parts[1].AngularVelocity);
        Assert.Equal("last", parts[1].Name);

        // Link 2: omega (3), click, sit target (3), shape, pos, name; then link 1: shape, click, sit target (3),
        // omega (3), phantom.
        var got = Fields(h, "got=", 19);
        Near(new Vector3(1, 0, 0), got[0]);
        Near(TwoPi, got[1]);
        Assert.Equal("4", got[3]);
        Assert.Equal("1", got[4]);
        Near(new Vector3(0.25f, 0, 0), got[5]);
        Near(quarter, got[6]);
        Assert.Equal("1", got[7]);
        Near(new Vector3(0, 0, 1.5f), got[8]);
        Assert.Equal("last", got[9]);
        Assert.Equal("2", got[10]);
        Assert.Equal("1", got[11]);
        Assert.Equal("1", got[12]);
        Near(new Vector3(0, 0, 0.5f), got[13]);
        Near(new Vector3(0, 0, 1), got[15]);
        Assert.Equal("1", got[18]);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }
}
