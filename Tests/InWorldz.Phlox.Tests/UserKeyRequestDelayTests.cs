/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
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
/// llRequestUserKey's pause, as YEngine's (LSL_Api.cs llRequestUserKey): 100 ms after a request that goes to the
/// user-account lookup, none for an empty name or for an avatar in this region. The sleep a call sets is read on the
/// engine's clock, frozen by the test; that clock is process-wide, hence "phlox-state". The call is made on a loaded,
/// idle script's own API object from the test thread while nothing pumps the scheduler.
/// </summary>
[Collection("phlox-state")]
public class UserKeyRequestDelayTests : IDisposable
{
    private readonly SchedulerHarness H;
    private ulong m_now;
    private bool m_frozen;

    public UserKeyRequestDelayTests()
    {
        Clock.SetSourceForTesting(() => m_frozen ? m_now : (ulong)Environment.TickCount64);
        H = new SchedulerHarness();
    }

    public void Dispose()
    {
        try { H.Dispose(); } finally { Clock.SetSourceForTesting(null); }
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

    /// <summary>The sleep in ms one llRequestUserKey call sets on a loaded, idle script (0: none).</summary>
    private int SleepOf(string username)
    {
        UUID item = H.RezScript("default { state_entry() { } }");
        Assert.True(H.PumpUntil(() => H.RunStateOf(item) == "Waiting"), "the script never loaded: " + H.RunStateOf(item));
        var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
        var api = ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item];
        RuntimeState st = ((Interpreter)H.InterpreterFor(item)).ScriptState;

        m_now = (ulong)Environment.TickCount64;
        m_frozen = true;
        st.RunState = RuntimeState.Status.Waiting;
        st.NextWakeup = 0;
        api.llRequestUserKey(username);
        return st.RunState == RuntimeState.Status.Sleeping ? (int)((long)st.NextWakeup - (long)m_now) : 0;
    }

    [Theory]
    [InlineData("Absent Person")]
    [InlineData("absent.person")]
    [InlineData("absentperson")]
    public void ARequestThatGoesToTheAccountLookupSleeps100Ms(string username)
        => Assert.Equal(100, SleepOf(username));

    [Fact]
    public void AnEmptyNameDoesNotSleep()
        => Assert.Equal(0, SleepOf(""));

    [Fact]
    public void AnAvatarInThisRegionIsAnsweredWithoutTheSleep()
    {
        var client = H.AddClient();
        var sp = H.Scene.GetScenePresence(client.AgentId);
        Assert.Equal(0, SleepOf(sp.Firstname + " " + sp.Lastname));
    }
}
