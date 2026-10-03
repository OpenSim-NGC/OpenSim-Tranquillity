using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A link message from a Phlox script reaches every script in the targeted prims, whatever engine runs it. SL wiki
/// llMessageLinked: "It triggers a link_message event with the same parameters num, str, and id in all scripts in the
/// prim(s) described by link." LINK_SET "sends to all prims", LINK_ALL_OTHERS "sends to all other prims", LINK_THIS "sends
/// to the prim the script is in". SL wiki link_message: sender_num is "The link number of the prim that contained the
/// script that called llMessageLinked."
/// The shared pieces: a three-prim linkset (links 1, 2, 3), a Phlox sender in link 1 driven on channel 7, and receivers
/// that say "&lt;tag&gt; got &lt;sender&gt; &lt;num&gt; &lt;str&gt; &lt;id&gt;" for every link message.
/// </summary>
internal static class LinkMessageRig
{
    public const int LINK_SET = -1, LINK_ALL_OTHERS = -2, LINK_THIS = -4;
    public const string Key = "11111111-2222-4333-8444-555555555555";

    /// <summary>"send &lt;link&gt;" on channel 7 sends num 42, str "hello", id <see cref="Key"/> to that link, then says "S sent".</summary>
    public static string Sender => @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""S entry""); }
            listen(integer c, string n, key k, string m) {
                if (llGetSubString(m, 0, 4) == ""send "") {
                    llMessageLinked((integer)llGetSubString(m, 5, -1), 42, ""hello"", """ + Key + @""");
                    llSay(0, ""S sent"");
                }
            }
            link_message(integer s, integer n, string m, key k) {
                llSay(0, ""S got "" + (string)s + "" "" + (string)n + "" "" + m + "" "" + (string)k);
            }
        }";

    public static string Receiver(string tag) => @"
        default {
            state_entry() { llSay(0, """ + tag + @" entry""); }
            link_message(integer s, integer n, string m, key k) {
                llSay(0, """ + tag + @" got "" + (string)s + "" "" + (string)n + "" "" + m + "" "" + (string)k);
            }
        }";

    public static string Got(string tag) => tag + " got 1 42 hello " + Key;

    /// <summary>The harness prim as link 1 plus two children at links 2 and 3.</summary>
    public static SceneObjectPart[] ThreePrims(SchedulerHarness h)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p3", h.Prim.OwnerID));
        var g = h.Prim.ParentGroup;
        Assert.Equal(3, g.PrimCount);
        var parts = new[] { g.GetLinkNumPart(1), g.GetLinkNumPart(2), g.GetLinkNumPart(3) };
        Assert.Same(h.Prim, parts[0]);
        return parts;
    }

    public static void Send(DataserverToPrimTests.Rig r, int link)
    {
        int before = r.Count("S sent");
        r.Say("send " + link);
        Assert.True(r.PumpUntil(() => r.Count("S sent") == before + 1), "the sender did not send: " + string.Join(" | ", r.H.Said));
    }

    /// <summary>Each tag in <paramref name="expected"/> got the message exactly once, and every other tag of
    /// <paramref name="all"/> none, after a quiet window in which no late or duplicate delivery may land.</summary>
    public static void ExactlyOnce(DataserverToPrimTests.Rig r, ITestOutputHelper output, string[] all, string[] expected)
    {
        Assert.True(r.PumpUntil(() => expected.All(t => r.Count(Got(t)) >= 1)),
            "not every targeted script got it: " + string.Join(" | ", r.H.Said));
        r.Quiet(800, all.Select(t => t + " got").ToArray());
        output.WriteLine(string.Join(" | ", r.H.Said));
        foreach (var t in all)
        {
            Assert.Equal(expected.Contains(t) ? 1 : 0, r.CountStart(t + " got "));
            if (expected.Contains(t)) Assert.Equal(1, r.Count(Got(t)));
        }
    }

    // link, then the tags that must get it. Phlox: S (the sender) and P1 in link 1, P2 in link 2.
    // YEngine: Y1 in link 1, Y2 in link 2, Y3 in link 3.
    public static TheoryData<int, string[]> Targets => new()
    {
        { LINK_SET,        new[] { "S", "P1", "P2", "Y1", "Y2", "Y3" } },
        { LINK_THIS,       new[] { "S", "P1", "Y1" } },
        { LINK_ALL_OTHERS, new[] { "P2", "Y2", "Y3" } },
        { 2,               new[] { "P2", "Y2" } },
    };
}

/// <summary>
/// With YEngine hosted beside Phlox (the "phlox-yengine" collection: YEngine keeps compiler state in statics).
/// </summary>
[Collection("phlox-yengine")]
public class LinkMessageCrossEngineYEngineTests
{
    private readonly ITestOutputHelper _out;
    public LinkMessageCrossEngineYEngineTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] All = { "S", "P1", "P2", "Y1", "Y2", "Y3" };

    [Theory]
    [MemberData(nameof(LinkMessageRig.Targets), MemberType = typeof(LinkMessageRig))]
    public void APhloxLinkMessageReachesEveryTargetedScriptOfEitherEngineOnce(int link, string[] expected)
    {
        using var r = new DataserverToPrimTests.Rig(withYEngine: true);
        var parts = LinkMessageRig.ThreePrims(r.H);
        r.Rez(parts[0], "s sender", LinkMessageRig.Sender);
        r.Rez(parts[0], "p1 receiver", LinkMessageRig.Receiver("P1"));
        r.Rez(parts[1], "p2 receiver", LinkMessageRig.Receiver("P2"));
        SchedulerHarnessYEngine.Rez(r.H, parts[0], LinkMessageRig.Receiver("Y1"));
        SchedulerHarnessYEngine.Rez(r.H, parts[1], LinkMessageRig.Receiver("Y2"));
        SchedulerHarnessYEngine.Rez(r.H, parts[2], LinkMessageRig.Receiver("Y3"));
        Assert.True(r.PumpUntil(() => All.All(t => r.Count(t + " entry") == 1) && r.H.Engine.ListenManager.ListenCount == 1),
            "not all started: " + string.Join(" | ", r.H.Said));

        LinkMessageRig.Send(r, link);
        LinkMessageRig.ExactlyOnce(r, _out, All, expected);
    }
}

/// <summary>
/// Other engines stood in by a generated IScriptModule + IScriptEngine registered on each test's own scene, so the class
/// runs in parallel (no process-wide state). No network.
/// </summary>
public class LinkMessageOtherEngineTests
{
    private readonly ITestOutputHelper _out;
    public LinkMessageOtherEngineTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] Phlox = { "S", "P1", "P2" };

    private static (DataserverToPrimTests.Rig r, SceneObjectPart[] parts, UUID[] items) Linkset()
    {
        var r = new DataserverToPrimTests.Rig();
        var parts = LinkMessageRig.ThreePrims(r.H);
        var items = new[]
        {
            r.Rez(parts[0], "s sender", LinkMessageRig.Sender),
            r.Rez(parts[0], "p1 receiver", LinkMessageRig.Receiver("P1")),
            r.Rez(parts[1], "p2 receiver", LinkMessageRig.Receiver("P2")),
        };
        Assert.True(r.PumpUntil(() => Phlox.All(t => r.Count(t + " entry") == 1) && r.H.Engine.ListenManager.ListenCount == 1),
            "not all started: " + string.Join(" | ", r.H.Said));
        return (r, parts, items);
    }

    [Theory]
    [MemberData(nameof(LinkMessageRig.Targets), MemberType = typeof(LinkMessageRig))]
    public void APhloxOnlyRegionDeliversToPhloxScriptsExactlyAsBefore(int link, string[] expected)
    {
        var (r, _, _) = Linkset();
        using (r)
        {
            Assert.DoesNotContain(r.H.Scene.RequestModuleInterfaces<IScriptModule>(), m => !ReferenceEquals(m, r.H.Engine));
            LinkMessageRig.Send(r, link);
            LinkMessageRig.ExactlyOnce(r, _out, Phlox, expected.Where(Phlox.Contains).ToArray());
        }
    }

    [Fact]
    public void AnotherEngineIsOfferedEachTargetedScriptOnceWithPlainValues()
    {
        var (r, parts, items) = Linkset();
        using (r)
        {
            var other = StandInEngine.Create(throws: false);
            r.H.Scene.StackModuleInterface<IScriptModule>(other);
            var y = UUID.Random();                       // a script item the stand-in "runs", in link 3
            TaskInventoryHelpers.AddScript(r.H.Scene.AssetService, parts[2], y, UUID.Random(), "y item", "default { }");

            LinkMessageRig.Send(r, LinkMessageRig.LINK_SET);
            LinkMessageRig.ExactlyOnce(r, _out, Phlox, Phlox);

            var posts = StandInEngine.Of(other).Posts;
            Assert.Equal(items.Append(y).OrderBy(i => i), posts.Select(p => p.Item).OrderBy(i => i));
            foreach (var (_, parms) in posts)
            {
                Assert.Equal("link_message", parms.EventName);
                Assert.Equal(new object[] { 1, 42, "hello", LinkMessageRig.Key }, parms.Params);
                Assert.Equal(new[] { typeof(int), typeof(int), typeof(string), typeof(string) }, parms.Params.Select(p => p.GetType()));
            }
            Assert.Equal(posts.Count, posts.Select(p => p.Parms.Params).Distinct().Count());   // a fresh array per post
        }
    }

    [Fact]
    public void AnEngineThatThrowsAffectsNobodyElse()
    {
        var (r, _, items) = Linkset();
        using (r)
        {
            var thrower = StandInEngine.Create(throws: true);
            var after = StandInEngine.Create(throws: false);
            r.H.Scene.StackModuleInterface<IScriptModule>(thrower);
            r.H.Scene.StackModuleInterface<IScriptModule>(after);

            LinkMessageRig.Send(r, LinkMessageRig.LINK_SET);   // "S sent": no exception reached the sender
            LinkMessageRig.ExactlyOnce(r, _out, Phlox, Phlox);

            Assert.Equal(items.OrderBy(i => i), StandInEngine.Of(thrower).Posts.Select(p => p.Item).OrderBy(i => i));
            Assert.Equal(items.OrderBy(i => i), StandInEngine.Of(after).Posts.Select(p => p.Item).OrderBy(i => i));
        }
    }
}

/// <summary>A script module that is also a script engine, the shape the region's engines have.</summary>
public interface IStandInScriptEngine : IScriptModule, IScriptEngine { }

/// <summary>
/// A generated other engine: records PostScriptEvent(UUID, EventParams) and PostObjectEvent(uint, EventParams) and, when
/// asked, throws from them. Everything else returns its default.
/// </summary>
public class StandInEngine : DispatchProxy
{
    public readonly List<(UUID Item, EventParams Parms)> Posts = new();
    public readonly List<(uint LocalId, EventParams Parms)> ObjectPosts = new();
    public bool Throws;

    public static IStandInScriptEngine Create(bool throws)
    {
        var e = Create<IStandInScriptEngine, StandInEngine>();
        ((StandInEngine)(object)e).Throws = throws;
        return e;
    }

    public static StandInEngine Of(IStandInScriptEngine e) => (StandInEngine)(object)e;

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "PostScriptEvent" && args.Length == 2 && args[1] is EventParams p)
        {
            lock (Posts) Posts.Add(((UUID)args[0], p));
            if (Throws) throw new InvalidOperationException("stand-in engine failure");
            return false;
        }
        if (targetMethod.Name == "PostObjectEvent" && args.Length == 2 && args[0] is uint id && args[1] is EventParams o)
        {
            lock (ObjectPosts) ObjectPosts.Add((id, o));
            if (Throws) throw new InvalidOperationException("stand-in engine failure");
            return false;
        }
        if (targetMethod.Name == "get_Name" || targetMethod.Name == "get_ScriptEngineName") return "StandIn";
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
