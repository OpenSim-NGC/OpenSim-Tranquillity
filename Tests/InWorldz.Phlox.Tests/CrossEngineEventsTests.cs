using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// object_rez, email, linkset_data and osMessageObject's dataserver, raised by a Phlox script, reach every script SL names,
/// whatever engine runs it, once each, and no other.
/// SL wiki object_rez: "Triggers in all running scripts with an object_rez event, AND in the same prim as the script
/// calling llRezObject or llRezAtRoot. Does NOT trigger in linked prims."
/// SL wiki email: "The email queue is associated with the prim and any script in the prim can access it." (posted to the
/// calling script's prim, as OpenSimulator's LSL_Api does.)
/// SL wiki linkset_data: "The linkset_data event fires in all scripts in a linkset whenever the datastore has been modified
/// through a call to one of the llLinksetData functions."
/// SL wiki dataserver: "Dataserver requests will trigger dataserver events in all scripts within the same prim where the
/// request was made." OSSL osMessageObject raises it on every script of the target prim.
/// The shared pieces: a three-prim linkset (links 1, 2, 3) with a Phlox driver D and a Phlox hearer P1 in link 1 and a
/// Phlox hearer P2 in link 2. Each script says "&lt;tag&gt; &lt;event&gt; &lt;values&gt;" for every such event it gets.
/// </summary>
internal sealed class EventRig : IDisposable
{
    public const string Key = LinkMessageRig.Key;
    public readonly DataserverToPrimTests.Rig R;
    public readonly SceneObjectPart[] Parts;
    public readonly Dictionary<string, UUID> Items = new();
    public static readonly string[] Phlox = { "D", "P1", "P2" };

    public EventRig(bool withYEngine = false)
    {
        R = new DataserverToPrimTests.Rig(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"), withYEngine);
        Parts = LinkMessageRig.ThreePrims(R.H);
        TaskInventoryHelpers.AddSceneObject(R.H.Scene.AssetService, Parts[0], "child", UUID.Random(), R.H.Prim.OwnerID);
        R.H.Scene.RegisterModuleInterface<IEmailModule>(Fake<IEmailModule>.Create((m, a) =>
            m.Name == "GetNextEmail"
                ? new Email { time = "1700000000", sender = "sender@example.com", subject = "subj", message = "body", numLeft = 2 }
                : null));
        Items["D"] = R.Rez(Parts[0], "d driver", Hearer("D", driver: true));
        Items["P1"] = R.Rez(Parts[0], "p1 hearer", Hearer("P1"));
        Items["P2"] = R.Rez(Parts[1], "p2 hearer", Hearer("P2"));
    }

    public void WaitStarted(IEnumerable<string> tags)
        => Assert.True(R.PumpUntil(() => tags.All(t => R.Count(t + " entry") == 1) && R.H.Engine.ListenManager.ListenCount == 1),
            "not all started: " + string.Join(" | ", R.H.Said));

    /// <summary>The driver (D) takes commands on channel 7 and says "D did &lt;command&gt;" after the call returns.</summary>
    public static string Hearer(string tag, bool driver = false) => @"
        default {
            state_entry() {" + (driver ? @" llListen(7, """", NULL_KEY, """");" : "") + @" llSay(0, """ + tag + @" entry""); }" + (driver ? @"
            listen(integer c, string n, key k, string m) {
                if (m == ""rez"") llRezObject(""child"", llGetPos() + <0,0,1>, ZERO_VECTOR, ZERO_ROTATION, 0);
                else if (m == ""mail"") llGetNextEmail("""", """");
                else if (m == ""lsd"") llLinksetDataWrite(""color"", ""blue"");
                else if (m == ""msg"") osMessageObject(llGetLinkKey(2), ""ping"");
                else if (llGetSubString(m, 0, 3) == ""bot "") botMessageLinked(llGetSubString(m, 4, -1), 42, ""hello"", """ + Key + @""");
                llSay(0, ""D did "" + m);
            }" : "") + @"
            object_rez(key id) { llSay(0, """ + tag + @" rez "" + (string)id); }
            email(string t, string a, string s, string m, integer n) { llSay(0, """ + tag + @" email "" + t + "" "" + a + "" "" + s + "" "" + m + "" "" + (string)n); }
            linkset_data(integer a, string k, string v) { llSay(0, """ + tag + @" lsd "" + (string)a + "" "" + k + "" "" + v); }
            dataserver(key q, string d) { llSay(0, """ + tag + @" ds "" + (string)q + "" "" + d); }
            link_message(integer s, integer n, string m, key k) { llSay(0, """ + tag + @" link "" + (string)s + "" "" + (string)n + "" "" + m + "" "" + (string)k); }
        }";

    /// <summary>
    /// Drive <paramref name="command"/>; each tag of <paramref name="expected"/> says "&lt;tag&gt; &lt;evt&gt; ..." exactly once,
    /// every other tag of <paramref name="all"/> never, after a quiet window for late or duplicate deliveries. All say the
    /// same values; returns them.
    /// </summary>
    public string Fire(ITestOutputHelper output, string command, string evt, string[] all, string[] expected)
    {
        int before = R.Count("D did " + command);
        R.Say(command);
        Assert.True(R.PumpUntil(() => R.Count("D did " + command) == before + 1 && expected.All(t => R.CountStart(t + " " + evt + " ") >= 1)),
            "not every named script got " + evt + ": " + string.Join(" | ", R.H.Said));
        R.Quiet(800, all.Select(t => t + " " + evt + " ").ToArray());
        output.WriteLine(string.Join(" | ", R.H.Said));
        foreach (var t in all)
            Assert.True((expected.Contains(t) ? 1 : 0) == R.CountStart(t + " " + evt + " "),
                t + " got " + evt + " the wrong number of times: " + string.Join(" | ", R.H.Said));
        var values = expected.Select(t => R.H.Said.Single(s => s.StartsWith(t + " " + evt + " ")).Substring(t.Length + evt.Length + 2)).Distinct().ToList();
        Assert.True(values.Count == 1, "the scripts got different values: " + string.Join(" | ", values));
        return values[0];
    }

    // command, event word, the links (1-based) SL names, the values every script must say ("" = the rezzed key, checked apart).
    public static TheoryData<string, string, int[], string> Cases => new()
    {
        { "rez",  "rez",   new[] { 1 },       "" },
        { "mail", "email", new[] { 1 },       "1700000000 sender@example.com subj body 2" },
        { "lsd",  "lsd",   new[] { 1, 2, 3 }, "1 color blue" },
        { "msg",  "ds",    new[] { 2 },       null },   // the driver's prim key, then "ping"
    };

    public string Expected(string values) => values ?? Parts[0].UUID + " ping";

    public void CheckValues(string evt, string said, string values)
    {
        if (evt == "rez")
        {
            var rezzed = R.H.Scene.GetSceneObjectGroups().Single(g => g.UUID != Parts[0].ParentGroup.UUID);
            Assert.Equal(rezzed.RootPart.UUID.ToString(), said);
        }
        else Assert.Equal(Expected(values), said);
    }

    /// <summary>The plain values another engine is offered, by event.</summary>
    public object[] Plain(string evt, string values) => evt switch
    {
        "rez" => new object[] { R.H.Scene.GetSceneObjectGroups().Single(g => g.UUID != Parts[0].ParentGroup.UUID).RootPart.UUID.ToString() },
        "email" => new object[] { "1700000000", "sender@example.com", "subj", "body", 2 },
        "lsd" => new object[] { 1, "color", "blue" },
        _ => new object[] { Parts[0].UUID.ToString(), "ping" },
    };

    public static string EventName(string evt) => evt switch
    {
        "rez" => "object_rez", "email" => "email", "lsd" => "linkset_data", _ => "dataserver",
    };

    public static string[] PhloxIn(int[] links)
        => Phlox.Where(t => links.Contains(t == "P2" ? 2 : 1)).ToArray();

    public void Dispose() => R.Dispose();
}

/// <summary>A generated stand-in for an interface: every call goes to <c>answer</c>.</summary>
public class Fake<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object[], object> m_answer;

    public static T Create(Func<MethodInfo, object[], object> answer)
    {
        var t = Create<T, Fake<T>>();
        ((Fake<T>)(object)t).m_answer = answer;
        return t;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        var r = m_answer(targetMethod, args);
        if (r != null) return r;
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>
/// With YEngine hosted beside Phlox (the "phlox-yengine" collection: YEngine keeps compiler state in statics).
/// YEngine: Y1 in link 1, Y2 in link 2, Y3 in link 3.
/// </summary>
[Collection("phlox-yengine")]
public class CrossEngineEventsYEngineTests
{
    private readonly ITestOutputHelper _out;
    public CrossEngineEventsYEngineTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] All = { "D", "P1", "P2", "Y1", "Y2", "Y3" };

    [Theory]
    [MemberData(nameof(EventRig.Cases), MemberType = typeof(EventRig))]
    public void APhloxEventReachesEveryScriptSLNamesOfEitherEngineOnce(string command, string evt, int[] links, string values)
    {
        using var r = new EventRig(withYEngine: true);
        SchedulerHarnessYEngine.Rez(r.R.H, r.Parts[0], EventRig.Hearer("Y1"));
        SchedulerHarnessYEngine.Rez(r.R.H, r.Parts[1], EventRig.Hearer("Y2"));
        SchedulerHarnessYEngine.Rez(r.R.H, r.Parts[2], EventRig.Hearer("Y3"));
        r.WaitStarted(All);

        var expected = EventRig.PhloxIn(links).Concat(links.Select(l => "Y" + l)).ToArray();
        var said = r.Fire(_out, command, evt, All, expected);
        r.CheckValues(evt, said, values);
    }
}

/// <summary>
/// Other engines stood in by generated IScriptModule + IScriptEngine instances registered on each test's own scene, and
/// scripts no engine runs as their items; so the class runs in parallel (no process-wide state). No network.
/// </summary>
public class CrossEngineEventsOtherEngineTests
{
    private readonly ITestOutputHelper _out;
    public CrossEngineEventsOtherEngineTests(ITestOutputHelper o) => _out = o;

    /// <summary>A script item in each prim (link 1, 2, 3) that only the stand-in engine would run.</summary>
    private static UUID[] OtherItems(EventRig r)
        => r.Parts.Select(p =>
        {
            var y = UUID.Random();
            TaskInventoryHelpers.AddScript(r.R.H.Scene.AssetService, p, y, UUID.Random(), "y item", "default { }");
            return y;
        }).ToArray();

    private static UUID[] ItemsIn(EventRig r, UUID[] others, int[] links)
        => links.SelectMany(l => EventRig.Phlox.Where(t => (t == "P2" ? 2 : 1) == l).Select(t => r.Items[t]).Append(others[l - 1]))
            .OrderBy(i => i).ToArray();

    [Theory]
    [MemberData(nameof(EventRig.Cases), MemberType = typeof(EventRig))]
    public void APhloxOnlyRegionDeliversToPhloxScriptsExactlyAsBefore(string command, string evt, int[] links, string values)
    {
        using var r = new EventRig();
        r.WaitStarted(EventRig.Phlox);
        Assert.DoesNotContain(r.R.H.Scene.RequestModuleInterfaces<IScriptModule>(), m => !ReferenceEquals(m, r.R.H.Engine));
        var said = r.Fire(_out, command, evt, EventRig.Phlox, EventRig.PhloxIn(links));
        r.CheckValues(evt, said, values);
    }

    [Theory]
    [MemberData(nameof(EventRig.Cases), MemberType = typeof(EventRig))]
    public void AnotherEngineIsOfferedEachScriptSLNamesOnceWithPlainValues(string command, string evt, int[] links, string values)
    {
        using var r = new EventRig();
        r.WaitStarted(EventRig.Phlox);
        var others = OtherItems(r);
        var other = StandInEngine.Create(throws: false);
        r.R.H.Scene.StackModuleInterface<IScriptModule>(other);

        var said = r.Fire(_out, command, evt, EventRig.Phlox, EventRig.PhloxIn(links));
        r.CheckValues(evt, said, values);

        var posts = StandInEngine.Of(other).Posts;
        Assert.Equal(ItemsIn(r, others, links), posts.Select(p => p.Item).OrderBy(i => i));
        var plain = r.Plain(evt, values);
        foreach (var (_, parms) in posts)
        {
            Assert.Equal(EventRig.EventName(evt), parms.EventName);
            Assert.Equal(plain, parms.Params);
            Assert.Equal(plain.Select(p => p.GetType()), parms.Params.Select(p => p.GetType()));
        }
        Assert.Equal(posts.Count, posts.Select(p => p.Parms.Params).Distinct().Count());   // a fresh array per post
        Assert.Empty(StandInEngine.Of(other).ObjectPosts);
    }

    [Theory]
    [MemberData(nameof(EventRig.Cases), MemberType = typeof(EventRig))]
    public void AnEngineThatThrowsAffectsNobodyElse(string command, string evt, int[] links, string values)
    {
        using var r = new EventRig();
        r.WaitStarted(EventRig.Phlox);
        var others = OtherItems(r);
        var thrower = StandInEngine.Create(throws: true);
        var after = StandInEngine.Create(throws: false);
        r.R.H.Scene.StackModuleInterface<IScriptModule>(thrower);
        r.R.H.Scene.StackModuleInterface<IScriptModule>(after);

        var said = r.Fire(_out, command, evt, EventRig.Phlox, EventRig.PhloxIn(links));   // "D did": nothing reached the caller
        r.CheckValues(evt, said, values);

        Assert.Equal(ItemsIn(r, others, links), StandInEngine.Of(thrower).Posts.Select(p => p.Item).OrderBy(i => i));
        Assert.Equal(ItemsIn(r, others, links), StandInEngine.Of(after).Posts.Select(p => p.Item).OrderBy(i => i));
    }

    /// <summary>
    /// botMessageLinked (a Phlox extension, from Halcyon) posts link_message to every script of every attachment of the
    /// bot. A bot's attachments are rezzed like any avatar's, so their scripts may be another engine's: that engine is
    /// offered each of those items once, with plain values; a throwing engine affects nobody else.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BotMessageLinkedReachesTheBotsAttachmentScriptsOfEveryEngine(bool withEngines)
    {
        using var r = new EventRig();
        var bot = UUID.Random();
        r.R.H.Scene.RegisterModuleInterface<IBotManager>(Fake<IBotManager>.Create((m, a) => m.Name == "CheckPermission" ? (object)true : null));
        var sp = SceneHelpers.AddScenePresence(r.R.H.Scene, bot);
        var att = SceneHelpers.AddSceneObject(r.R.H.Scene, "worn", bot);
        sp.AddAttachment(att);
        var b = r.R.Rez(att.RootPart, "b hearer", EventRig.Hearer("B"));
        var y = UUID.Random();
        TaskInventoryHelpers.AddScript(r.R.H.Scene.AssetService, att.RootPart, y, UUID.Random(), "y item", "default { }");
        var all = EventRig.Phlox.Append("B").ToArray();
        r.WaitStarted(all);

        IStandInScriptEngine thrower = null, after = null;
        if (withEngines)
        {
            thrower = StandInEngine.Create(throws: true);
            after = StandInEngine.Create(throws: false);
            r.R.H.Scene.StackModuleInterface<IScriptModule>(thrower);
            r.R.H.Scene.StackModuleInterface<IScriptModule>(after);
        }

        var said = r.Fire(_out, "bot " + bot, "link", all, new[] { "B" });
        Assert.Equal("1 42 hello " + EventRig.Key, said);
        if (!withEngines) return;

        var expected = new[] { b, y }.OrderBy(i => i);
        Assert.Equal(expected, StandInEngine.Of(thrower).Posts.Select(p => p.Item).OrderBy(i => i));
        var posts = StandInEngine.Of(after).Posts;
        Assert.Equal(expected, posts.Select(p => p.Item).OrderBy(i => i));
        foreach (var (_, parms) in posts)
        {
            Assert.Equal("link_message", parms.EventName);
            Assert.Equal(new object[] { 1, 42, "hello", EventRig.Key }, parms.Params);
            Assert.Equal(new[] { typeof(int), typeof(int), typeof(string), typeof(string) }, parms.Params.Select(p => p.GetType()));
        }
        Assert.Equal(posts.Count, posts.Select(p => p.Parms.Params).Distinct().Count());
    }

    /// <summary>
    /// A dataserver answer to a Phlox script is offered to each other engine for the asking prim (PostDataserverToPrim);
    /// an engine that throws is logged and contained: the engines after it are still offered it, and nothing reaches the
    /// asking script's call.
    /// </summary>
    [Fact]
    public void ADataserverOfferToAThrowingEngineAffectsNobodyElse()
    {
        using var r = new DataserverToPrimTests.Rig();
        var gate = r.SlowNotecard("only line");
        gate.Release.Set();
        r.Rez(r.H.Prim, "a asker", DataserverToPrimTests.Asker(@"llGetNotecardLine(""card"", 0)"));
        r.Rez(r.H.Prim, "b hearer", DataserverToPrimTests.Hearer("B"));
        Assert.True(r.PumpUntil(() => r.Count("A entry") == 1 && r.Count("B entry") == 1 && r.H.Engine.ListenManager.ListenCount == 2));
        var thrower = StandInEngine.Create(throws: true);
        var after = StandInEngine.Create(throws: false);
        r.H.Scene.StackModuleInterface<IScriptModule>(thrower);
        r.H.Scene.StackModuleInterface<IScriptModule>(after);

        r.Say("ask");
        Assert.True(r.PumpUntil(() => r.CountStart("A asked ") == 1), "A did not ask: " + string.Join(" | ", r.H.Said));
        string key = r.AskedKey();
        Assert.True(r.PumpUntil(() => r.Count("A got " + key + " only line") == 1 && r.Count("B got " + key + " only line") == 1
                                      && StandInEngine.Of(after).ObjectPosts.Count == 1),
            "not everyone got it: " + string.Join(" | ", r.H.Said));
        r.Quiet(500, "A got", "B got");
        _out.WriteLine(string.Join(" | ", r.H.Said));
        Assert.Equal(1, r.CountStart("A got"));
        Assert.Equal(1, r.CountStart("B got"));
        Assert.DoesNotContain(r.H.Said, s => s.Contains("could not be found"));   // the engine's exception is not the asker's error
        foreach (var e in new[] { thrower, after })
        {
            var (id, parms) = Assert.Single(StandInEngine.Of(e).ObjectPosts);
            Assert.Equal(r.H.Prim.LocalId, id);
            Assert.Equal("dataserver", parms.EventName);
            Assert.Equal(new object[] { key, "only line" }, parms.Params.Select(p => (object)p.ToString()));
        }
    }
}
