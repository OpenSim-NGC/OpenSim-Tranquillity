/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// After a script moves to another engine, the old engine's late cleanup takes away nothing the new engine's instance set
/// up in the core modules that keep things by item id: the prim's script event mask (touch, collision, target), URLs,
/// XML-RPC channels and HTTP requests in flight. Phlox lets a script go on its loader thread, after the region has
/// started the script in YEngine; YEngine lets a script go at once, before the region starts it in Phlox.
/// <para>The region's URL, XML-RPC and HTTP modules are fakes that record what each engine asks of them, wired as the
/// real URL module is (it drops an item's URLs when an engine raises OnScriptRemoved). Phlox and YEngine are the real
/// engines. The harness drives Phlox's loader from the test, so Phlox's unload runs exactly when the test pumps it,
/// while YEngine runs on its own threads. In "phlox-yengine" (YEngine's statics).</para>
/// </summary>
[Collection("phlox-yengine")]
public class EngineSwitchKeepsSharedResourcesTests
{
    private const string Phlox = "InWorldz.Phlox";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    // Each start asks for a URL, an XML-RPC channel and an HTTP request, then says "entry". It handles touch, collision
    // and target events, so the engine running it sets those in the prim's event mask.
    private const string Body =
        "default {\n" +
        "  state_entry() {\n" +
        "    llRequestURL();\n" +
        "    llOpenRemoteDataChannel();\n" +
        "    llHTTPRequest(\"http://example.org/\", [], \"\");\n" +
        "    llSay(0, \"entry\");\n" +
        "  }\n" +
        "  touch_start(integer t) { llSay(0, \"touched\"); }\n" +
        "  collision_start(integer n) { llSay(0, \"bumped\"); }\n" +
        "  at_target(integer t, vector a, vector b) { llSay(0, \"there\"); }\n" +
        "  http_request(key id, string method, string body) { }\n" +
        "  http_response(key id, integer status, list meta, string body) { }\n" +
        "  remote_data(integer type, key channel, key message, string sender, integer i, string s) { }\n" +
        "}\n";

    // Takes the controls of the avatar that grants TAKE_CONTROLS: "ask <avatar>" asks, "take" takes CONTROL_FWD.
    private const string ControlsBody =
        "default {\n" +
        "  state_entry() { llListen(7, \"\", NULL_KEY, \"\"); llSay(0, \"entry\"); }\n" +
        "  listen(integer c, string name, key id, string msg) {\n" +
        "    list w = llParseString2List(msg, [\" \"], []);\n" +
        "    string cmd = llList2String(w, 0);\n" +
        "    if (cmd == \"ask\") llRequestPermissions(llList2Key(w, 1), PERMISSION_TAKE_CONTROLS);\n" +
        "    else if (cmd == \"take\") { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, \"took\"); }\n" +
        "  }\n" +
        "  run_time_permissions(integer p) { llSay(0, \"rtp=\" + (string)p); }\n" +
        "  control(key id, integer level, integer edge) { }\n" +
        "}\n";

    private const scriptEvents MaskEvents = scriptEvents.touch_start | scriptEvents.collision_start | scriptEvents.at_target;

    private sealed class Req : IHttpServiceRequest
    {
        public int Status { get; set; } = 200;
        public string ResponseBody { get; set; } = "";
        public bool Finished => false;
        public UUID ItemID { get; set; }
        public uint LocalID { get; set; }
        public UUID ReqID { get; set; }
        public void Process() { }
        public void SendRequest() { }
        public void Stop() { }
    }

    /// <summary>The region's HTTP module: a request stays in flight until StopHttpRequest stops its item's requests.</summary>
    private sealed class Http : IHttpRequestModule
    {
        public readonly ConcurrentDictionary<UUID, Req> InFlight = new();
        public readonly ConcurrentQueue<UUID> Started = new();
        public UUID MakeHttpRequest(string url, string parameters, string body) => UUID.Zero;
        public UUID StartHttpRequest(uint localID, UUID itemID, string url, List<string> parameters, Dictionary<string, string> headers, string body)
        {
            var r = new Req { ItemID = itemID, LocalID = localID, ReqID = UUID.Random() };
            InFlight[r.ReqID] = r;
            Started.Enqueue(r.ReqID);
            return r.ReqID;
        }
        public void StopHttpRequest(uint localID, UUID itemID)
        {
            foreach (var kvp in InFlight)
                if (kvp.Value.ItemID == itemID) InFlight.TryRemove(kvp.Key, out _);
        }
        /// <summary>Completed requests, handed only to the thread <see cref="PumpThread"/> names (0: to none).</summary>
        public readonly ConcurrentQueue<Req> Completed = new();
        public int PumpThread;
        public IHttpServiceRequest GetNextCompletedRequest()
            => PumpThread == Environment.CurrentManagedThreadId && Completed.TryPeek(out Req r) ? r : null;
        public void RemoveCompletedRequest(UUID id)
        {
            if (Completed.TryPeek(out Req r) && r.ReqID == id) Completed.TryDequeue(out _);
        }
        public bool CheckThrottle(uint localID, UUID onerID) => true;
        public bool CheckAllowed(Uri url) => true;
    }

    /// <summary>The region's URL module: URLs by item, all of an item's dropped by ScriptRemoved.</summary>
    private sealed class Url : IUrlModule
    {
        public readonly ConcurrentDictionary<UUID, int> ByItem = new();
        public string ExternalHostNameForLSL => "example.org";
        public UUID RequestURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options) { ByItem.AddOrUpdate(itemID, 1, (_, n) => n + 1); return UUID.Random(); }
        public UUID RequestSecureURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options) => RequestURL(engine, host, itemID, options);
        public void ReleaseURL(string url) { }
        public void HttpResponse(UUID request, int status, string body) { }
        public void HttpContentType(UUID request, string type) { }
        public string GetHttpHeader(UUID request, string header) => string.Empty;
        public int GetFreeUrls() => 1000;
        public void ScriptRemoved(UUID itemID) => ByItem.TryRemove(itemID, out _);
        public void ObjectRemoved(UUID objectID) { }
        public int GetUrlCount(UUID groupID) => 0;
        public int Of(UUID itemID) => ByItem.TryGetValue(itemID, out int n) ? n : 0;
    }

    /// <summary>The region's XML-RPC module: one channel per item, as the core module keeps them, dropped by DeleteChannels.</summary>
    private sealed class XmlRpc : IXMLRPC
    {
        public readonly ConcurrentDictionary<UUID, UUID> ChannelByItem = new();
        public UUID OpenXMLRPCChannel(uint localID, UUID itemID, UUID channelID) => ChannelByItem.GetOrAdd(itemID, _ => UUID.Random());
        public void CloseXMLRPCChannel(UUID channelKey) { }
        public bool hasRequests() => false;
        public void RemoteDataReply(string channel, string message_id, string sdata, int idata) { }
        public bool IsEnabled() => true;
        public readonly ConcurrentQueue<IXmlRpcRequestInfo> Completed = new();
        public int PumpThread;
        public IXmlRpcRequestInfo GetNextCompletedRequest()
            => PumpThread == Environment.CurrentManagedThreadId && Completed.TryPeek(out var r) ? r : null;
        public void RemoveCompletedRequest(UUID id)
        {
            if (Completed.TryPeek(out var r) && r.GetMessageID() == id) Completed.TryDequeue(out _);
        }
        public void DeleteChannels(UUID itemID) => ChannelByItem.TryRemove(itemID, out _);
        public int RemoteDataSent;
        public UUID SendRemoteData(uint localID, UUID itemID, string channel, string dest, int idata, string sdata) { System.Threading.Interlocked.Increment(ref RemoteDataSent); return UUID.Random(); }
        public IServiceRequest GetNextCompletedSRDRequest() => null;
        public void RemoveCompletedSRDRequest(UUID id) { }
        public void CancelSRDRequests(UUID itemID) { }
        public int Port => 0;
    }

    /// <summary>An XML-RPC request that came in for a script's channel.</summary>
    private sealed class Incoming : IXmlRpcRequestInfo
    {
        public UUID Item, Channel, Message = UUID.Random();
        public string Text = "";
        public bool IsProcessed() => false;
        public UUID GetChannelKey() => Channel;
        public void SetProcessed(bool processed) { }
        public void SetStrRetval(string resp) { }
        public string GetStrRetval() => "";
        public void SetIntRetval(int resp) { }
        public int GetIntRetval() => 0;
        public uint GetLocalID() => 0;
        public UUID GetItemID() => Item;
        public string GetStrVal() => Text;
        public int GetIntValue() => 0;
        public UUID GetMessageID() => Message;
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly Http HttpModule = new();
        public readonly Url UrlModule = new();
        public readonly XmlRpc Xml = new();
        public readonly UUID Owner = UUID.Random();
        public SceneObjectGroup Group;
        public TaskInventoryItem Item;

        public Rig(bool osFunctions = false)
        {
            H = osFunctions
                ? new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"), withYEngine: true)
                : new SchedulerHarness(withYEngine: true);
            H.Scene.RegisterModuleInterface<IHttpRequestModule>(HttpModule);
            H.Scene.RegisterModuleInterface<IUrlModule>(UrlModule);
            H.Scene.RegisterModuleInterface<IXMLRPC>(Xml);
            // As the real URL module does in RegionLoaded.
            H.Engine.OnScriptRemoved += UrlModule.ScriptRemoved;
            H.YEngine.OnScriptRemoved += UrlModule.ScriptRemoved;
        }

        public void Build(int choice, string source = Body)
        {
            typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(H.Scene, Phlox);
            Group = SceneHelpers.AddSceneObject(H.Scene, "Example Object", Owner);
            Item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, Group.RootPart, UUID.Random(), UUID.Random(), "switched", source);
            Item.Flags = ScriptEngineChoices.WithChoice(Item.Flags, choice);
            Assert.True(Group.RootPart.Inventory.CreateScriptInstance(Item.ItemID, 0, false, Phlox, 0));
            Group.ResumeScripts();
        }

        public bool InPhlox => H.Engine.HasScript(Item.ItemID, out _);
        public bool InYEngine => H.YEngine.HasScript(Item.ItemID, out _);
        public int Entries => H.Said.Count(s => s == "entry");
        public ScriptEngineChoiceResult Set(string target) => H.Scene.SetScriptEngine(Owner, Group.RootPart.UUID, Item.ItemID, target);

        /// <summary>HTTP requests started from now on, by their id.</summary>
        public int HttpMark() => HttpModule.Started.Count;
        public List<UUID> HttpSince(int mark) => HttpModule.Started.Skip(mark).ToList();

        /// <summary>Whether everything the current instance set up is there: the mask, a URL, a channel, the HTTP requests given.</summary>
        public string Missing(List<UUID> requests)
        {
            var missing = new List<string>();
            if ((Group.RootPart.ScriptEvents & MaskEvents) != MaskEvents) missing.Add($"event mask ({Group.RootPart.ScriptEvents})");
            if (UrlModule.Of(Item.ItemID) == 0) missing.Add("URL");
            if (!Xml.ChannelByItem.ContainsKey(Item.ItemID)) missing.Add("XML-RPC channel");
            if (requests.Count == 0 || requests.Any(r => !HttpModule.InFlight.ContainsKey(r))) missing.Add("HTTP request");
            return string.Join(", ", missing);
        }

        public string Text => $"phlox={InPhlox} yengine={InYEngine} said=[{string.Join(" | ", H.Said)}]";

        public void Dispose() => H.Dispose();
    }

    /// <summary>
    /// Wait for YEngine's instance to have started and set everything up, without pumping Phlox: Phlox's unload of its
    /// instance stays queued until the test pumps it.
    /// </summary>
    private static List<UUID> WaitForYEngineSetUp(Rig rig, int entries, int httpMark)
    {
        Assert.True(System.Threading.SpinWait.SpinUntil(
            () => rig.InYEngine && rig.Entries >= entries && rig.UrlModule.Of(rig.Item.ItemID) > 0 && rig.HttpSince(httpMark).Count > 0
                  && (rig.Group.RootPart.ScriptEvents & MaskEvents) == MaskEvents, Cap), rig.Text);
        return rig.HttpSince(httpMark);
    }

    private static void AssertNothingMissing(Rig rig, List<UUID> requests)
    {
        string missing = rig.Missing(requests);
        Assert.True(missing == "", "missing: " + missing + "; " + rig.Text);
    }

    /// <summary>Let Phlox's loader run its queued unload, then keep pumping for a second: nothing may be taken away.</summary>
    private static void LetPhloxUnload(Rig rig)
    {
        Assert.True(rig.H.PumpUntil(() => !rig.InPhlox, Cap), rig.Text);
        rig.H.PumpFor(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PhloxsLateUnloadLeavesYEngineItsEventsUrlChannelAndRequests()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1 && rig.UrlModule.Of(rig.Item.ItemID) > 0, Cap), rig.Text);

        int mark = rig.HttpMark();
        List<UUID> phloxRequests = rig.HttpSince(0);
        Assert.NotEmpty(phloxRequests);
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        List<UUID> yRequests = WaitForYEngineSetUp(rig, 2, mark);
        // Phlox still has its instance: its unload is queued and has not run.
        Assert.True(rig.InPhlox, rig.Text);
        AssertNothingMissing(rig, yRequests);

        LetPhloxUnload(rig);

        Assert.True(rig.InYEngine, rig.Text);
        AssertNothingMissing(rig, yRequests);
        // What Phlox's instance asked for before the switch is not left behind: its HTTP request was stopped.
        Assert.DoesNotContain(phloxRequests, r => rig.HttpModule.InFlight.ContainsKey(r));
    }

    [Fact]
    public void AQuickSwitchBackAndForthLeavesYEngineItsEventsUrlChannelAndRequests()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1 && rig.UrlModule.Of(rig.Item.ItemID) > 0, Cap), rig.Text);

        // To YEngine, back to Phlox and to YEngine again before Phlox's loader has run (nothing is pumped in between), so
        // Phlox's unload of its first instance, and whatever it queued for the second start, come after all three.
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));
        int mark = rig.HttpMark();
        int entries = rig.Entries;
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        List<UUID> yRequests = WaitForYEngineSetUp(rig, entries + 1, mark);
        // Phlox still has its first instance: its unload is queued and has not run.
        Assert.True(rig.InPhlox, rig.Text);
        AssertNothingMissing(rig, yRequests);

        LetPhloxUnload(rig);

        Assert.True(rig.InYEngine, rig.Text);
        AssertNothingMissing(rig, yRequests);
    }

    [Fact]
    public void YEnginesCleanupLeavesPhloxItsEventsUrlChannelAndRequests()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.YEngine);
        Assert.True(rig.H.PumpUntil(() => rig.InYEngine && rig.Entries >= 1 && rig.UrlModule.Of(rig.Item.ItemID) > 0, Cap), rig.Text);

        int mark = rig.HttpMark();
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && !rig.InYEngine && rig.Entries >= 2 && rig.UrlModule.Of(rig.Item.ItemID) > 0
                                           && rig.HttpSince(mark).Count > 0, Cap), rig.Text);
        List<UUID> pRequests = rig.HttpSince(mark);
        AssertNothingMissing(rig, pRequests);

        // A real window, with both engines running: YEngine has nothing left to release for the item.
        rig.H.PumpFor(TimeSpan.FromSeconds(1));
        Assert.True(rig.InPhlox, rig.Text);
        AssertNothingMissing(rig, pRequests);
    }

    [Fact]
    public void ControlsAPhloxScriptTookAreReleasedWhenItMovesToYEngine()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, ControlsBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        // An avatar grants TAKE_CONTROLS and the script takes its forward key.
        var sp = SceneHelpers.AddScenePresence(rig.H.Scene, UUID.Random());
        var client = (TestClient)sp.ControllingClient;
        int asked = client.ScriptQuestions.Count;
        rig.H.Scene.SimChat("ask " + client.AgentId, ChatTypeEnum.Region, 7, rig.Group.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(rig.H.PumpUntil(() => client.ScriptQuestions.Count > asked, Cap), "no permission question: " + rig.Text);
        client.FireScriptAnswer(rig.Group.RootPart.UUID, rig.Item.ItemID, 4);
        Assert.True(rig.H.PumpUntil(() => rig.H.Said.Contains("rtp=4"), Cap), rig.Text);
        rig.H.Scene.SimChat("take", ChatTypeEnum.Region, 7, rig.Group.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(rig.H.PumpUntil(() => sp.HasScriptControls(rig.Item.ItemID), Cap), rig.Text);

        // The script moves to YEngine, whose fresh instance takes no controls: the avatar's key must be let go, also once
        // Phlox's unload has run.
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        Assert.True(System.Threading.SpinWait.SpinUntil(() => rig.InYEngine && rig.Entries >= 2, Cap), rig.Text);
        LetPhloxUnload(rig);
        Assert.True(rig.InYEngine, rig.Text);
        Assert.False(sp.HasScriptControls(rig.Item.ItemID), "the avatar's controls are still taken for the item");
    }

    // ── The old Phlox instance after a switch ─────────────────────────────────────────────────────────────────────────
    // Until Phlox's loader unloads it, Phlox's instance of a script that moved to YEngine is still loaded. It must run
    // no further event and set nothing up under the item's id: YEngine's instance has the item now, and Phlox's unload
    // leaves what the core keeps for the item while YEngine holds it.

    // YEngine's instance of this script sets nothing up; only a touch does, and the tests post touches to Phlox alone.
    private const string QuietBody =
        "default {\n" +
        "  state_entry() { llSay(0, \"entry\"); }\n" +
        "  touch_start(integer t) {\n" +
        "    llSay(0, \"go\");\n" +
        "    llSleep(0.5);\n" +
        "    llRequestURL();\n" +
        "    llSay(0, \"late\");\n" +
        "  }\n" +
        "  http_request(key id, string method, string body) { }\n" +
        "}\n";

    /// <summary>The region's bot module: records each call, so a registration for a bot's path events shows.</summary>
    public class BotRecorder : System.Reflection.DispatchProxy
    {
        public readonly ConcurrentQueue<string> Calls = new();
        protected override object Invoke(System.Reflection.MethodInfo method, object[] args)
        {
            Calls.Enqueue(method.Name);
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    /// <summary>Switch a quiet script from Phlox to YEngine and wait for YEngine's instance, without pumping Phlox's loader.</summary>
    private static void SwitchQuietScriptToYEngine(Rig rig)
    {
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        Assert.True(System.Threading.SpinWait.SpinUntil(() => rig.InYEngine && rig.Entries >= 2, Cap), rig.Text);
        // Phlox still has its instance: its unload is queued and has not run.
        Assert.True(rig.InPhlox, rig.Text);
    }

    /// <summary>Everything a script can set up under its item id, as one line.</summary>
    private static string SetUps(Rig rig, ScenePresence sp, BotRecorder bots)
    {
        UUID item = rig.Item.ItemID;
        var phlox = rig.H.Engine;
        return $"urls={rig.UrlModule.Of(item)} http={rig.HttpModule.Started.Count} channels={rig.Xml.ChannelByItem.Count} " +
               $"remoteData={rig.Xml.RemoteDataSent} controls={sp.HasScriptControls(item)} targets={rig.Group.AtTargets.Count} " +
               $"rotTargets={rig.Group.RotTargets.Count} listens={phlox.ListenManager.ScriptsWithListens} " +
               $"sensors={phlox.AsyncCommands.SensorRepeatPlugin.RepeatersFor(item)} mask={rig.Group.RootPart.ScriptEvents} " +
               $"perms={rig.Item.PermsGranter}/{rig.Item.PermsMask} bots={bots.Calls.Count}";
    }

    [Theory]
    [InlineData("URL")]
    [InlineData("secure URL")]
    [InlineData("OSSL URL")]
    [InlineData("HTTP request")]
    [InlineData("XML-RPC channel")]
    [InlineData("remote data request")]
    [InlineData("taken controls")]
    [InlineData("permissions")]
    [InlineData("target")]
    [InlineData("rotation target")]
    [InlineData("listen")]
    [InlineData("sensor")]
    [InlineData("event mask")]
    [InlineData("bot path events")]
    public void AnOldPhloxInstanceSetsNothingUpOnceYEngineHoldsTheItem(string what)
    {
        using var rig = new Rig(osFunctions: what == "OSSL URL");
        var bots = System.Reflection.DispatchProxy.Create<IBotManager, BotRecorder>();
        rig.H.Scene.RegisterModuleInterface<IBotManager>(bots);
        var recorder = (BotRecorder)(object)bots;
        rig.Build(ScriptEngineChoices.Phlox, QuietBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);
        var sp = SceneHelpers.AddScenePresence(rig.H.Scene, UUID.Random());
        var client = (TestClient)sp.ControllingClient;

        SwitchQuietScriptToYEngine(rig);
        var api = rig.H.ApiFor(rig.Item.ItemID);
        Assert.NotNull(api);
        if (what == "taken controls")
        {
            // The avatar granted TAKE_CONTROLS to the item (to YEngine's instance, which holds it now).
            rig.Item.PermsGranter = sp.UUID;
            rig.Item.PermsMask = 4;
        }
        string before = SetUps(rig, sp, recorder);

        switch (what)
        {
            case "URL": api.llRequestURL(); break;
            case "secure URL": api.llRequestSecureURL(); break;
            case "OSSL URL": api.osRequestURL(new InWorldz.Phlox.Types.LSLList()); break;
            case "HTTP request": api.llHTTPRequest("http://example.org/", new InWorldz.Phlox.Types.LSLList(), ""); break;
            case "XML-RPC channel": api.llOpenRemoteDataChannel(); break;
            case "remote data request": api.llSendRemoteData(UUID.Random().ToString(), "http://example.org/", 0, ""); break;
            case "taken controls": api.llTakeControls(1, 1, 0); break;
            case "permissions":
                int asked = client.ScriptQuestions.Count;
                api.llRequestPermissions(sp.UUID.ToString(), 16);
                if (client.ScriptQuestions.Count > asked) client.FireScriptAnswer(rig.Group.RootPart.UUID, rig.Item.ItemID, 16);
                break;
            case "target": api.llTarget(new Vector3(1, 2, 3), 1f); break;
            case "rotation target": api.llRotTarget(Quaternion.Identity, 0.1f); break;
            case "listen": api.llListen(5, "", "", ""); break;
            case "sensor": api.llSensorRepeat("", "", 1, 10f, 3.14f, 5f); break;
            case "event mask":
                // The old instance stopped (as by its owner): its mask would be none, which would take YEngine's away.
                object interp = rig.H.InterpreterFor(rig.Item.ItemID);
                object state = interp.GetType().GetProperty("ScriptState")!.GetValue(interp)!;
                state.GetType().GetField("GeneralEnable")!.SetValue(state, false);
                api.SetScriptEventFlags();
                break;
            case "bot path events": api.botRegisterForNavigationEvents(UUID.Random().ToString()); break;
        }

        string after = SetUps(rig, sp, recorder);
        Assert.True(before == after, $"{what} set up by the old instance: before [{before}] after [{after}]; {rig.Text}");
    }

    [Fact]
    public void AnEventPostedToTheOldPhloxInstanceAfterTheSwitchDoesNotRun()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, QuietBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        SwitchQuietScriptToYEngine(rig);
        rig.H.PostTouch(rig.Item.ItemID);
        // A window that proves the event did NOT run: Phlox's scheduler is pumped, its loader is not.
        rig.H.PumpExeFor(TimeSpan.FromSeconds(1.5));

        Assert.True(rig.InPhlox, rig.Text);
        Assert.False(rig.H.Said.Contains("go"), "the old instance ran the touch: " + rig.Text);
        Assert.Equal(0, rig.UrlModule.Of(rig.Item.ItemID));
    }

    [Fact]
    public void AnEventTheOldPhloxInstanceIsRunningStopsAtTheSwitch()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, QuietBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        // The touch runs to its llSleep, then the script moves to YEngine.
        rig.H.PostTouch(rig.Item.ItemID);
        Assert.True(rig.H.PumpExeUntil(() => rig.H.Said.Contains("go"), Cap), rig.Text);
        SwitchQuietScriptToYEngine(rig);
        // Well past the sleep: the rest of the event must not run.
        rig.H.PumpExeFor(TimeSpan.FromSeconds(1.5));

        Assert.True(rig.InPhlox, rig.Text);
        Assert.False(rig.H.Said.Contains("late"), "the old instance went on with its event: " + rig.Text);
        Assert.Equal(0, rig.UrlModule.Of(rig.Item.ItemID));
    }

    [Fact]
    public void AResetOfTheOldPhloxInstanceLeavesTheItemsPermissionsAlone()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, QuietBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        SwitchQuietScriptToYEngine(rig);
        // An avatar granted YEngine's instance TRIGGER_ANIMATION.
        UUID granter = UUID.Random();
        rig.Item.PermsGranter = granter;
        rig.Item.PermsMask = 16;
        rig.H.ResetOnScheduler(rig.Item.ItemID);
        rig.H.PumpExeFor(TimeSpan.FromSeconds(1));

        Assert.True(rig.InPhlox, rig.Text);
        Assert.Equal(granter, rig.Item.PermsGranter);
        Assert.Equal(16, rig.Item.PermsMask);
    }

    // The responses that come back for YEngine's instance in that window reach it: Phlox's pumps of the core's shared
    // queues do not take them for Phlox's let-go instance.
    private const string AnswersBody =
        "default {\n" +
        "  state_entry() { llHTTPRequest(\"http://example.org/\", [], \"\"); llSay(0, \"entry\"); }\n" +
        "  http_response(key id, integer status, list meta, string body) { llSay(0, \"response \" + body); }\n" +
        "  remote_data(integer type, key channel, key message, string sender, integer i, string s) {\n" +
        "    if (type == 2) llSay(0, \"remote \" + s);\n" +
        "  }\n" +
        "}\n";

    [Fact]
    public void AnHttpResponseForYEnginesInstanceReachesItThroughPhloxsPump()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, AnswersBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        int mark = rig.HttpMark();
        SwitchQuietScriptToYEngine(rig);
        Assert.True(System.Threading.SpinWait.SpinUntil(() => rig.HttpSince(mark).Count > 0, Cap), rig.Text);
        Assert.True(rig.HttpModule.InFlight.TryGetValue(rig.HttpSince(mark)[0], out Req yRequest), rig.Text);

        // YEngine's request completes, and Phlox's pump is the one that takes it from the core's queue.
        yRequest.ResponseBody = "ok";
        rig.HttpModule.PumpThread = Environment.CurrentManagedThreadId;
        rig.HttpModule.Completed.Enqueue(yRequest);
        rig.H.Engine.AsyncCommands.HttpRequestPlugin.CheckHttpRequests();

        Assert.True(System.Threading.SpinWait.SpinUntil(() => rig.H.Said.Contains("response ok"), Cap),
            "YEngine's instance never got its http_response: " + rig.Text);
    }

    [Fact]
    public void RemoteDataForYEnginesInstanceReachesItThroughPhloxsPump()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, AnswersBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        SwitchQuietScriptToYEngine(rig);

        // An XML-RPC request comes in for the script, and Phlox's pump is the one that takes it from the core's queue.
        rig.Xml.PumpThread = Environment.CurrentManagedThreadId;
        rig.Xml.Completed.Enqueue(new Incoming { Item = rig.Item.ItemID, Channel = UUID.Random(), Text = "hello" });
        rig.H.Engine.AsyncCommands.XmlRequestPlugin.CheckXMLRPCRequests();

        Assert.True(System.Threading.SpinWait.SpinUntil(() => rig.H.Said.Contains("remote hello"), Cap),
            "YEngine's instance never got its remote_data: " + rig.Text);
    }

    // The same holds when no other engine takes the item, and a later instance of the item runs as usual.

    [Fact]
    public void AnInstanceTheRegionRemovedFromPhloxSetsNothingUpBeforeItsUnload()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, QuietBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);
        var api = rig.H.ApiFor(rig.Item.ItemID);
        Assert.NotNull(api);

        // The owner deletes the script: the region removes it from Phlox, and no engine holds the item.
        rig.Group.RootPart.Inventory.RemoveInventoryItem(rig.Item.ItemID);
        Assert.Null(rig.H.Scene.ScriptItemHolders.HolderOf(rig.Item.ItemID));
        Assert.True(rig.InPhlox, rig.Text);   // its unload is queued and has not run

        api.llRequestURL();
        api.llHTTPRequest("http://example.org/", new InWorldz.Phlox.Types.LSLList(), "");
        rig.H.PostTouch(rig.Item.ItemID);
        rig.H.PumpExeFor(TimeSpan.FromSeconds(1));

        Assert.Equal(0, rig.UrlModule.Of(rig.Item.ItemID));
        Assert.Empty(rig.HttpModule.Started);
        Assert.False(rig.H.Said.Contains("go"), "the removed instance ran the touch: " + rig.Text);
    }

    [Fact]
    public void ASavedPhloxScriptRunsItsNewInstanceOnceTheOldOneIsUnloaded()
    {
        using var h = new SchedulerHarness();
        UUID item = h.RezScript(QuietBody);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry"), Cap), string.Join(" | ", h.Said));

        // A save removes the script from Phlox and starts it again, there.
        h.ResaveScript(item, QuietBody.Replace("\"entry\"", "\"entry again\""));
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry again") && !h.Engine.HasLetGo(item), Cap),
            $"letGo={h.Engine.HasLetGo(item)} said=[{string.Join(" | ", h.Said)}]");

        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("go"), Cap), "the new instance did not run the touch: " + string.Join(" | ", h.Said));
    }

    [Fact]
    public void AScriptSwitchedAwayAndBackBeforeTheUnloadRunsInPhloxAgain()
    {
        using var rig = new Rig();
        rig.Build(ScriptEngineChoices.Phlox, QuietBody);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && rig.Entries >= 1, Cap), rig.Text);

        object first = rig.H.InterpreterFor(rig.Item.ItemID);
        // Both removals come before Phlox's loader runs, so two let-go unloads are queued ahead of the new load.
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));
        Assert.True(rig.H.Engine.HasLetGo(rig.Item.ItemID), rig.Text);
        Assert.True(rig.H.PumpUntil(() => rig.InPhlox && !rig.InYEngine && !rig.H.Engine.HasLetGo(rig.Item.ItemID)
                                           && rig.H.InterpreterFor(rig.Item.ItemID) is { } now && !ReferenceEquals(now, first), Cap), rig.Text);

        rig.H.PostTouch(rig.Item.ItemID);
        Assert.True(rig.H.PumpUntil(() => rig.H.Said.Contains("go"), Cap), "the new instance did not run the touch: " + rig.Text);
        Assert.True(rig.H.PumpUntil(() => rig.UrlModule.Of(rig.Item.ItemID) > 0, Cap), "the new instance's URL was refused: " + rig.Text);
    }
}
