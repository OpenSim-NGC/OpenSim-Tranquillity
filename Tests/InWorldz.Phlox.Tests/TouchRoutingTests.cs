/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Touches through the region's own path (Scene.ProcessObjectGrab, ProcessObjectGrabUpdate, ProcessObjectDeGrab).
/// <list type="bullet">
/// <item>The touch reaches only the prim the region routed it to (SL llPassTouches: the touched prim when it has a
/// handler, the root as well only on pass-through; Halcyon EngineInterface.PostObjectEvent posts to that prim).</item>
/// <item>llDetectedLinkNumber is the prim actually touched, also when the root takes it (SL llDetectedLinkNumber;
/// Halcyon EventRouter touch_start uses originalID).</item>
/// <item>The toucher's detect data comes from the avatar (Halcyon DetectParams.Populate); llDetectedGrab is zero
/// outside touch() (SL llDetectedGrab: "only works in the touch event").</item>
/// <item>An index past the detected set gives TOUCH_INVALID_FACE and TOUCH_INVALID_TEXCOORD (SL wiki, Halcyon and
/// YEngine alike) and NULL_KEY for keys.</item>
/// <item>touch() repeats while the touch is held, every 100 ms as Halcyon's scheduler does (SL touch: "each minimum
/// event delay while held"); grab updates only refresh the data the next repeat carries; touch_end stops it.</item>
/// </list>
/// </summary>
// Each test builds its own scene and touches nothing process-wide, so the class runs in parallel.
public class TouchRoutingTests
{
    private readonly ITestOutputHelper _out;
    public TouchRoutingTests(ITestOutputHelper o) => _out = o;

    private static string Tagged(string tag) => @"
default
{
    touch_start(integer n)
    {
        llSay(0, """ + tag + @" ts "" + (string)llDetectedLinkNumber(0));
    }
}";

    private const string Passes = @"
default
{
    state_entry() { llPassTouches(TRUE); }
    touch_start(integer n)
    {
        llSay(0, ""C ts "" + (string)llDetectedLinkNumber(0));
    }
}";

    private static SceneObjectPart[] TwoPrims(SchedulerHarness h)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "child", h.Prim.OwnerID));
        var g = h.Prim.ParentGroup;
        Assert.Equal(2, g.PrimCount);
        return new[] { g.GetLinkNumPart(1), g.GetLinkNumPart(2) };
    }

    private static int Count(SchedulerHarness h, string prefix) => h.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));

    private static void WaitForMask(SchedulerHarness h, SceneObjectPart part, scriptEvents ev)
        => Assert.True(h.PumpUntil(() => (part.ScriptEvents & ev) != 0), "the script never registered " + ev);

    private static List<SurfaceTouchEventArgs> Surface(int face = 2)
        => new() { new SurfaceTouchEventArgs { FaceIndex = face, STCoord = new Vector3(0.25f, 0.5f, 0), UVCoord = new Vector3(0.75f, 0.5f, 0) } };

    [Fact]
    public void ATouchOnAChildWithItsOwnHandlerReachesOnlyTheChild()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrims(h);
        h.RezScriptInto(parts[0], Tagged("R"));
        h.RezScriptInto(parts[1], Tagged("C"));
        WaitForMask(h, parts[0], scriptEvents.touch_start);
        WaitForMask(h, parts[1], scriptEvents.touch_start);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(parts[1].LocalId, Vector3.Zero, client, Surface());

        Assert.True(h.PumpUntil(() => Count(h, "C ts") == 1), string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));   // a "did not arrive" window
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        _out.WriteLine(string.Join(" | ", h.Said));
        Assert.Equal(new[] { "C ts 2" }, h.Said.Where(s => s.Contains(" ts ")).ToArray());
    }

    [Fact]
    public void PassTouchesGivesTheChildAndTheRootTheTouchOnceEach()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrims(h);
        h.RezScriptInto(parts[0], Tagged("R"));
        h.RezScriptInto(parts[1], Passes);
        WaitForMask(h, parts[0], scriptEvents.touch_start);
        WaitForMask(h, parts[1], scriptEvents.touch_start);
        Assert.True(h.PumpUntil(() => parts[1].PassTouches));
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(parts[1].LocalId, Vector3.Zero, client, Surface());

        Assert.True(h.PumpUntil(() => Count(h, "C ts") == 1 && Count(h, "R ts") == 1), string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        _out.WriteLine(string.Join(" | ", h.Said));
        Assert.Equal(1, Count(h, "C ts"));
        Assert.Equal(1, Count(h, "R ts"));
        // Both see the prim that was touched
        Assert.Contains("C ts 2", h.Said);
        Assert.Contains("R ts 2", h.Said);
    }

    [Fact]
    public void TheRootTakingAChildsTouchSeesTheChildsLinkNumber()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrims(h);
        h.RezScriptInto(parts[0], Tagged("R"));
        WaitForMask(h, parts[0], scriptEvents.touch_start);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(parts[1].LocalId, Vector3.Zero, client, Surface());

        Assert.True(h.PumpUntil(() => Count(h, "R ts") == 1), string.Join(" | ", h.Said));
        Assert.Contains("R ts 2", h.Said);
    }

    private const string Details = @"
default
{
    touch_start(integer n)
    {
        llSay(0, ""D "" + (string)llDetectedType(0) + "" "" + (string)llDetectedGrab(0) + "" "" + llDetectedName(0)
            + "" "" + (string)llDetectedGroup(0) + "" "" + (string)llDetectedTouchFace(0));
        llSay(0, ""X "" + (string)llDetectedTouchFace(5) + "" "" + (string)llDetectedTouchST(5) + "" ""
            + (string)llDetectedTouchUV(5) + "" "" + llDetectedKey(5) + "" "" + (string)llDetectedLinkNumber(5)
            + "" "" + (string)llDetectedGroup(5));
    }
}";

    [Fact]
    public void TouchDetectDataComesFromTheAvatar()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Details);
        WaitForMask(h, h.Prim, scriptEvents.touch_start);
        var client = h.AddClient();
        ScenePresence sp = h.Scene.GetScenePresence(client.AgentId);

        // The viewer's grab offset on touch_start is not passed on: llDetectedGrab is for touch() only.
        h.Scene.ProcessObjectGrab(h.Prim.LocalId, new Vector3(1, 2, 3), client, Surface(face: 3));

        Assert.True(h.PumpUntil(() => Count(h, "D ") == 1 && Count(h, "X ") == 1), string.Join(" | ", h.Said));
        _out.WriteLine(string.Join(" | ", h.Said));
        string d = h.Said.First(s => s.StartsWith("D ", StringComparison.Ordinal));
        // Neither the prim nor the toucher has a group: the same group, as llSameGroup counts it (SL wiki) and as
        // Halcyon and YEngine compare the group captured with the touch.
        Assert.Equal("D 1 <0.00000, 0.00000, 0.00000> " + sp.Firstname + " " + sp.Lastname + " 1 3", d);
    }

    [Fact]
    public void AnIndexPastTheDetectedSetGivesTheInvalidTouchValues()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Details);
        WaitForMask(h, h.Prim, scriptEvents.touch_start);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(h.Prim.LocalId, Vector3.Zero, client, Surface());

        Assert.True(h.PumpUntil(() => Count(h, "X ") == 1), string.Join(" | ", h.Said));
        Assert.Equal("X -1 <-1.00000, -1.00000, 0.00000> <-1.00000, -1.00000, 0.00000> "
                     + UUID.Zero + " 0 0", h.Said.First(s => s.StartsWith("X ", StringComparison.Ordinal)));
    }

    // ── touch() repeat while held ────────────────────────────────────────────

    private const string Repeater = @"
default
{
    touch_start(integer n) { llSay(0, ""S""); }
    touch(integer n) { llSay(0, ""T "" + (string)llDetectedGrab(0) + "" "" + (string)llDetectedTouchFace(0)); }
    touch_end(integer n) { llSay(0, ""E""); }
}";

    [Fact]
    public void AHeldTouchRepeatsWithoutGrabUpdates()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Repeater);
        WaitForMask(h, h.Prim, scriptEvents.touch);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(h.Prim.LocalId, Vector3.Zero, client, Surface());

        // The viewer sends nothing more while the mouse is held still; the region repeats touch() itself.
        Assert.True(h.PumpUntil(() => Count(h, "T ") >= 3, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
        Assert.Equal(1, Count(h, "S"));
        Assert.StartsWith("T <0.00000, 0.00000, 0.00000> 2", h.Said.First(s => s.StartsWith("T ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TouchEndStopsTheRepeat()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Repeater);
        WaitForMask(h, h.Prim, scriptEvents.touch);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(h.Prim.LocalId, Vector3.Zero, client, Surface());
        Assert.True(h.PumpUntil(() => Count(h, "T ") >= 2, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
        h.Scene.ProcessObjectDeGrab(h.Prim.LocalId, client, Surface());
        Assert.True(h.PumpUntil(() => Count(h, "E") == 1), string.Join(" | ", h.Said));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        int after = Count(h, "T ");

        h.PumpFor(TimeSpan.FromMilliseconds(600));   // six repeat intervals: nothing may arrive
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(after, Count(h, "T "));
    }

    [Fact]
    public void AGrabUpdateRefreshesTheRepeatsDataAndIsNotAnEventOfItsOwn()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Repeater);
        WaitForMask(h, h.Prim, scriptEvents.touch);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(h.Prim.LocalId, Vector3.Zero, client, Surface());
        Assert.True(h.PumpUntil(() => Count(h, "T ") >= 1, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));

        // Twenty grab updates in one burst: today each became its own touch(). Now they refresh the data only.
        Vector3 at = h.Prim.AbsolutePosition + new Vector3(0, 0, 1);
        for (int i = 0; i < 20; i++)
            h.Scene.ProcessObjectGrabUpdate(h.Prim.UUID, Vector3.Zero, at, client, Surface(face: 4));

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("T <0.00000, 0.00000, 1.00000> 4", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
        h.Scene.ProcessObjectDeGrab(h.Prim.LocalId, client, Surface());
        Assert.True(h.PumpUntil(() => Count(h, "E") == 1));
        _out.WriteLine(string.Join(" | ", h.Said));
        // Not one touch() per grab update: the repeats came at their own pace.
        Assert.True(Count(h, "T <0.00000, 0.00000, 1.00000>") < 20, string.Join(" | ", h.Said));
    }

    private const string TouchOnly = @"
default
{
    touch(integer n) { llSay(0, ""T "" + (string)llDetectedGrab(0)); }
}";

    [Fact]
    public void AScriptWithOnlyTouchRepeatsWhileHeldAndStopsAtRelease()
    {
        // SL touch: "Triggered on touch start, each minimum event delay while held, and touch end." A state with touch()
        // asks the region for touch_start and touch_end as well, so a script with touch() alone is started and stopped
        // like any other: it repeats without grab updates and goes quiet at the release.
        using var h = new SchedulerHarness();
        h.RezScript(TouchOnly);
        WaitForMask(h, h.Prim, scriptEvents.touch);
        Assert.Equal(scriptEvents.touch_start | scriptEvents.touch_end,
            h.Prim.ScriptEvents & (scriptEvents.touch_start | scriptEvents.touch_end));
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(h.Prim.LocalId, Vector3.Zero, client, Surface());
        Assert.True(h.PumpUntil(() => Count(h, "T ") >= 3, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));

        h.Scene.ProcessObjectDeGrab(h.Prim.LocalId, client, Surface());
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        int after = Count(h, "T ");
        h.PumpFor(TimeSpan.FromMilliseconds(600));   // six repeat intervals: nothing may arrive
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(after, Count(h, "T "));
    }

    [Fact]
    public void AChildWithOnlyTouchTakesItsOwnTouch()
    {
        // SL llPassTouches: whether a touch passes to the root "may also depend on if there is a script that in the prim
        // that handles one of the touch events". A child whose script handles touch() takes the touch; the root's
        // touch_start does not fire.
        using var h = new SchedulerHarness();
        var parts = TwoPrims(h);
        h.RezScriptInto(parts[0], Tagged("R"));
        h.RezScriptInto(parts[1], TouchOnly);
        WaitForMask(h, parts[0], scriptEvents.touch_start);
        WaitForMask(h, parts[1], scriptEvents.touch);
        var client = h.AddClient();

        h.Scene.ProcessObjectGrab(parts[1].LocalId, Vector3.Zero, client, Surface());
        Assert.True(h.PumpUntil(() => Count(h, "T ") >= 1, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
        h.Scene.ProcessObjectDeGrab(parts[1].LocalId, client, Surface());
        h.PumpFor(TimeSpan.FromMilliseconds(500));   // a "did not arrive" window
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        _out.WriteLine(string.Join(" | ", h.Said));
        Assert.Equal(0, Count(h, "R ts"));
    }
}
