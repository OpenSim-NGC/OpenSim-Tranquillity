/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A crossing holds the object's scripts from the moment it is announced (EventManager.OnGroupBeginInTransit) until it
/// ends, as Halcyon's crossing wait did (EngineInterface.OnGroupBeginInTransit; ExecutionScheduler: "CrossingWait
/// scripts should not drop events ... everything should be queued"). An event that reaches a held script waits on its
/// queue: it travels with the script's state and runs in the new region, or, when the crossing fails, runs where the
/// object stayed. The timer stops while held and carries on with what it had left. Chat a listen hears while held is
/// not kept: Halcyon took the script's listens away for the hold. SL keeps a paused script's pending events, in order
/// ("Events ... are queued FIFO"; "If the script is paused ... pending events are preserved", LSL Events).
/// <para>
/// Each test posts its events from its own OnGroupBeginInTransit handler, which runs after the engine's, on the thread
/// that started the crossing and before the crossing's own thread starts, so the events are posted mid-crossing. A
/// handler that waits holds the crossing there.
/// </para>
/// </summary>
// Runs in parallel: regions (7400, 7400), (7400, 7399) up to (7470, 7470), (7470, 7469) are used by no other test; the
// scenes, engines and items are its own.
public class CrossingHoldTests
{
    private const string Counter = @"
        integer t;
        default {
            state_entry() { llListen(5, """", NULL_KEY, """"); llSay(0, ""entry""); }
            link_message(integer s, integer n, string m, key k) { llSay(0, ""lm "" + m); }
            listen(integer c, string nm, key k, string m) { llSay(0, ""heard "" + m); }
            touch_start(integer n) { llSetTimerEvent(0.3); }
            timer() { t++; llSay(0, ""tick "" + (string)t); }
        }";

    private const string UrlHolder = @"
        default {
            state_entry() { llRequestURL(); llSay(0, ""entry""); }
            link_message(integer s, integer n, string m, key k) { llSay(0, ""lm "" + m); }
        }";

    /// <summary>A region's URL module: the URLs each script holds, and the scripts whose URLs were released.</summary>
    private sealed class Urls : IUrlModule
    {
        public readonly ConcurrentDictionary<UUID, int> HeldBy = new();
        public readonly ConcurrentBag<UUID> ReleasedFor = new();
        public string ExternalHostNameForLSL => "localhost";
        public UUID RequestURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options) { HeldBy.AddOrUpdate(itemID, 1, (_, n) => n + 1); return UUID.Random(); }
        public UUID RequestSecureURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options) => RequestURL(engine, host, itemID, options);
        public void ReleaseURL(string url) { }
        public void HttpResponse(UUID request, int status, string body) { }
        public void HttpContentType(UUID request, string type) { }
        public string GetHttpHeader(UUID request, string header) => string.Empty;
        public int GetFreeUrls() => 100000;
        public void ScriptRemoved(UUID itemID) { ReleasedFor.Add(itemID); HeldBy.TryRemove(itemID, out _); }
        public void ObjectRemoved(UUID objectID) { }
        public int GetUrlCount(UUID groupID) => 0;
    }

    private static (SceneObjectGroup Sog, UUID Item, Urls Urls) RezUrlHolder(VehicleCrossingStateTests.Region r, Vector3 pos)
    {
        var urls = new Urls();
        r.Scene.RegisterModuleInterface<IUrlModule>(urls);
        var sog = SceneHelpers.AddSceneObject(r.Scene, "Example Server", UUID.Random());
        sog.AbsolutePosition = pos;
        UUID item = UUID.Random();
        TaskInventoryHelpers.AddScript(r.Scene.AssetService, sog.RootPart, item, UUID.Random(), "server", UrlHolder);
        sog.CreateScriptInstances(0, true, r.Engine.Name, 1);
        return (sog, item, urls);
    }

    private static void LinkMessage(VehicleCrossingStateTests.Region r, UUID item, string text)
        => r.Engine.PostScriptEvent(item, "link_message", new object[] { 0, 0, text, UUID.Zero.ToString() });

    private static (SceneObjectGroup Sog, UUID Item) Rez(VehicleCrossingStateTests.Region r, string name, Vector3 pos)
    {
        var sog = SceneHelpers.AddSceneObject(r.Scene, name, UUID.Random());
        sog.AbsolutePosition = pos;
        UUID item = UUID.Random();
        TaskInventoryHelpers.AddScript(r.Scene.AssetService, sog.RootPart, item, UUID.Random(), "counter", Counter);
        sog.CreateScriptInstances(0, true, r.Engine.Name, 1);
        return (sog, item);
    }

    private static string[] Said(VehicleCrossingStateTests.Region r, string prefix)
    {
        lock (r.Said) return r.Said.Where(s => s.StartsWith(prefix)).ToArray();
    }

    private static int Ticks(VehicleCrossingStateTests.Region r) => Said(r, "tick ").Length;

    /// <summary>Wait out a window in which something must NOT happen.</summary>
    private static void Window(int ms) => System.Threading.Thread.Sleep(ms);

    [Fact]
    public void EventsRaisedMidCrossingRunInTheNewRegionInOrderAndOnlyThere()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7400);
        using var ra = a;
        using var rb = b;
        var (sog, item) = Rez(a, "Example Vehicle", new Vector3(128, 3, 30));
        UUID objectId = sog.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());

        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            LinkMessage(a, item, "1");
            LinkMessage(a, item, "2");
            LinkMessage(a, item, "3");
        };

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the object did not reach region B");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => Said(b, "lm ").Length >= 3), "region B: " + b.Text() + " region A: " + a.Text());

        Window(500);   // nothing more comes, in either region
        Assert.Equal(new[] { "lm 1", "lm 2", "lm 3" }, Said(b, "lm "));
        Assert.Empty(Said(a, "lm "));
    }

    [Fact]
    public void AFailedCrossingGivesTheHeldEventsBackInOrderWhereTheObjectStayed()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7410);
        using var ra = a;
        using var rb = b;
        var (sog, item) = Rez(a, "Example Object", new Vector3(250, 128, 30));
        UUID objectId = sog.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());

        string[] duringHold = null;
        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            LinkMessage(a, item, "1");
            LinkMessage(a, item, "2");
            LinkMessage(a, item, "3");
            Window(1000);   // the crossing waits here: the held script runs none of them
            duringHold = Said(a, "lm ");
        };

        // No region lies east of region A: the crossing fails and the object is put back inside region A.
        sog.UpdateGroupPosition(new Vector3(262, 128, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => Said(a, "lm ").Length >= 3), "region A: " + a.Text());

        Assert.Empty(duringHold);
        Window(500);   // nothing runs twice
        Assert.Equal(new[] { "lm 1", "lm 2", "lm 3" }, Said(a, "lm "));
        Assert.Same(sog, a.Scene.GetSceneObjectGroup(objectId));
        Assert.Empty(Said(b, "lm "));

        // Held no longer: the next event runs at once.
        LinkMessage(a, item, "4");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm 4")), a.Text());
    }

    [Fact]
    public void AScriptWhoseObjectIsNotCrossingRunsItsEventsDuringAnotherObjectsCrossing()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7420);
        using var ra = a;
        using var rb = b;
        var (crossing, crossingItem) = Rez(a, "Example Object", new Vector3(250, 128, 30));
        var (staying, stayingItem) = Rez(a, "Example Bystander", new Vector3(100, 100, 30));
        UUID objectId = crossing.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => Said(a, "entry").Length == 2), a.Text());

        bool bystanderRanDuringHold = false;
        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            LinkMessage(a, crossingItem, "held");
            LinkMessage(a, stayingItem, "free");
            bystanderRanDuringHold = VehicleCrossingStateTests.WaitFor(() => a.Heard("lm free"), 30);
        };

        crossing.UpdateGroupPosition(new Vector3(262, 128, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm held")), a.Text());
        Assert.True(bystanderRanDuringHold, a.Text());
        Assert.False(staying.inTransit);
        Assert.Equal(new[] { "lm free", "lm held" }, Said(a, "lm "));
    }

    [Fact]
    public void TheTimerStopsWhileHeldAndCarriesOnAfterAFailedCrossing()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7430);
        using var ra = a;
        using var rb = b;
        var (sog, item) = Rez(a, "Example Object", new Vector3(250, 128, 30));
        UUID objectId = sog.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());
        a.Engine.PostScriptEvent(item, new OpenSim.Region.ScriptEngine.Shared.EventParams("touch_start", new object[] { 1 },
            Array.Empty<OpenSim.Region.ScriptEngine.Shared.DetectParams>()));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => Ticks(a) >= 2), a.Text());

        int atHold = -1, afterWindow = -1;
        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            // The hold is taken on the scheduler thread; once a posted event is held, so is the timer.
            LinkMessage(a, item, "marker");
            Window(200);
            atHold = Ticks(a);
            Window(1500);   // five intervals: no timer() while held
            afterWindow = Ticks(a);
        };

        sog.UpdateGroupPosition(new Vector3(262, 128, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm marker")), a.Text());
        Assert.Equal(atHold, afterWindow);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => Ticks(a) >= afterWindow + 2), "the timer did not carry on: " + a.Text());
    }

    [Fact]
    public void ChatAListenHearsWhileHeldIsNotKept()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7440);
        using var ra = a;
        using var rb = b;
        var (sog, item) = Rez(a, "Example Object", new Vector3(250, 128, 30));
        UUID objectId = sog.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());
        a.Scene.SimChat("before", OpenSim.Framework.ChatTypeEnum.Region, 5, sog.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("heard before")), a.Text());

        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            LinkMessage(a, item, "marker");
            a.Scene.SimChat("during", OpenSim.Framework.ChatTypeEnum.Region, 5, g.AbsolutePosition, "tester", UUID.Random(), false);
        };

        sog.UpdateGroupPosition(new Vector3(262, 128, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm marker")), a.Text());
        a.Scene.SimChat("after", OpenSim.Framework.ChatTypeEnum.Region, 5, sog.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("heard after")), a.Text());
        Assert.False(a.Heard("heard during"), a.Text());
    }

    [Fact]
    public void ChatHeardBetweenAHoldAndAnEndTakenInOnePassIsNotKept()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7450);
        using var ra = a;
        using var rb = b;
        var (sog, item) = Rez(a, "Example Object", new Vector3(128, 128, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());
        var exe = (global::Phlox.ScriptEngine.PhloxExecutionScheduler)SavedStateRig.Field(a.Engine, "m_ExeScheduler");

        // A failed crossing can end before the scheduler has taken its hold. While this lock is held the scheduler takes
        // no posted event, so the hold, the events raised during it and its end all reach it in one pass.
        lock (SavedStateRig.Field(exe, "m_PendingEvents"))
        {
            exe.RequestCrossingHold(sog, true);
            LinkMessage(a, item, "marker");
            a.Engine.PostScriptEvent(item, "listen", new object[] { 5, "tester", UUID.Random().ToString(), "during" });
            exe.RequestCrossingHold(sog, false);
        }

        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm marker")), a.Text());
        LinkMessage(a, item, "after");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm after")), a.Text());
        Assert.False(a.Heard("heard during"), a.Text());
    }

    /// <summary>
    /// The hold does not release the script's URLs. A crossing that succeeds removes the script from the region it
    /// left, and that unload releases them there (SL: "deleting the prim ... release URLs").
    /// </summary>
    [Fact]
    public void ASuccessfulCrossingReleasesTheScriptsUrlsInTheRegionItLeft()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7460);
        using var ra = a;
        using var rb = b;
        var (sog, item, urls) = RezUrlHolder(a, new Vector3(128, 3, 30));
        UUID objectId = sog.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());
        Assert.Equal(1, urls.HeldBy.GetValueOrDefault(item));

        bool releasedDuringHold = true;
        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            LinkMessage(a, item, "marker");
            Window(200);
            releasedDuringHold = urls.ReleasedFor.Contains(item);
        };

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the object did not reach region B");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => b.Heard("lm marker")), "region B: " + b.Text() + " region A: " + a.Text());
        Assert.False(releasedDuringHold);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => urls.ReleasedFor.Contains(item)), "region A kept the script's URLs");
        Assert.False(urls.HeldBy.ContainsKey(item));
    }

    /// <summary>
    /// A crossing that fails leaves the object where it was, so the script keeps its URLs there. Halcyon released them
    /// when the hold started, so a failed crossing lost them.
    /// </summary>
    [Fact]
    public void AFailedCrossingKeepsTheScriptsUrls()
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7470);
        using var ra = a;
        using var rb = b;
        var (sog, item, urls) = RezUrlHolder(a, new Vector3(250, 128, 30));
        UUID objectId = sog.UUID;
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());
        Assert.Equal(1, urls.HeldBy.GetValueOrDefault(item));

        a.Scene.EventManager.OnGroupBeginInTransit += g =>
        {
            if (g.UUID != objectId) return;
            LinkMessage(a, item, "marker");
        };

        // No region lies east of region A: the crossing fails and the object is put back inside region A.
        sog.UpdateGroupPosition(new Vector3(262, 128, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm marker")), a.Text());
        Assert.Same(sog, a.Scene.GetSceneObjectGroup(objectId));
        LinkMessage(a, item, "after");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("lm after")), a.Text());
        Window(500);   // no late release
        Assert.DoesNotContain(item, urls.ReleasedFor);
        Assert.Equal(1, urls.HeldBy.GetValueOrDefault(item));
    }
}
