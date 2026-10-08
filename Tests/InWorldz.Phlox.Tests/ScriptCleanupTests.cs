using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What ends with a script. On delete, reset and unload a script's sensor repeat, outstanding
/// HTTP requests, URLs, listens, timer, sleep and owed dataserver replies end (XML-RPC channels on delete/unload); a late
/// reply for a script that is gone or was reset is dropped at once; events for an item that is not loaded are not kept
/// (only a load in flight holds them, at most 32 for 60 s, as Halcyon's DeferredEventManager); a reset flood is throttled.
///
/// <para>The region's HTTP, URL and XML-RPC modules are fakes registered on the test scene: they record what the engine
/// asks of them and let a test hand back a response whenever it likes, including after the script is gone. The engine,
/// its plugins, its scheduler and its loader are the real ones.</para>
/// </summary>
[Collection("phlox-state")]
public class ScriptCleanupTests
{
    private readonly ITestOutputHelper _out;
    public ScriptCleanupTests(ITestOutputHelper o) => _out = o;

    // ── fakes ──────────────────────────────────────────────────────────────────

    private sealed class FakeReq : IHttpServiceRequest
    {
        public int Status { get; set; } = 200;
        public string ResponseBody { get; set; } = "body";
        public bool Finished => true;
        public UUID ItemID { get; set; }
        public uint LocalID { get; set; }
        public UUID ReqID { get; set; }
        public void Process() { }
        public void SendRequest() { }
        public void Stop() { }
    }

    /// <summary>The region's HTTP module: a request stays in flight until the test completes it.</summary>
    private sealed class FakeHttp : IHttpRequestModule
    {
        public readonly ConcurrentDictionary<UUID, FakeReq> InFlight = new();
        public readonly ConcurrentQueue<IHttpServiceRequest> Completed = new();
        public readonly ConcurrentBag<UUID> StoppedFor = new();
        public UUID MakeHttpRequest(string url, string parameters, string body) => UUID.Zero;
        public UUID StartHttpRequest(uint localID, UUID itemID, string url, List<string> parameters, Dictionary<string, string> headers, string body)
        {
            var r = new FakeReq { ItemID = itemID, LocalID = localID, ReqID = UUID.Random() };
            InFlight[r.ReqID] = r;
            return r.ReqID;
        }
        public void StopHttpRequest(uint localID, UUID itemID)
        {
            StoppedFor.Add(itemID);
            foreach (var kvp in InFlight.Where(k => k.Value.ItemID == itemID).ToList()) InFlight.TryRemove(kvp.Key, out _);
        }
        /// <summary>The response arrives - also for a request the core has already stopped (it had completed first).</summary>
        public void Respond(FakeReq r, string body) { InFlight.TryRemove(r.ReqID, out _); r.ResponseBody = body; Completed.Enqueue(r); }
        public IHttpServiceRequest GetNextCompletedRequest() => Completed.TryDequeue(out var r) ? r : null;
        public void RemoveCompletedRequest(UUID id) { }
        public bool CheckThrottle(uint localID, UUID onerID) => true;
        public bool CheckAllowed(Uri url) => true;
    }

    private sealed class FakeUrl : IUrlModule
    {
        public readonly ConcurrentDictionary<UUID, int> UrlsByItem = new();
        public readonly ConcurrentBag<UUID> RemovedFor = new();
        public string ExternalHostNameForLSL => "localhost";
        public UUID RequestURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options) { UrlsByItem.AddOrUpdate(itemID, 1, (_, n) => n + 1); return UUID.Random(); }
        public UUID RequestSecureURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options) => RequestURL(engine, host, itemID, options);
        public void ReleaseURL(string url) { }
        public void HttpResponse(UUID request, int status, string body) { }
        public void HttpContentType(UUID request, string type) { }
        public string GetHttpHeader(UUID request, string header) => string.Empty;
        public int GetFreeUrls() => 100000;
        public void ScriptRemoved(UUID itemID) { RemovedFor.Add(itemID); UrlsByItem.TryRemove(itemID, out _); }
        public void ObjectRemoved(UUID objectID) { }
        public int GetUrlCount(UUID groupID) => 0;
        public int Total => UrlsByItem.Values.Sum();
    }

    private sealed class FakeXmlRpc : IXMLRPC
    {
        public readonly ConcurrentDictionary<UUID, UUID> ChannelByItem = new();
        public readonly ConcurrentBag<UUID> CancelledFor = new();
        public UUID OpenXMLRPCChannel(uint localID, UUID itemID, UUID channelID) => ChannelByItem.GetOrAdd(itemID, _ => UUID.Random());
        public void CloseXMLRPCChannel(UUID channelKey) { }
        public bool hasRequests() => false;
        public void RemoteDataReply(string channel, string message_id, string sdata, int idata) { }
        public bool IsEnabled() => true;
        public IXmlRpcRequestInfo GetNextCompletedRequest() => null;
        public void RemoveCompletedRequest(UUID id) { }
        public void DeleteChannels(UUID itemID) => ChannelByItem.TryRemove(itemID, out _);
        public UUID SendRemoteData(uint localID, UUID itemID, string channel, string dest, int idata, string sdata) => UUID.Random();
        public IServiceRequest GetNextCompletedSRDRequest() => null;
        public void RemoveCompletedSRDRequest(UUID id) { }
        public void CancelSRDRequests(UUID itemID) => CancelledFor.Add(itemID);
        public int Port => 0;
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly FakeHttp Http = new();
        public readonly FakeUrl Url = new();
        public readonly FakeXmlRpc Xml = new();

        public Rig(bool resetThrottle = true, bool chatThrottle = true)
        {
            H = new SchedulerHarness(cfg =>
            {
                cfg.Configs["InWorldz.Phlox"].Set("ResetThrottle", resetThrottle ? "true" : "false");
                cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", chatThrottle ? "true" : "false");
            });
            H.Scene.RegisterModuleInterface<IHttpRequestModule>(Http);
            H.Scene.RegisterModuleInterface<IUrlModule>(Url);
            H.Scene.RegisterModuleInterface<IXMLRPC>(Xml);
        }

        // Every table is read through its private field, so the same test runs against an engine without them (red)
        // and after it; a table the old engine does not have reads as 0 there.
        public PhloxExecutionScheduler Exe => (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
        public int Sensors => CountOf(H.Engine.AsyncCommands.SensorRepeatPlugin, "SenseRepeaters");
        public int HttpTracked => CountOf(H.Engine.AsyncCommands.HttpRequestPlugin, "m_Outstanding");
        public int ListenScripts => CountOf(H.Engine.ListenManager, "m_ByItem");
        public int Listens => H.Engine.ListenManager.ListenCount;
        public int RateRecords => CountOf(H.Engine.ListenManager, "m_RateTracker");
        public int PendingEvents => CountOf(Exe, "m_PendingEvents");
        /// <summary>Items with events held for them while not loaded, and how many events.</summary>
        public (int Items, int Events) Held
        {
            get
            {
                var d = (IDictionary)Field(Exe, "m_DeferredEvents");
                int events = 0;
                foreach (var v in d.Values)
                    events += v is ICollection c ? c.Count : ((ICollection)v.GetType().GetField("Events")!.GetValue(v)).Count;
                return (d.Count, events);
            }
        }
        /// <summary>Events dropped because their item was not loaded (-1 on an engine without the counter).</summary>
        public long Dropped => (long?)Field(Exe, "m_DroppedForUnloaded", optional: true) ?? -1;
        public int ResetSleeps(UUID item)
            => (int?)typeof(LSLSystemAPI).GetProperty("ResetSleepCount", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Api(item)) ?? 0;
        public int Timers => ((ICollection)Field(Exe, "m_TimerHandles")).Count;
        public int Sleeps => ((ICollection)Field(Exe, "m_StdSleepHandles")).Count;
        public int Apis => ((ICollection)Field(Exe, "m_Apis")).Count;
        public int Loaded => ((ICollection)Field(Exe, "m_AllScripts")).Count;
        public LSLSystemAPI Api(UUID item) => ((Dictionary<UUID, LSLSystemAPI>)Field(Exe, "m_Apis"))[item];
        public Interpreter Interp(UUID item) => (Interpreter)H.InterpreterFor(item);

        public bool PumpUntil(Func<bool> done, int seconds = 30)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow >= until) return false;
                if (!H.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
            }
            return true;
        }

        public UUID Rez(SceneObjectPart part, string source, UUID assetId = default)
        {
            var item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, part, UUID.Random(),
                assetId.IsZero() ? UUID.Random() : assetId, "s" + Guid.NewGuid().ToString("N").Substring(0, 6), source);
            // the region's own path (SceneObjectPartInventory.CreateScriptInstance -> EventManager.OnRezScript), so the part
            // records the script and a derez (RemoveScriptInstances) reaches the engine as it does on a real region
            Assert.True(part.Inventory.CreateScriptInstance(item.ItemID, 0, false, H.Engine.Name, 0), "the core did not start the script");
            return item.ItemID;
        }

        /// <summary>The owner deletes the script from the prim's contents: the core raises OnRemoveScript.</summary>
        public void Delete(SceneObjectPart part, UUID item) => part.Inventory.RemoveInventoryItem(item);

        public void Say(int channel, string msg)
            => H.Scene.SimChat(msg, ChatTypeEnum.Region, channel, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

        public int Count(string line) => H.Said.Count(s => s == line);

        public void Dispose() => H.Dispose();
    }

    private static object Field(object o, string name, bool optional = false)
    {
        var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null && optional) return null;
        return f!.GetValue(o);
    }

    private static int CountOf(object o, string field)
        => o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o) is ICollection c ? c.Count : 0;

    /// <summary>The region's asset service, with one asset held back until the test lets it go - a slow notecard fetch.</summary>
    public class AssetGate : DispatchProxy
    {
        public OpenSim.Services.Interfaces.IAssetService Inner;
        public string HeldId;
        public readonly System.Threading.ManualResetEventSlim Release = new(false);
        public int Waiting;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "Get" && args.Length == 1 && args[0] as string == HeldId)
            {
                System.Threading.Interlocked.Increment(ref Waiting);
                Release.Wait(TimeSpan.FromSeconds(60));
            }
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }

        public static AssetGate Install(Scene scene, UUID held)
        {
            var proxy = Create<OpenSim.Services.Interfaces.IAssetService, AssetGate>();
            var gate = (AssetGate)(object)proxy;
            gate.Inner = scene.AssetService;
            gate.HeldId = held.ToString();
            typeof(Scene).GetField("m_AssetService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, proxy);
            return gate;
        }
    }

    /// <summary>
    /// Channel 7 drives it. "arm": a sensor repeat (nothing in range, so no_sensor every 0.2 s), a listen on 8, a timer, a
    /// URL, an HTTP request and an XML-RPC channel. "reset": llResetScript. "state": state other.
    /// </summary>
    private const string Armable = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            listen(integer c, string n, key k, string m) {
                if (c == 8) { llSay(0, ""heard8""); return; }
                if (m == ""arm"") {
                    llSensorRepeat(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI, 0.2);
                    llListen(8, """", NULL_KEY, """");
                    llSetTimerEvent(0.2);
                    llRequestURL();
                    llOpenRemoteDataChannel();
                    llSay(0, ""http "" + (string)llHTTPRequest(""http://example.invalid/phlox46"", [], """"));
                    llSay(0, ""armed"");
                }
                else if (m == ""reset"") llResetScript();
                else if (m == ""state"") state other;
            }
            no_sensor() { llSay(0, ""nosensor""); }
            timer() { llSay(0, ""tick""); }
            http_response(key id, integer status, list meta, string body) { llSay(0, ""response "" + body); }
            dataserver(key id, string data) { llSay(0, ""ds "" + data); }
        }
        state other {
            state_entry() { llSay(0, ""other""); }
            no_sensor() { llSay(0, ""nosensor""); }
            timer() { llSay(0, ""tick""); }
            http_response(key id, integer status, list meta, string body) { llSay(0, ""response "" + body); }
            dataserver(key id, string data) { llSay(0, ""ds "" + data); }
        }";

    private UUID Armed(Rig r)
    {
        var id = r.Rez(r.H.Prim, Armable);
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1), "never started: " + r.H.Diagnose(id));
        r.Say(7, "arm");
        Assert.True(r.PumpUntil(() => r.Count("armed") == 1), "never armed");
        Assert.True(r.PumpUntil(() => r.Count("nosensor") >= 1 && r.Count("tick") >= 1), "sensor or timer never fired");
        Assert.Equal(1, r.Sensors);
        Assert.Single(r.Http.InFlight);
        Assert.Equal(1, r.Url.Total);
        Assert.True(r.Xml.ChannelByItem.ContainsKey(id));
        Assert.Equal(2, r.Listens);   // 7 and 8
        Assert.True(r.Interp(id).ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.SensorRepeat));
        return id;
    }

    /// <summary>Nothing more is said for <paramref name="ms"/>, pumping all the while.</summary>
    private static void Quiet(Rig r, int ms, params string[] lines)
    {
        var before = lines.ToDictionary(l => l, l => r.H.Said.Count(s => s.StartsWith(l, StringComparison.Ordinal)));
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (!r.H.PumpOnceBusy()) System.Threading.Thread.Sleep(2); }
        foreach (var l in lines)
            Assert.True(before[l] == r.H.Said.Count(s => s.StartsWith(l, StringComparison.Ordinal)), "'" + l + "' was said after the end");
    }

    // ── delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteEndsEveryResourceAndLeavesNoTableEntry()
    {
        using var r = new Rig();
        var id = Armed(r);
        r.Delete(r.H.Prim, id);
        Assert.True(r.PumpUntil(() => r.Loaded == 0), "script never unloaded");

        Assert.Equal(0, r.Sensors);
        Assert.Equal(0, r.HttpTracked);
        Assert.Empty(r.Http.InFlight);                 // the core was told to stop it
        Assert.Contains(id, r.Http.StoppedFor);
        Assert.Equal(0, r.Url.Total);
        Assert.Contains(id, r.Url.RemovedFor);
        Assert.False(r.Xml.ChannelByItem.ContainsKey(id));
        Assert.Contains(id, r.Xml.CancelledFor);
        Assert.Equal(0, r.Listens);
        Assert.Equal(0, r.ListenScripts);
        Assert.Equal(0, r.RateRecords);
        Assert.Equal(0, r.Timers);
        Assert.Equal(0, r.Sleeps);
        Assert.Equal(0, r.Apis);
        Quiet(r, 1000, "nosensor", "tick");
    }

    [Fact]
    public void DeleteEndsAScriptAsleepInLlSleep()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"default { state_entry() { llSay(0, ""entry""); llSleep(60.0); llSay(0, ""woke""); } }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Sleeps == 1), "never slept");
        r.Delete(r.H.Prim, id);
        Assert.True(r.PumpUntil(() => r.Loaded == 0));
        Assert.Equal(0, r.Sleeps);
        Assert.Equal(0, r.Timers);
    }

    [Fact]
    public void DerezEndsEveryResource()
    {
        using var r = new Rig();
        var sog = SceneHelpers.AddSceneObject(r.H.Scene, "derezzed", UUID.Random());
        var id = r.Rez(sog.RootPart, Armable.Replace("llListen(7,", "llListen(9,"));
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1));
        r.H.Scene.SimChat("arm", ChatTypeEnum.Region, 9, sog.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(r.PumpUntil(() => r.Count("armed") == 1 && r.Sensors == 1 && r.Http.InFlight.Count == 1));
        r.H.Scene.DeleteSceneObject(sog, false);
        Assert.True(r.PumpUntil(() => r.Loaded == 0), "not unloaded on derez: " + r.H.Diagnose(id)
            + " pendingUnloads=" + ((ICollection)Field(r.H.Loader, "m_PendingUnloads")).Count + " deleted=" + sog.IsDeleted);
        Assert.Equal(0, r.Sensors);
        Assert.Equal(0, r.HttpTracked);
        Assert.Equal(0, r.Url.Total);
        Assert.False(r.Xml.ChannelByItem.ContainsKey(id));
        Assert.Equal(0, r.Listens);
        Assert.Equal(0, r.Timers);
    }

    // ── reset ──────────────────────────────────────────────────────────────────

    [Fact]
    public void LlResetScriptEndsSensorHttpUrlListenTimerAndKeepsTheXmlRpcChannel()
    {
        using var r = new Rig();
        var id = Armed(r);
        r.Say(7, "reset");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2), "no state_entry after the reset");

        Assert.Equal(0, r.Sensors);
        Assert.False(r.Interp(id).ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.SensorRepeat),
            "the sensor record survived the reset - a restore or parcel resume would bring the old sensor back");
        Assert.Equal(0, r.HttpTracked);
        Assert.Empty(r.Http.InFlight);
        Assert.Equal(0, r.Url.Total);                  // SL: "Any granted URLs are released."
        Assert.Equal(1, r.Listens);                     // only the new state_entry's channel 7
        Assert.Equal(0, r.Timers);
        Assert.Equal(0, r.Interp(id).ScriptState.TimerInterval);
        Assert.True(r.Xml.ChannelByItem.ContainsKey(id), "Halcyon keeps the XML-RPC channel across a reset");

        r.Say(8, "anyone");
        Quiet(r, 1200, "nosensor", "tick", "heard8");
    }

    [Fact]
    public void ViewerResetEndsTheSameResources()
    {
        using var r = new Rig();
        var id = Armed(r);
        r.H.Engine.ResetScript(id);   // the viewer's Reset (EventManager.OnScriptReset) and llResetOtherScript take this path
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2));
        Assert.Equal(0, r.Sensors);
        Assert.Equal(0, r.HttpTracked);
        Assert.Equal(0, r.Url.Total);
        Assert.Equal(1, r.Listens);
        Assert.Equal(0, r.Timers);
        Quiet(r, 1000, "nosensor", "tick");
    }

    [Fact]
    public void StateChangeEndsSensorAndListensButKeepsHttpAndUrls()
    {
        using var r = new Rig();
        var id = Armed(r);
        r.Say(7, "state");
        Assert.True(r.PumpUntil(() => r.Count("other") == 1));
        Assert.Equal(0, r.Sensors);                    // SL: "Repeating sensors are released."
        Assert.Equal(0, r.Listens);                     // SL: "All listens are released."
        Assert.Single(r.Http.InFlight);             // an http_response is taken in the new state
        Assert.Equal(1, r.Url.Total);                   // SL: "Unlike listeners, URLs persist across state changes"

        var req = r.Http.InFlight.Values.Single();
        r.Http.Respond(req, "after-state");
        Assert.True(r.PumpUntil(() => r.Count("response after-state") == 1), "the new state never got its http_response");
        Quiet(r, 800, "nosensor");
    }

    /// <summary>
    /// A sensor sweep already under way when the script changes state. The sensor plugin holds its repeater lock for the
    /// whole pass (CheckSenseRepeaterEvents -> SensorSweep -> PostScriptEvent), and the state change's release of the
    /// sensor (LSLSystemAPI ReleaseScriptResources -> SensorRepeat.RemoveScript) waits for that lock. The sweep's
    /// no_sensor must not run after the state statement, in either state. SL State: "The event queue is cleared."
    /// "Repeating sensors are released." A helper thread stands in for the sweep: it holds the plugin's lock, waits (no
    /// clock) until the state change has begun - the release drops the script's listens before it reaches the sensor -
    /// posts the no_sensor as SensorSweep does, and lets go.
    /// </summary>
    [Fact]
    public void ASweepUnderWayAtAStateChangeDoesNotReachTheNewState()
    {
        using var r = new Rig();
        // a 30 s repeat: no sweep of its own comes in the test; the helper's is the only one
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llListen(7, """", NULL_KEY, """"); llSensorRepeat(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI, 30.0); llSay(0, ""armed""); }
                listen(integer c, string n, key k, string m) { if (m == ""state"") state other; }
                no_sensor() { llSay(0, ""old nosensor""); }
            }
            state other {
                state_entry() { llSay(0, ""other""); llMessageLinked(LINK_THIS, 0, ""check"", NULL_KEY); }
                no_sensor() { llSay(0, ""late nosensor""); }
                link_message(integer s, integer n, string str, key k) { llSay(0, ""checked""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("armed") == 1), "never armed: " + r.H.Diagnose(id));
        Assert.Equal(1, r.Sensors);
        Assert.Equal(1, r.Listens);

        object sweepLock = Field(r.H.Engine.AsyncCommands.SensorRepeatPlugin, "SenseRepeatListLock");
        using var sweeping = new System.Threading.ManualResetEventSlim(false);
        bool sawChange = false, posted = false, signalled = false;
        var sweep = new System.Threading.Thread(() =>
        {
            lock (sweepLock)
            {
                sweeping.Set();
                sawChange = System.Threading.SpinWait.SpinUntil(() => r.Listens == 0, TimeSpan.FromSeconds(30));
                r.H.Engine.PostScriptEvent(id, new EventParams("no_sensor", Array.Empty<object>(), Array.Empty<DetectParams>()));
                // an event a caller waits on, posted in the same window: it is done with whether it runs or is cleared
                r.H.Engine.PostScriptEvent(id, new EventParams("touch_start", new object[] { 1 }, Array.Empty<DetectParams>()), () => signalled = true);
                posted = true;
            }
        }) { IsBackground = true };
        sweep.Start();
        Assert.True(sweeping.Wait(TimeSpan.FromSeconds(30)), "the stand-in sweep never started");

        r.Say(7, "state");
        Assert.True(r.PumpUntil(() => r.Count("checked") == 1), "the new state never ran: " + r.H.Diagnose(id));
        Assert.True(r.PumpUntil(() => System.Threading.Volatile.Read(ref signalled)), "a waiter was left waiting: " + r.H.Diagnose(id));
        Assert.True(sweep.Join(TimeSpan.FromSeconds(30)) && posted, "the stand-in sweep never posted");
        Assert.True(sawChange, "the state change did not release the listens while the sweep held the lock");
        Assert.Equal(0, r.Sensors);
        Assert.Equal(0, r.Count("late nosensor"));   // SL: "Repeating sensors are released." "The event queue is cleared."
        Assert.Equal(0, r.Count("old nosensor"));    // and the old state's handler does not run it after the state statement
    }

    /// <summary>
    /// An event already posted when the state statement runs, and not yet taken into the script's queue, is cleared with
    /// the queue: it runs neither in the old state's handler after the statement nor in the new state. llSensor posts its
    /// result on the script's own thread, so its no_sensor is waiting when the next statement changes state.
    /// </summary>
    [Fact]
    public void AnEventPostedJustBeforeTheStateStatementDoesNotRunAfterIt()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""armed""); }
                listen(integer c, string n, key k, string m) { llSensor(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI); state other; }
                no_sensor() { llSay(0, ""old nosensor""); }
                state_exit() { llSay(0, ""exit""); }
            }
            state other {
                state_entry() { llSay(0, ""other""); llMessageLinked(LINK_THIS, 0, ""check"", NULL_KEY); }
                no_sensor() { llSay(0, ""new nosensor""); }
                link_message(integer s, integer n, string str, key k) { llSay(0, ""checked""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("armed") == 1), "never armed: " + r.H.Diagnose(id));

        r.Say(7, "go");
        Assert.True(r.PumpUntil(() => r.Count("checked") == 1), "the new state never ran: " + r.H.Diagnose(id));
        Assert.Equal(1, r.Count("exit"));
        Assert.Equal(1, r.Count("other"));
        Assert.Equal(0, r.Count("old nosensor"));   // SL State: "The event queue is cleared."
        Assert.Equal(0, r.Count("new nosensor"));
    }

    /// <summary>
    /// A line of chat that has matched a listen of the old state, with its event not yet posted when the state change
    /// releases the listen, is not delivered: not to the old state's handler and not to the new state. SL State: "All
    /// listens are released." The listen manager's test hook holds the delivery between the match and the post while the
    /// script changes state; the delivery then goes on.
    /// </summary>
    [Fact]
    public void AListenDeliveryUnderWayAtAStateChangeDoesNotReachTheNewState()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llListen(7, """", NULL_KEY, """"); llListen(8, """", NULL_KEY, """"); llSay(0, ""armed""); }
                listen(integer c, string n, key k, string m) { if (c == 7) state other; else llSay(0, ""old heard""); }
            }
            state other {
                state_entry() { llSay(0, ""other""); }
                listen(integer c, string n, key k, string m) { llSay(0, ""new heard""); }
                link_message(integer s, integer n, string str, key k) { llSay(0, ""checked""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("armed") == 1), "never armed: " + r.H.Diagnose(id));
        Assert.Equal(2, r.Listens);

        var listens = r.H.Engine.ListenManager;
        using var matched = new System.Threading.ManualResetEventSlim(false);
        using var goOn = new System.Threading.ManualResetEventSlim(false);
        listens.BeforePostForTest = (item, channel) =>
        {
            if (item != id || channel != 8) return;
            matched.Set();
            goOn.Wait(TimeSpan.FromSeconds(30));
        };
        var delivery = new System.Threading.Thread(() => listens.DeliverChat(8, "tester", UUID.Random(), "late"))
            { IsBackground = true };
        delivery.Start();
        Assert.True(matched.Wait(TimeSpan.FromSeconds(30)), "the delivery never matched the listen");

        r.Say(7, "state");
        Assert.True(r.PumpUntil(() => r.Count("other") == 1), "the new state never ran: " + r.H.Diagnose(id));
        Assert.Equal(0, r.Listens);
        goOn.Set();
        Assert.True(delivery.Join(TimeSpan.FromSeconds(30)), "the delivery never finished");
        listens.BeforePostForTest = null;

        // Posted after the delivery finished, so once it has run anything the delivery posted has run before it
        r.H.Engine.PostScriptEvent(id, new EventParams("link_message", new object[] { 0, 0, "check", UUID.Zero.ToString() }, Array.Empty<DetectParams>()));
        Assert.True(r.PumpUntil(() => r.Count("checked") == 1), "the check never ran: " + r.H.Diagnose(id));
        Assert.Equal(0, r.Count("new heard"));
        Assert.Equal(0, r.Count("old heard"));
    }

    /// <summary>
    /// What a state change keeps: the timer goes on into the new state (SL llSetTimerEvent: "The timer persists across
    /// state changes"), and state_exit then state_entry run once each, in that order, also with a sensor repeat, a listen
    /// and an event waiting at the change.
    /// </summary>
    [Fact]
    public void TheTimerAndTheStateEventsSurviveTheStateChange()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llListen(7, """", NULL_KEY, """"); llSensorRepeat(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI, 30.0); llSay(0, ""armed""); }
                listen(integer c, string n, key k, string m) { llSetTimerEvent(0.2); llSensor(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI); state other; }
                state_exit() { llSay(0, ""exit""); }
                timer() { llSay(0, ""old tick""); }
            }
            state other {
                state_entry() { llSay(0, ""entry""); }
                timer() { llSetTimerEvent(0.0); llSay(0, ""new tick""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("armed") == 1), "never armed: " + r.H.Diagnose(id));

        r.Say(7, "go");
        Assert.True(r.PumpUntil(() => r.Count("new tick") == 1), "the timer did not carry into the new state: " + r.H.Diagnose(id));
        var said = r.H.Said.Where(s => s == "exit" || s == "entry").ToList();
        Assert.Equal(new[] { "exit", "entry" }, said);
        Assert.Equal(0, r.Count("old tick"));
        Assert.Equal(0, r.Sensors);
        Assert.Equal(0, r.Listens);
    }

    /// <summary>
    /// A line of chat delivered while a reset is under way does not reach the fresh script. SL llResetScript: "Listeners
    /// are removed." "The event queue is cleared." "If it has a state_entry event, then it is queued." The listen
    /// manager's test hook delivers the line at the moment the reset starts to release the script's listens, as a
    /// delivery on another thread can; the fresh script's state_entry runs first and once, and the line never runs.
    /// </summary>
    [Fact]
    public void AListenDeliveryUnderWayAtAResetDoesNotReachTheFreshScript()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llSay(0, ""entry""); llListen(7, """", NULL_KEY, """"); llListen(8, """", NULL_KEY, """"); }
                listen(integer c, string n, key k, string m) { if (c == 7) llResetScript(); else llSay(0, ""heard""); }
                link_message(integer s, integer n, string str, key k) { llSay(0, ""checked""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Listens == 2), "never listened: " + r.H.Diagnose(id));

        var listens = r.H.Engine.ListenManager;
        int delivered = 0;
        listens.BeforeRemoveForTest = item =>
        {
            if (item != id || System.Threading.Interlocked.Exchange(ref delivered, 1) != 0) return;
            listens.DeliverChat(8, "tester", UUID.Random(), "late");
        };
        r.Say(7, "reset");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2), "the fresh script never ran its state_entry: " + r.H.Diagnose(id));
        listens.BeforeRemoveForTest = null;
        Assert.Equal(1, delivered);

        // Posted after the reset, so once it has run anything the reset left behind has run before it
        r.H.Engine.PostScriptEvent(id, new EventParams("link_message", new object[] { 0, 0, "check", UUID.Zero.ToString() }, Array.Empty<DetectParams>()));
        Assert.True(r.PumpUntil(() => r.Count("checked") == 1), "the check never ran: " + r.H.Diagnose(id));
        Assert.Equal(new[] { "entry", "entry", "checked" }, r.H.Said.Where(s => s == "entry" || s == "heard" || s == "checked").ToArray());
        Assert.Equal(2, r.Listens);
    }

    /// <summary>
    /// A sensor sweep under way when the owner resets the script (the viewer's Reset, the queued path) does not reach
    /// the fresh script. SL llResetScript: "Timers (including repeating sensors) are cleared." "The event queue is
    /// cleared." A helper thread stands in for the sweep, as in the state change test: it holds the sensor plugin's lock,
    /// waits (no clock) until the reset has released the listens, posts a no_sensor and an event a caller waits on, and
    /// lets go. state_entry runs first and once; the no_sensor never runs; the waiter is not left waiting.
    /// </summary>
    [Fact]
    public void ASweepUnderWayAtAResetDoesNotReachTheFreshScript()
    {
        using var r = new Rig();
        // a 30 s repeat: no sweep of its own comes in the test; the helper's is the only one
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llSay(0, ""entry""); llListen(7, """", NULL_KEY, """"); llSensorRepeat(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI, 30.0); }
                no_sensor() { llSay(0, ""nosensor""); }
                link_message(integer s, integer n, string str, key k) { llSay(0, ""checked""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Sensors == 1 && r.Listens == 1), "never armed: " + r.H.Diagnose(id));

        object sweepLock = Field(r.H.Engine.AsyncCommands.SensorRepeatPlugin, "SenseRepeatListLock");
        using var sweeping = new System.Threading.ManualResetEventSlim(false);
        bool sawReset = false, posted = false, signalled = false;
        var sweep = new System.Threading.Thread(() =>
        {
            lock (sweepLock)
            {
                sweeping.Set();
                sawReset = System.Threading.SpinWait.SpinUntil(() => r.Listens == 0, TimeSpan.FromSeconds(30));
                r.H.Engine.PostScriptEvent(id, new EventParams("no_sensor", Array.Empty<object>(), Array.Empty<DetectParams>()));
                r.H.Engine.PostScriptEvent(id, new EventParams("touch_start", new object[] { 1 }, Array.Empty<DetectParams>()), () => signalled = true);
                posted = true;
            }
        }) { IsBackground = true };
        sweep.Start();
        Assert.True(sweeping.Wait(TimeSpan.FromSeconds(30)), "the stand-in sweep never started");

        r.H.Engine.ResetScript(id);
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2), "the fresh script never ran its state_entry: " + r.H.Diagnose(id));
        Assert.True(sweep.Join(TimeSpan.FromSeconds(30)) && posted, "the stand-in sweep never posted");
        Assert.True(sawReset, "the reset did not release the listens while the sweep held the lock");
        Assert.True(r.PumpUntil(() => System.Threading.Volatile.Read(ref signalled)), "a waiter was left waiting: " + r.H.Diagnose(id));

        r.H.Engine.PostScriptEvent(id, new EventParams("link_message", new object[] { 0, 0, "check", UUID.Zero.ToString() }, Array.Empty<DetectParams>()));
        Assert.True(r.PumpUntil(() => r.Count("checked") == 1), "the check never ran: " + r.H.Diagnose(id));
        Assert.Equal(new[] { "entry", "entry", "checked" }, r.H.Said.Where(s => s == "entry" || s == "nosensor" || s == "checked").ToArray());
        Assert.Equal(1, r.Sensors);   // the fresh state_entry's own repeat
    }

    /// <summary>
    /// An event already in the script's own queue when it resets is cleared ("The event queue is cleared.") and does not
    /// run, and a caller waiting on it is told it is done. The script is busy in a handler when the event arrives, so the
    /// event waits in its queue; the handler then calls llResetScript.
    /// </summary>
    [Fact]
    public void AnEventQueuedAtAResetIsClearedAndItsWaiterIsNotLeftWaiting()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"
            default {
                state_entry() { llSay(0, ""entry""); llListen(7, """", NULL_KEY, """"); }
                listen(integer c, string n, key k, string m) { llSay(0, ""busy""); integer i; for (i = 0; i < 2000; ++i) { } llResetScript(); }
                touch_start(integer n) { llSay(0, ""touched""); }
            }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Listens == 1), "never listened: " + r.H.Diagnose(id));

        r.Say(7, "go");
        Assert.True(r.PumpUntil(() => r.Count("busy") == 1), "the handler never ran: " + r.H.Diagnose(id));
        bool signalled = false;
        r.H.Engine.PostScriptEvent(id, new EventParams("touch_start", new object[] { 1 }, Array.Empty<DetectParams>()), () => signalled = true);
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2), "the fresh script never ran its state_entry: " + r.H.Diagnose(id));
        Assert.True(r.PumpUntil(() => System.Threading.Volatile.Read(ref signalled)), "a waiter was left waiting: " + r.H.Diagnose(id));
        Assert.Equal(0, r.Count("touched"));
    }

    /// <summary>
    /// Chat floods the channels of scripts that change state on every line they hear, while other threads reset scripts,
    /// delete and add them, and the scripts call llListenRemove. A listen delivery posts while it holds the listen
    /// manager's lock, which the state change, the reset, llListenRemove and the unload take to release a listen, so this
    /// checks that none of them freezes against a delivery: every thread, the scheduler's pump included, must finish
    /// inside its limit. And no listen of the state a script left is heard after its state statement (SL State: "All
    /// listens are released.").
    /// </summary>
    [Fact]
    public void ListensUnderAChatFloodWithStateChangesResetsAndRemovals()
    {
        using var r = new Rig(resetThrottle: false, chatThrottle: false);
        // default hears 7 (and 9, removed at once); state two hears only 8. Any other channel is an old state's listen.
        const string flipper = @"
            default {
                state_entry() { llListen(7, """", NULL_KEY, """"); integer h = llListen(9, """", NULL_KEY, """"); llListenRemove(h); }
                listen(integer c, string n, key k, string m) { if (c == 8) llSay(0, ""BAD default heard 8""); else if (c == 7) state two; }
                link_message(integer s, integer i, string str, key k) { llSay(0, ""checked""); }
            }
            state two {
                state_entry() { llSay(0, ""flip""); llListen(8, """", NULL_KEY, """"); }
                listen(integer c, string n, key k, string m) { if (c != 8) llSay(0, ""BAD two heard "" + (string)c); else state default; }
                link_message(integer s, integer i, string str, key k) { llSay(0, ""checked""); }
            }";
        // Reset by its own llResetScript and from outside, on the same channels.
        const string resetter = @"
            default {
                state_entry() { llSay(0, ""fresh""); llListen(7, """", NULL_KEY, """"); llListen(8, """", NULL_KEY, """"); }
                listen(integer c, string n, key k, string m) { if (m == ""reset"") llResetScript(); }
                link_message(integer s, integer i, string str, key k) { llSay(0, ""checked""); }
            }";
        var flippers = new System.Collections.Concurrent.ConcurrentDictionary<UUID, byte>();
        var resetters = new List<UUID>();
        for (int i = 0; i < 4; i++) flippers[r.Rez(r.H.Prim, flipper)] = 0;
        for (int i = 0; i < 2; i++) resetters.Add(r.Rez(r.H.Prim, resetter));
        Assert.True(r.PumpUntil(() => r.Listens == 4 + 2 * 2), "the scripts never listened: " + r.Listens);

        int stop = 0, removals = 0, outsideResets = 0;
        bool Stopping() => System.Threading.Volatile.Read(ref stop) != 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        System.Threading.Thread Run(string name, Action body) => new(() =>
        {
            try { body(); } catch (Exception e) { failures.Enqueue(e); }
        }) { IsBackground = true, Name = name };

        var listens = r.H.Engine.ListenManager;
        listens.MaxListenEventsPerSecond = 0;   // no per-script cap: every line the flood sends is delivered
        long beats = 0, lines = 0;
        // The prim's inventory is changed on the pump thread, between pumps: LSLSystemAPI.GetInventorySelf locks the
        // dictionary itself, not the TaskInventoryDictionary lock the core's writers take, so a change from another
        // thread can break its enumeration. That is not what this test is about.
        var inventoryChanges = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var pump = Run("pump", () =>
        {
            while (!Stopping())
            {
                System.Threading.Interlocked.Increment(ref beats);
                while (inventoryChanges.TryDequeue(out var change)) change();
                if (!r.H.PumpOnceBusy()) System.Threading.Thread.Yield();
            }
        });
        var flood = Run("flood", () =>
        {
            UUID speaker = UUID.Random();
            for (int i = 0; !Stopping(); i++)
            {
                // as fast as the scheduler takes it: a flood that outruns it only grows its queue
                if (r.Exe.PendingEventCount >= 100) { System.Threading.Thread.Yield(); continue; }
                listens.DeliverChat(7 + i % 3, "flood", speaker, i % 40 == 0 ? "reset" : "x");
                System.Threading.Interlocked.Increment(ref lines);
            }
        });
        var resets = Run("resets", () =>
        {
            for (int i = 0; !Stopping(); i++)
            {
                // the next reset once the last one has started the script afresh (no clock), as a resident's would
                int fresh = r.Count("fresh");
                r.H.Engine.ResetScript(resetters[i % resetters.Count]);
                System.Threading.Interlocked.Increment(ref outsideResets);
                System.Threading.SpinWait.SpinUntil(() => Stopping() || r.Count("fresh") > fresh, TimeSpan.FromSeconds(10));
            }
        });
        var removal = Run("removal", () =>
        {
            while (!Stopping())
            {
                // wait for flips between removals (no clock), so the scripts live long enough to change state
                int seen = r.Count("flip");
                if (!System.Threading.SpinWait.SpinUntil(() => Stopping() || r.Count("flip") >= seen + 20, TimeSpan.FromSeconds(10))) return;
                if (Stopping()) return;
                UUID gone = flippers.Keys.First();
                flippers.TryRemove(gone, out _);
                int done = 0;
                inventoryChanges.Enqueue(() =>
                {
                    r.Delete(r.H.Prim, gone);
                    flippers[r.Rez(r.H.Prim, flipper)] = 0;
                    System.Threading.Interlocked.Increment(ref removals);
                    System.Threading.Volatile.Write(ref done, 1);
                });
                // the pump stops taking changes once the test stops it
                if (!System.Threading.SpinWait.SpinUntil(() => Stopping() || System.Threading.Volatile.Read(ref done) != 0, TimeSpan.FromSeconds(10))) return;
            }
        });
        foreach (var t in new[] { pump, flood, resets, removal }) t.Start();

        // Until enough of everything has happened, with a generous cap
        bool enough = System.Threading.SpinWait.SpinUntil(
            () => r.Count("flip") >= 400 && r.Count("fresh") >= 100 && System.Threading.Volatile.Read(ref removals) >= 10 || !failures.IsEmpty,
            TimeSpan.FromSeconds(30));
        System.Threading.Volatile.Write(ref stop, 1);
        int pending0 = r.PendingEvents; long beats0 = System.Threading.Interlocked.Read(ref beats);
        var stuck = new[] { pump, flood, resets, removal }.Where(t => !t.Join(TimeSpan.FromSeconds(10))).Select(t => t.Name).ToList();
        Assert.True(stuck.Count == 0, "froze: " + string.Join(", ", stuck) + " did not finish;" +
            $" pendingAtStop={pending0} pendingNow={r.PendingEvents} beatsAtStop={beats0} beatsNow={System.Threading.Interlocked.Read(ref beats)} lines={System.Threading.Interlocked.Read(ref lines)} enough={enough}" +
            $" flips={r.Count("flip")} fresh={r.Count("fresh")} removals={removals} outside resets={outsideResets}");
        Assert.True(failures.IsEmpty, string.Join("\n", failures));
        Assert.True(enough, $"too little happened: flips={r.Count("flip")} fresh={r.Count("fresh")} removals={removals} outside resets={outsideResets}");

        // A reset or a load still queued would clear the check, so they run first. Everything posted before this point
        // has run once the check has run in every script.
        Assert.True(r.H.PumpUntilIdle(TimeSpan.FromSeconds(30)), "the scheduler never went idle after the flood");
        foreach (UUID id in flippers.Keys.Concat(resetters))
            r.H.Engine.PostScriptEvent(id, new EventParams("link_message", new object[] { 0, 0, "check", UUID.Zero.ToString() }, Array.Empty<DetectParams>()));
        int scripts = flippers.Count + resetters.Count;
        Assert.True(r.PumpUntil(() => r.Count("checked") >= scripts), "the check never ran in every script: " + r.Count("checked") + "/" + scripts);
        var bad = r.H.Said.Where(s => s.StartsWith("BAD", StringComparison.Ordinal)).ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} old listen event(s) ran: {string.Join(" | ", bad.GroupBy(s => s).Select(g => g.Key + " x" + g.Count()))}" +
            $" (flips={r.Count("flip")} fresh={r.Count("fresh")} removals={removals} outside resets={outsideResets})");
    }

    // ── late replies ───────────────────────────────────────────────────────────

    [Fact]
    public void LateHttpResponseForADeletedScriptIsDropped()
    {
        using var r = new Rig();
        // a second script in the prim: http_response goes to every script in the prim, so it would hear a leak
        r.Rez(r.H.Prim, @"default { state_entry() { llSay(0, ""witness""); }
            http_response(key id, integer s, list m, string b) { llSay(0, ""witness heard "" + b); } }");
        Assert.True(r.PumpUntil(() => r.Count("witness") == 1));
        var id = Armed(r);
        var req = r.Http.InFlight.Values.Single();

        r.Delete(r.H.Prim, id);
        Assert.True(r.PumpUntil(() => r.Loaded == 1));
        r.Http.Respond(req, "late");                    // completed in the core before the stop reached it
        Assert.True(r.PumpUntil(() => r.Http.Completed.IsEmpty), "the pump never took the response");
        Quiet(r, 800, "witness heard", "response");
        Assert.Equal((0, 0), r.Held);
    }

    [Fact]
    public void LateHttpResponseForAResetScriptIsDropped()
    {
        using var r = new Rig();
        var id = Armed(r);
        var req = r.Http.InFlight.Values.Single();
        r.Say(7, "reset");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2));
        r.Http.Respond(req, "late");
        Assert.True(r.PumpUntil(() => r.Http.Completed.IsEmpty), "the pump never took the response");
        Quiet(r, 800, "response");
    }

    [Fact]
    public void LiveHttpResponseIsStillDelivered()
    {
        using var r = new Rig();
        Armed(r);
        r.Http.Respond(r.Http.InFlight.Values.Single(), "fresh");
        Assert.True(r.PumpUntil(() => r.Count("response fresh") == 1));
        Assert.Equal(0, r.HttpTracked);
    }

    /// <summary>
    /// A response to another engine's request that Phlox's pump happens to take is not dropped. It goes to that
    /// engine as YEngine's pump sends it (PhloxCrossEngineHttpResponseTests proves the delivery with YEngine running).
    /// Phlox's own scripts in the prim get it too, once: SL - "triggered in all scripts in the prim, not just in the
    /// requesting script" (they once did not, as YEngine's pump never gave them one).
    /// </summary>
    [Fact]
    public void AnotherEnginesResponseIsNotDropped()
    {
        using var r = new Rig();
        r.Rez(r.H.Prim, @"default { state_entry() { llSay(0, ""witness""); }
            http_response(key id, integer s, list m, string b) { llSay(0, ""witness heard "" + b); } }");
        Assert.True(r.PumpUntil(() => r.Count("witness") == 1));
        // an item in the prim that Phlox does not run (another engine's script)
        var other = TaskInventoryHelpers.AddScript(r.H.Scene.AssetService, r.H.Prim, UUID.Random(), UUID.Random(), "yengine-one", "// other engine");
        r.Http.Respond(new FakeReq { ItemID = other.ItemID, LocalID = r.H.Prim.LocalId, ReqID = UUID.Random() }, "theirs");
        Assert.True(r.PumpUntil(() => r.Http.Completed.IsEmpty), "the pump never took the response");
        Assert.Equal(0L, r.H.Engine.AsyncCommands.HttpRequestPlugin.DroppedResponses);   // taken as another engine's, not dropped
        Assert.True(r.PumpUntil(() => r.Count("witness heard theirs") == 1), "the prim's Phlox script never heard it");
        Quiet(r, 800, "witness heard");                                                 // once
    }

    /// <summary>The response as the core's pump (Shared/Api/Plugins/HttpRequest.cs) offers one it took to Phlox.</summary>
    private static bool OfferAsCorePump(Rig r, UUID reqID, string body) =>
        r.H.Engine.PostObjectEvent(r.H.Prim.LocalId, new EventParams("http_response", new object[]
        {
            new LSL_Types.LSLString(reqID.ToString()), new LSL_Types.LSLInteger(200), new LSL_Types.list(), new LSL_Types.LSLString(body)
        }, new DetectParams[0]));

    /// <summary>
    /// YEngine's pump (the core) can take a Phlox script's response and offer it to Phlox's PostObjectEvent. A live
    /// one is delivered once and is no longer outstanding.
    /// </summary>
    [Fact]
    public void LiveHttpResponseOfferedByAnotherPumpIsDelivered()
    {
        using var r = new Rig();
        Armed(r);
        var req = r.Http.InFlight.Values.Single();
        r.Http.InFlight.TryRemove(req.ReqID, out _);                 // taken by the other pump, never by Phlox's
        Assert.True(OfferAsCorePump(r, req.ReqID, "fresh"));
        Assert.True(r.PumpUntil(() => r.Count("response fresh") == 1));
        Quiet(r, 800, "response");
        Assert.Equal(0, r.HttpTracked);
    }

    /// <summary>
    /// The same when the script was reset since it asked - dropped, whichever pump took it.
    /// </summary>
    [Fact]
    public void LateHttpResponseForAResetScriptOfferedByAnotherPumpIsDropped()
    {
        using var r = new Rig();
        Armed(r);
        var req = r.Http.InFlight.Values.Single();
        r.Say(7, "reset");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2));
        Assert.False(OfferAsCorePump(r, req.ReqID, "late"));
        Quiet(r, 800, "response");
        Assert.Equal(1L, r.H.Engine.AsyncCommands.HttpRequestPlugin.DroppedResponses);
    }

    private const string Reader = @"
        default {
            state_entry() { llSay(0, ""entry""); llListen(7, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) {
                if (m == ""read"") { llGetNotecardLine(""card"", 1); llSay(0, ""asked""); }
                else if (m == ""reset"") llResetScript();
            }
            dataserver(key id, string d) { llSay(0, ""ds "" + d); }
        }";

    /// <summary>A notecard in the prim whose fetch is held until the test releases it: the answer comes when the test says.</summary>
    private static AssetGate SlowNotecard(Rig r)
    {
        var nc = AssetHelpers.CreateNotecardAsset(UUID.Random(), "line zero\nline one");
        r.H.Scene.AssetService.Store(nc);
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = nc.FullID, Name = "card", Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard,
        }, false);
        return AssetGate.Install(r.H.Scene, nc.FullID);
    }

    [Fact]
    public void AnOwedDataserverAnswerIsDelivered()
    {
        using var r = new Rig();
        var gate = SlowNotecard(r);
        r.Rez(r.H.Prim, Reader);
        // The script says "entry" before it opens its listen, and llSay now sleeps 15 ms: wait for the listen
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Listens == 1));
        r.Say(7, "read");
        Assert.True(r.PumpUntil(() => r.Count("asked") == 1 && gate.Waiting == 1));
        gate.Release.Set();
        Assert.True(r.PumpUntil(() => r.Count("ds line one") == 1), "the answer never came");
    }

    [Fact]
    public void LateDataserverAnswerAfterAResetIsDropped()
    {
        using var r = new Rig();
        var gate = SlowNotecard(r);
        r.Rez(r.H.Prim, Reader);
        // The script says "entry" before it opens its listen, and llSay now sleeps 15 ms: wait for the listen
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Listens == 1));
        r.Say(7, "read");
        Assert.True(r.PumpUntil(() => r.Count("asked") == 1 && gate.Waiting == 1), "the fetch never started");
        r.Say(7, "reset");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 2));
        gate.Release.Set();                              // the answer arrives after the reset
        Quiet(r, 1500, "ds");
        Assert.Equal(0, r.PendingEvents);
    }

    [Fact]
    public void LateDataserverAnswerForADeletedScriptIsDroppedAndNotHeld()
    {
        using var r = new Rig();
        var gate = SlowNotecard(r);
        var id = r.Rez(r.H.Prim, Reader);
        // The script says "entry" before it opens its listen, and llSay now sleeps 15 ms: wait for the listen
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1 && r.Listens == 1));
        r.Say(7, "read");
        Assert.True(r.PumpUntil(() => r.Count("asked") == 1 && gate.Waiting == 1), "the fetch never started");
        r.Delete(r.H.Prim, id);
        Assert.True(r.PumpUntil(() => r.Loaded == 0));
        gate.Release.Set();                              // the answer arrives after the delete
        var until = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < until) { if (!r.H.PumpOnceBusy()) System.Threading.Thread.Sleep(2); }
        Assert.Equal((0, 0), r.Held);                   // not kept for an item that is gone
        Assert.Equal(0, r.PendingEvents);
    }

    // ── events for items that are not loaded ───────────────────────────────────

    private static EventParams LinkMessage(int n) => new("link_message", new object[] { 1, n, "x", UUID.Zero.ToString() }, null);

    [Fact]
    public void TenThousandEventsForAnUnloadedItemDoNotAccumulate()
    {
        using var r = new Rig();
        var gone = r.Rez(r.H.Prim, "default { state_entry() { llSay(0, \"entry\"); } }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1));
        r.Delete(r.H.Prim, gone);
        Assert.True(r.PumpUntil(() => r.Loaded == 0));
        var stranger = UUID.Random();                    // never a Phlox script (e.g. another engine's)

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long memBefore = GC.GetTotalMemory(true);
        long droppedBefore = r.Dropped;
        int maxPending = 0;
        for (int i = 0; i < 10_000; i++)
        {
            r.H.Engine.PostScriptEvent(i % 2 == 0 ? gone : stranger, LinkMessage(i));
            if (i % 500 == 499) { maxPending = Math.Max(maxPending, r.PendingEvents); r.H.PumpOnce(); }
        }
        r.H.PumpOnce();
        var st = r.Held;
        long dropped = r.Dropped - droppedBefore;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long memAfter = GC.GetTotalMemory(true);
        _out.WriteLine($"10,000 events: held items={st.Items} held events={st.Events} dropped={dropped} " +
                       $"pending after={r.PendingEvents} max pending between pumps={maxPending} memory delta={(memAfter - memBefore) / 1024} KiB");
        Assert.Equal(0, st.Items);
        Assert.Equal(0, st.Events);
        Assert.Equal(10_000, dropped);
        Assert.Equal(0, r.PendingEvents);
        Assert.True(maxPending <= 500, "pending grew past one batch: " + maxPending);
        Assert.True(memAfter - memBefore < 2 * 1024 * 1024, "memory grew " + (memAfter - memBefore) + " bytes");
    }

    [Fact]
    public void EventsForAScriptStillLoadingAreHeldUpTo32()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"integer n;
            default { state_entry() { llSay(0, ""entry""); llSetTimerEvent(1.0); }
                      link_message(integer s, integer num, string str, key k) { n++; }
                      timer() { llSay(0, ""got "" + (string)n); llSetTimerEvent(0); } }");
        for (int i = 0; i < 100; i++) r.H.Engine.PostScriptEvent(id, LinkMessage(i));   // before it has loaded
        Assert.True(r.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("got ", StringComparison.Ordinal))), r.H.Diagnose(id));
        Assert.Equal("got 32", r.H.Said.First(s => s.StartsWith("got ", StringComparison.Ordinal)));   // Halcyon MAX_DEFERRED_EVENTS
        Assert.Equal(0, r.Held.Items);
    }

    [Fact]
    public void HeldEventsExpire()
    {
        using var r = new Rig();
        var exe = r.Exe;
        var add = typeof(PhloxExecutionScheduler).GetMethod("AddDeferredEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var expire = typeof(PhloxExecutionScheduler).GetMethod("ExpireDeferredEvents", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var id = UUID.Random();
        for (int i = 0; i < 40; i++) add.Invoke(exe, new object[] { id, new PostedEvent() });
        Assert.Equal((1, 32), r.Held);
        Assert.True(expire != null, "held events never expire");
        expire.Invoke(exe, null);
        Assert.Equal(1, r.Held.Items);       // not yet 60 s old
        var entries = (IDictionary)Field(exe, "m_DeferredEvents");
        foreach (var v in entries.Values) v.GetType().GetField("ExpiresOn")!.SetValue(v, 0UL);
        typeof(PhloxExecutionScheduler).GetField("m_NextDeferredExpiry", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(exe, 0UL);
        expire.Invoke(exe, null);
        Assert.Equal((0, 0), r.Held);
    }

    // ── 500 scripts ────────────────────────────────────────────────────────────

    private const string Holder = @"
        default { state_entry() {
            llSensorRepeat(""no-such-thing"", NULL_KEY, ACTIVE | PASSIVE, 5.0, PI, 60.0);
            llListen(5, """", NULL_KEY, """");
            llRequestURL();
            llHTTPRequest(""http://example.invalid/holder"", [], """");
            llSetTimerEvent(60.0);
        } }";

    [Fact]
    public void RezAndDelete500ScriptsLeavesThePluginTablesEmpty()
    {
        using var r = new Rig();
        var asset = UUID.Random();
        var objects = Enumerable.Range(0, 10).Select(i => SceneHelpers.AddSceneObject(r.H.Scene, "holder" + i, UUID.Random())).ToList();
        var items = new List<(SceneObjectGroup Sog, UUID Id)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var sog in objects)
            for (int i = 0; i < 50; i++) items.Add((sog, r.Rez(sog.RootPart, Holder, asset)));
        // Halcyon's in-flight cap holds 10 requests per object, so 10 of each object's 50 are in flight (100);
        // the other 400 scripts' requests got NULL_KEY. The cleanup below is unchanged.
        const int httpInFlight = 10 * 10;
        Assert.True(r.PumpUntil(() => r.Sensors == 500 && r.Http.InFlight.Count == httpInFlight && r.Url.Total == 500 && r.Listens == 500 && r.Timers == 500, 180),
            $"not all armed: loaded={r.Loaded} sensors={r.Sensors} http={r.Http.InFlight.Count} urls={r.Url.Total} listens={r.Listens} timers={r.Timers}");
        long armedMs = sw.ElapsedMilliseconds;
        r.Say(5, "hello all");                           // a listen-rate record for every script
        Assert.True(r.PumpUntil(() => r.RateRecords == 500));

        // half by deleting the script from the contents, half by derezzing the object
        foreach (var (sog, id) in items.Where(x => objects.IndexOf(x.Sog) < 5)) r.Delete(sog.RootPart, id);
        foreach (var sog in objects.Skip(5)) r.H.Scene.DeleteSceneObject(sog, false);
        Assert.True(r.PumpUntil(() => r.Loaded == 0, 120), "loaded=" + r.Loaded);
        r.H.PumpOnce();
        var st = r.Held;
        _out.WriteLine($"500 scripts armed in {armedMs} ms; after delete/derez: loaded={r.Loaded} apis={r.Apis} sensors={r.Sensors} " +
                       $"http tracked={r.HttpTracked} http in flight in core={r.Http.InFlight.Count} urls={r.Url.Total} listens={r.Listens} " +
                       $"listen scripts={r.ListenScripts} rate records={r.RateRecords} timers={r.Timers} sleeps={r.Sleeps} " +
                       $"held items={st.Items} held events={st.Events} pending={r.PendingEvents}");
        Assert.Equal(0, r.Apis);
        Assert.Equal(0, r.Sensors);
        Assert.Equal(0, r.HttpTracked);
        Assert.Empty(r.Http.InFlight);
        Assert.Equal(0, r.Url.Total);
        Assert.Equal(0, r.Listens);
        Assert.Equal(0, r.ListenScripts);
        Assert.Equal(0, r.RateRecords);
        Assert.Equal(0, r.Timers);
        Assert.Equal(0, r.Sleeps);
        Assert.Equal(0, st.Items);
        Assert.Equal(0, r.PendingEvents);
    }

    // ── reset throttle ─────────────────────────────────────────────────────────

    /// <summary>Up to <paramref name="n"/> resets back to back; with <paramref name="untilThrottled"/>, stop at the first throttle.</summary>
    private static int ResetFlood(Rig r, UUID id, int n, bool untilThrottled = false)
    {
        for (int i = 0; i < n; i++)
        {
            r.H.Engine.ResetScript(id);
            r.H.PumpOnce();
            if (untilThrottled && r.ResetSleeps(id) > 0) return i + 1;
        }
        return n;
    }

    /// <summary>
    /// The throttle counts resets per second of the engine's clock (Clock), and its 5 s sleep is on the same clock, so this
    /// test stops that clock: the whole flood falls in one second, and the throttle must trip at exactly the sixth reset.
    /// The clock is then moved by hand to show the sleep lasts the full 5 s. The class is already in "phlox-state", which
    /// the process-wide clock needs.
    /// </summary>
    [Fact]
    public void TwentyResetsInASecondTriggerTheThrottle()
    {
        bool frozen = false;
        ulong now = 0;
        InWorldz.Phlox.Util.Clock.SetSourceForTesting(() => frozen ? now : (ulong)Environment.TickCount64);
        try
        {
            // The chat pause (ChatThrottle) is a sleep on the same clock; off, so only the reset throttle can put it to sleep.
            using var r = new Rig(chatThrottle: false);
            var id = r.Rez(r.H.Prim, @"default { state_entry() { llSay(0, ""entry""); } }");
            Assert.True(r.PumpUntil(() => r.Count("entry") == 1));
            now = (ulong)Environment.TickCount64;
            frozen = true;
            int resets = ResetFlood(r, id, 20, untilThrottled: true);
            _out.WriteLine("throttled at reset " + resets);
            Assert.True(r.ResetSleeps(id) >= 1, "no throttle after 20 resets");
            Assert.Equal(6, resets);   // more than 5 in one second: the sixth
            Assert.Equal(RuntimeState.Status.Sleeping, r.Interp(id).ScriptState.RunState);
            Assert.Contains(r.H.SaidOn, s => s.Channel == 0x7FFFFFFF && s.Message.Contains("calling llResetScript too frequently"));

            // it runs its state_entry once the 5 s are up, and not before
            int entries = r.Count("entry");
            now += 4999;
            Assert.True(r.H.PumpUntilIdle(TimeSpan.FromSeconds(10)));
            Assert.Equal(entries, r.Count("entry"));
            Assert.Equal(RuntimeState.Status.Sleeping, r.Interp(id).ScriptState.RunState);
            now += 1;
            Assert.True(r.PumpUntil(() => r.Count("entry") > entries, 15), "never woke from the throttle 5 s on");
        }
        finally
        {
            InWorldz.Phlox.Util.Clock.SetSourceForTesting(null);
        }
    }

    /// <summary>The flood Halcyon's throttle is for: a script that resets itself from its own state_entry.</summary>
    [Fact]
    public void AScriptResettingItselfTwentyTimesIsSlowedByFiveSeconds()
    {
        using var r = new Rig();
        r.H.Prim.Description = "0";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var id = r.Rez(r.H.Prim, @"default { state_entry() {
            integer n = (integer)llGetObjectDesc();
            if (n < 20) { llSetObjectDesc((string)(n + 1)); llResetScript(); }
            else llSay(0, ""done"");
        } }");
        Assert.True(r.PumpUntil(() => r.Count("done") == 1, 60), "never finished: desc=" + r.H.Prim.Description + " " + r.H.StatusOf(id));
        _out.WriteLine($"20 self-resets took {sw.ElapsedMilliseconds} ms, throttle sleeps {r.ResetSleeps(id)}");
        Assert.True(r.ResetSleeps(id) >= 1, "never throttled");
        Assert.True(sw.ElapsedMilliseconds >= 4500, "20 self-resets in " + sw.ElapsedMilliseconds + " ms: not throttled");
    }

    [Fact]
    public void ResetThrottleOffLeavesResetsAlone()
    {
        using var r = new Rig(resetThrottle: false);
        var id = r.Rez(r.H.Prim, @"default { state_entry() { llSay(0, ""entry""); } }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1));
        ResetFlood(r, id, 20);
        Assert.Equal(0, r.ResetSleeps(id));
        Assert.NotEqual(RuntimeState.Status.Sleeping, r.Interp(id).ScriptState.RunState);
    }

    [Fact]
    public void FiveResetsInASecondAreNotThrottled()
    {
        using var r = new Rig();
        var id = r.Rez(r.H.Prim, @"default { state_entry() { llSay(0, ""entry""); } }");
        Assert.True(r.PumpUntil(() => r.Count("entry") == 1));
        // Halcyon: "++m_resetCount > MAX_RESETS_PER_SECOND" - the sixth in one second is the first punished
        ResetFlood(r, id, 5);
        Assert.Equal(0, r.ResetSleeps(id));
    }
}
