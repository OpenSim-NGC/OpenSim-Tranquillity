using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL side-effect family (osSet*, osForce*, sound, links, attachments, misc), each ported from
/// OSSL_Api.cs under its upstream threat level through OsslGate. One dispatch test per group with an assertion on
/// the scene, not on what the script said about itself.
/// </summary>
public class OsslSideEffectTests
{
    private readonly ITestOutputHelper _out;
    public OsslSideEffectTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene(string threat = "Severe")
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat));

    private string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    // ------------------------------------------------------------------ the did-it-land pair

    [Fact]
    public void SetRotTurnsThePrimNinetyDegrees()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() { osSetRot(llGetKey(), <0,0,0.707107,0.707107>); llSay(0, ""done""); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("done"));
        _out.WriteLine("rot=" + h.Prim.ParentGroup.GroupRotation + " errors=[" + Errors(h) + "]");

        Assert.Contains("done", h.Said);
        var q = h.Prim.ParentGroup.GroupRotation;
        Assert.Equal(0.7071f, q.Z, 3);
        Assert.Equal(0.7071f, q.W, 3);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void ForceCreateLinkJoinsTwoPrimsWithoutChangeLinksPermissionAndForceBreakUndoesIt()
    {
        using var h = Scene();
        var other = SceneHelpers.AddSceneObject(h.Scene, "other prim", h.Prim.OwnerID);
        Assert.Equal(1, h.Prim.ParentGroup.PrimCount);

        h.RezScript(@"default { state_entry() {
            osForceCreateLink(""" + other.UUID + @""", 1);
            llSay(0, ""prims="" + (string)llGetNumberOfPrims());
            osForceBreakLink(2);
            llSay(0, ""after="" + (string)llGetNumberOfPrims());
        } }");
        h.PumpFor(TimeSpan.FromSeconds(4));
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("after=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        // no PERMISSION_CHANGE_LINKS was ever granted, and llCreateLink would have refused; the forced form links
        Assert.Contains("prims=2", h.Said);
        Assert.Contains("after=1", h.Said);
        Assert.Equal(1, h.Prim.ParentGroup.PrimCount);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ prim state

    [Fact]
    public void PrimSettersLandOnTheScenePart()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            osSetSitActiveRange(12.0);
            osSetStandTarget(<1,2,3>);
            osSetProjectionParams(TRUE, ""8b5fec65-8d8d-9dc5-cda8-8fdf2716e361"", 1.5, 2.0, 0.25);
            osSetSoundRadius(LINK_THIS, 7.5);
            osCollisionSound("""", 0.0);
            osSetPrimitiveParams(llGetKey(), [PRIM_NAME, ""renamed""]);
            llSay(0, ""name="" + llList2String(osGetPrimitiveParams(llGetKey(), [PRIM_NAME]), 0));
            llSay(0, ""rs="" + osReplaceString(""aaa"", ""a"", ""b"", 2, 0));
            llSay(0, ""done"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("done"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("done", h.Said);
        Assert.Equal(12f, h.Prim.SitActiveRange);
        Assert.Equal(new Vector3(1, 2, 3), h.Prim.StandOffset);
        Assert.True(h.Prim.Shape.ProjectionEntry);
        Assert.Equal(1.5f, h.Prim.Shape.ProjectionFOV);
        Assert.Equal(7.5, h.Prim.SoundRadius);
        Assert.Equal(-1, h.Prim.CollisionSoundType);
        Assert.Equal("renamed", h.Prim.Name);
        Assert.Contains("name=renamed", h.Said);
        Assert.Contains("rs=bba", h.Said);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void TeleportObjectMovesTheLinkset()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            integer r = osTeleportObject(llGetKey(), <100, 100, 30>, ZERO_ROTATION, OSTPOBJ_STOPATTARGET);
            llSay(0, ""r="" + (string)r);
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("r=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] pos=" + h.Prim.ParentGroup.AbsolutePosition + " errors=[" + Errors(h) + "]");

        Assert.Contains("r=1", h.Said);
        var p = h.Prim.ParentGroup.AbsolutePosition;
        Assert.Equal(100f, p.X, 1);
        Assert.Equal(100f, p.Y, 1);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ messaging

    [Fact]
    public void MessageObjectRaisesDataserverOnTheTarget()
    {
        using var h = Scene();
        h.RezScript(@"default {
            state_entry() { osMessageObject(llGetKey(), ""ping""); }
            dataserver(key q, string data) { llSay(0, ""ds="" + data + ""|from="" + (string)(q == llGetKey())); }
        }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("ds=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("ds=ping|from=1", h.Said);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void TheGateHoldsAtItsUpstreamLevel()
    {
        using var h = Scene("VeryLow");
        h.RezScript(@"default { state_entry() { osSetRot(llGetKey(), <0,0,0.707107,0.707107>); llSay(0, ""after""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied")));

        Assert.DoesNotContain("after", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied"));
        Assert.Equal(Quaternion.Identity, h.Prim.ParentGroup.GroupRotation);
    }
}
