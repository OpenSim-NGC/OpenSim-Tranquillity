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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Halcyon's remaining checks and errors. Item 3: where Halcyon raised an error through
/// ScriptShoutError / LSLError / NotImplemented / Deprecated and Phlox was silent, Phlox now says Halcyon's text on
/// DEBUG_CHANNEL with the 15 ms pause (ChatThrottle); items 1, 2, 4 and 5: iwSetGround's estate limits, the NaN-rotation
/// rez guard, llClearCameraParams' permission, llGiveMoney's granter, the HTTP in-flight caps, a script's own inventory
/// key, and [YEngine] AutomaticLinkPermission.
///
/// <para>"Accounted" runs the call inside a SyscallContext, where every ScriptSleep of the call adds up, so the 15 ms is
/// seen even when a later sleep of the call sets the wake-up; "Effective" is the wake-up left on the script's thread on
/// a frozen clock (Halcyon's sleeps replace one another), as ErrorPauseTests reads them.</para>
///
/// <para>Collection "phlox-state": these tests freeze the process-wide engine Clock.</para>
/// </summary>
[Collection("phlox-state")]
public class HalcyonChecksTests
{
    private readonly ITestOutputHelper _out;
    public HalcyonChecksTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string Idle = "default { state_entry() { } }";
    private const string LslErr = "Script error: LSL Runtime Error: ";
    private const string NotImpl = "Script error: Command not implemented: ";
    private const string Depr = "Script error: Command deprecated: ";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly UUID Item;
        private bool m_frozen;
        private ulong m_now;

        public Rig(bool chatThrottle = true, Action<Nini.Config.IConfigSource> configure = null)
        {
            Clock.SetSourceForTesting(() => m_frozen ? m_now : (ulong)Environment.TickCount64);
            H = new SchedulerHarness(cfg =>
            {
                if (!chatThrottle) cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", "false");
                configure?.Invoke(cfg);
            });
            Item = H.RezScript(Idle);
            WaitLoaded(H, Item);
        }

        public LSLSystemAPI Api => ApiOf(H, Item);

        public TaskInventoryItem Self { get { lock (H.Prim.TaskInventory) return H.Prim.TaskInventory[Item]; } }

        public List<string> Errors => H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

        public (int Ms, object Ret) Accounted(Func<LSLSystemAPI, object> call) => AccountedFor(Item, Api, call);

        public (int Ms, object Ret) AccountedFor(UUID item, LSLSystemAPI api, Func<LSLSystemAPI, object> call)
        {
            H.ClearSaid(item);
            var ctx = new SyscallContext(item, SyscallContext.NextSeq());
            ctx.Enter();
            object ret;
            try { ret = call(api); }
            finally { SyscallContext.Exit(); }
            return (ctx.DelayMs, ret);
        }

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

    private static void WaitLoaded(SchedulerHarness h, UUID item)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (h.RunStateOf(item) != "Waiting")
        {
            Assert.True(DateTime.UtcNow < until, "the script never loaded: " + h.RunStateOf(item));
            if (!h.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
        }
    }

    private static LSLSystemAPI ApiOf(SchedulerHarness h, UUID item)
    {
        var exe = (PhloxExecutionScheduler)Field(h.Engine, "m_ExeScheduler");
        return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item];
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

    /// <summary>An HTTP module that records what starts and never completes anything.</summary>
    private sealed class HoldingHttp : IHttpRequestModule
    {
        public int Started;
        public UUID MakeHttpRequest(string url, string parameters, string body) => UUID.Zero;
        public UUID StartHttpRequest(uint localID, UUID itemID, string url, List<string> parameters, Dictionary<string, string> headers, string body)
        {
            System.Threading.Interlocked.Increment(ref Started);
            return UUID.Random();
        }
        public void StopHttpRequest(uint localID, UUID itemID) { }
        public IHttpServiceRequest GetNextCompletedRequest() => null;
        public void RemoveCompletedRequest(UUID id) { }
        public bool CheckThrottle(uint localID, UUID onerID) => true;
        public bool CheckAllowed(Uri url) => true;
    }

    private static void OwnLand(Rig r) => r.H.Scene.LandChannel = new StripLand(r.H.Scene, (256, r.H.Prim.OwnerID));

    private static void GrantReturn(Rig r)
    {
        r.Self.PermsGranter = r.H.Prim.OwnerID;
        r.Self.PermsMask = SlConst.PERMISSION_RETURN_OBJECTS;
    }

    private static void GrantDebitByOwner(Rig r)
    {
        r.Self.PermsGranter = r.Self.OwnerID;
        r.Self.PermsMask = SlConst.PERMISSION_DEBIT;
    }

    private static void Dialogs(Rig r) => r.H.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out _));

    private static readonly Vector3 V = new Vector3(1, 2, 3);
    private static readonly Quaternion Q = new Quaternion(0, 0, 0, 1);

    /// <summary>One error path: the text Halcyon said, and the sleep the call sets after it (that one replaces the 15 ms).</summary>
    private sealed record Case(string Name, Func<LSLSystemAPI, object> Call, string Text, int Later = 0, Action<Rig> Setup = null, object Returns = null);

    private static readonly Dictionary<string, Case> Errors = new Case[]
    {
        new("llGetNumberOfNotecardLines missing", a => a.llGetNumberOfNotecardLines("nope"),
            "Script error: Notecard 'nope' could not be found.", Later: 100, Returns: UUID.Zero.ToString()),
        new("llGetNotecardLine missing", a => a.llGetNotecardLine("nope", 0),
            "Script error: Notecard 'nope' could not be found.", Returns: UUID.Zero.ToString()),
        new("llGetNotecardLine empty name", a => a.llGetNotecardLine("", 0),
            "Script error: Notecard '' could not be found.", Returns: UUID.Zero.ToString()),
        new("llDialog not a key", a => { a.llDialog("bob", "m", L("a"), 5); return null; }, LslErr + "First parameter to llDialog needs to be a key"),
        new("llDialog 13 buttons", a => { a.llDialog(UUID.Random().ToString(), "m", L(Enumerable.Range(1, 13).Select(i => (object)i.ToString()).ToArray()), 5); return null; },
            LslErr + "No more than 12 buttons can be shown"),
        new("llDialog blank label", a => { a.llDialog(UUID.Random().ToString(), "m", L("a", ""), 5); return null; }, LslErr + "button label cannot be blank"),
        new("llDialog 25-character label", a => { a.llDialog(UUID.Random().ToString(), "m", L(new string('x', 25)), 5); return null; },
            LslErr + "button label cannot be longer than 24 characters"),
        new("llTextBox not a key", a => { a.llTextBox("bob", "m", 5); return null; }, LslErr + "First parameter to llDialog needs to be a key", Setup: Dialogs),
        new("llSetVehicleType invalid", a => { a.llSetVehicleType(6); return null; }, LslErr + "llSetVehicleType(6) is not valid."),
        new("llSetVehicleFloatParam invalid", a => { a.llSetVehicleFloatParam(99, 0.5f); return null; }, LslErr + "llSetVehicleFloatParam(99, 0.5) is not valid."),
        new("llSetVehicleFloatParam NaN", a => { a.llSetVehicleFloatParam(27, float.NaN); return null; }, LslErr + "llSetVehicleFloatParam(27, NaN) is not valid."),
        new("llSetVehicleVectorParam invalid", a => { a.llSetVehicleVectorParam(24, V); return null; }, LslErr + "llSetVehicleVectorParam(24, " + V + ") is not valid."),
        new("llSetVehicleRotationParam invalid", a => { a.llSetVehicleRotationParam(45, Q); return null; }, LslErr + "llSetVehicleRotationParam(45, " + Q + ") is not valid."),
        new("llHTTPRequest odd option count", a => a.llHTTPRequest("http://example.invalid/", L(0, "GET", 1), ""),
            "Script error: Invalid number of parameters in options list for llHTTPRequest.", Setup: r => r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(new HoldingHttp()),
            Returns: UUID.Zero.ToString()),
        new("llHTTPRequest HTTP_CUSTOM_HEADER count", a => a.llHTTPRequest("http://example.invalid/", L(5, "X-Name"), ""),
            "Script error: Invalid number of parameters in the HTTP_CUSTOM_HEADER options for llHTTPRequest.",
            Setup: r => r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(new HoldingHttp()), Returns: UUID.Zero.ToString()),
        new("llReturnObjectsByOwner no permission", a => a.llReturnObjectsByOwner(UUID.Random().ToString(), SlConst.OBJECT_RETURN_REGION),
            LslErr + "No permissions to return objects", Returns: SlConst.ERR_RUNTIME_PERMISSIONS),
        new("llReturnObjectsByOwner region not estate", a => a.llReturnObjectsByOwner(UUID.Random().ToString(), SlConst.OBJECT_RETURN_REGION),
            LslErr + "No parcel/region permission to return objects", Setup: r => { OwnLand(r); GrantReturn(r); }, Returns: SlConst.ERR_PARCEL_PERMISSIONS),
        new("llReturnObjectsByID no permission", a => a.llReturnObjectsByID(L(UUID.Random().ToString())),
            LslErr + "No permissions to return objects", Returns: SlConst.ERR_RUNTIME_PERMISSIONS),
        new("llSound", a => { a.llSound("s", 1f, 0, 0); return null; }, Depr + "llSound"),
        new("llMakeExplosion", a => { a.llMakeExplosion(1, 1f, 1f, 1f, 1f, "t", V); return null; }, Depr + "llMakeExplosion"),
        new("llMakeFountain", a => { a.llMakeFountain(1, 1f, 1f, 1f, 1f, 0, "t", V, 0f); return null; }, Depr + "llMakeFountain"),
        new("llMakeSmoke", a => { a.llMakeSmoke(1, 1f, 1f, 1f, 1f, "t", V); return null; }, Depr + "llMakeSmoke"),
        new("llMakeFire", a => { a.llMakeFire(1, 1f, 1f, 1f, 1f, "t", V); return null; }, Depr + "llMakeFire"),
        new("llTakeCamera", a => { a.llTakeCamera(UUID.Random().ToString()); return null; }, Depr + "llTakeCamera"),
        new("llReleaseCamera", a => { a.llReleaseCamera(UUID.Random().ToString()); return null; }, Depr + "llReleaseCamera"),
        new("llPointAt", a => { a.llPointAt(V); return null; }, NotImpl + "llPointAt"),
        new("llStopPointAt", a => { a.llStopPointAt(); return null; }, NotImpl + "llStopPointAt"),
        new("llGodLikeRezObject", a => { a.llGodLikeRezObject("x", V); return null; }, NotImpl + "llGodLikeRezObject"),
        new("llCollisionSprite", a => { a.llCollisionSprite("x"); return null; }, NotImpl + "llCollisionSprite"),
        new("botChangeOwner", a => { a.botChangeOwner(UUID.Random().ToString(), UUID.Random().ToString()); return null; }, NotImpl + "botChangeOwner"),
        new("llGiveMoney bad key", a => a.llGiveMoney("not a key", 5), LslErr + "Bad key in llGiveMoney", Setup: GrantDebitByOwner, Returns: 0),
        new("llGiveMoney no money module", a => a.llGiveMoney(UUID.Random().ToString(), 5), NotImpl + "llGiveMoney", Setup: GrantDebitByOwner, Returns: 0),
        new("llGiveMoney DEBIT granted by another", a => a.llGiveMoney(UUID.Random().ToString(), 5), "Script error: llGiveMoney: PERMISSION_DEBIT not granted.",
            Setup: r => { r.Self.PermsGranter = UUID.Random(); r.Self.PermsMask = SlConst.PERMISSION_DEBIT; }, Returns: 0),
        new("llParcelMediaCommandList unsupported", a => { a.llParcelMediaCommandList(L(13, 1.0f)); return null; },
            NotImpl + "llParcelMediaCommandList parameter not supported yet: " + ((ParcelMediaCommandEnum)13), Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList SIZE first", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Size, "x", 5)); return null; },
            "Script error: The first argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer.", Later: 2000, Setup: OwnLand),
        new("llParcelMediaCommandList SIZE second", a => { a.llParcelMediaCommandList(L((int)ParcelMediaCommandEnum.Size, 5, "x")); return null; },
            "Script error: The second argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer.", Later: 2000, Setup: OwnLand),
        new("llParcelMediaQuery unsupported", a => a.llParcelMediaQuery(L((int)ParcelMediaCommandEnum.Stop)),
            NotImpl + "llParcelMediaQuery parameter do not supported yet: " + ParcelMediaCommandEnum.Stop, Later: 2000, Setup: OwnLand),
    }.ToDictionary(c => c.Name);

    public static IEnumerable<object[]> ErrorNames() => Errors.Keys.Select(k => new object[] { k });

    private static string Show(object o) => o is LSLList l ? "[" + string.Join(",", l.Data) + "]" : o?.ToString();

    private static (int Ms, object Ret, List<string> Errors) Run(Case c, bool chatThrottle)
    {
        using var r = new Rig(chatThrottle);
        c.Setup?.Invoke(r);
        var (ms, ret) = r.Accounted(c.Call);
        return (ms, ret, r.Errors);
    }

    [Theory]
    [MemberData(nameof(ErrorNames))]
    public void HalcyonsErrorIsSaidOnceWithItsTextAndPauses15Ms(string name)
    {
        Case c = Errors[name];
        var on = Run(c, chatThrottle: true);
        _out.WriteLine(on.Ms + " ms: " + string.Join(" | ", on.Errors));
        Assert.Equal(new[] { c.Text }, on.Errors);
        Assert.Equal(c.Later + 15, on.Ms);
        if (c.Returns != null) Assert.Equal(Show(c.Returns), Show(on.Ret));
    }

    [Theory]
    [MemberData(nameof(ErrorNames))]
    public void WithChatThrottleOffTheErrorIsSaidWithoutThePause(string name)
    {
        Case c = Errors[name];
        var off = Run(c, chatThrottle: false);
        Assert.Equal(new[] { c.Text }, off.Errors);
        Assert.Equal(c.Later, off.Ms);
        if (c.Returns != null) Assert.Equal(Show(c.Returns), Show(off.Ret));
    }

    [Theory]
    [MemberData(nameof(ErrorNames))]
    public void TheScriptWakesAfterThePauseOrTheLaterSleep(string name)
    {
        Case c = Errors[name];
        using var r = new Rig();
        c.Setup?.Invoke(r);
        Assert.Equal(c.Later > 0 ? c.Later : 15, r.Effective(c.Call));
    }

    // ── what the refusals leave undone ──

    [Fact]
    public void ARefusedDialogIsNotSentAndDoesNotSleepASecond()
    {
        using var r = new Rig();
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        var client = new DialogClient(acd, r.H.Scene);
        SceneHelpers.AddScenePresence(r.H.Scene, client, acd);
        foreach (var buttons in new[] { L("a", ""), L(new string('x', 25)), L(Enumerable.Range(1, 13).Select(i => (object)i.ToString()).ToArray()) })
        {
            var (ms, _) = r.Accounted(a => { a.llDialog(acd.AgentID.ToString(), "m", buttons, 5); return null; });
            Assert.Equal(15, ms);
        }
        Assert.Empty(client.Dialogs);
        // A good one is sent whole (12 labels of 24) and sleeps its second, as before.
        var labels = Enumerable.Range(1, 12).Select(i => (object)new string('y', 24)).ToArray();
        var (okMs, _) = r.Accounted(a => { a.llDialog(acd.AgentID.ToString(), "m", L(labels), 5); return null; });
        Assert.Empty(r.Errors);
        Assert.Equal(1000, okMs);
        Assert.Single(client.Dialogs);
        Assert.Equal(labels.Select(o => (string)o), client.Dialogs[0]);
    }

    private sealed class DialogClient : TestClient, IClientAPI
    {
        public readonly List<string[]> Dialogs = new();
        public DialogClient(AgentCircuitData a, Scene s) : base(a, s) { }
        void IClientAPI.SendDialog(string objectname, UUID objectID, UUID ownerID, string ownerFirstName, string ownerLastName,
            string msg, UUID textureID, int ch, string[] buttonlabels) => Dialogs.Add(buttonlabels);
    }

    /// <summary>Every VEHICLE_* type and parameter Phlox defines goes to its setter with no error, as Halcyon's validators.</summary>
    [Fact]
    public void EveryDefinedVehicleConstantIsAcceptedByItsSetter()
    {
        using var r = new Rig();
        var consts = InWorldz.Phlox.Compiler.DefaultConstants.Constants;
        int V_(string n) => int.Parse(consts[n].ConstValue);
        var types = consts.Keys.Where(k => k.StartsWith("VEHICLE_TYPE_")).ToList();
        var floats = new[] { "HOVER_HEIGHT", "HOVER_EFFICIENCY", "HOVER_TIMESCALE", "BUOYANCY", "LINEAR_DEFLECTION_EFFICIENCY",
            "LINEAR_DEFLECTION_TIMESCALE", "LINEAR_MOTOR_TIMESCALE", "LINEAR_MOTOR_DECAY_TIMESCALE", "ANGULAR_DEFLECTION_EFFICIENCY",
            "ANGULAR_DEFLECTION_TIMESCALE", "ANGULAR_MOTOR_TIMESCALE", "ANGULAR_MOTOR_DECAY_TIMESCALE", "VERTICAL_ATTRACTION_EFFICIENCY",
            "VERTICAL_ATTRACTION_TIMESCALE", "BANKING_EFFICIENCY", "BANKING_MIX", "BANKING_TIMESCALE", "MOUSELOOK_AZIMUTH",
            "MOUSELOOK_ALTITUDE", "BANKING_AZIMUTH", "DISABLE_MOTORS_HEIGHT", "DISABLE_MOTORS_DELAY", "INVERTED_BANKING_MODIFIER",
            // vector parameters, which Halcyon's float setter also takes
            "LINEAR_FRICTION_TIMESCALE", "ANGULAR_FRICTION_TIMESCALE", "LINEAR_MOTOR_DIRECTION", "ANGULAR_MOTOR_DIRECTION",
            "LINEAR_MOTOR_OFFSET", "LINEAR_WIND_EFFICIENCY", "ANGULAR_WIND_EFFICIENCY" };
        var vectors = new[] { "LINEAR_FRICTION_TIMESCALE", "ANGULAR_FRICTION_TIMESCALE", "LINEAR_MOTOR_DIRECTION", "ANGULAR_MOTOR_DIRECTION",
            "LINEAR_MOTOR_OFFSET", "LINEAR_MOTOR_TIMESCALE", "LINEAR_MOTOR_DECAY_TIMESCALE", "ANGULAR_MOTOR_TIMESCALE",
            "ANGULAR_MOTOR_DECAY_TIMESCALE", "LINEAR_WIND_EFFICIENCY", "ANGULAR_WIND_EFFICIENCY" };
        Assert.Equal(8, types.Count);
        r.H.ClearSaid(r.Item);
        foreach (string t in types) r.Api.llSetVehicleType(V_(t));
        foreach (string f in floats) r.Api.llSetVehicleFloatParam(V_("VEHICLE_" + f), 1f);
        foreach (string v in vectors) r.Api.llSetVehicleVectorParam(V_("VEHICLE_" + v), V);
        r.Api.llSetVehicleRotationParam(V_("VEHICLE_REFERENCE_FRAME"), Q);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void AnOddHttpOptionListStartsNoRequest()
    {
        using var r = new Rig();
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        r.Api.llHTTPRequest("http://example.invalid/", L(0, "GET", 1), "");
        r.Api.llHTTPRequest("http://example.invalid/", L(5, "X-Name"), "");
        Assert.Equal(0, http.Started);
        // A whole list still starts one.
        string key = r.Api.llHTTPRequest("http://example.invalid/", L(0, "GET", 5, "X-Name", "v"), "");
        Assert.Equal(1, http.Started);
        Assert.NotEqual(UUID.Zero.ToString(), key);
    }

    // ── item 2: the HTTP in-flight caps ──

    // The plugin class shares its full name with the core's (OpenSim.Region.ScriptEngine.Shared), so it is not named here;
    // its caps are Halcyon's 10 per object and 200 per region.
    private const int PerObject = 10, PerRegion = 200;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheEleventhRequestInFlightFromOneObjectGetsNullKeyAnd80Ms(bool chatThrottle)
    {
        using var r = new Rig(chatThrottle);
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        for (int i = 0; i < PerObject; i++)
        {
            var (ms, key) = r.Accounted(a => a.llHTTPRequest("http://example.invalid/" + i, new LSLList(), ""));
            Assert.Equal(0, ms);
            Assert.NotEqual(UUID.Zero.ToString(), key);
        }
        var (cappedMs, capped) = r.Accounted(a => a.llHTTPRequest("http://example.invalid/x", new LSLList(), ""));
        Assert.Equal(UUID.Zero.ToString(), capped);
        Assert.Equal(80, cappedMs);   // Halcyon ERROR_DELAY; not chat: ChatThrottle does not remove it
        Assert.Equal(80, r.Effective(a => a.llHTTPRequest("http://example.invalid/y", new LSLList(), "")));
        Assert.Empty(r.Errors);   // Halcyon said nothing
        Assert.Equal(PerObject, http.Started);
    }

    [Fact]
    public void AnswersAndResetsFreeTheObjectsSlots()
    {
        using var r = new Rig();
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        var keys = Enumerable.Range(0, 10).Select(i => r.Api.llHTTPRequest("http://example.invalid/" + i, new LSLList(), "")).ToList();
        Assert.Equal(UUID.Zero.ToString(), r.Api.llHTTPRequest("http://example.invalid/x", new LSLList(), ""));

        Assert.True(r.H.Engine.AsyncCommands.HttpRequestPlugin.Offered(keys[0]));   // a response taken: one slot free
        Assert.NotEqual(UUID.Zero.ToString(), r.Api.llHTTPRequest("http://example.invalid/a", new LSLList(), ""));
        Assert.Equal(UUID.Zero.ToString(), r.Api.llHTTPRequest("http://example.invalid/b", new LSLList(), ""));

        r.H.Engine.AsyncCommands.HttpRequestPlugin.RemoveEvents(r.H.Prim.LocalId, r.Item);   // the script reset: all its requests end
        Assert.Equal(0, r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount);
        Assert.NotEqual(UUID.Zero.ToString(), r.Api.llHTTPRequest("http://example.invalid/c", new LSLList(), ""));
    }

    /// <summary>A request of another object through the plugin's Start, as llHTTPRequest calls it (by reflection, so this
    /// class also builds against code without these checks for the red run).</summary>
    private static UUID StartOther(Rig r)
    {
        object plugin = r.H.Engine.AsyncCommands.HttpRequestPlugin;
        MethodInfo start = plugin.GetType().GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance,
            new[] { typeof(UUID), typeof(UUID), typeof(Func<UUID>), typeof(bool).MakeByRefType() });
        Assert.NotNull(start);
        return (UUID)start.Invoke(plugin, new object[] { UUID.Random(), UUID.Random(), (Func<UUID>)(() => UUID.Random()), false });
    }

    private static bool AutoLink(Rig r)
    {
        PropertyInfo p = r.H.Engine.GetType().GetProperty("AutomaticLinkPermission");
        Assert.NotNull(p);
        return (bool)p.GetValue(r.H.Engine);
    }

    [Fact]
    public void AnotherObjectHasItsOwnTenAndTheRegionStopsAt200()
    {
        using var r = new Rig();
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        var other = SceneHelpers.AddSceneObject(r.H.Scene, "other", r.H.Prim.OwnerID);
        UUID otherItem = r.H.RezScriptInto(other.RootPart, Idle);
        WaitLoaded(r.H, otherItem);
        LSLSystemAPI otherApi = ApiOf(r.H, otherItem);

        for (int i = 0; i < 10; i++) r.Api.llHTTPRequest("http://example.invalid/" + i, new LSLList(), "");
        Assert.Equal(UUID.Zero.ToString(), r.Api.llHTTPRequest("http://example.invalid/x", new LSLList(), ""));
        Assert.NotEqual(UUID.Zero.ToString(), otherApi.llHTTPRequest("http://example.invalid/o", new LSLList(), ""));

        // Fill the region to 200 with requests of other objects (the plugin's own entry point, as llHTTPRequest uses it).
        int start = r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount;
        for (int i = start; i < PerRegion; i++)
            Assert.NotEqual(UUID.Zero, StartOther(r));
        Assert.Equal(PerRegion, r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount);
        var (ms, key) = r.AccountedFor(otherItem, otherApi, a => a.llHTTPRequest("http://example.invalid/p", new LSLList(), ""));
        Assert.Equal(UUID.Zero.ToString(), key);
        Assert.Equal(80, ms);
    }

    // ── [InWorldz.Phlox] HttpInFlightThrottle, the caps' switch (default true) ──

    private static Rig HttpRig(string setting) => new Rig(configure: setting == null ? null
        : cfg => cfg.Configs["InWorldz.Phlox"].Set("HttpInFlightThrottle", setting));

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void WithTheSwitchOnTheEleventhRequestGetsNullKeyAnd80Ms(string setting)
    {
        using var r = HttpRig(setting);
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        for (int i = 0; i < PerObject; i++)
            Assert.NotEqual(UUID.Zero.ToString(), r.Api.llHTTPRequest("http://example.invalid/" + i, new LSLList(), ""));
        var (ms, key) = r.Accounted(a => a.llHTTPRequest("http://example.invalid/x", new LSLList(), ""));
        Assert.Equal(UUID.Zero.ToString(), key);
        Assert.Equal(80, ms);
        Assert.Equal(PerObject, http.Started);
    }

    [Fact]
    public void WithTheSwitchOffNeitherCapNorThe80MsApplies()
    {
        using var r = HttpRig("false");
        var http = new HoldingHttp();
        r.H.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        for (int i = 0; i < 3 * PerObject; i++)
        {
            var (ms, key) = r.Accounted(a => a.llHTTPRequest("http://example.invalid/" + i, new LSLList(), ""));
            Assert.Equal(0, ms);
            Assert.NotEqual(UUID.Zero.ToString(), key);
        }
        Assert.Equal(0, r.Effective(a => a.llHTTPRequest("http://example.invalid/y", new LSLList(), "")));
        Assert.Equal(3 * PerObject + 1, http.Started);

        // The region's 200: filled past it through the plugin's own entry point, and this object still starts.
        for (int i = r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount; i < PerRegion + 5; i++)
            Assert.NotEqual(UUID.Zero, StartOther(r));
        var (ms2, key2) = r.Accounted(a => a.llHTTPRequest("http://example.invalid/z", new LSLList(), ""));
        Assert.Equal(0, ms2);
        Assert.NotEqual(UUID.Zero.ToString(), key2);
        Assert.Equal(PerRegion + 6, r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount);
    }

    // ── item 1 ──

    private static void Terrain(Rig r, bool god, Func<Vector3, bool> mayTerraform, float bakedHeight, double raise, double lower)
    {
        var scene = r.H.Scene;
        scene.Permissions.OnIsAdministrator += _ => god;
        scene.Permissions.OnTerraformLand += (_, pos) => mayTerraform(pos);
        ITerrainChannel baked = scene.Heightmap.MakeCopy();
        for (int x = 0; x < 64; x++) for (int y = 0; y < 64; y++) baked[x, y] = bakedHeight;
        scene.Bakedmap = baked;
        scene.RegionInfo.RegionSettings.TerrainRaiseLimit = raise;
        scene.RegionInfo.RegionSettings.TerrainLowerLimit = lower;
    }

    [Fact]
    public void IwSetGroundHoldsANonGodWithinTheEstateLimitsOfTheBakedTerrain()
    {
        using var r = new Rig();
        Terrain(r, god: false, _ => true, bakedHeight: 20f, raise: 4, lower: -4);
        r.Api.iwSetGround(10, 10, 11, 11, 30f);
        Assert.Equal(24f, r.H.Scene.Heightmap[10, 10]);
        Assert.Equal(24f, r.H.Scene.Heightmap[11, 11]);
        r.Api.iwSetGround(10, 10, 10, 10, 5f);
        Assert.Equal(16f, r.H.Scene.Heightmap[10, 10]);
        r.Api.iwSetGround(12, 12, 12, 12, 22.5f);   // within the limits: as asked
        Assert.Equal(22.5f, r.H.Scene.Heightmap[12, 12]);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void IwSetGroundLetsAGodPastTheLimitsAndTheLand()
    {
        using var r = new Rig();
        Terrain(r, god: true, _ => false, bakedHeight: 20f, raise: 4, lower: -4);
        r.Api.iwSetGround(10, 10, 10, 10, 30f);
        Assert.Equal(30f, r.H.Scene.Heightmap[10, 10]);
    }

    [Fact]
    public void IwSetGroundChangesOnlyTheCellsTheOwnerMayTerraform()
    {
        using var r = new Rig();
        Terrain(r, god: false, pos => pos.X < 11, bakedHeight: 20f, raise: 100, lower: -100);
        float before = r.H.Scene.Heightmap[11, 10];
        r.Api.iwSetGround(10, 10, 11, 10, 25f);
        Assert.Equal(25f, r.H.Scene.Heightmap[10, 10]);
        Assert.Equal(before, r.H.Scene.Heightmap[11, 10]);
    }

    [Theory]
    [InlineData(10, 10, 10, 10, -0.5f)]
    [InlineData(10, 10, 10, 10, 1024.5f)]
    [InlineData(10, 10, 10, 10, float.NaN)]
    [InlineData(-1, 10, 10, 10, 25f)]
    [InlineData(10, 10, 10, 256, 25f)]
    public void IwSetGroundOutsideHalcyonsBoundsChangesNothing(int x1, int y1, int x2, int y2, float height)
    {
        using var r = new Rig();
        Terrain(r, god: false, _ => true, bakedHeight: 20f, raise: 100, lower: -100);
        float before = r.H.Scene.Heightmap[10, 10];
        r.Api.iwSetGround(x1, y1, x2, y2, height);
        Assert.Equal(before, r.H.Scene.Heightmap[10, 10]);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void ANaNRotationRezIsRefusedWithHalcyonsErrorNoPauseAndNoDelay()
    {
        var nan = new Quaternion(float.NaN, 0, 0, 1);
        foreach (bool chat in new[] { true, false })
        {
            using var r = new Rig(chat);
            var (ms1, _) = r.Accounted(a => { a.llRezObject("anything", r.H.Prim.AbsolutePosition, Vector3.Zero, nan, 0); return null; });
            Assert.Equal(0, ms1);
            Assert.Equal(new[] { "Script error: Unable to create requested object. Position is invalid." }, r.Errors);
            var (ms2, key) = r.Accounted(a => a.iwRezAt("anything", 0, r.H.Prim.AbsolutePosition, Vector3.Zero, nan, 0));
            Assert.Equal(0, ms2);
            Assert.Equal(UUID.Zero.ToString(), key);
            Assert.Equal(new[] { "Script error: Unable to create requested object. Position is invalid." }, r.Errors);
            // A good rotation still reaches the rez (and its 100 ms), failing here on the missing item as before.
            var (ms3, _) = r.Accounted(a => { a.llRezObject("anything", r.H.Prim.AbsolutePosition, Vector3.Zero, Quaternion.Identity, 0); return null; });
            Assert.Equal(100, ms3);
        }
    }

    /// <summary>A client that counts the camera clears it is sent (interface re-implementation over TestClient).</summary>
    private sealed class CamClient : TestClient, IClientAPI
    {
        public int Cleared;
        public CamClient(AgentCircuitData a, Scene s) : base(a, s) { }
        void IClientAPI.SendClearFollowCamProperties(UUID objectID) => Cleared++;
    }

    [Fact]
    public void LlClearCameraParamsNeedsPermissionControlCameraFromItsGranter()
    {
        using var r = new Rig();
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        var client = new CamClient(acd, r.H.Scene);
        SceneHelpers.AddScenePresence(r.H.Scene, client, acd);

        r.Self.PermsGranter = acd.AgentID;
        r.Self.PermsMask = SlConst.PERMISSION_TAKE_CONTROLS;   // some other permission
        r.Api.llClearCameraParams();
        Assert.Equal(0, client.Cleared);

        r.Self.PermsMask = SlConst.PERMISSION_CONTROL_CAMERA;
        r.Api.llClearCameraParams();
        Assert.Equal(1, client.Cleared);
        Assert.Empty(r.Errors);   // Halcyon refused silently
    }

    [Fact]
    public void LlGiveMoneyWithDebitFromTheOwnerAndNoMoneyModuleSaysOnlyNotImplemented()
    {
        using var r = new Rig();
        GrantDebitByOwner(r);
        var (ms, ret) = r.Accounted(a => a.llGiveMoney(UUID.Random().ToString(), 5));
        Assert.Equal(0, ret);
        Assert.Equal(new[] { NotImpl + "llGiveMoney" }, r.Errors);
        Assert.Equal(15, ms);   // no sleep (SL forced delay 0.0): its 15 ms error pause only
        // NULL_KEY and a non-positive amount stay silent refusals.
        r.Accounted(a => a.llGiveMoney(UUID.Zero.ToString(), 5));
        Assert.Empty(r.Errors);
        r.Accounted(a => a.llGiveMoney(UUID.Random().ToString(), 0));
        Assert.Empty(r.Errors);
    }

    // ── item 3: iwMakeNotecard storage failure (Halcyon: long-running, no pause) ──

    public class FailingStore : DispatchProxy
    {
        public IAssetService Inner;
        protected override object Invoke(MethodInfo m, object[] a)
            => m.Name == "Store" ? null : m.Invoke(Inner, a);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IwMakeNotecardSaysWhenStorageFailsAndMakesNoItem(bool chatThrottle)
    {
        using var r = new Rig(chatThrottle);
        var proxy = DispatchProxy.Create<IAssetService, FailingStore>();
        ((FailingStore)(object)proxy).Inner = r.H.Scene.AssetService;
        typeof(Scene).GetField("m_AssetService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r.H.Scene, proxy);
        var (ms, _) = r.Accounted(a => { a.iwMakeNotecard("card", L("line")); return null; });
        Assert.Equal(new[] { "Script error: Notecard asset storage failed!" }, r.Errors);
        Assert.Equal(5000, ms);   // its existing 5 s only
        Assert.Null(r.H.Prim.Inventory.GetInventoryItem("card"));
    }

    // ── item 4: a script's own inventory key ──

    [Fact]
    public void AScriptGetsItsOwnAssetKeyButNoOtherNonFullPermItem()
    {
        using var r = new Rig();
        UUID other = r.H.RezScript(Idle);
        WaitLoaded(r.H, other);
        const uint noMod = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Transfer);
        TaskInventoryItem self = r.Self, sibling;
        lock (r.H.Prim.TaskInventory) sibling = r.H.Prim.TaskInventory[other];
        self.CurrentPermissions = noMod;
        sibling.CurrentPermissions = noMod;

        string own = r.Api.llGetScriptName();
        Assert.Equal(self.AssetID.ToString(), r.Api.llGetInventoryKey(own));
        Assert.Equal(self.AssetID.ToString(), r.Api.iwGetLinkInventoryKey(SlConst.LINK_THIS, own));
        Assert.Equal(UUID.Zero.ToString(), r.Api.llGetInventoryKey(sibling.Name));
        Assert.Equal(UUID.Zero.ToString(), r.Api.iwGetLinkInventoryKey(SlConst.LINK_THIS, sibling.Name));
        // The sibling asking for its own key gets it.
        Assert.Equal(sibling.AssetID.ToString(), ApiOf(r.H, other).llGetInventoryKey(sibling.Name));
    }

    // ── item 5: AutomaticLinkPermission ──

    [Fact]
    public void AutomaticLinkPermissionIsReadFromYEnginesKey()
    {
        using (var r = new Rig(configure: c => c.AddConfig("YEngine").Set("AutomaticLinkPermission", "true")))
        {
            Assert.True(AutoLink(r));
            r.Self.PermsMask = 0;
            Assert.Equal(SlConst.PERMISSION_CHANGE_LINKS, r.Api.llGetPermissions());
            var (ms, _) = r.Accounted(a => { a.llBreakLink(2); return null; });
            Assert.Empty(r.Errors);
            Assert.Equal(0, ms);
            r.Accounted(a => { a.llCreateLink(UUID.Random().ToString(), 1); return null; });
            Assert.DoesNotContain(r.Errors, e => e.Contains("PERMISSION_CHANGE_LINKS"));
        }
        using (var r = new Rig())
        {
            Assert.False(AutoLink(r));
            r.Self.PermsMask = 0;
            Assert.Equal(0, r.Api.llGetPermissions());
            r.Accounted(a => { a.llBreakLink(2); return null; });
            Assert.Equal(new[] { "Script error: llBreakLink: PERMISSION_CHANGE_LINKS not set" }, r.Errors);
        }
    }
}
