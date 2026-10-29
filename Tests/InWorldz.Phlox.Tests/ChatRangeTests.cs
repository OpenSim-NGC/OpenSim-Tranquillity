using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Chat reaches a Phlox listen only when the listener is in range, as Halcyon's WorldCommModule.DeliverMessage
/// does: whisper, say and shout use the region's [Chat] whisper_distance / say_distance / shout_distance (the
/// same keys and defaults the chat module and YEngine read), llRegionSay and dialog replies are region-wide,
/// the distance runs from the speaker to the listening prim, and an attachment speaks and listens at its
/// avatar. A prim never hears its own chat; other prims of the same object do. llRegionSayTo reaches only
/// its target (Halcyon's DestIdMatches), on the channel it was sent on, and never its sender.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class ChatRangeTests
{
    private readonly ITestOutputHelper _out;
    public ChatRangeTests(ITestOutputHelper o) => _out = o;

    private const int ReportChannel = 99;
    private static readonly Vector3 Origin = new Vector3(10, 128, 30);

    // The chat module's, WorldComm's and YEngine's defaults when [Chat] does not set them.
    private const float DefaultWhisper = 10, DefaultSay = 20, DefaultShout = 100;

    private static SceneObjectGroup Place(SchedulerHarness h, string name, Vector3 pos, UUID owner = default)
    {
        var sog = SceneHelpers.AddSceneObject(h.Scene, name, owner.IsZero() ? UUID.Random() : owner);
        sog.AbsolutePosition = pos;
        return sog;
    }

    private static SceneObjectGroup Wear(SchedulerHarness h, ScenePresence sp, string name)
    {
        var sog = SceneHelpers.AddSceneObject(h.Scene, name, sp.UUID);
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = (uint)AttachmentPoint.Chest;
        sp.AddAttachment(sog);
        // A worn object's group position is its offset from the attach point, not a region position.
        sog.AbsolutePosition = new Vector3(0.1f, 0, 0.3f);
        return sog;
    }

    private static ScenePresence Avatar(SchedulerHarness h, Vector3 pos)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.AbsolutePosition = pos;
        Assert.True(Vector3.Distance(sp.AbsolutePosition, pos) < 0.01f, "avatar did not move to " + pos + ": " + sp.AbsolutePosition);
        return sp;
    }

    private static string Listener(string tag, params int[] channels)
    {
        var listens = string.Concat(channels.Select(c => $"llListen({c}, \"\", NULL_KEY, \"\"); "));
        return "default { state_entry() { " + listens + $"llSay({ReportChannel}, \"ready {tag}\"); }} " +
               $"listen(integer c, string n, key k, string m) {{ llSay({ReportChannel}, \"{tag} heard \" + (string)c + \":\" + m); }} }}";
    }

    private static UUID Listen(SchedulerHarness h, SceneObjectPart part, string tag, params int[] channels)
        => h.RezScriptInto(part, Listener(tag, channels));

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private static void WaitReady(SchedulerHarness h, params string[] tags)
    {
        WaitFor(h, said => tags.All(t => said.Contains("ready " + t)));
        foreach (var t in tags)
            Assert.True(h.Said.Contains("ready " + t), $"listener {t} never started: [{string.Join(" | ", h.Said)}]");
    }

    /// <summary>Let any stray delivery arrive before asserting that something was NOT heard.</summary>
    private static void Settle(SchedulerHarness h) => h.PumpFor(TimeSpan.FromMilliseconds(400));

    private static bool Heard(SchedulerHarness h, string tag, string what) => h.Said.Contains($"{tag} heard {what}");
    private static bool HeardAnything(SchedulerHarness h, string tag) => h.Said.Any(s => s.StartsWith(tag + " heard "));

    private void Dump(SchedulerHarness h) => _out.WriteLine(string.Join("\n", h.Said));

    private static SchedulerHarness NewHarness(int? whisper = null, int? say = null, int? shout = null)
        => new SchedulerHarness(whisper is null && say is null && shout is null ? null : cfg =>
        {
            var chat = cfg.AddConfig("Chat");
            if (whisper is not null) chat.Set("whisper_distance", whisper.Value);
            if (say is not null) chat.Set("say_distance", say.Value);
            if (shout is not null) chat.Set("shout_distance", shout.Value);
        });

    // ── Object chat ranges ──────────────────────────────────────────────────

    [Theory]
    [InlineData("llWhisper", DefaultWhisper)]
    [InlineData("llSay", DefaultSay)]
    [InlineData("llShout", DefaultShout)]
    public void ObjectChatReachesListenersInsideTheRangeOnly(string fn, float range)
    {
        using var h = NewHarness();
        var inside = Place(h, "inside", Origin + new Vector3(range - 0.5f, 0, 0));
        var outside = Place(h, "outside", Origin + new Vector3(range + 0.5f, 0, 0));
        Listen(h, inside.RootPart, "IN", 5);
        Listen(h, outside.RootPart, "OUT", 5);
        WaitReady(h, "IN", "OUT");

        var speaker = Place(h, "speaker", Origin);
        h.RezScriptInto(speaker.RootPart, $"default {{ state_entry() {{ {fn}(5, \"hello\"); }} }}");
        WaitFor(h, _ => Heard(h, "IN", "5:hello"));
        Settle(h);
        Dump(h);

        Assert.True(Heard(h, "IN", "5:hello"), $"{fn}: a listener {range - 0.5f} m away did not hear");
        Assert.False(HeardAnything(h, "OUT"), $"{fn}: a listener {range + 0.5f} m away heard it");
    }

    [Fact]
    public void RegionSayIsHeardAcrossTheRegion()
    {
        using var h = NewHarness();
        var far = Place(h, "far", new Vector3(250, 250, 30));
        Listen(h, far.RootPart, "FAR", 5);
        WaitReady(h, "FAR");

        var speaker = Place(h, "speaker", new Vector3(5, 5, 30));
        h.RezScriptInto(speaker.RootPart, "default { state_entry() { llRegionSay(5, \"everyone\"); } }");
        WaitFor(h, _ => Heard(h, "FAR", "5:everyone"));
        Dump(h);
        Assert.True(Heard(h, "FAR", "5:everyone"), "llRegionSay did not reach a listener " + Vector3.Distance(far.AbsolutePosition, speaker.AbsolutePosition) + " m away");
    }

    // ── Avatar typed chat ───────────────────────────────────────────────────

    [Theory]
    [InlineData(ChatTypeEnum.Whisper, DefaultWhisper)]
    [InlineData(ChatTypeEnum.Say, DefaultSay)]
    [InlineData(ChatTypeEnum.Shout, DefaultShout)]
    public void AvatarTypedChatObeysTheSameRanges(ChatTypeEnum type, float range)
    {
        using var h = NewHarness();
        var inside = Place(h, "inside", Origin + new Vector3(0, range - 0.5f, 0));
        var outside = Place(h, "outside", Origin + new Vector3(0, -(range + 0.5f), 0));
        Listen(h, inside.RootPart, "IN", 0);
        Listen(h, outside.RootPart, "OUT", 0);
        WaitReady(h, "IN", "OUT");

        var sp = Avatar(h, Origin);
        // The chat module re-raises the viewer's ChatFromViewer as EventManager.OnChatFromClient (ChatModule.cs).
        h.Scene.EventManager.TriggerOnChatFromClient(sp.ControllingClient, new OSChatMessage
        {
            Channel = 0, Message = "typed", Type = type, Position = sp.AbsolutePosition,
            Scene = h.Scene, Sender = sp.ControllingClient,
        });
        WaitFor(h, _ => Heard(h, "IN", "0:typed"));
        Settle(h);
        Dump(h);

        Assert.True(Heard(h, "IN", "0:typed"), $"{type}: a listener {range - 0.5f} m from the avatar did not hear");
        Assert.False(HeardAnything(h, "OUT"), $"{type}: a listener {range + 0.5f} m from the avatar heard it");
    }

    // ── Attachments ─────────────────────────────────────────────────────────

    [Fact]
    public void AnAttachmentSpeaksAtItsAvatarsPosition()
    {
        using var h = NewHarness();
        var avatarPos = new Vector3(200, 200, 30);
        var nearAvatar = Place(h, "near avatar", avatarPos + new Vector3(5, 0, 0));
        // Right beside the worn object's attach-point offset: in range only if the offset were a region position.
        var nearOffset = Place(h, "near offset", new Vector3(1, 1, 1));
        Listen(h, nearAvatar.RootPart, "NEAR", 5);
        Listen(h, nearOffset.RootPart, "OFFSET", 5);
        WaitReady(h, "NEAR", "OFFSET");

        var sp = Avatar(h, avatarPos);
        var worn = Wear(h, sp, "worn speaker");
        h.RezScriptInto(worn.RootPart, "default { state_entry() { llSay(5, \"from the chest\"); } }");
        WaitFor(h, _ => Heard(h, "NEAR", "5:from the chest"));
        Settle(h);
        Dump(h);

        Assert.True(Heard(h, "NEAR", "5:from the chest"), "a prim 5 m from the wearer did not hear the attachment");
        Assert.False(HeardAnything(h, "OFFSET"), "a prim beside the attach offset, far from the wearer, heard the attachment");
    }

    [Fact]
    public void AnAttachmentListensAtItsAvatarsPosition()
    {
        using var h = NewHarness();
        var avatarPos = new Vector3(200, 200, 30);
        var sp = Avatar(h, avatarPos);
        var worn = Wear(h, sp, "worn listener");
        Listen(h, worn.RootPart, "WORN", 5);
        WaitReady(h, "WORN");

        var near = Place(h, "near wearer", avatarPos + new Vector3(0, 5, 0));
        var nearOffset = Place(h, "near offset", new Vector3(1, 1, 1));
        h.RezScriptInto(near.RootPart, "default { state_entry() { llSay(5, \"near the wearer\"); } }");
        h.RezScriptInto(nearOffset.RootPart, "default { state_entry() { llSay(5, \"near the offset\"); } }");
        WaitFor(h, _ => Heard(h, "WORN", "5:near the wearer"));
        Settle(h);
        Dump(h);

        Assert.True(Heard(h, "WORN", "5:near the wearer"), "the attachment did not hear a prim 5 m from its wearer");
        Assert.False(Heard(h, "WORN", "5:near the offset"), "the attachment heard a prim beside its attach offset, far from its wearer");
    }

    // ── A prim and itself ───────────────────────────────────────────────────

    [Fact]
    public void APrimDoesNotHearItselfButASiblingPrimDoes()
    {
        using var h = NewHarness();
        var owner = UUID.Random();
        var root = SceneHelpers.CreateSceneObjectPart("root", UUID.Random(), owner);
        var sog = new SceneObjectGroup(root);
        var child = SceneHelpers.CreateSceneObjectPart("child", UUID.Random(), owner);
        sog.AddPart(child);
        h.Scene.AddNewSceneObject(sog, false);
        sog.AbsolutePosition = Origin;

        Listen(h, root, "SELF", 5);          // the speaking prim's own listen
        Listen(h, child, "SIBLING", 5);      // another prim of the same object
        WaitReady(h, "SELF", "SIBLING");

        h.RezScriptInto(root, "default { state_entry() { llSay(5, \"me\"); } }");
        WaitFor(h, _ => Heard(h, "SIBLING", "5:me"));
        Settle(h);
        Dump(h);

        Assert.True(Heard(h, "SIBLING", "5:me"), "a sibling prim of the same object did not hear");
        Assert.False(HeardAnything(h, "SELF"), "a prim heard its own chat");
    }

    // ── llRegionSayTo ───────────────────────────────────────────────────────

    [Fact]
    public void RegionSayToAnObjectReachesOnlyThatObjectOnThatChannel()
    {
        using var h = NewHarness();
        var target = Place(h, "target", new Vector3(250, 250, 30));
        var other = Place(h, "other", Origin + new Vector3(1, 0, 0));
        Listen(h, target.RootPart, "TARGET", 7, 8);
        Listen(h, other.RootPart, "OTHER", 7);
        var sender = Place(h, "sender", Origin);
        Listen(h, sender.RootPart, "SENDER", 7);
        WaitReady(h, "TARGET", "OTHER", "SENDER");

        h.RezScriptInto(sender.RootPart, $"default {{ state_entry() {{ llRegionSayTo(\"{target.RootPart.UUID}\", 7, \"for you\"); llRegionSayTo(llGetKey(), 7, \"to myself\"); }} }}");
        WaitFor(h, _ => Heard(h, "TARGET", "7:for you"));
        Settle(h);
        Dump(h);

        Assert.True(Heard(h, "TARGET", "7:for you"), "the target did not hear");
        Assert.False(Heard(h, "TARGET", "8:for you"), "the target heard it on a channel it was not sent on");
        Assert.False(HeardAnything(h, "OTHER"), "a listener that was not the target heard it");
        Assert.False(HeardAnything(h, "SENDER"), "the sender heard its own llRegionSayTo");
    }

    [Fact]
    public void RegionSayToAnAvatarReachesItsAttachmentsOnTheChannelAndTheAvatarOnlyOnChannelZero()
    {
        using var h = NewHarness();
        var sp = Avatar(h, new Vector3(200, 200, 30));
        var bystander = Avatar(h, new Vector3(201, 200, 30));
        var worn = Wear(h, sp, "target's attachment");
        var theirs = Wear(h, bystander, "bystander's attachment");
        var loose = Place(h, "loose prim", new Vector3(200, 201, 30));
        Listen(h, worn.RootPart, "WORN", 0, 7);
        Listen(h, theirs.RootPart, "THEIRS", 0, 7);
        Listen(h, loose.RootPart, "LOOSE", 0, 7);
        WaitReady(h, "WORN", "THEIRS", "LOOSE");

        var toAvatar = new List<(string Message, byte Type)>();
        var toBystander = new List<string>();
        ((TestClient)sp.ControllingClient).OnReceivedChatMessage += (m, t, p, n, f, o, s, a) => { lock (toAvatar) toAvatar.Add((m, t)); };
        ((TestClient)bystander.ControllingClient).OnReceivedChatMessage += (m, t, p, n, f, o, s, a) => { lock (toBystander) toBystander.Add(m); };

        var sender = Place(h, "sender", Origin);
        h.RezScriptInto(sender.RootPart, $"default {{ state_entry() {{ llRegionSayTo(\"{sp.UUID}\", 7, \"seven\"); llRegionSayTo(\"{sp.UUID}\", 0, \"zero\"); }} }}");
        WaitFor(h, _ => Heard(h, "WORN", "7:seven") && Heard(h, "WORN", "0:zero"));
        Settle(h);
        Dump(h);
        lock (toAvatar) _out.WriteLine("avatar got: " + string.Join(" | ", toAvatar.Select(x => x.Type + ":" + x.Message)));

        Assert.True(Heard(h, "WORN", "7:seven"), "the avatar's attachment did not hear channel 7");
        Assert.True(Heard(h, "WORN", "0:zero"), "the avatar's attachment did not hear channel 0");
        Assert.False(Heard(h, "WORN", "0:seven") || Heard(h, "WORN", "7:zero"), "the attachment heard a message on the wrong channel");
        Assert.False(HeardAnything(h, "THEIRS"), "another avatar's attachment heard it");
        Assert.False(HeardAnything(h, "LOOSE"), "a prim that is not worn by the target heard it");
        lock (toAvatar)
        {
            Assert.Contains(toAvatar, x => x.Message == "zero" && x.Type == (byte)ChatTypeEnum.Direct);
            Assert.DoesNotContain(toAvatar, x => x.Message == "seven");
        }
        lock (toBystander) Assert.DoesNotContain(toBystander, m => m == "zero" || m == "seven");
    }

    [Fact]
    public void RegionSayToNullKeyReachesNoOne()
    {
        using var h = NewHarness();
        var a = Place(h, "a", Origin + new Vector3(1, 0, 0));
        var b = Place(h, "b", new Vector3(250, 250, 30));
        Listen(h, a.RootPart, "A", 7);
        Listen(h, b.RootPart, "B", 7);
        var sp = Avatar(h, Origin + new Vector3(0, 1, 0));
        var worn = Wear(h, sp, "worn");
        Listen(h, worn.RootPart, "WORN", 7);
        WaitReady(h, "A", "B", "WORN");
        var toAvatar = new List<string>();
        ((TestClient)sp.ControllingClient).OnReceivedChatMessage += (m, t, p, n, f, o, s, x) => { lock (toAvatar) toAvatar.Add(m); };

        var sender = Place(h, "sender", Origin);
        h.RezScriptInto(sender.RootPart, "default { state_entry() { llRegionSayTo(NULL_KEY, 7, \"nobody\"); llRegionSayTo(NULL_KEY, 0, \"nobody0\"); llSay(" + ReportChannel + ", \"sent\"); } }");
        WaitFor(h, said => said.Contains("sent"));
        Settle(h);
        Dump(h);

        Assert.Contains("sent", h.Said);
        Assert.False(HeardAnything(h, "A") || HeardAnything(h, "B") || HeardAnything(h, "WORN"), "llRegionSayTo(NULL_KEY, ...) reached a listener");
        lock (toAvatar) Assert.DoesNotContain(toAvatar, m => m.StartsWith("nobody"));
    }

    // ── Configured distances ────────────────────────────────────────────────

    [Fact]
    public void TheConfiguredDistancesSetTheRanges()
    {
        using var h = NewHarness(whisper: 3, say: 5, shout: 40);
        var p45 = Place(h, "4.5", Origin + new Vector3(4.5f, 0, 0));
        var p55 = Place(h, "5.5", Origin + new Vector3(-5.5f, 0, 0));
        var p39 = Place(h, "39", Origin + new Vector3(0, 39, 0));
        var p41 = Place(h, "41", Origin + new Vector3(0, -41, 0));
        Listen(h, p45.RootPart, "P45", 5);
        Listen(h, p55.RootPart, "P55", 5);
        Listen(h, p39.RootPart, "P39", 5);
        Listen(h, p41.RootPart, "P41", 5);
        WaitReady(h, "P45", "P55", "P39", "P41");

        var speaker = Place(h, "speaker", Origin);
        h.RezScriptInto(speaker.RootPart, "default { state_entry() { llWhisper(5, \"w\"); llSay(5, \"s\"); llShout(5, \"sh\"); } }");
        WaitFor(h, _ => Heard(h, "P45", "5:s") && Heard(h, "P39", "5:sh"));
        Settle(h);
        Dump(h);

        Assert.False(Heard(h, "P45", "5:w"), "whisper_distance = 3 reached 4.5 m");
        Assert.True(Heard(h, "P45", "5:s"), "say_distance = 5 did not reach 4.5 m");
        Assert.False(Heard(h, "P55", "5:s"), "say_distance = 5 reached 5.5 m (the default of 20 would)");
        Assert.True(Heard(h, "P39", "5:sh"), "shout_distance = 40 did not reach 39 m");
        Assert.False(HeardAnything(h, "P41"), "shout_distance = 40 reached 41 m");
    }
}
