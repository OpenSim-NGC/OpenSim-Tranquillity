using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Halcyon paused a script 15 ms on every script error it reported with ScriptShoutError (LSLError,
/// NotImplemented and Deprecated go through it): the error is chat, and Halcyon's SimChat sleeps 15 ms
/// (LSLSystemAPI.cs:14483-14486, :1042-1046). Phlox pauses at the same errors, under the ChatThrottle setting. Errors
/// Halcyon reported with its plain ShoutError, or inside a long-running call (where its ScriptSleep did nothing,
/// :145-156), do not pause.
///
/// <para>Two measurements. "Accounted" runs the call inside a SyscallContext, where every ScriptSleep of the call adds
/// to one total, so the 15 ms is seen even when a later sleep of the same call (llCreateLink's 1000 ms, say) sets the
/// wake-up. "Effective" is the wake-up the call leaves on the script's own thread, read on a frozen clock, as
/// AntiAbuseSlowdownTests reads it: Halcyon's sleeps replace one another, so a later, longer sleep still wins.</para>
///
/// <para>Collection "phlox-state": these tests freeze the process-wide engine Clock.</para>
/// </summary>
[Collection("phlox-state")]
public class ErrorPauseTests
{
    private readonly ITestOutputHelper _out;
    public ErrorPauseTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string Idle = "default { state_entry() { } }";
    private const string NoSuchPrim = "5eed0000-0000-4000-8000-000000000058";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly UUID Item;
        private bool m_frozen;
        private ulong m_now;

        public Rig(bool chatThrottle)
        {
            Clock.SetSourceForTesting(() => m_frozen ? m_now : (ulong)Environment.TickCount64);
            H = new SchedulerHarness(cfg => { if (!chatThrottle) cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", "false"); });
            Item = H.RezScript(Idle);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (H.RunStateOf(Item) != "Waiting")
            {
                Assert.True(DateTime.UtcNow < until, "the script never loaded: " + H.RunStateOf(Item));
                H.PumpOnce();
                System.Threading.Thread.Sleep(1);
            }
        }

        public LSLSystemAPI Api
        {
            get
            {
                var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
                return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[Item];
            }
        }

        public TaskInventoryItem Self { get { lock (H.Prim.TaskInventory) return H.Prim.TaskInventory[Item]; } }

        /// <summary>Errors on DEBUG_CHANNEL since the last call started.</summary>
        public List<string> Errors => H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

        /// <summary>Every ScriptSleep of the call, added up.</summary>
        public (int Ms, object Ret) Accounted(Func<LSLSystemAPI, object> call)
        {
            H.ClearSaid(Item);
            var ctx = new SyscallContext(Item, SyscallContext.NextSeq());
            ctx.Enter();
            object ret;
            try { ret = call(Api); }
            finally { SyscallContext.Exit(); }
            return (ctx.DelayMs, ret);
        }

        /// <summary>The wake-up the call sets on the script's own thread, in ms from now (0: none).</summary>
        public int Effective(Func<LSLSystemAPI, object> call)
        {
            m_now = (ulong)Environment.TickCount64;
            m_frozen = true;
            RuntimeState st = ((Interpreter)H.InterpreterFor(Item)).ScriptState;
            st.RunState = RuntimeState.Status.Waiting;
            st.NextWakeup = 0;
            H.ClearSaid(Item);
            call(Api);
            return st.RunState == RuntimeState.Status.Sleeping ? (int)((long)st.NextWakeup - (long)m_now) : 0;
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

    private static LSLList L(params object[] items) => new LSLList(new List<object>(items));

    private sealed class NoHttp : IHttpRequestModule
    {
        public UUID MakeHttpRequest(string url, string parameters, string body) => UUID.Zero;
        public UUID StartHttpRequest(uint localID, UUID itemID, string url, List<string> parameters, Dictionary<string, string> headers, string body)
            => throw new InvalidOperationException("no request may start in this test");
        public void StopHttpRequest(uint localID, UUID itemID) { }
        public IHttpServiceRequest GetNextCompletedRequest() => null;
        public void RemoveCompletedRequest(UUID id) { }
        public bool CheckThrottle(uint localID, UUID onerID) => true;
        public bool CheckAllowed(Uri url) => true;
    }

    /// <summary>
    /// One error path. <c>Later</c> is the sleep the call sets after its error either way (the pause adds to it in the
    /// accounting; the effective wake-up is <c>Later</c> when it is set, else the 15 ms).
    /// </summary>
    private sealed record Case(string Name, Func<LSLSystemAPI, object> Call, int Later = 0, Action<Rig> Setup = null);

    private static object Reset(LSLSystemAPI api)
    {
        // Halcyon ThrottleScriptResets: the 6th reset in one second warns and sleeps 5 s. Put the script at its 5th reset
        // of this second and make the 6th; retry if the second turned over in between (then nothing was counted).
        var t = typeof(LSLSystemAPI);
        var count = t.GetProperty("ResetSleepCount", BindingFlags.NonPublic | BindingFlags.Instance);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int before = (int)count.GetValue(api);
            t.GetField("m_resetSecond", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(api, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            t.GetField("m_resetCount", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(api, 5);
            t.GetMethod("ThrottleScriptResets", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(api, null);
            if ((int)count.GetValue(api) > before) return null;
        }
        throw new InvalidOperationException("the reset throttle never fired");
    }

    private static void OwnLand(Rig r)
    {
        var land = new StripLand(r.H.Scene, (256, r.H.Prim.OwnerID));
        r.H.Scene.LandChannel = land;
    }

    private static readonly Dictionary<string, Case> Paused = new Case[]
    {
        new("ThrottleScriptResets", Reset, Later: 5000),
        new("llRegionSay channel 0", a => { a.llRegionSay(0, "x"); return null; }),
        new("llRegionSayTo DEBUG_CHANNEL", a => { a.llRegionSayTo(UUID.Random().ToString(), DEBUG_CHANNEL, "x"); return null; }),
        new("llGetCameraPos", a => a.llGetCameraPos(), Setup: r => { r.Self.PermsGranter = UUID.Random(); r.Self.PermsMask = 0; }),
        new("llGetCameraRot", a => a.llGetCameraRot(), Setup: r => { r.Self.PermsGranter = UUID.Random(); r.Self.PermsMask = 0; }),
        new("llResetOtherScript", a => { a.llResetOtherScript("nope"); return null; }),
        new("llGetScriptState", a => a.llGetScriptState("nope")),
        new("llSetScriptState", a => { a.llSetScriptState("nope", 1); return null; }),
        new("llRemoteLoadScriptPin PIN 0", a => { a.llRemoteLoadScriptPin(UUID.Random().ToString(), "x", 0, 1, 0); return null; }, Later: 3000),
        new("llRemoteLoadScriptPin target not found", a => { a.llRemoteLoadScriptPin(NoSuchPrim, "x", 42, 1, 0); return null; }, Later: 3000),
        new("llRemoteLoadScriptPin source prim", a => { a.llRemoteLoadScriptPin(a.llGetKey(), "x", 42, 1, 0); return null; }, Later: 3000),
        new("iwGetNotecardSegment", a => a.iwGetNotecardSegment("nope", 0, 0, 10)),
        new("iwSearchInventory IW_MATCH_COUNT", a => a.iwSearchInventory(-1, "x", 3)),
        new("iwSearchLinkInventory IW_MATCH_COUNT_REGEX", a => a.iwSearchLinkInventory(SlConst.LINK_THIS, -1, "x", 4)),
        new("iwGetLinkNumberOfNotecardLines one prim", a => a.iwGetLinkNumberOfNotecardLines(SlConst.LINK_THIS, "nope"), Later: 100),
        new("iwGetLinkNumberOfNotecardLines no prim", a => a.iwGetLinkNumberOfNotecardLines(42, "nope")),
        new("iwGetLinkNotecardLine", a => a.iwGetLinkNotecardLine(SlConst.LINK_THIS, "nope", 0)),
        new("llCreateLink", a => { a.llCreateLink(UUID.Random().ToString(), 1); return null; }, Later: 1000),
        new("llBreakLink", a => { a.llBreakLink(2); return null; }, Later: 1000),
        new("IW_PRIM_PROJECTOR NULL_KEY", a => { a.llSetLinkPrimitiveParamsFast(SlConst.LINK_THIS, L(SlConst.IW_PRIM_PROJECTOR, 1, UUID.Zero.ToString(), 1.0f, 1.0f, 1.0f)); return null; }),
        new("IW_PRIM_PROJECTOR_TEXTURE NULL_KEY", a => { a.llSetLinkPrimitiveParamsFast(SlConst.LINK_THIS, L(SlConst.IW_PRIM_PROJECTOR_TEXTURE, UUID.Zero.ToString())); return null; }),
        new("llParcelMediaCommandList AGENT", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Agent, 5)); return null; }, Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList URL", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Url, 5)); return null; }, Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList TEXTURE", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Texture, 5)); return null; }, Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList TIME", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Time, "x")); return null; }, Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList AUTO_ALIGN", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.AutoAlign, "x")); return null; }, Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList TYPE", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Type, 5)); return null; }, Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList DESC", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Desc, 5)); return null; }, Later: 2000, Setup: OwnLand),
        new("llHTTPRequest bad flag", a => a.llHTTPRequest("http://example.invalid/", L("x", "y"), ""),
            Setup: r => r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(new NoHttp())),
        new("iwFormatString over 64 kB", a => a.iwFormatString(new string('a', 32768) + "{0}", L("b"))),
        new("iwMatchList IW_MATCH_REGEX", a => a.iwMatchList(L("a"), L("a"), 2)),
        new("iwMatchList IW_MATCH_COUNT", a => a.iwMatchList(L("a"), L("a"), 3)),
        new("iwMatchList IW_MATCH_COUNT_REGEX", a => a.iwMatchList(L("a"), L("a"), 4)),
        new("iwReverseList stride", a => a.iwReverseList(L(1, 2, 3), 2)),
        new("llGiveMoney no PERMISSION_DEBIT", a => a.llGiveMoney(UUID.Random().ToString(), 5)),   // no sleep since SL's forced delay 0.0
        new("llCastRay no hits asked", a => a.llCastRay(Vector3.Zero, Vector3.UnitX, L(SlConst.RC_MAX_HITS, 0))),
        new("iwGroupInvite not creator", a => a.iwGroupInvite(UUID.Random().ToString(), UUID.Random().ToString(), ""),
            Setup: r => r.Self.CreatorID = UUID.Random()),
        new("iwGroupEject not creator", a => a.iwGroupEject(UUID.Random().ToString(), UUID.Random().ToString()),
            Setup: r => r.Self.CreatorID = UUID.Random()),
        new("iwSearchLinksByName IW_MATCH_COUNT", a => a.iwSearchLinksByName("x", 3, 0)),
        new("iwSearchLinksByDesc IW_MATCH_COUNT_REGEX", a => a.iwSearchLinksByDesc("x", 4, 0)),
        new("botSetNavigationPoints repeated option", a => { a.botSetNavigationPoints(UUID.Random().ToString(), L(Vector3.One), L(0), L(1, 1, 1, 1)); return null; }),
        new("botWanderWithin repeated option", a => { a.botWanderWithin(UUID.Random().ToString(), Vector3.One, 5f, 5f, L(1, 1, 1, 1)); return null; }),
        new("iwStringCodec bad codec", a => a.iwStringCodec("Hello", "rot13", 1, new LSLList())),
    }.ToDictionary(c => c.Name);

    /// <summary>Errors Phlox reports where Halcyon did not pause; the error is still said.</summary>
    private static readonly Dictionary<string, Case> Unpaused = new Case[]
    {
        new("llRezObject too far (Halcyon: async)", a => { a.llRezObject("nope", new Vector3(9999, 9999, 9999), Vector3.Zero, Quaternion.Identity, 0); return null; }),
        new("iwRezAt too far", a => a.iwRezAt("nope", 0, new Vector3(9999, 9999, 9999), Vector3.Zero, Quaternion.Identity, 0)),
        new("llGiveInventory missing item (Halcyon: async)", a => { a.llGiveInventory(UUID.Random().ToString(), "nope"); return null; }),
        new("iwGetLinkNotecardLine no prim (Halcyon: silent)", a => a.iwGetLinkNotecardLine(42, "nope", 0)),
        new("llGetInventoryDesc (not in Halcyon)", a => a.llGetInventoryDesc("nope")),
        new("llComputeHash (not in Halcyon)", a => a.llComputeHash("x", "nope")),
        new("llHTTPRequest HTTP_MIMETYPE (YEngineError, not in Halcyon)",
            a => a.llHTTPRequest("http://example.invalid/", L(1, "not a type\r\nX: y"), ""),
            Setup: r => r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(new NoHttp())),
    }.ToDictionary(c => c.Name);

    public static IEnumerable<object[]> PausedNames() => Paused.Keys.Select(k => new object[] { k });
    public static IEnumerable<object[]> UnpausedNames() => Unpaused.Keys.Select(k => new object[] { k });

    private static (int Ms, object Ret, List<string> Errors) Run(Case c, bool chatThrottle)
    {
        using var r = new Rig(chatThrottle);
        c.Setup?.Invoke(r);
        var (ms, ret) = r.Accounted(c.Call);
        return (ms, ret, r.Errors);
    }

    private static string Show(object o) => o is LSLList l ? "[" + string.Join(",", l.Data) + "]" : o?.ToString();

    [Theory]
    [MemberData(nameof(PausedNames))]
    public void AnErrorHalcyonPausedOnPauses15Ms(string name)
    {
        Case c = Paused[name];
        var on = Run(c, chatThrottle: true);
        Assert.NotEmpty(on.Errors);
        Assert.Equal(c.Later + 15, on.Ms);
    }

    [Theory]
    [MemberData(nameof(PausedNames))]
    public void WithChatThrottleOffAnErrorDoesNotPause(string name)
    {
        Case c = Paused[name];
        var off = Run(c, chatThrottle: false);
        Assert.NotEmpty(off.Errors);
        Assert.Equal(c.Later, off.Ms);
    }

    /// <summary>The pause changes nothing the script sees: the same text on DEBUG_CHANNEL, once, and the same result.</summary>
    [Theory]
    [MemberData(nameof(PausedNames))]
    public void ThePauseChangesNoTextChannelOrResult(string name)
    {
        Case c = Paused[name];
        var on = Run(c, chatThrottle: true);
        var off = Run(c, chatThrottle: false);
        _out.WriteLine(string.Join(" | ", on.Errors));
        Assert.Equal(off.Errors, on.Errors);
        Assert.Equal(Show(off.Ret), Show(on.Ret));
    }

    /// <summary>
    /// On the script's own thread the pause is Halcyon's ScriptSleep at the error: the script wakes 15 ms later, or at a
    /// longer sleep the call sets after it (Halcyon's replace one another), never earlier than that later sleep.
    /// </summary>
    [Theory]
    [MemberData(nameof(PausedNames))]
    public void TheScriptWakesAfterThePauseOrTheLaterSleep(string name)
    {
        Case c = Paused[name];
        using var r = new Rig(chatThrottle: true);
        c.Setup?.Invoke(r);
        Assert.Equal(c.Later > 0 ? c.Later : 15, r.Effective(c.Call));
        Assert.NotEmpty(r.Errors);
    }

    [Theory]
    [MemberData(nameof(UnpausedNames))]
    public void AnErrorHalcyonDidNotPauseOnIsUnchanged(string name)
    {
        Case c = Unpaused[name];
        var on = Run(c, chatThrottle: true);
        var off = Run(c, chatThrottle: false);
        _out.WriteLine(on.Ms + " ms: " + string.Join(" | ", on.Errors));
        Assert.NotEmpty(on.Errors);
        Assert.Equal(off.Ms, on.Ms);
        Assert.Equal(off.Errors, on.Errors);
        Assert.Equal(Show(off.Ret), Show(on.Ret));
    }

    /// <summary>
    /// A missing notecard: Halcyon shouted and paused. Phlox was silent there; it now
    /// raises Halcyon's error, which pauses 15 ms like the others (the other new errors: HalcyonChecksTests).
    /// </summary>
    [Fact]
    public void AMissingNotecardForLlGetNotecardLineNowErrorsAndPauses()
    {
        var c = new Case("llGetNotecardLine", a => a.llGetNotecardLine("nope", 0));
        var on = Run(c, chatThrottle: true);
        var off = Run(c, chatThrottle: false);
        Assert.Equal(new[] { "Script error: Notecard 'nope' could not be found." }, on.Errors);
        Assert.Equal(on.Errors, off.Errors);
        Assert.Equal(15, on.Ms);
        Assert.Equal(0, off.Ms);
    }

    /// <summary>iwStringCodec already paused on its own errors; it pauses once, not twice.</summary>
    [Fact]
    public void IwStringCodecPausesOnceNotTwice()
    {
        using var r = new Rig(chatThrottle: true);
        var (ms, _) = r.Accounted(a => a.iwStringCodec("Hello", "rot13", 1, new LSLList()));
        Assert.Equal(15, ms);
        Assert.Equal(new[] { "Script error: LSL Runtime Error: Error: \"rot13\" is not a valid codec for iwStringCodec!" }, r.Errors);
    }

    /// <summary>Two errors in one call (Halcyon SetPrimParams goes on after one): two pauses in the accounting.</summary>
    [Fact]
    public void EachErrorInOneCallPauses()
    {
        using var r = new Rig(chatThrottle: true);
        var (ms, _) = r.Accounted(a =>
        {
            a.llSetLinkPrimitiveParamsFast(SlConst.LINK_THIS, L(
                SlConst.IW_PRIM_PROJECTOR_TEXTURE, UUID.Zero.ToString(),
                SlConst.IW_PRIM_PROJECTOR_TEXTURE, UUID.Zero.ToString()));
            return null;
        });
        Assert.Equal(2, r.Errors.Count);
        Assert.Equal(30, ms);
    }
}
