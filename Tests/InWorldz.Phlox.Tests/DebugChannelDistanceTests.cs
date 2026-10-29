/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script's run-time error reaches scripts listening on DEBUG_CHANNEL as far as llSay reaches, not a shout's
/// 100 m. SL wiki (DEBUG_CHANNEL): "Server-generated errors are broadcast the same distance as llSay." The text and
/// the error pause are unchanged (ErrorPauseTests). A script's own llShout or llWhisper on DEBUG_CHANNEL keeps its own
/// distance, also when the chat module has rewritten the chat's type to DebugChannel before Phlox sees it, as it does
/// on a region (ChatModule.DeliverChatToAvatars runs first: it subscribes in AddRegion, Phlox in RegionLoaded).
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class DebugChannelDistanceTests
{
    private readonly ITestOutputHelper _out;
    public DebugChannelDistanceTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private static readonly Vector3 Origin = new Vector3(60, 128, 30);
    private const float Say = 20, Shout = 100;

    private static SceneObjectGroup Place(SchedulerHarness h, string name, Vector3 pos)
    {
        var sog = SceneHelpers.AddSceneObject(h.Scene, name, UUID.Random());
        sog.AbsolutePosition = pos;
        return sog;
    }

    private static void Listen(SchedulerHarness h, SceneObjectPart part, string tag)
        => h.RezScriptInto(part,
            "default { state_entry() { llListen(DEBUG_CHANNEL, \"\", NULL_KEY, \"\"); llSay(99, \"ready " + tag + "\"); } " +
            "listen(integer c, string n, key k, string m) { llSay(99, \"" + tag + " heard \" + m); } }");

    private static bool HeardAnything(SchedulerHarness h, string tag) => h.Said.Any(s => s.StartsWith(tag + " heard "));

    /// <summary>
    /// Put a handler ahead of every other OnChatFromWorld handler that does what ChatModule.cs:211 does to chat on
    /// DEBUG_CHANNEL (c.Type = ChatTypeEnum.DebugChannel), so Phlox sees the chat as it does on a region.
    /// </summary>
    private static void RewriteDebugChatFirst(SchedulerHarness h)
    {
        var em = h.Scene.EventManager;
        FieldInfo f = typeof(EventManager).GetField("OnChatFromWorld", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);
        var existing = (Delegate)f.GetValue(em);
        EventManager.ChatFromWorldEvent rewrite = (s, c) => { if (c.Channel == DEBUG_CHANNEL) c.Type = ChatTypeEnum.DebugChannel; };
        f.SetValue(em, Delegate.Combine(rewrite, existing));
    }

    private (SchedulerHarness H, SceneObjectGroup Speaker) Rig(float range, bool rewrite)
    {
        var h = new SchedulerHarness();
        if (rewrite) RewriteDebugChatFirst(h);
        var inside = Place(h, "inside", Origin + new Vector3(range - 0.5f, 0, 0));
        var outside = Place(h, "outside", Origin + new Vector3(range + 0.5f, 0, 0));
        Listen(h, inside.RootPart, "IN");
        Listen(h, outside.RootPart, "OUT");
        Assert.True(h.PumpUntil(() => h.Said.Contains("ready IN") && h.Said.Contains("ready OUT")), "the listeners never started");
        return (h, Place(h, "speaker", Origin));
    }

    private void Check(SchedulerHarness h, string what, float range)
    {
        Assert.True(h.PumpUntil(() => HeardAnything(h, "IN")), $"{what}: a listener {range - 0.5f} m away did not hear it");
        h.PumpFor(TimeSpan.FromMilliseconds(400));   // a real wait: the far listener must NOT hear
        _out.WriteLine(string.Join("\n", h.Said));
        Assert.False(HeardAnything(h, "OUT"), $"{what}: a listener {range + 0.5f} m away heard it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnErrorReachesListenersAsFarAsLlSay(bool rewrite)
    {
        var (h, speaker) = Rig(Say, rewrite);
        using (h)
        {
            // llRegionSay on channel 0 is refused with an error (Halcyon's text).
            h.RezScriptInto(speaker.RootPart, "default { state_entry() { llRegionSay(0, \"x\"); } }");
            Check(h, "an error", Say);
            Assert.Contains(h.Said, s => s == "IN heard Script error: llRegionSay: cannot use channel 0");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShoutOnDebugChannelStillReachesShoutDistance(bool rewrite)
    {
        var (h, speaker) = Rig(Shout, rewrite);
        using (h)
        {
            h.RezScriptInto(speaker.RootPart, "default { state_entry() { llShout(DEBUG_CHANNEL, \"loud\"); } }");
            Check(h, "llShout(DEBUG_CHANNEL)", Shout);
        }
    }

    [Fact]
    public void AWhisperOnDebugChannelKeepsWhisperDistanceUnderTheRewrite()
    {
        var (h, speaker) = Rig(10, rewrite: true);
        using (h)
        {
            h.RezScriptInto(speaker.RootPart, "default { state_entry() { llWhisper(DEBUG_CHANNEL, \"quiet\"); } }");
            Check(h, "llWhisper(DEBUG_CHANNEL)", 10);
        }
    }

    [Fact]
    public void DebugChannelChatFromAnotherSourceGoesSayDistance()
    {
        // YEngine's errors arrive as SimChat(ChatTypeEnum.DebugChannel) (LSL_Api.cs ShoutError): no Phlox script spoke
        // them, so they take SL's server-error distance.
        var (h, speaker) = Rig(Say, rewrite: false);
        using (h)
        {
            h.Scene.SimChat("from elsewhere", ChatTypeEnum.DebugChannel, DEBUG_CHANNEL, speaker.AbsolutePosition,
                speaker.Name, speaker.UUID, false);
            Check(h, "DebugChannel chat", Say);
        }
    }

    [Fact]
    public void AYEngineStyleErrorReachesTheOtherEnginesListensAtSayDistance()
    {
        // Phlox's errors in YEngine's own words (an llHTTPRequest custom header YEngine refuses) also go to the core
        // WorldComm, which holds YEngine's listens. They are server-generated errors too, so they go as far as llSay.
        using var r = new ApiCallRig();
        r.H.Scene.RegisterModuleInterface(Fake<OpenSim.Region.Framework.Interfaces.IHttpRequestModule>.Create((m, a) =>
            m.Name == "CheckThrottle" || m.Name == "CheckAllowed" ? true : null));
        FieldInfo f = typeof(global::Phlox.ScriptEngine.PhloxEngine).GetField("m_WorldComm", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);
        object real = f.GetValue(r.H.Engine);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<(ChatTypeEnum Type, int Channel, string Text)>();
        f.SetValue(r.H.Engine, Fake<OpenSim.Region.Framework.Interfaces.IWorldComm>.Create((m, a) =>
        {
            if (m.Name == "DeliverMessage" && a.Length == 6) sent.Enqueue(((ChatTypeEnum)a[0], (int)a[1], (string)a[4]));
            return null;
        }));
        try
        {
            var (_, ret) = r.Accounted(api => api.llHTTPRequest("http://example.org/", ApiCallRig.L(5, "Host", "v"), ""));
            Assert.Equal("", ret);
            var error = Assert.Single(sent);
            Assert.Equal(DEBUG_CHANNEL, error.Channel);
            Assert.Equal("llHTTPRequest: Name is invalid as a custom header at parameter 1", error.Text);
            Assert.Equal(ChatTypeEnum.Say, error.Type);
        }
        finally { f.SetValue(r.H.Engine, real); }
    }
}
