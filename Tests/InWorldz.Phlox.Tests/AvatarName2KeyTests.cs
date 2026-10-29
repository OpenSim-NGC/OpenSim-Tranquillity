using System.Diagnostics;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwAvatarName2Key is an InWorldz extension, so Halcyon is its specification
/// (InWorldz.Phlox.Engine/LSLSystemAPI.cs iwAvatarName2Key): the avatar's key is the call's value,
/// handed back through SysReturn - not a query key answered by a dataserver event. A blank last
/// name is "Resident"; both names are trimmed; the region's root agents are matched first, without
/// regard to case, then the account service; an unknown or blank name is NULL_KEY.
/// </summary>
[Collection("phlox-state")]
public class AvatarName2KeyTests
{
    private readonly ITestOutputHelper _out;
    public AvatarName2KeyTests(ITestOutputHelper o) => _out = o;

    private const string NullKey = "00000000-0000-0000-0000-000000000000";

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            h.PumpFor(TimeSpan.FromMilliseconds(50));
            if (done()) return true;
        }
        return done();
    }

    /// <summary>
    /// One script makes each call in turn and says what it got; a dataserver handler says anything
    /// that arrives. Returns the results in call order.
    /// </summary>
    private List<string> Ask(SchedulerHarness h, params string[] args)
    {
        var body = string.Concat(args.Select((a, i) => $"llSay(0, \"K{i}:\" + (string)iwAvatarName2Key({a})); "));
        h.RezScript("default { state_entry() { " + body + "llSay(0, \"done\"); } " +
                    "dataserver(key q, string d) { llSay(0, \"DS:\" + d); } }");
        Assert.True(PumpUntil(h, () => h.Said.Contains("done"), TimeSpan.FromSeconds(30)),
            "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));   // room for a stray dataserver event
        _out.WriteLine(string.Join(" | ", h.Said) + " || debug: " +
                       string.Join(" | ", h.SaidOn.Where(s => s.Channel == 0x7FFFFFFF).Select(s => s.Message)));
        Assert.DoesNotContain(h.Said, s => s.StartsWith("DS:"));
        return args.Select((_, i) => h.Said.First(s => s.StartsWith($"K{i}:")).Substring($"K{i}:".Length)).ToList();
    }

    private static UUID AddAvatar(SchedulerHarness h, string first, string last)
    {
        var ua = new UserAccount(UUID.Random()) { FirstName = first, LastName = last };
        SceneHelpers.AddScenePresence(h.Scene, ua);
        return ua.PrincipalID;
    }

    [Fact]
    public void AnAvatarInTheRegionIsFoundByFirstAndLastNameInAnyCase()
    {
        using var h = new SchedulerHarness();
        var id = AddAvatar(h, "Ada", "Lovelace");
        var got = Ask(h, "\"Ada\", \"Lovelace\"", "\"ada\", \"LOVELACE\"", "\"  Ada \", \" Lovelace  \"");
        Assert.All(got, g => Assert.Equal(id.ToString(), g));
    }

    [Fact]
    public void ABlankLastNameIsResident()
    {
        using var h = new SchedulerHarness();
        var id = AddAvatar(h, "Mono", "Resident");
        var got = Ask(h, "\"Mono\", \"\"", "\"mono\", \"   \"", "\"Mono\", \"Resident\"");
        Assert.All(got, g => Assert.Equal(id.ToString(), g));
    }

    [Fact]
    public void AnAvatarElsewhereIsFoundThroughTheAccountService()
    {
        using var h = new SchedulerHarness();
        var id = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Far", "Away", id, "pw");
        Assert.Equal(id.ToString(), Ask(h, "\"Far\", \"Away\"").Single());
    }

    /// <summary>
    /// Halcyon takes the two names as given and never splits one: "First.Last" or "First Last" in
    /// the first-name argument is a first name, with "Resident" for the blank last name.
    /// </summary>
    [Fact]
    public void AnUnknownOrBlankNameIsNullKey()
    {
        using var h = new SchedulerHarness();
        AddAvatar(h, "Ada", "Lovelace");
        var got = Ask(h, "\"Nobody\", \"Here\"", "\"\", \"Lovelace\"", "\"  \", \"\"", "\"Ada.Lovelace\", \"\"", "\"Ada Lovelace\", \"\"");
        Assert.All(got, g => Assert.Equal(NullKey, g));
    }

    /// <summary>
    /// The account lookup runs off the scheduler thread (this test's thread drives the scheduler),
    /// and its answer comes back as the call's value.
    /// </summary>
    [Fact]
    public void TheAccountLookupRunsOffTheSchedulerThreadAndReturnsItsValue()
    {
        using var h = new SchedulerHarness();
        var id = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Off", "Thread", id, "pw");
        int lookupThread = -1;
        InstallNameLookupHook(h, "Off", () => lookupThread = Environment.CurrentManagedThreadId);

        Assert.Equal(id.ToString(), Ask(h, "\"Off\", \"Thread\"").Single());
        Assert.NotEqual(-1, lookupThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, lookupThread);
    }

    /// <summary>
    /// Script A's lookup is held open by the account service; script B keeps ticking the whole time,
    /// and A gets the key once the service answers.
    /// </summary>
    [Fact]
    public void OtherScriptsKeepRunningWhileOneWaitsOnTheLookup()
    {
        using var h = new SchedulerHarness();
        var id = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Held", "Open", id, "pw");
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        InstallNameLookupHook(h, "Held", () => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(30)); });

        try
        {
            h.RezScript(@"default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, ""B tick""); } }");
            h.PumpFor(TimeSpan.FromMilliseconds(500));
            h.RezScript(@"default { state_entry() { llSay(0, ""A asks""); key k = iwAvatarName2Key(""Held"", ""Open""); llSay(0, ""A got "" + (string)k); }
                                    dataserver(key q, string d) { llSay(0, ""DS:"" + d); } }");

            Assert.True(PumpUntil(h, () => entered.IsSet, TimeSpan.FromSeconds(30)), "the lookup never reached the account service");
            int ticksBefore = h.Said.Count(s => s == "B tick");
            h.PumpFor(TimeSpan.FromMilliseconds(1500));
            h.PumpUntil(() => h.Said.Count(s => s == "B tick") - ticksBefore >= 8);
            int ticksWhileHeld = h.Said.Count(s => s == "B tick") - ticksBefore;
            _out.WriteLine($"B ticked {ticksWhileHeld} times while A's lookup was held");
            Assert.DoesNotContain(h.Said, s => s.StartsWith("A got"));
            Assert.True(ticksWhileHeld >= 8, $"B ticked only {ticksWhileHeld} times while A waited");
        }
        finally { gate.Set(); }

        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("A got")), TimeSpan.FromSeconds(30)),
            "A never answered: " + string.Join(" | ", h.Said.TakeLast(5)));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        Assert.Contains("A got " + id, h.Said);
        Assert.DoesNotContain(h.Said, s => s.StartsWith("DS:"));
    }

    /// <summary>Run <paramref name="hook"/> inside the account service's by-name lookup for <paramref name="firstName"/>.</summary>
    private static void InstallNameLookupHook(SchedulerHarness h, string firstName, Action hook)
    {
        var proxy = DelayProxy<IUserAccountService>.Wrap(h.Scene.UserAccountService, (m, a) =>
        {
            if (m.Name == nameof(IUserAccountService.GetUserAccount) && a.Length == 3 &&
                a[1] is string fn && fn.Equals(firstName, StringComparison.OrdinalIgnoreCase))
                hook();
            return 0;
        });
        typeof(Scene).GetField("m_UserAccountService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Scene, proxy);
    }
}
