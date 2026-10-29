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
/// A service call the test holds open: the stub blocks on it until the test opens it, so a call is outstanding for
/// exactly as long as the test says and no assertion rests on how long a sleep happened to last. The wait is capped
/// (<see cref="SafetyCap"/>) so a scheduler that runs the call on its own thread fails the test instead of hanging it.
/// </summary>
public sealed class ServiceGate : IDisposable
{
    public static readonly TimeSpan SafetyCap = TimeSpan.FromSeconds(60);
    private readonly ManualResetEventSlim m_open = new(false);
    private int m_entered, m_inside;

    /// <summary>Called on the calling thread as a call reaches the gate, before it waits.</summary>
    public Action OnEnter { get; set; }
    /// <summary>How many calls have reached the gate.</summary>
    public int Entered => Volatile.Read(ref m_entered);
    /// <summary>How many calls are waiting at the gate right now.</summary>
    public int Inside => Volatile.Read(ref m_inside);

    internal void Pass()
    {
        Interlocked.Increment(ref m_inside);
        try
        {
            OnEnter?.Invoke();
            Interlocked.Increment(ref m_entered);
            m_open.Wait(SafetyCap);
        }
        finally { Interlocked.Decrement(ref m_inside); }
    }

    public void Open() => m_open.Set();
    public void Dispose() => m_open.Set();
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

    /// <summary>The same stub, holding each lookup whose id maps to a gate until the test opens that gate.</summary>
    internal static void InstallAccountGate(SchedulerHarness h, Func<UUID, ServiceGate> gateForId)
        => InstallAccountDelay(h, id => { gateForId(id)?.Pass(); return 0; });

    /// <summary>Service threads waiting for work: a thread is idle again only after its call's answer was handled.</summary>
    private static int IdleServiceThreads(SchedulerHarness h)
    {
        var exe = typeof(global::Phlox.ScriptEngine.PhloxEngine).GetField("m_ExeScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Engine)!;
        return (int)exe.GetType().GetField("m_ServiceThreadsIdle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(exe)!;
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
        string n = iwGetAgentData(""" + slow + @""", DATA_NAME);
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

    internal const int TicksWhileWaiting = 15;

    /// <summary>
    /// A's lookup is held at a gate the test controls; B's timer ticks are counted from the moment the call reached
    /// the service. Stops counting once B has ticked <see cref="TicksWhileWaiting"/> times, or once the call is no
    /// longer held (it can leave the gate unopened only if the scheduler sat blocked in it for
    /// <see cref="ServiceGate.SafetyCap"/>).
    /// </summary>
    internal (int ticksWhileHeld, bool stillHeld, bool answeredEarly, string answer) RunGatedLiveness(string deferral)
    {
        using var h = Harness(deferral, timeoutMs: 120000);
        using var gate = new ServiceGate();
        var slow = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Slow", "Lookup", slow, "pw");
        InstallAccountGate(h, id => id == slow ? gate : null);

        h.RezScript(@"default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, ""B tick""); } }");
        Assert.True(PumpUntil(h, () => h.Said.Contains("B tick"), TimeSpan.FromSeconds(30)), "B never ticked");

        int saidAtEntry = -1;
        gate.OnEnter = () => saidAtEntry = h.Said.Count;
        h.RezScript(@"
default
{
    state_entry()
    {
        llSay(0, ""A asks"");
        string n = iwGetAgentData(""" + slow + @""", DATA_NAME);
        llSay(0, ""A got ["" + n + ""]"");
    }
}");
        Assert.True(PumpUntil(h, () => gate.Entered > 0, TimeSpan.FromSeconds(30)), "A's lookup never reached the service: " + string.Join(",", h.Said.TakeLast(5)));

        int TicksSinceEntry() => h.Said.Skip(saidAtEntry).Count(m => m == "B tick");
        PumpUntil(h, () => TicksSinceEntry() >= TicksWhileWaiting || gate.Inside == 0, TimeSpan.FromSeconds(30));
        bool stillHeld = gate.Inside > 0;
        int ticks = TicksSinceEntry();
        bool answeredEarly = h.Said.Any(m => m.StartsWith("A got"));

        gate.Open();
        Assert.True(PumpUntil(h, () => h.Said.Any(m => m.StartsWith("A got")), TimeSpan.FromSeconds(30)), "A never answered: " + string.Join(",", h.Said.TakeLast(5)));
        return (ticks, stillHeld, answeredEarly, h.Said.First(m => m.StartsWith("A got")));
    }

    /// <summary>
    /// THE HEADLINE PROOF. Script A asks for an account and the (stub) service holds the call until the test lets it
    /// go; script B, in the same region, ticks <see cref="TicksWhileWaiting"/> times while A's call is still held, and
    /// A then gets the right answer. A scheduler that ran the call on its own thread could not tick B at all while
    /// the call is held, so the count is a fact about the scheduler, not about how fast the machine is.
    /// </summary>
    [Fact]
    public void OtherScriptsKeepRunningWhileOneWaitsOnASlowService()
    {
        var (ticks, stillHeld, answeredEarly, answer) = RunGatedLiveness("auto");
        _out.WriteLine($"deferred: B ticked {ticks} times while A's call was held; A said '{answer}'");
        Assert.True(stillHeld, $"A's call left the gate before the test opened it (B ticked {ticks} times while it was held)");
        Assert.False(answeredEarly, "A answered while its call was still held");
        Assert.True(ticks >= TicksWhileWaiting, $"B ticked only {ticks} times while A's call was held");
        Assert.Equal("A got [Slow Lookup]", answer);
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
        using var h = Harness(timeoutMs: 2000);
        using var firstGate = new ServiceGate();
        using var secondGate = new ServiceGate();
        var first = UUID.Random(); var second = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "First", "User", first, "pw");
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Second", "User", second, "pw");
        InstallAccountGate(h, id => id == first ? firstGate : id == second ? secondGate : null);

        h.RezScript(@"
default
{
    state_entry()
    {
        string a = iwGetAgentData(""" + first + @""", DATA_NAME);
        llSay(0, ""T1 ["" + a + ""]"");
        string b = iwGetAgentData(""" + second + @""", DATA_NAME);
        llSay(0, ""T2 ["" + b + ""]"");
        llSay(0, ""done"");
    }
}");
        // The first call is held past its deadline: the script resumes with the failure value and parks in the second.
        Assert.True(PumpUntil(h, () => h.Said.Any(m => m.StartsWith("T1")) && secondGate.Inside > 0, TimeSpan.FromSeconds(30)), string.Join(",", h.Said));
        Assert.Equal(1, firstGate.Inside);
        // The first answer arrives now, while the script waits on the second; its thread is idle once that answer is handled.
        firstGate.Open();
        Assert.True(PumpUntil(h, () => firstGate.Inside == 0 && IdleServiceThreads(h) > 0, TimeSpan.FromSeconds(30)), "the first call's thread never finished");
        Assert.Equal(1, secondGate.Inside);
        secondGate.Open();
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
        using var firstGate = new ServiceGate();
        using var secondGate = new ServiceGate();
        var first = UUID.Random(); var second = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "First", "User", first, "pw");
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Second", "User", second, "pw");
        InstallAccountGate(h, id => id == first ? firstGate : id == second ? secondGate : null);

        h.Prim.Description = first.ToString();
        var item = h.RezScript(@"
default
{
    state_entry()
    {
        string n = iwGetAgentData(llGetObjectDesc(), DATA_NAME);
        llSay(0, ""got ["" + n + ""]"");
    }
}");
        Assert.True(PumpUntil(h, () => firstGate.Inside > 0, TimeSpan.FromSeconds(30)), "never parked in the first call: " + string.Join(",", h.Said));
        h.Prim.Description = second.ToString();
        h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, item);
        Assert.True(PumpUntil(h, () => secondGate.Inside > 0, TimeSpan.FromSeconds(30)), "the reset run never parked in its own call: " + string.Join(",", h.Said));
        // The first run's answer arrives now, while the reset run waits on its own call; its thread is idle once that answer is handled.
        firstGate.Open();
        Assert.True(PumpUntil(h, () => firstGate.Inside == 0 && IdleServiceThreads(h) > 0, TimeSpan.FromSeconds(30)), "the first call's thread never finished");
        Assert.Equal(1, secondGate.Inside);
        secondGate.Open();
        Assert.True(PumpUntil(h, () => h.Said.Any(m => m.StartsWith("got")), TimeSpan.FromSeconds(30)), string.Join(",", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        var got = h.Said.Where(m => m.StartsWith("got")).ToList();
        _out.WriteLine("said: " + string.Join(" | ", got));
        Assert.Equal(new[] { "got [Second User]" }, got);
    }

    /// <summary>
    /// A storm of lookups held at the service never holds more than ServiceCallThreads threads, and every script
    /// answers. The calls are held until the cap is full, so the lane is seen at its cap whatever the machine's speed.
    /// </summary>
    [Fact]
    public void TheServiceLaneStaysWithinItsThreadCap()
    {
        using var h = new SchedulerHarness(cfg => cfg.Configs["InWorldz.Phlox"].Set("ServiceCallThreads", "3"));
        using var gate = new ServiceGate();
        var ids = Enumerable.Range(0, 9).Select(_ => UUID.Random()).ToList();
        for (int i = 0; i < ids.Count; i++) UserAccountHelpers.CreateUserWithInventory(h.Scene, "Storm", "User" + i, ids[i], "pw");
        InstallAccountGate(h, _ => gate);
        foreach (var id in ids)
            h.RezScript(@"default { state_entry() { llSay(0, ""storm "" + iwGetAgentData(""" + id + @""", DATA_NAME)); } }");

        var exe = typeof(global::Phlox.ScriptEngine.PhloxEngine).GetField("m_ExeScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Engine)!;
        var count = exe.GetType().GetProperty("ServiceThreadCount", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int max = 0;
        void Sample() => max = Math.Max(max, (int)count.GetValue(exe)!);
        Assert.True(PumpUntil(h, () => { Sample(); return gate.Inside >= 3; }, TimeSpan.FromSeconds(30)), $"only {gate.Inside} calls reached the service");
        gate.Open();
        PumpUntil(h, () => { Sample(); return h.Said.Count(m => m.StartsWith("storm")) == ids.Count; }, TimeSpan.FromSeconds(30));
        _out.WriteLine($"answered {h.Said.Count(m => m.StartsWith("storm"))}/{ids.Count}; service threads peaked at {max}");
        Assert.Equal(ids.Count, h.Said.Count(m => m.StartsWith("storm")));
        Assert.InRange(max, 1, 3);
    }
}
