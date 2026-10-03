using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A dataserver answer goes to every script in the asking script's prim, each with
/// the same query key and data. SL wiki dataserver: "Dataserver requests will trigger dataserver events in all scripts
/// within the same prim where the request was made." "dataserver events will not be triggered in scripts contained in
/// other prims in the same linked object." The wiki says nothing of a reset, state change or removal before the answer,
/// so the reset rule is kept for the asker (an answer it is no longer owed is not posted to it) and the others still get
/// it. The cross-engine case (a YEngine script in the prim) is DataserverToPrimYEngineTests.
/// Each test has its own harness and scene (the notecard gate is that scene's asset service, the key-value fake is
/// registered on that scene only), so the class runs in parallel. No network.
/// </summary>
public class DataserverToPrimTests
{
    private readonly ITestOutputHelper _out;
    public DataserverToPrimTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// The asker: channel 7 drives it. "ask": the request, its key said as "A asked &lt;key&gt;". "reset": llResetScript.
    /// Every dataserver event it gets is said as "A got &lt;key&gt; &lt;data&gt;".
    /// </summary>
    internal static string Asker(string request) => @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""A entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""ask"") { key q = " + request + @"; llSay(0, ""A asked "" + (string)q); }
                else if (m == ""reset"") llResetScript();
            }
            dataserver(key id, string d) { llSay(0, ""A got "" + (string)id + "" "" + d); }
        }";

    /// <summary>Another script: says "&lt;tag&gt; got &lt;key&gt; &lt;data&gt;" for every dataserver event; "go" on 7 moves it to
    /// state two, where it says "&lt;tag&gt;2 got ...".</summary>
    internal static string Hearer(string tag) => @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, """ + tag + @" entry""); }
            listen(integer c, string n, key k, string m) { if (m == ""go"") state two; }
            dataserver(key id, string d) { llSay(0, """ + tag + @" got "" + (string)id + "" "" + d); }
        }
        state two {
            state_entry() { llSay(0, """ + tag + @" in two""); }
            dataserver(key id, string d) { llSay(0, """ + tag + @"2 got "" + (string)id + "" "" + d); }
        }";

    internal sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public Rig(Action<Nini.Config.IConfigSource> configure = null, bool withYEngine = false)
            => H = new SchedulerHarness(configure, withYEngine);

        /// <summary>The region's own path (EventManager.OnRezScript), so a delete reaches the engine as on a real region.</summary>
        public UUID Rez(SceneObjectPart part, string name, string source)
        {
            var item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, part, UUID.Random(), UUID.Random(), name, source);
            Assert.True(part.Inventory.CreateScriptInstance(item.ItemID, 0, false, H.Engine.Name, 0), "the core did not start " + name);
            return item.ItemID;
        }

        public SceneObjectPart OtherPrim() => SceneHelpers.AddSceneObject(H.Scene, "other prim", UUID.Random()).RootPart;

        public void Say(string msg)
            => H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

        public int Count(string line) => H.Said.Count(s => s == line);
        public int CountStart(string prefix) => H.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));

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

        /// <summary>A "did NOT happen" window: pump for <paramref name="ms"/> and say nothing new may start with these.</summary>
        public void Quiet(int ms, params string[] prefixes)
        {
            var before = prefixes.ToDictionary(p => p, CountStart);
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (!H.PumpOnceBusy()) System.Threading.Thread.Sleep(2); }
            foreach (var p in prefixes)
                Assert.True(before[p] == CountStart(p), "'" + p + "' was said in the quiet window: " + string.Join(" | ", H.Said));
        }

        /// <summary>The key the asker said it got back from its request.</summary>
        public string AskedKey() => H.Said.Single(s => s.StartsWith("A asked ")).Substring("A asked ".Length);

        /// <summary>A notecard "card" in the prim whose fetch is held until the test lets it go.</summary>
        public ScriptCleanupTests.AssetGate SlowNotecard(string text)
        {
            var nc = AssetHelpers.CreateNotecardAsset(UUID.Random(), text);
            H.Scene.AssetService.Store(nc);
            H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(), AssetID = nc.FullID, Name = "card", Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard,
            }, false);
            return ScriptCleanupTests.AssetGate.Install(H.Scene, nc.FullID);
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>Rez the asker A and hearers B and C in the harness prim and D in another prim; wait for every listen.</summary>
    private static (UUID A, UUID B, UUID C, UUID D) Prim(Rig r, string request)
    {
        var a = r.Rez(r.H.Prim, "a asker", Asker(request));
        var b = r.Rez(r.H.Prim, "b hearer", Hearer("B"));
        var c = r.Rez(r.H.Prim, "c hearer", Hearer("C"));
        var d = r.Rez(r.OtherPrim(), "d other prim", Hearer("D"));
        Assert.True(r.PumpUntil(() => new[] { "A", "B", "C", "D" }.All(t => r.Count(t + " entry") == 1)
                                      && r.H.Engine.ListenManager.ListenCount == 4),
            "not all started: " + string.Join(" | ", r.H.Said));
        return (a, b, c, d);
    }

    /// <summary>Every one of <paramref name="tags"/> said "got &lt;the asked key&gt; &lt;data&gt;" exactly once, and nothing else.</summary>
    private void AllGotTheSame(Rig r, string data, params string[] tags)
    {
        string key = r.AskedKey();
        Assert.True(UUID.TryParse(key, out var k) && k != UUID.Zero, "no query key: " + key);
        Assert.True(r.PumpUntil(() => tags.All(t => r.Count(t + " got " + key + " " + data) == 1)),
            "not every script got the answer: " + string.Join(" | ", r.H.Said));
        r.Quiet(800, "D got", "D2 got");                 // never a script of another prim; no duplicate lands late
        _out.WriteLine(string.Join(" | ", r.H.Said));
        foreach (var t in tags)
            Assert.Equal(1, r.CountStart(t + " got "));
    }

    [Fact]
    public void ANotecardLineReachesEveryScriptInThePrimWithTheSameKeyAndData()
    {
        using var r = new Rig();
        var gate = r.SlowNotecard("line zero\nline one");
        Prim(r, @"llGetNotecardLine(""card"", 1)");
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1 && gate.Waiting == 1), "the fetch never started");
        gate.Release.Set();
        AllGotTheSame(r, "line one", "A", "B", "C");
    }

    [Fact]
    public void AgentDataReachesEveryScriptInThePrim()
    {
        using var r = new Rig();
        var client = r.H.AddClient();
        var sp = r.H.Scene.GetScenePresence(client.AgentId);
        Prim(r, $"llRequestAgentData(\"{client.AgentId}\", DATA_NAME)");
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1));
        AllGotTheSame(r, sp.Name, "A", "B", "C");
    }

    [Fact]
    public void UsernameRequestReachesEveryScriptInThePrimOnTheOnePath()
    {
        using var r = new Rig();
        var client = r.H.AddClient();
        var sp = r.H.Scene.GetScenePresence(client.AgentId);
        Prim(r, $"llRequestUsername(\"{client.AgentId}\")");
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1));
        AllGotTheSame(r, sp.Name, "A", "B", "C");
    }

    [Fact]
    public void AKeyValueReadReachesEveryScriptInThePrim()
    {
        var exp = new UUID("66666666-0000-4000-8000-00000000e066");
        var store = new KeyValueSlFormTests.FakeStore();
        store.Data[exp] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["colour"] = "teal" };
        using var r = new Rig();
        r.H.Scene.RegisterModuleInterface<IExperienceService>(store);
        var (a, _, _, _) = Prim(r, @"llReadKeyValue(""colour"")");
        r.H.Prim.Inventory.GetInventoryItem(a).ExperienceID = exp;
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1));
        AllGotTheSame(r, "1,teal", "A", "B", "C");
    }

    [Fact]
    public void AnAnswerAfterTheAskerWasResetGoesToTheOthersOnly()
    {
        using var r = new Rig();
        var gate = r.SlowNotecard("line zero\nline one");
        Prim(r, @"llGetNotecardLine(""card"", 0)");
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1 && gate.Waiting == 1), "the fetch never started");
        r.Say("reset");
        Assert.True(r.PumpUntil(() => r.Count("A entry") == 2 && r.H.Engine.ListenManager.ListenCount == 4), "A was not reset");
        gate.Release.Set();                              // the answer arrives after the reset
        string key = r.AskedKey();
        Assert.True(r.PumpUntil(() => r.Count("B got " + key + " line zero") == 1 && r.Count("C got " + key + " line zero") == 1),
            "the other scripts did not get it: " + string.Join(" | ", r.H.Said));
        r.Quiet(1000, "A got", "D got");                 // Not to the reset asker; never another prim
        Assert.Equal(0, r.CountStart("A got"));
    }

    [Fact]
    public void AnAnswerAfterTheAskerWasRemovedGoesToTheOthersOnly()
    {
        using var r = new Rig();
        var gate = r.SlowNotecard("line zero\nline one");
        var (a, _, _, _) = Prim(r, @"llGetNumberOfNotecardLines(""card"")");
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1 && gate.Waiting == 1), "the fetch never started");
        r.H.Prim.Inventory.RemoveInventoryItem(a);
        Assert.True(r.PumpUntil(() => r.H.Engine.ListenManager.ListenCount == 3), "A was not removed");
        gate.Release.Set();                              // the answer arrives after the delete
        string key = r.AskedKey();
        Assert.True(r.PumpUntil(() => r.Count("B got " + key + " 2") == 1 && r.Count("C got " + key + " 2") == 1),
            "the other scripts did not get it: " + string.Join(" | ", r.H.Said));
        r.Quiet(1000, "A got", "D got");
    }

    [Fact]
    public void AnotherScriptThatChangedStateGetsItInItsNewState()
    {
        using var r = new Rig();
        var gate = r.SlowNotecard("line zero\nline one");
        Prim(r, @"llGetNotecardLine(""card"", 1)");
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1 && gate.Waiting == 1), "the fetch never started");
        r.Say("go");                                     // B, C (and D, in its own prim) change state; A ignores it
        Assert.True(r.PumpUntil(() => r.Count("B in two") == 1 && r.Count("C in two") == 1));
        gate.Release.Set();
        string key = r.AskedKey();
        Assert.True(r.PumpUntil(() => r.Count("A got " + key + " line one") == 1 && r.Count("B2 got " + key + " line one") == 1
                                      && r.Count("C2 got " + key + " line one") == 1),
            "not every script got it: " + string.Join(" | ", r.H.Said));
        r.Quiet(800, "D got", "D2 got", "B got", "C got");
    }

    [Fact]
    public void AnAnswerOwedToNoOneElseStillReachesTheAskerAlone()
    {
        // one script in the prim: it gets its answer once
        using var r = new Rig();
        var gate = r.SlowNotecard("only line");
        r.Rez(r.H.Prim, "a asker", Asker(@"llGetNotecardLine(""card"", 0)"));
        Assert.True(r.PumpUntil(() => r.Count("A entry") == 1 && r.H.Engine.ListenManager.ListenCount == 1));
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1 && gate.Waiting == 1));
        gate.Release.Set();
        string key = r.AskedKey();
        Assert.True(r.PumpUntil(() => r.Count("A got " + key + " only line") == 1));
        r.Quiet(500, "A got");
        Assert.Equal(1, r.CountStart("A got"));
    }
}

/// <summary>
/// Item 3: a YEngine script in the asking Phlox script's prim gets the answer too, through the region's other
/// script engines (IScriptModule that is IScriptEngine, PostObjectEvent(localID, EventParams)) - no core change. A YEngine
/// script in another prim does not. In "phlox-yengine", as CrossEngineChatTests (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class DataserverToPrimYEngineTests
{
    private readonly ITestOutputHelper _out;
    public DataserverToPrimYEngineTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AYEngineScriptInThePrimGetsAPhloxScriptsAnswer()
    {
        using var r = new DataserverToPrimTests.Rig(withYEngine: true);
        var gate = r.SlowNotecard("line zero\nline one");
        r.Rez(r.H.Prim, "a asker", DataserverToPrimTests.Asker(@"llGetNotecardLine(""card"", 1)"));
        r.Rez(r.H.Prim, "b hearer", DataserverToPrimTests.Hearer("B"));
        SchedulerHarnessYEngine.Rez(r.H, r.H.Prim, DataserverToPrimTests.Hearer("Y"));
        SchedulerHarnessYEngine.Rez(r.H, r.OtherPrim(), DataserverToPrimTests.Hearer("Z"));
        Assert.True(r.PumpUntil(() => new[] { "A", "B", "Y", "Z" }.All(t => r.Count(t + " entry") == 1)
                                      && r.H.Engine.ListenManager.ListenCount == 2),
            "not all started: " + string.Join(" | ", r.H.Said));
        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1 && gate.Waiting == 1), "the fetch never started: " + string.Join(" | ", r.H.Said));
        gate.Release.Set();
        string key = r.AskedKey();
        Assert.True(r.PumpUntil(() => new[] { "A", "B", "Y" }.All(t => r.Count(t + " got " + key + " line one") == 1)),
            "not every script got it: " + string.Join(" | ", r.H.Said));
        r.Quiet(800, "Z got", "Y got", "B got", "A got");   // no duplicate lands late; never another prim
        _out.WriteLine(string.Join(" | ", r.H.Said));
        Assert.Equal(1, r.CountStart("Y got "));
        Assert.Equal(0, r.CountStart("Z got"));
    }
}
