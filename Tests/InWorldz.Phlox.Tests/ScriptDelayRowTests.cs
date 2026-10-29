/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llEmail's 20 s delay applies with or without an email module (SL: "This function causes the script to sleep for
/// 20.0 seconds"; Halcyon sleeps in a finally, LSLSystemAPI.cs:3944-3957). llSoundPreload, which SL deprecated in
/// favour of llPreloadSound, preloads without llPreloadSound's 1 s delay (Halcyon: no delay,
/// LSLSystemAPI.cs:4050-4057, "documented to have no delay").
/// <para>A delay is the script's ScriptSleep: the call sets the script Sleeping with its wake-up that far ahead. The
/// clock is read, not frozen (the delays are seconds apart from any test noise), so no process-wide state is touched
/// and the class runs in parallel.</para>
/// </summary>
// No test reaches a network service.
public class ScriptDelayRowTests
{
    private static (SchedulerHarness H, UUID Item) Loaded()
    {
        var h = new SchedulerHarness();
        UUID item = h.RezScript("default { state_entry() { } }");
        Assert.True(h.PumpUntil(() => h.RunStateOf(item) == "Waiting"), "the script never loaded: " + h.RunStateOf(item));
        return (h, item);
    }

    /// <summary>The sleep one call sets, in ms (0: none).</summary>
    private static long Sleep(SchedulerHarness h, UUID item, Action<LSLSystemAPI> call)
    {
        RuntimeState st = ((Interpreter)h.InterpreterFor(item)).ScriptState;
        st.RunState = RuntimeState.Status.Waiting;
        st.NextWakeup = 0;
        object exe = Field(h.Engine, "m_ExeScheduler");
        call(((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item]);
        return st.RunState == RuntimeState.Status.Sleeping ? (long)st.NextWakeup - (long)Clock.Now : 0;
    }

    [Fact]
    public void LlEmailSleepsTwentySecondsWithoutAnEmailModule()
    {
        var (h, item) = Loaded();
        using (h)
        {
            long ms = Sleep(h, item, api => api.llEmail("someone@example.org", "subject", "body"));
            Assert.InRange(ms, 19000, 20000);
        }
    }

    [Fact]
    public void LlSoundPreloadDoesNotSleep()
    {
        var (h, item) = Loaded();
        using (h)
        {
            Assert.Equal(0, Sleep(h, item, api => api.llSoundPreload(UUID.Random().ToString())));
        }
    }

    [Fact]
    public void LlPreloadSoundStillSleepsOneSecond()
    {
        var (h, item) = Loaded();
        using (h)
        {
            Assert.InRange(Sleep(h, item, api => api.llPreloadSound(UUID.Random().ToString())), 900, 1000);
        }
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }
}
