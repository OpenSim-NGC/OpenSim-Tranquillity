using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Chat crosses between the two script engines on a region that runs both. Phlox keeps its own listens
/// (PhloxListenManager) and YEngine's are carried by the core WorldComm module, so every kind of chat has to
/// reach both: llWhisper, llSay and llShout within the region's ranges, llRegionSay region-wide, llRegionSayTo
/// to its target only (the addressed prim, or the addressed avatar's attachments) on its channel, and
/// broadcast chat (EventManager.OnChatBroadcast). The sender never hears itself, and no listen in either
/// engine hears one message twice.
/// </summary>
[Collection("phlox-yengine")]
public class CrossEngineChatTests
{
    private readonly ITestOutputHelper _out;
    public CrossEngineChatTests(ITestOutputHelper o) => _out = o;

    private const int ReportChannel = 99;
    private static readonly Vector3 Origin = new Vector3(10, 128, 30);

    // The chat module's, WorldComm's, YEngine's and Phlox's defaults when [Chat] does not set them.
    private const float DefaultWhisper = 10, DefaultSay = 20, DefaultShout = 100;

    private const string Phlox = "Phlox", YEngine = "YEngine";

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
        return sp;
    }

    /// <summary>Rez a script into a part on the named engine: Phlox's OnRezScript, or the region's own path for YEngine.</summary>
    private static UUID Rez(SchedulerHarness h, string engine, SceneObjectPart part, string source)
    {
        if (engine == Phlox)
            return h.RezScriptInto(part, source);
        var item = TaskInventoryHelpers.AddScript(
            h.Scene.AssetService, part, UUID.Random(), UUID.Random(), "yscript" + Guid.NewGuid().ToString("N")[..6], source);
        Assert.True(part.Inventory.CreateScriptInstance(item.ItemID, 0, false, h.YEngine.ScriptEngineName, 1));
        var errors = h.YEngine.GetScriptErrors(item.ItemID);
        Assert.True(errors.Count == 0, "YEngine did not compile it: " + string.Join(" | ", errors.Cast<object>()));
        // YEngine holds a new script suspended until the region resumes the object's scripts, as it does after a rez.
        part.ParentGroup.ResumeScripts();
        return item.ItemID;
    }

    /// <summary>A listen on each channel; every message it hears is reported once on the report channel.</summary>
    private static string Listener(string tag, params int[] channels)
    {
        var listens = string.Concat(channels.Select(c => $"llListen({c}, \"\", NULL_KEY, \"\"); "));
        return "default { state_entry() { " + listens + $"llSay({ReportChannel}, \"ready {tag}\"); }} " +
               $"listen(integer c, string n, key k, string m) {{ llSay({ReportChannel}, \"{tag} heard \" + (string)c + \":\" + m); }} }}";
    }

    /// <summary>A speaker that listens on its own channel first, so hearing itself would show as "SELF heard".</summary>
    private static string Speaker(int channel, string call)
        => $"default {{ state_entry() {{ llListen({channel}, \"\", NULL_KEY, \"\"); {call}; }} " +
           $"listen(integer c, string n, key k, string m) {{ llSay({ReportChannel}, \"SELF heard \" + (string)c + \":\" + m); }} }}";

    private static void Listen(SchedulerHarness h, string engine, SceneObjectPart part, string tag, params int[] channels)
        => Rez(h, engine, part, Listener(tag, channels));

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private static void WaitReady(SchedulerHarness h, params string[] tags)
    {
        WaitFor(h, said => tags.All(t => said.Contains("ready " + t)), 60);
        foreach (var t in tags)
            Assert.True(h.Said.Contains("ready " + t), $"listener {t} never started: [{string.Join(" | ", h.Said)}]");
    }

    /// <summary>Let any stray or repeated delivery arrive before asserting that something was NOT heard, or heard once.</summary>
    private static void Settle(SchedulerHarness h) => h.PumpFor(TimeSpan.FromMilliseconds(1000));

    private static int Times(SchedulerHarness h, string tag, string what) => h.Said.Count(s => s == $"{tag} heard {what}");
    private static bool HeardAnything(SchedulerHarness h, string tag) => h.Said.Any(s => s.StartsWith(tag + " heard "));

    private void Dump(SchedulerHarness h) => _out.WriteLine(string.Join("\n", h.Said));

    private static string Other(string engine) => engine == Phlox ? YEngine : Phlox;

    private static SchedulerHarness NewHarness() => new SchedulerHarness(withYEngine: true);

    // ── Whisper, say and shout ──────────────────────────────────────────────

    /// <summary>
    /// A listener of the other engine inside the range hears the chat once; one just outside does not; the
    /// sender does not hear itself. A listener of the sender's own engine inside the range hears it once too.
    /// </summary>
    [Theory]
    [InlineData(Phlox, "llWhisper", DefaultWhisper)]
    [InlineData(Phlox, "llSay", DefaultSay)]
    [InlineData(Phlox, "llShout", DefaultShout)]
    [InlineData(YEngine, "llWhisper", DefaultWhisper)]
    [InlineData(YEngine, "llSay", DefaultSay)]
    [InlineData(YEngine, "llShout", DefaultShout)]
    public void RangedChatReachesTheOtherEngineInsideTheRangeOnly(string sender, string fn, float range)
    {
        using var h = NewHarness();
        var other = Other(sender);
        var inside = Place(h, "inside", Origin + new Vector3(range - 0.5f, 0, 0));
        var outside = Place(h, "outside", Origin + new Vector3(range + 0.5f, 0, 0));
        Listen(h, other, inside.RootPart, "IN", 5);
        Listen(h, other, outside.RootPart, "OUT", 5);
        Listen(h, sender, inside.RootPart, "SAME", 5);
        WaitReady(h, "IN", "OUT", "SAME");

        var speaker = Place(h, "speaker", Origin);
        Rez(h, sender, speaker.RootPart, Speaker(5, $"{fn}(5, \"hello\")"));
        WaitFor(h, _ => Times(h, "IN", "5:hello") > 0 && Times(h, "SAME", "5:hello") > 0);
        Settle(h);
        Dump(h);

        Assert.True(Times(h, "IN", "5:hello") == 1, $"{sender} {fn}: a {other} listener {range - 0.5f} m away heard it {Times(h, "IN", "5:hello")} times");
        Assert.False(HeardAnything(h, "OUT"), $"{sender} {fn}: a {other} listener {range + 0.5f} m away heard it");
        Assert.True(Times(h, "SAME", "5:hello") == 1, $"{sender} {fn}: a {sender} listener in range heard it {Times(h, "SAME", "5:hello")} times");
        Assert.False(HeardAnything(h, "SELF"), $"{sender} {fn}: the sender heard itself");
    }

    // ── llRegionSay ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void RegionSayReachesTheOtherEngineAcrossTheRegionOnce(string sender)
    {
        using var h = NewHarness();
        var other = Other(sender);
        var far = Place(h, "far", new Vector3(250, 250, 30));
        Listen(h, other, far.RootPart, "FAR", 5);
        Listen(h, other, far.RootPart, "WRONG", 6);
        Listen(h, sender, far.RootPart, "SAME", 5);
        WaitReady(h, "FAR", "WRONG", "SAME");

        var speaker = Place(h, "speaker", new Vector3(5, 5, 30));
        Rez(h, sender, speaker.RootPart, Speaker(5, "llRegionSay(5, \"everyone\")"));
        WaitFor(h, _ => Times(h, "FAR", "5:everyone") > 0 && Times(h, "SAME", "5:everyone") > 0);
        Settle(h);
        Dump(h);

        Assert.True(Times(h, "FAR", "5:everyone") == 1, $"{sender} llRegionSay: a far {other} listener heard it {Times(h, "FAR", "5:everyone")} times");
        Assert.True(Times(h, "SAME", "5:everyone") == 1, $"{sender} llRegionSay: a far {sender} listener heard it {Times(h, "SAME", "5:everyone")} times");
        Assert.False(HeardAnything(h, "WRONG"), $"{sender} llRegionSay: a {other} listener on another channel heard it");
        Assert.False(HeardAnything(h, "SELF"), $"{sender} llRegionSay: the sender heard itself");
    }

    // ── llRegionSayTo ───────────────────────────────────────────────────────

    /// <summary>
    /// To an object of the other engine: only the addressed prim's listen on that channel hears it, once; a
    /// listen on another channel in the same prim, a listen in another prim, and the sender do not.
    /// </summary>
    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void RegionSayToAnObjectOfTheOtherEngineReachesOnlyThatObjectOnThatChannel(string sender)
    {
        using var h = NewHarness();
        var other = Other(sender);
        var target = Place(h, "target", new Vector3(200, 200, 30));
        var bystander = Place(h, "bystander", Origin + new Vector3(1, 0, 0));
        Listen(h, other, target.RootPart, "T5", 5);
        Listen(h, other, target.RootPart, "T6", 6);
        Listen(h, other, bystander.RootPart, "B5", 5);
        WaitReady(h, "T5", "T6", "B5");

        var speaker = Place(h, "speaker", Origin);
        Rez(h, sender, speaker.RootPart, Speaker(5, $"llRegionSayTo(\"{target.RootPart.UUID}\", 5, \"psst\")"));
        WaitFor(h, _ => Times(h, "T5", "5:psst") > 0);
        Settle(h);
        Dump(h);

        Assert.True(Times(h, "T5", "5:psst") == 1, $"{sender} llRegionSayTo: the addressed {other} prim heard it {Times(h, "T5", "5:psst")} times");
        Assert.False(HeardAnything(h, "T6"), $"{sender} llRegionSayTo: the addressed prim's listen on another channel heard it");
        Assert.False(HeardAnything(h, "B5"), $"{sender} llRegionSayTo: a {other} prim that was not addressed heard it");
        Assert.False(HeardAnything(h, "SELF"), $"{sender} llRegionSayTo: the sender heard itself");
    }

    /// <summary>To an avatar: its attachment running the other engine hears it, once; an object it is not wearing does not.</summary>
    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void RegionSayToAnAvatarReachesItsAttachmentOfTheOtherEngine(string sender)
    {
        using var h = NewHarness();
        var other = Other(sender);
        var sp = Avatar(h, new Vector3(180, 180, 25));
        var worn = Wear(h, sp, "worn");
        var loose = Place(h, "loose", new Vector3(181, 180, 25), sp.UUID);
        Listen(h, other, worn.RootPart, "WORN", 5);
        Listen(h, other, loose.RootPart, "LOOSE", 5);
        WaitReady(h, "WORN", "LOOSE");

        var speaker = Place(h, "speaker", Origin);
        Rez(h, sender, speaker.RootPart, Speaker(5, $"llRegionSayTo(\"{sp.UUID}\", 5, \"hud\")"));
        WaitFor(h, _ => Times(h, "WORN", "5:hud") > 0);
        Settle(h);
        Dump(h);

        Assert.True(Times(h, "WORN", "5:hud") == 1, $"{sender} llRegionSayTo(avatar): its {other} attachment heard it {Times(h, "WORN", "5:hud")} times");
        Assert.False(HeardAnything(h, "LOOSE"), $"{sender} llRegionSayTo(avatar): an object the avatar owns but is not wearing heard it");
        Assert.False(HeardAnything(h, "SELF"), $"{sender} llRegionSayTo(avatar): the sender heard itself");
    }

    /// <summary>
    /// A prim holding one listen of each engine, addressed by either engine: each listen hears the message
    /// exactly once - neither engine's route delivers to the other's listens a second time.
    /// </summary>
    [Theory]
    [InlineData(Phlox, "llRegionSayTo")]
    [InlineData(YEngine, "llRegionSayTo")]
    [InlineData(Phlox, "llSay")]
    [InlineData(YEngine, "llSay")]
    [InlineData(Phlox, "llRegionSay")]
    [InlineData(YEngine, "llRegionSay")]
    public void APrimWithAListenInEachEngineHearsTheMessageOnceInEach(string sender, string fn)
    {
        using var h = NewHarness();
        var both = Place(h, "both", Origin + new Vector3(3, 0, 0));
        Listen(h, Phlox, both.RootPart, "P", 5);
        Listen(h, YEngine, both.RootPart, "Y", 5);
        WaitReady(h, "P", "Y");

        var speaker = Place(h, "speaker", Origin);
        var call = fn == "llRegionSayTo" ? $"llRegionSayTo(\"{both.RootPart.UUID}\", 5, \"once\")" : $"{fn}(5, \"once\")";
        Rez(h, sender, speaker.RootPart, Speaker(5, call));
        WaitFor(h, _ => Times(h, "P", "5:once") > 0 && Times(h, "Y", "5:once") > 0);
        Settle(h);
        Dump(h);

        Assert.True(Times(h, "P", "5:once") == 1, $"{sender} {fn}: the Phlox listen heard it {Times(h, "P", "5:once")} times");
        Assert.True(Times(h, "Y", "5:once") == 1, $"{sender} {fn}: the YEngine listen heard it {Times(h, "Y", "5:once")} times");
        Assert.False(HeardAnything(h, "SELF"), $"{sender} {fn}: the sender heard itself");
    }

    // ── Broadcast chat ──────────────────────────────────────────────────────

    /// <summary>
    /// Chat raised as OnChatBroadcast (Scene.SimChatBroadcast, which region modules use) reaches Phlox listens as
    /// WorldComm delivers it to YEngine's: in range for say, on its channel, once.
    /// </summary>
    [Fact]
    public void BroadcastChatReachesAPhloxListenInRangeOnItsChannelOnce()
    {
        using var h = NewHarness();
        var inside = Place(h, "inside", Origin + new Vector3(DefaultSay - 0.5f, 0, 0));
        var outside = Place(h, "outside", Origin + new Vector3(DefaultSay + 0.5f, 0, 0));
        Listen(h, Phlox, inside.RootPart, "IN", 5);
        Listen(h, Phlox, inside.RootPart, "WRONG", 6);
        Listen(h, Phlox, outside.RootPart, "OUT", 5);
        Listen(h, YEngine, inside.RootPart, "Y", 5);
        WaitReady(h, "IN", "WRONG", "OUT", "Y");

        h.Scene.SimChatBroadcast("news", ChatTypeEnum.Say, 5, Origin, "Broadcaster", UUID.Zero, false);
        WaitFor(h, _ => Times(h, "IN", "5:news") > 0 && Times(h, "Y", "5:news") > 0);
        Settle(h);
        Dump(h);

        Assert.Equal(1, Times(h, "IN", "5:news"));
        Assert.Equal(1, Times(h, "Y", "5:news"));   // WorldComm's own route, unchanged
        Assert.False(HeardAnything(h, "WRONG"), "a Phlox listen on another channel heard broadcast chat");
        Assert.False(HeardAnything(h, "OUT"), "a Phlox listen beyond say range heard broadcast chat");
    }

    [Fact]
    public void RegionBroadcastChatReachesAFarPhloxListen()
    {
        using var h = NewHarness();
        var far = Place(h, "far", new Vector3(250, 250, 30));
        Listen(h, Phlox, far.RootPart, "FAR", 5);
        WaitReady(h, "FAR");

        h.Scene.SimChatBroadcast("all hands", ChatTypeEnum.Region, 5, new Vector3(5, 5, 30), "Broadcaster", UUID.Zero, false);
        WaitFor(h, _ => Times(h, "FAR", "5:all hands") > 0);
        Settle(h);
        Dump(h);

        Assert.Equal(1, Times(h, "FAR", "5:all hands"));
    }
}
