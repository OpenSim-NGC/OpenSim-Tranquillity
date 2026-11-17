/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script's reset keeps the floor llMinEventDelay set and the time before which its next handler may not start:
/// RuntimeState.Reset clears the timer, permissions, listens and globals and leaves MinEventDelayMs and NextEventAllowedOn alone.
/// Another script engine that matches Phlox's reset relies on it.
/// </summary>
// No process-wide state: the class runs in parallel.
public class MinEventDelayResetTests
{
    [Fact]
    public void AResetKeepsTheFloorOfLlMinEventDelayAndTheTimeTheNextHandlerWaitsFor()
    {
        using var r = new ApiCallRig();
        r.Api.llMinEventDelay(1.0f);
        r.State.TimerInterval = 5000;   // a field a reset does clear, to tell when the reset has happened
        ulong waitsUntil = InWorldz.Phlox.Util.Clock.Now + 30_000;
        r.State.NextEventAllowedOn = waitsUntil;
        Assert.Equal(1000, r.State.MinEventDelayMs);

        r.H.Engine.ResetScript(r.Item);

        Assert.True(r.H.PumpUntil(() => r.State.TimerInterval == 0, TimeSpan.FromSeconds(30)), "the reset did not happen");
        Assert.Equal(1000, r.State.MinEventDelayMs);
        Assert.True(r.State.NextEventAllowedOn >= waitsUntil, "the time the next handler waits for was cleared: " + r.State.NextEventAllowedOn);
    }
}
