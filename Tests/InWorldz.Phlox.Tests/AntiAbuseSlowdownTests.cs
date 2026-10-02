using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Halcyon's anti-abuse slowdowns, each on by default under its own
/// [InWorldz.Phlox] setting - ChatThrottle, BotThrottle, PhysicsThrottle, LinkMessageThrottle, NotecardThrottle,
/// NotecardCache, FormatStringThrottle - and false restores what Phlox did before. Each rule's Halcyon source is on its
/// LSLSystemAPI helper.
///
/// <para>A slowdown is Halcyon's ScriptSleep: the call returns and the script's wake-up is set that many ms ahead. The
/// engine's clock (Clock) is frozen by these tests, so the sleep a call sets is read exactly as NextWakeup - now, and a
/// sleeping script provably does not run on until the clock is moved past it. That clock is process-wide, hence
/// "phlox-state". The calls under test are made on a loaded, idle script's own API object from the test thread while
/// nothing pumps the scheduler, which is how the script thread makes them.</para>
/// </summary>
[Collection("phlox-state")]
public class AntiAbuseSlowdownTests
{
    private readonly ITestOutputHelper _out;
    public AntiAbuseSlowdownTests(ITestOutputHelper o) => _out = o;

    private const string Idle = "default { state_entry() { } }";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        private bool m_frozen;
        public ulong Now;

        /// <param name="off">settings to turn off, e.g. "ChatThrottle"</param>
        public Rig(params string[] off)
        {
            Clock.SetSourceForTesting(() => m_frozen ? Now : (ulong)Environment.TickCount64);
            H = new SchedulerHarness(cfg => { foreach (var k in off) cfg.Configs["InWorldz.Phlox"].Set(k, "false"); });
        }

        /// <summary>Stop the engine's clock where it is; from here only the test moves it.</summary>
        public void Freeze() { Now = (ulong)Environment.TickCount64; m_frozen = true; }

        /// <summary>A loaded script that has run its state_entry and waits for events.</summary>
        public UUID Loaded(string source = Idle)
        {
            UUID item = H.RezScript(source);
            Assert.True(PumpUntil(() => H.RunStateOf(item) == "Waiting"), "the script never loaded: " + H.RunStateOf(item));
            return item;
        }

        public bool PumpUntil(Func<bool> done, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow >= until) return false;
                if (!H.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
            }
            return true;
        }

        public PhloxExecutionScheduler Exe => (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
        public LSLSystemAPI Api(UUID item) => ((Dictionary<UUID, LSLSystemAPI>)Field(Exe, "m_Apis"))[item];
        public RuntimeState State(UUID item) => ((Interpreter)H.InterpreterFor(item)).ScriptState;

        /// <summary>
        /// Make one call on the script's API as its own thread would, and return the sleep it set in ms (0: none).
        /// The clock must be frozen.
        /// </summary>
        public int Sleep(UUID item, Action<LSLSystemAPI> call)
        {
            Assert.True(m_frozen, "freeze the clock first");
            RuntimeState st = State(item);
            st.RunState = RuntimeState.Status.Waiting;
            st.NextWakeup = 0;
            call(Api(item));
            return st.RunState == RuntimeState.Status.Sleeping ? (int)((long)st.NextWakeup - (long)Now) : 0;
        }

        public void Dispose()
        {
            try { H.Dispose(); } finally { Clock.SetSourceForTesting(null); }
        }
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    // ── 1. chat: 15 ms per call (Halcyon SimChat / llRegionSay / llOwnerSay) ───────────────────────

    public static IEnumerable<object[]> ChatCalls() => new[]
    {
        new object[] { "llSay" }, new object[] { "llShout" }, new object[] { "llWhisper" },
        new object[] { "llRegionSay" }, new object[] { "llRegionSayTo" }, new object[] { "llOwnerSay" },
    };

    private static void Chat(LSLSystemAPI api, string fn)
    {
        switch (fn)
        {
            case "llSay": api.llSay(5, "x"); break;
            case "llShout": api.llShout(5, "x"); break;
            case "llWhisper": api.llWhisper(5, "x"); break;
            case "llRegionSay": api.llRegionSay(5, "x"); break;
            case "llRegionSayTo": api.llRegionSayTo(UUID.Random().ToString(), 5, "x"); break;
            case "llOwnerSay": api.llOwnerSay("x"); break;
        }
    }

    [Theory]
    [MemberData(nameof(ChatCalls))]
    public void EachChatCallSleeps15Ms(string fn)
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(15, r.Sleep(item, api => Chat(api, fn)));
    }

    [Theory]
    [MemberData(nameof(ChatCalls))]
    public void WithChatThrottleOffAChatCallDoesNotSleep(string fn)
    {
        using var r = new Rig("ChatThrottle");
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(0, r.Sleep(item, api => Chat(api, fn)));
    }

    /// <summary>
    /// Halcyon returns before its ScriptSleep on these refusals: nothing is sent and nothing sleeps. (llRegionSay on
    /// channel 0 and llRegionSayTo on DEBUG_CHANNEL are refused with an LSLError, which pauses 15 ms as every Halcyon
    /// script error did: ErrorPauseTests.)
    /// </summary>
    [Fact]
    public void RefusedChatDoesNotSleep()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(0, r.Sleep(item, api => api.llRegionSayTo(UUID.Zero.ToString(), 5, "x")));
        Assert.Equal(0, r.Sleep(item, api => api.llRegionSayTo("not a key", 5, "x")));
    }

    /// <summary>
    /// What the script sees: the message goes out at once, and the next line of the script runs 15 ms later - not at
    /// 14 ms. The engine's clock is the test's; the script's own scheduler decides when it wakes.
    /// </summary>
    [Fact]
    public void AScriptRunsOnFifteenMillisecondsAfterItsLlSay()
    {
        using var r = new Rig();
        r.Freeze();
        UUID item = r.H.RezScript("default { state_entry() { llSay(5, \"one\"); llSetObjectDesc(\"after\"); } }");
        Assert.True(r.PumpUntil(() => r.H.Said.Contains("one")), "the llSay never arrived");
        Assert.Equal("Sleeping", r.H.RunStateOf(item));
        Assert.Equal(r.Now + 15, r.State(item).NextWakeup);

        r.Now += 14;
        for (int i = 0; i < 50; i++) r.H.PumpOnce();
        Assert.NotEqual("after", r.H.Prim.Description);

        r.Now += 1;
        Assert.True(r.PumpUntil(() => r.H.Prim.Description == "after"), "the script did not run on at 15 ms");
    }

    [Fact]
    public void WithChatThrottleOffAScriptRunsStraightOnAfterItsLlSay()
    {
        using var r = new Rig("ChatThrottle");
        r.Freeze();
        UUID item = r.H.RezScript("default { state_entry() { llSay(5, \"one\"); llSetObjectDesc(\"after\"); } }");
        Assert.True(r.PumpUntil(() => r.H.Prim.Description == "after"), "the script stopped after llSay with the throttle off");
        Assert.Contains("one", r.H.Said);
    }

    // ── 2. bot calls: 15 ms (Halcyon botWhisper ... botTouchObject) ────────────────────────────────

    public static IEnumerable<object[]> BotCalls() => new[]
    {
        new object[] { "botWhisper" }, new object[] { "botSay" }, new object[] { "botShout" },
        new object[] { "botStartTyping" }, new object[] { "botStopTyping" }, new object[] { "botSitObject" },
        new object[] { "botStandUp" }, new object[] { "botTouchObject" },
    };

    private static void Bot(LSLSystemAPI api, string fn, string bot, string obj)
    {
        switch (fn)
        {
            case "botWhisper": api.botWhisper(bot, 5, "x"); break;
            case "botSay": api.botSay(bot, 5, "x"); break;
            case "botShout": api.botShout(bot, 5, "x"); break;
            case "botStartTyping": api.botStartTyping(bot); break;
            case "botStopTyping": api.botStopTyping(bot); break;
            case "botSitObject": api.botSitObject(bot, obj); break;
            case "botStandUp": api.botStandUp(bot); break;
            case "botTouchObject": api.botTouchObject(bot, obj); break;
        }
    }

    /// <summary>Halcyon sleeps after the call whether or not the region has a bot module (none here).</summary>
    [Theory]
    [MemberData(nameof(BotCalls))]
    public void EachBotCallSleeps15Ms(string fn)
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(15, r.Sleep(item, api => Bot(api, fn, UUID.Random().ToString(), UUID.Random().ToString())));
    }

    [Theory]
    [MemberData(nameof(BotCalls))]
    public void ABotCallWithABadKeyDoesNotSleep(string fn)
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(0, r.Sleep(item, api => Bot(api, fn, UUID.Zero.ToString(), UUID.Random().ToString())));
        Assert.Equal(0, r.Sleep(item, api => Bot(api, fn, "not a key", UUID.Random().ToString())));
    }

    [Theory]
    [MemberData(nameof(BotCalls))]
    public void WithBotThrottleOffABotCallDoesNotSleep(string fn)
    {
        using var r = new Rig("BotThrottle");
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(0, r.Sleep(item, api => Bot(api, fn, UUID.Random().ToString(), UUID.Random().ToString())));
    }

    // ── 3. PhySleep: sleep the 10-frame average physics frame time when it is over 30 ms ──────────

    /// <summary>The region's own frames: the scene's physics times for a frame, then its OnFrame.</summary>
    private static void Frame(Rig r, int updateMs, int prepareMs)
    {
        var scene = r.H.Scene;
        var t = typeof(OpenSim.Region.Framework.Scenes.Scene);
        t.GetField("physicsMS", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, (float)updateMs);
        t.GetField("physicsMS2", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, (float)prepareMs);
        scene.EventManager.TriggerOnFrame();
    }

    private static void Frames(Rig r, int n, int updateMs, int prepareMs = 0)
    {
        for (int i = 0; i < n; i++) Frame(r, updateMs, prepareMs);
    }

    public static IEnumerable<object[]> PhysicsCalls() => new[]
    {
        new object[] { "llSetScale" }, new object[] { "PRIM_SIZE" }, new object[] { "llSetStatus PHYSICS" },
        new object[] { "llSetStatus PHANTOM" }, new object[] { "llSetForce" }, new object[] { "llSetTorque" },
        new object[] { "llSetForceAndTorque" }, new object[] { "llApplyImpulse" }, new object[] { "llApplyRotationalImpulse" },
        new object[] { "llSetVehicleType" }, new object[] { "llSetVehicleFloatParam" }, new object[] { "llSetVehicleVectorParam" },
    };

    private static void Physics(LSLSystemAPI api, string fn)
    {
        var v = new Vector3(0.5f, 0.5f, 0.5f);
        switch (fn)
        {
            case "llSetScale": api.llSetScale(v); break;
            // the Fast variant: llSetPrimitiveParams' own 200 ms is set after the PhySleep and replaces it, in Halcyon too
            case "PRIM_SIZE": api.llSetLinkPrimitiveParamsFast(SlConst.LINK_THIS, new LSLList(new List<object> { SlConst.PRIM_SIZE, v })); break;
            case "llSetStatus PHYSICS": api.llSetStatus(SlConst.STATUS_PHYSICS, 0); break;
            case "llSetStatus PHANTOM": api.llSetStatus(SlConst.STATUS_PHANTOM, 0); break;
            case "llSetForce": api.llSetForce(v, 0); break;
            case "llSetTorque": api.llSetTorque(v, 0); break;
            case "llSetForceAndTorque": api.llSetForceAndTorque(v, v, 0); break;
            case "llApplyImpulse": api.llApplyImpulse(v, 0); break;
            case "llApplyRotationalImpulse": api.llApplyRotationalImpulse(v, 0); break;
            case "llSetVehicleType": api.llSetVehicleType(0); break;                    // VEHICLE_TYPE_NONE
            case "llSetVehicleFloatParam": api.llSetVehicleFloatParam(27, 0.5f); break;  // VEHICLE_BUOYANCY
            case "llSetVehicleVectorParam": api.llSetVehicleVectorParam(16, v); break;   // VEHICLE_LINEAR_FRICTION_TIMESCALE
        }
    }

    /// <summary>
    /// Halcyon: MAX_PHYSICS_TIME_BEFORE_DILATION = 30, "if (cmdTime > 30) ScriptSleep(cmdTime)". 31 trips and sleeps 31;
    /// 30 does not. The frame time is the scene's own (UpdatePhysics + UpdatePreparePhysics, as the sim stats add them),
    /// averaged over the last 10 frames with integer division as Halcyon's MovingIntegerAverage.
    /// </summary>
    [Theory]
    [MemberData(nameof(PhysicsCalls))]
    public void APhysicsCallSleepsTheFrameTimeOnlyOver30Ms(string fn)
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();

        Frames(r, 10, 20, 10);                                  // 30 ms frames: not over
        Assert.Equal(0, r.Sleep(item, api => Physics(api, fn)));

        Frames(r, 10, 21, 10);                                  // 31 ms frames: over
        Assert.Equal(31, r.Sleep(item, api => Physics(api, fn)));
    }

    [Fact]
    public void ThePhysicsAverageIsTheLastTenFramesWithIntegerDivision()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();

        Frames(r, 10, 100);
        Assert.Equal(100, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));
        Frames(r, 9, 0);                                        // one 100 ms frame left in the window: 100/10 = 10
        Assert.Equal(0, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));
        Frames(r, 1, 0);                                        // ten zeros
        Frames(r, 5, 62);                                       // 0 x5, 62 x5: 310 / 10 = 31
        Assert.Equal(31, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));
        Frames(r, 5, 0);                                        // the five zeros leave: 62 x5, 0 x5, still 31
        Assert.Equal(31, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));
        Frames(r, 1, 0);                                        // the first 62 leaves: 248 / 10 = 24
        Assert.Equal(0, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));
    }

    [Fact]
    public void BeforeTenFramesTheAverageIsOverTheFramesSeen()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        Assert.Equal(0, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));   // no frame yet: 0
        Frames(r, 2, 40);
        Assert.Equal(40, r.Sleep(item, api => api.llSetForce(Vector3.Zero, 0)));
    }

    /// <summary>Halcyon's SetPrimParams sleeps only on PRIM_SIZE; PRIM_PHYSICS and PRIM_PHANTOM there do not.</summary>
    [Fact]
    public void PrimPhysicsAndPrimPhantomDoNotPhySleep()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        Frames(r, 10, 50);
        int slept = r.Sleep(item, api => api.llSetLinkPrimitiveParamsFast(SlConst.LINK_THIS, new LSLList(new List<object>
            { SlConst.PRIM_PHYSICS, 0, SlConst.PRIM_PHANTOM, 0 })));
        Assert.Equal(0, slept);
    }

    [Theory]
    [MemberData(nameof(PhysicsCalls))]
    public void WithPhysicsThrottleOffAPhysicsCallDoesNotSleep(string fn)
    {
        using var r = new Rig("PhysicsThrottle");
        UUID item = r.Loaded();
        r.Freeze();
        Frames(r, 10, 80);
        Assert.Equal(0, r.Sleep(item, api => Physics(api, fn)));
    }

    // ── 4. link-message back-pressure: 50 ms when a receiver's queue is 80% full ──────────────────

    private static void FillQueue(Rig r, UUID item, int n)
    {
        for (int i = 0; i < n; i++) r.H.QueueEventOnScriptState(item);
        Assert.Equal(n, r.State(item).EventQueue.Count);
    }

    /// <summary>
    /// Halcyon: "if (GetEventQueueFreeSpacePercentage(item) <= 0.2f) DELAY = 50", with free space
    /// 1 - queued / 64. 51 queued is 0.203 (no delay); 52 is 0.1875 (50 ms).
    /// </summary>
    [Fact]
    public void LlMessageLinkedSleeps50MsWhenAReceiverHas52Of64Queued()
    {
        using var r = new Rig();
        UUID sender = r.Loaded();
        UUID receiver = r.Loaded();
        r.Freeze();

        FillQueue(r, receiver, 51);
        Assert.Equal(0, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_THIS, 1, "m", "")));

        r.H.QueueEventOnScriptState(receiver);                // 52
        Assert.Equal(50, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_THIS, 1, "m", "")));
    }

    [Fact]
    public void AFullQueueIsStill50MsNotMore()
    {
        using var r = new Rig();
        UUID sender = r.Loaded();
        UUID a = r.Loaded();
        UUID b = r.Loaded();
        r.Freeze();
        FillQueue(r, a, 64);
        FillQueue(r, b, 60);
        Assert.Equal(50, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_SET, 1, "m", "")));
    }

    /// <summary>The message is still sent: the sender is slowed, nothing is refused.</summary>
    [Fact]
    public void TheSlowedMessageIsStillDelivered()
    {
        using var r = new Rig();
        UUID receiver = r.Loaded("default { link_message(integer s, integer n, string m, key k) { if (m == \"late\") llSetObjectDesc(\"got it\"); } }");
        UUID sender = r.Loaded();
        r.Freeze();
        FillQueue(r, receiver, 52);
        Assert.Equal(50, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_THIS, 1, "late", "")));
        r.State(sender).RunState = RuntimeState.Status.Waiting;
        r.Now += 1000;
        Assert.True(r.PumpUntil(() => r.H.Prim.Description == "got it"), "the link message was lost");
    }

    [Fact]
    public void WithLinkMessageThrottleOffAFullReceiverDoesNotSlowTheSender()
    {
        using var r = new Rig("LinkMessageThrottle");
        UUID sender = r.Loaded();
        UUID receiver = r.Loaded();
        r.Freeze();
        FillQueue(r, receiver, 64);
        Assert.Equal(0, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_THIS, 1, "m", "")));
    }

    /// <summary>A receiver in another prim of the linkset counts, as Halcyon's GetLinkPrimsOnly loop does.</summary>
    [Fact]
    public void AReceiverInAnotherLinkCounts()
    {
        using var r = new Rig();
        var owner = UUID.Random();
        var root = SceneHelpers.CreateSceneObjectPart("root", UUID.Random(), owner);
        var sog = new OpenSim.Region.Framework.Scenes.SceneObjectGroup(root);
        var child = SceneHelpers.CreateSceneObjectPart("child", UUID.Random(), owner);
        sog.AddPart(child);
        r.H.Scene.AddNewSceneObject(sog, false);
        UUID sender = r.H.RezScriptInto(root, Idle);
        UUID receiver = r.H.RezScriptInto(child, Idle);
        Assert.True(r.PumpUntil(() => r.H.RunStateOf(sender) == "Waiting" && r.H.RunStateOf(receiver) == "Waiting"));
        r.Freeze();
        FillQueue(r, receiver, 52);
        Assert.Equal(0, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_THIS, 1, "m", "")));
        Assert.Equal(50, r.Sleep(sender, api => api.llMessageLinked(SlConst.LINK_ALL_OTHERS, 1, "m", "")));
    }

    // ── 5. notecards: delays and the cache ─────────────────────────────────────────────────────────

    private const string Card = "line zero\nline one\nline two";

    private static TaskInventoryItem AddCard(Rig r, string name = "card", string text = Card)
        => TaskInventoryHelpers.AddNotecard(r.H.Scene.AssetService, r.H.Prim, name, UUID.Random(), UUID.Random(), text);

    /// <summary>The region's notecard cache, by reflection so this file builds against an engine without one.</summary>
    private static CacheView CacheOf(Rig r)
    {
        var p = typeof(PhloxEngine).GetProperty("NotecardCache", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p != null, "PhloxEngine has no notecard cache");
        return new CacheView(p!.GetValue(r.H.Engine)!);
    }

    private sealed class CacheView
    {
        private readonly object m_cache;
        public CacheView(object cache) => m_cache = cache;
        public bool IsCached(UUID asset) => (bool)m_cache.GetType().GetMethod("IsCached")!.Invoke(m_cache, new object[] { asset })!;
    }

    /// <summary>Wait for the uncached read to fill the cache (the fetch runs on the thread pool, as before).</summary>
    private static void UntilCached(Rig r, UUID asset)
        => Assert.True(r.PumpUntil(() => CacheOf(r).IsCached(asset)), "the notecard never reached the cache");

    [Fact]
    public void LineCountsSleep50Uncached25CachedAnd100WhenTheCardIsMissing()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        var card = AddCard(r);
        r.Freeze();

        Assert.Equal(50, r.Sleep(item, api => api.llGetNumberOfNotecardLines("card")));
        UntilCached(r, card.AssetID);
        Assert.Equal(25, r.Sleep(item, api => api.llGetNumberOfNotecardLines("card")));
        Assert.Equal(25, r.Sleep(item, api => api.iwGetLinkNumberOfNotecardLines(SlConst.LINK_THIS, "card")));
        Assert.Equal(100, r.Sleep(item, api => api.llGetNumberOfNotecardLines("no such card")));
    }

    /// <summary>
    /// Halcyon GetNotecardSegment: 25 ms uncached; cached, 1 ms only on lines 0, 16, 32 ... read from offset 0; a
    /// missing card has no read sleep (it has Halcyon's error, whose 15 ms is ChatThrottle's).
    /// </summary>
    [Fact]
    public void LineReadsSleep25UncachedAnd1MsOnEverySixteenthCachedLine()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        var card = AddCard(r, text: string.Join("\n", Enumerable.Range(0, 40).Select(i => "l" + i)));
        r.Freeze();

        Assert.Equal(25, r.Sleep(item, api => api.llGetNotecardLine("card", 3)));
        UntilCached(r, card.AssetID);
        Assert.Equal(1, r.Sleep(item, api => api.llGetNotecardLine("card", 0)));
        Assert.Equal(0, r.Sleep(item, api => api.llGetNotecardLine("card", 1)));
        Assert.Equal(0, r.Sleep(item, api => api.llGetNotecardLine("card", 15)));
        Assert.Equal(1, r.Sleep(item, api => api.llGetNotecardLine("card", 16)));
        Assert.Equal(1, r.Sleep(item, api => api.llGetNotecardLine("card", 32)));
        Assert.Equal(1, r.Sleep(item, api => api.iwGetNotecardSegment("card", 16, 0, 5)));
        Assert.Equal(0, r.Sleep(item, api => api.iwGetNotecardSegment("card", 16, 1, 5)));
        Assert.Equal(1, r.Sleep(item, api => api.iwGetLinkNotecardLine(SlConst.LINK_THIS, "card", 16)));
        Assert.Equal(0, r.Sleep(item, api => api.iwGetLinkNotecardSegment(SlConst.LINK_THIS, "card", 17, 0, 5)));
        Assert.Equal(15, r.Sleep(item, api => api.llGetNotecardLine("no such card", 0)));   // The error's pause
    }

    [Fact]
    public void WithNotecardThrottleOffNoReadSleeps()
    {
        using var r = new Rig("NotecardThrottle");
        UUID item = r.Loaded();
        var card = AddCard(r);
        r.Freeze();
        Assert.Equal(0, r.Sleep(item, api => api.llGetNumberOfNotecardLines("card")));
        Assert.Equal(0, r.Sleep(item, api => api.llGetNotecardLine("card", 0)));
        UntilCached(r, card.AssetID);
        Assert.Equal(0, r.Sleep(item, api => api.llGetNumberOfNotecardLines("card")));
        Assert.Equal(0, r.Sleep(item, api => api.llGetNotecardLine("card", 0)));
        // The missing card's error still pauses 15 ms - ChatThrottle's pause, not a notecard read delay.
        Assert.Equal(15, r.Sleep(item, api => api.llGetNumberOfNotecardLines("no such card")));
    }

    /// <summary>
    /// A cached read answers with the same text as a fetched one, for every call, and without a fetch: the asset is
    /// taken out of the asset service after the first read and the cached answers still come.
    /// </summary>
    /// <summary>One read of each kind, each asked after the last answered, reported on channel 7 as one line.</summary>
    // Every reader in the prim gets every answer (SL), so each takes only its own (the wiki's advice:
    // "always use the queryid key"); before, a finished first reader went on asking on the second reader's answers.
    private const string Reader = @"
        list got;
        integer step = 0;
        key q;
        ask() {
            if (step == 0) q = llGetNumberOfNotecardLines(""card"");
            else if (step <= 5) q = llGetNotecardLine(""card"", step - 1);
            else q = iwGetNotecardSegment(""card"", 1, 5, 3);
        }
        default {
            state_entry() { ask(); }
            dataserver(key id, string d) {
                if (id != q) return;
                got += [d];
                ++step;
                if (step == 7) llRegionSay(7, llDumpList2String(got, ""|""));
                else ask();
            }
        }";

    /// <summary>Run <see cref="Reader"/> in a new script, moving the frozen clock past each read's sleep, and return its line.</summary>
    private static string RunReader(Rig r)
    {
        int before = r.H.SaidOn.Count(s => s.Channel == 7);
        r.H.RezScript(Reader);
        Assert.True(r.PumpUntil(() => { r.Now += 5; return r.H.SaidOn.Count(s => s.Channel == 7) > before; }),
            "the reads never all answered");
        return r.H.SaidOn.Where(s => s.Channel == 7).Last().Message;
    }

    /// <summary>
    /// A cached read answers with the same text as a fetched one, for every kind of read, and without a fetch: the
    /// asset is removed from the asset service after the first reader and the second reader still gets it all.
    /// </summary>
    [Fact]
    public void CachedReadsAnswerTheSameTextWithoutAFetch()
    {
        using var r = new Rig();
        var card = AddCard(r, text: "alpha\r\nbeta gamma\n\ndelta");
        r.Freeze();

        string fetched = RunReader(r);
        Assert.True(CacheOf(r).IsCached(card.AssetID));
        // the asset service's copy is overwritten with other text: a read that fetched would now see it
        var changed = r.H.Scene.AssetService.Get(card.AssetID.ToString());
        changed.Data = System.Text.Encoding.UTF8.GetBytes(
            "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 7\nchanged}\n");
        r.H.Scene.AssetService.Store(changed);
        Assert.Contains("changed", System.Text.Encoding.UTF8.GetString(r.H.Scene.AssetService.Get(card.AssetID.ToString()).Data));
        string cached = RunReader(r);

        _out.WriteLine("fetched: " + fetched.Replace("\n", "\\n"));
        _out.WriteLine("cached:  " + cached.Replace("\n", "\\n"));
        Assert.Equal(fetched, cached);
        Assert.Equal("4|alpha|beta gamma||delta|\n\n\n|gam", fetched);
    }

    [Fact]
    public void ACardIsDroppedSixtySecondsAfterItsLastUseAtTheNextUncachedRead()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        var card = AddCard(r);
        var other = AddCard(r, "other", "x");
        r.Freeze();
        r.Sleep(item, api => api.llGetNotecardLine("card", 0));
        UntilCached(r, card.AssetID);

        r.Now += 30_000;
        r.Sleep(item, api => api.llGetNotecardLine("card", 1));          // a use: the 60 s start again
        r.Now += 60_000;
        r.Sleep(item, api => api.llGetNotecardLine("other", 0));         // an uncached read purges: 60 s is not over 60 s
        Assert.True(CacheOf(r).IsCached(card.AssetID), "dropped at exactly 60 s");
        // That read fetches "other" on the thread pool; until it is cached the next read is uncached too and
        // purges "card" at 61 s. The clock is frozen, so waiting does not move the 60 s.
        UntilCached(r, other.AssetID);

        r.Now += 1;
        r.Sleep(item, api => api.llGetNotecardLine("other", 0));         // cached now: no purge on a cached read
        Assert.True(CacheOf(r).IsCached(card.AssetID), "a cached read purged");
        var third = AddCard(r, "third", "y");
        r.Sleep(item, api => api.llGetNotecardLine("third", 0));         // uncached: purge
        Assert.False(CacheOf(r).IsCached(card.AssetID), "kept past 60 s of no use");
    }

    [Fact]
    public void WithNotecardCacheOffEveryReadFetches()
    {
        using var r = new Rig("NotecardCache");
        UUID item = r.Loaded();
        var card = AddCard(r);
        r.Freeze();
        Assert.Equal(25, r.Sleep(item, api => api.llGetNotecardLine("card", 0)));
        Assert.True(r.PumpUntil(() => r.H.Engine.ObjectPostsInFlight == 0));
        for (int i = 0; i < 20; i++) r.H.PumpOnce();
        Assert.False(CacheOf(r).IsCached(card.AssetID));
        Assert.Equal(25, r.Sleep(item, api => api.llGetNotecardLine("card", 0)));   // still the uncached delay
        Assert.Equal(50, r.Sleep(item, api => api.llGetNumberOfNotecardLines("card")));
    }

    // ── 6. iwFormatString: 100 ms when the tick count moved during a substitution step ────────────

    [Fact]
    public void IwFormatStringDoesNotSleepWhileTheClockStandsStill()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        string result = null;
        Assert.Equal(0, r.Sleep(item, api => result = api.iwFormatString("{0}-{1}", new LSLList(new List<object> { "a", "b" }))));
        Assert.Equal("a-b", result);
    }

    /// <summary>
    /// Halcyon: "if (time2 - time1 > 0) { ScriptSleep(100); time1 = time2; }" after each step. A clock that moves 1 ms
    /// between reads trips it on every step; the sleep is set, not added, so it is 100 ms from the last trip.
    /// </summary>
    [Fact]
    public void IwFormatStringSleeps100MsWhenTheClockMovedDuringAStep()
    {
        using var r = new Rig();
        UUID item = r.Loaded();
        r.Freeze();
        RuntimeState st = r.State(item);
        st.RunState = RuntimeState.Status.Waiting;
        ulong last = 0;
        Clock.SetSourceForTesting(() => last = ++r.Now);
        string result = r.Api(item).iwFormatString("{0}{1}{2}", new LSLList(new List<object> { "a", "b", "c" }));
        Clock.SetSourceForTesting(() => r.Now);
        Assert.Equal("abc", result);
        Assert.Equal(RuntimeState.Status.Sleeping, st.RunState);
        Assert.Equal(last + 100, st.NextWakeup);
    }

    [Fact]
    public void WithFormatStringThrottleOffIwFormatStringNeverSleeps()
    {
        using var r = new Rig("FormatStringThrottle");
        UUID item = r.Loaded();
        r.Freeze();
        RuntimeState st = r.State(item);
        st.RunState = RuntimeState.Status.Waiting;
        Clock.SetSourceForTesting(() => ++r.Now);
        string result = r.Api(item).iwFormatString("{0}{1}{2}", new LSLList(new List<object> { "a", "b", "c" }));
        Clock.SetSourceForTesting(() => r.Now);
        Assert.Equal("abc", result);
        Assert.Equal(RuntimeState.Status.Waiting, st.RunState);
    }

    // ── settings and the startup line ──────────────────────────────────────────────────────────────

    private static readonly string[] Keys =
        { "ResetThrottle", "ChatThrottle", "BotThrottle", "PhysicsThrottle", "LinkMessageThrottle", "NotecardThrottle", "NotecardCache", "FormatStringThrottle",
          "HttpInFlightThrottle" };

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        public readonly List<string> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.Lines) m_c.Lines.Add(formatter(state, exception)); }
        }
    }

    private static List<string> StartupLines(Action<IConfig> set)
    {
        var capture = new Capture();
        ILoggerFactory before = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try
        {
            var config = new IniConfigSource();
            var phlox = config.AddConfig("InWorldz.Phlox");
            phlox.Set("Enabled", "true");
            set?.Invoke(phlox);
            new PhloxEngine().Initialise(config);
        }
        finally { LoggerProvider.LoggerFactory = before; }
        lock (capture.Lines) return capture.Lines.Where(l => l.Contains("Anti-abuse slowdowns")).ToList();
    }

    [Fact]
    public void OneStartupLineListsEverySettingOnByDefault()
    {
        var lines = StartupLines(null);
        Assert.Single(lines);
        _out.WriteLine(lines[0]);
        Assert.Equal("[PhloxEngine]: Anti-abuse slowdowns: " + string.Join(", ", Keys.Select(k => k + " = True")), lines[0]);
    }

    /// <summary>The line's exact text, nine settings, HttpInFlightThrottle last (log checks may read it).</summary>
    [Fact]
    public void TheStartupLineListsNineSettingsWithTheHttpSwitchLast()
    {
        var lines = StartupLines(null);
        Assert.Single(lines);
        Assert.Equal("[PhloxEngine]: Anti-abuse slowdowns: ResetThrottle = True, ChatThrottle = True, BotThrottle = True, " +
            "PhysicsThrottle = True, LinkMessageThrottle = True, NotecardThrottle = True, NotecardCache = True, " +
            "FormatStringThrottle = True, HttpInFlightThrottle = True", lines[0]);
        Assert.Equal(9, lines[0].Split(" = ").Length - 1);
        Assert.EndsWith("HttpInFlightThrottle = False", StartupLines(c => c.Set("HttpInFlightThrottle", "false")).Single());
    }

    [Fact]
    public void TheStartupLineShowsEachSettingThatIsOff()
    {
        foreach (string off in Keys)
        {
            var lines = StartupLines(c => c.Set(off, "false"));
            Assert.Single(lines);
            Assert.Equal("[PhloxEngine]: Anti-abuse slowdowns: " + string.Join(", ", Keys.Select(k => k + " = " + (k == off ? "False" : "True"))), lines[0]);
        }
    }

    /// <summary>The engine property behind each key (the cache's property is NotecardCacheEnabled; NotecardCache is the cache).</summary>
    private static bool Setting(PhloxEngine e, string key)
    {
        string prop = key == "NotecardCache" ? "NotecardCacheEnabled" : key;
        var p = typeof(PhloxEngine).GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(p != null, "PhloxEngine has no " + prop);
        return (bool)p!.GetValue(e)!;
    }

    [Fact]
    public void EachSettingIsReadFromItsOwnKey()
    {
        foreach (string key in Keys)
        {
            using var r = new Rig(key);
            Assert.False(Setting(r.H.Engine, key), key + " stayed on");
            foreach (string other in Keys.Where(k => k != key))
                Assert.True(Setting(r.H.Engine, other), other + " went off with " + key);
        }
    }

    // ── 7. iwAvatarName2Key: Halcyon's 100 ms / 1000 ms, already live; pinned here ───────────────────

    private static ulong Name2KeyWake(Rig r, string first, string last)
    {
        UUID item = r.Loaded("default { state_entry() { } touch_start(integer n) { key k = iwAvatarName2Key(\"" + first + "\", \"" + last + "\"); llSetObjectDesc((string)k); } }");
        r.H.PostTouch(item);
        Assert.True(r.PumpUntil(() => r.H.RunStateOf(item) == "Sleeping"), "no SysReturn delay: " + r.H.RunStateOf(item));
        return r.State(item).NextWakeup - r.Now;
    }

    [Fact]
    public void IwAvatarName2KeyKeepsHalcyons1000MsForANameNotInTheRegion()
    {
        using var r = new Rig();
        r.Freeze();
        Assert.Equal(1000UL, Name2KeyWake(r, "Nobody", "Here"));
    }

    [Fact]
    public void IwAvatarName2KeyKeepsHalcyons100MsForAnAvatarInTheRegion()
    {
        using var r = new Rig();
        var sp = r.H.Scene.GetScenePresence(r.H.AddClient().AgentId);
        r.Freeze();
        Assert.Equal(100UL, Name2KeyWake(r, sp.Firstname, sp.Lastname));
    }
}
