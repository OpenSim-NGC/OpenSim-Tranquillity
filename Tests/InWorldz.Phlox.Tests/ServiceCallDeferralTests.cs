using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;
using Xunit.Abstractions;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Wraps a service interface and sleeps before chosen calls: a stub service with a configurable
/// delay, in front of the real test service, so values are real and only the latency is injected.
/// </summary>
public class DelayProxy<T> : DispatchProxy where T : class
{
    private T m_inner;
    private Func<MethodInfo, object[], int> m_delayFor;

    public static T Wrap(T inner, Func<MethodInfo, object[], int> delayFor)
    {
        T proxy = Create<T, DelayProxy<T>>();
        var dp = (DelayProxy<T>)(object)proxy;
        dp.m_inner = inner;
        dp.m_delayFor = delayFor;
        return proxy;
    }

    protected override object Invoke(MethodInfo method, object[] args)
    {
        int d = m_delayFor(method, args);
        if (d > 0) Thread.Sleep(d);
        try { return method.Invoke(m_inner, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}

/// <summary>
/// Syscalls that can reach a service run on the region's service lane, not inline on its
/// scheduler thread; the value a script receives is unchanged, only when it receives it.
/// </summary>
public class ServiceCallDeferralTests
{
    private readonly ITestOutputHelper _out;
    public ServiceCallDeferralTests(ITestOutputHelper output) { _out = output; }

    /// <summary>
    /// Put a delaying user-account service in front of the scene's own. Like the remote connector, a
    /// lookup the account cache can answer costs nothing; only a miss pays the delay.
    /// </summary>
    internal static void InstallAccountDelay(SchedulerHarness h, Func<UUID, int> delayForId)
    {
        var inner = h.Scene.UserAccountService;
        var cache = h.Scene.RequestModuleInterface<IUserAccountCacheModule>();
        var proxy = DelayProxy<IUserAccountService>.Wrap(inner, (m, a) =>
        {
            if (m.Name != nameof(IUserAccountService.GetUserAccount)) return 0;
            if (a.Length == 2 && a[1] is UUID id)
            {
                bool cached = false;
                if (cache != null) cache.Get(id, out cached);
                return cached ? 0 : delayForId(id);
            }
            return 0;
        });
        typeof(Scene).GetField("m_UserAccountService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Scene, proxy);
    }

    private static SchedulerHarness Harness(string deferral = "auto", int timeoutMs = 35000)
        => new SchedulerHarness(cfg =>
        {
            cfg.Configs["InWorldz.Phlox"].Set("ServiceCallDeferral", deferral);
            cfg.Configs["InWorldz.Phlox"].Set("ServiceCallTimeoutMs", timeoutMs.ToString());
        });

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

    private (int ticksWhileWaiting, TimeSpan waited, string answer) RunLiveness(string deferral)
    {
        using var h = Harness(deferral);
        var slow = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Slow", "Lookup", slow, "pw");
        InstallAccountDelay(h, id => id == slow ? 3000 : 0);

        h.RezScript(@"default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, ""B tick""); } }");
        h.PumpFor(TimeSpan.FromMilliseconds(500));   // B is ticking before A asks

        h.RezScript(@"
default
{
    state_entry()
    {
        llSay(0, ""A asks"");
        string n = llGetUsername(""" + slow + @""");
        llSay(0, ""A got ["" + n + ""]"");
    }
}");
        var sw = Stopwatch.StartNew();
        Assert.True(PumpUntil(h, () => h.Said.Any(m => m.StartsWith("A got")), TimeSpan.FromSeconds(30)), "A never answered: " + string.Join(",", h.Said.TakeLast(5)));
        var waited = sw.Elapsed;

        var said = h.Said.ToList();
        int ask = said.IndexOf("A asks"), got = said.FindIndex(m => m.StartsWith("A got"));
        int ticks = said.Skip(ask + 1).Take(got - ask - 1).Count(m => m == "B tick");
        return (ticks, waited, said[got]);
    }

    /// <summary>
    /// THE HEADLINE PROOF. Script A asks for an account the (stub) service takes 3 s to return; script
    /// B, in the same region, keeps ticking at 10 Hz the whole time, and A still gets the right answer.
    /// </summary>
    [Fact]
    public void OtherScriptsKeepRunningWhileOneWaitsOnASlowService()
    {
        var (ticks, waited, answer) = RunLiveness("auto");
        _out.WriteLine($"deferred: A waited {waited.TotalMilliseconds:F0} ms; B ticked {ticks} times meanwhile; A said '{answer}'");
        Assert.Equal("A got [Slow Lookup]", answer);
        Assert.True(waited >= TimeSpan.FromMilliseconds(2500), "the stub delay was not in the path");
        Assert.True(ticks >= 15, $"B ticked only {ticks} times while A waited");
    }

    /// <summary>The same scenario with deferral off (everything inline): the region stops dead.</summary>
    [Fact]
    public void WithDeferralOffTheWholeRegionStopsForTheCall()
    {
        var (ticks, waited, answer) = RunLiveness("never");
        _out.WriteLine($"inline (pre-B2): A waited {waited.TotalMilliseconds:F0} ms; B ticked {ticks} times meanwhile; A said '{answer}'");
        Assert.Equal("A got [Slow Lookup]", answer);
        Assert.True(ticks <= 2, $"B ticked {ticks} times - the inline call did not block the scheduler?");
    }

    /// <summary>
    /// Timeout: the first call outlives the deadline and the script resumes with the function's failure
    /// value. Its answer then arrives WHILE the script is parked in the second call; the sequence number
    /// must drop it, or the second call would return the first user's name.
    /// </summary>
    [Fact]
    public void TimeoutResumesWithTheFailureValueAndTheLateAnswerIsDropped()
    {
        using var h = Harness(timeoutMs: 1000);
        var first = UUID.Random(); var second = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "First", "User", first, "pw");
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Second", "User", second, "pw");
        InstallAccountDelay(h, id => id == first ? 1500 : id == second ? 800 : 0);

        h.RezScript(@"
default
{
    state_entry()
    {
        string a = llGetUsername(""" + first + @""");
        llSay(0, ""T1 ["" + a + ""]"");
        string b = llGetUsername(""" + second + @""");
        llSay(0, ""T2 ["" + b + ""]"");
        llSay(0, ""done"");
    }
}");
        Assert.True(PumpUntil(h, () => h.Said.Contains("done"), TimeSpan.FromSeconds(30)), string.Join(",", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        var said = h.Said.Where(m => m.StartsWith("T") || m == "done").ToList();
        _out.WriteLine("said: " + string.Join(" | ", said));
        Assert.Equal(new[] { "T1 []", "T2 [Second User]", "done" }, said);
    }

    /// <summary>
    /// Reset during a deferred call: the first run's answer arrives while the SECOND run is parked in
    /// its own call. The fresh state must not receive it (process-wide sequence numbers).
    /// </summary>
    [Fact]
    public void AResetScriptNeverReceivesTheAnswerMeantForItsPreviousRun()
    {
        using var h = Harness();
        var first = UUID.Random(); var second = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "First", "User", first, "pw");
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Second", "User", second, "pw");
        InstallAccountDelay(h, id => id == first ? 1500 : id == second ? 2500 : 0);

        h.Prim.Description = first.ToString();
        var item = h.RezScript(@"
default
{
    state_entry()
    {
        string n = llGetUsername(llGetObjectDesc());
        llSay(0, ""got ["" + n + ""]"");
    }
}");
        h.PumpFor(TimeSpan.FromMilliseconds(400));   // parked in the first call
        h.Prim.Description = second.ToString();
        h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, item);
        Assert.True(PumpUntil(h, () => h.Said.Any(m => m.StartsWith("got")), TimeSpan.FromSeconds(30)), string.Join(",", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        var got = h.Said.Where(m => m.StartsWith("got")).ToList();
        _out.WriteLine("said: " + string.Join(" | ", got));
        Assert.Equal(new[] { "got [Second User]" }, got);
    }

    /// <summary>A storm of slow lookups never holds more than ServiceCallThreads threads, and every script answers.</summary>
    [Fact]
    public void TheServiceLaneStaysWithinItsThreadCap()
    {
        using var h = new SchedulerHarness(cfg => cfg.Configs["InWorldz.Phlox"].Set("ServiceCallThreads", "3"));
        var ids = Enumerable.Range(0, 9).Select(_ => UUID.Random()).ToList();
        for (int i = 0; i < ids.Count; i++) UserAccountHelpers.CreateUserWithInventory(h.Scene, "Storm", "User" + i, ids[i], "pw");
        InstallAccountDelay(h, _ => 400);
        foreach (var id in ids)
            h.RezScript(@"default { state_entry() { llSay(0, ""storm "" + llGetUsername(""" + id + @""")); } }");

        var exe = typeof(global::Phlox.ScriptEngine.PhloxEngine).GetField("m_ExeScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Engine)!;
        var count = exe.GetType().GetProperty("ServiceThreadCount", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int max = 0;
        PumpUntil(h, () => { max = Math.Max(max, (int)count.GetValue(exe)!); return h.Said.Count(m => m.StartsWith("storm")) == ids.Count; }, TimeSpan.FromSeconds(30));
        _out.WriteLine($"answered {h.Said.Count(m => m.StartsWith("storm"))}/{ids.Count}; service threads peaked at {max}");
        Assert.Equal(ids.Count, h.Said.Count(m => m.StartsWith("storm")));
        Assert.InRange(max, 1, 3);
    }
}
