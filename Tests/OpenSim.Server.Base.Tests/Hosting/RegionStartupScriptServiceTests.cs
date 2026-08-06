/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="RegionStartupScriptService"/>, the extracted
/// startup/shutdown/timed console-command script orchestration. The protected
/// <c>RunCommandScript</c> is bridged in via a delegate, which lets these tests assert
/// exactly which scripts are dispatched without a live region.
/// </summary>
public sealed class RegionStartupScriptServiceTests
{
    [Fact]
    public void RunStartupScript_WithEmptyPath_DoesNotRunAnything()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.RunStartupScript(string.Empty);

        Assert.Empty(ran);
    }

    [Fact]
    public void RunStartupScript_WithPath_RunsThatScript()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.RunStartupScript("startup_commands.txt");

        Assert.Equal(new[] { "startup_commands.txt" }, ran);
    }

    [Fact]
    public void RunShutdownScript_WithEmptyPath_DoesNotRunAnything()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.RunShutdownScript(string.Empty);

        Assert.Empty(ran);
    }

    [Fact]
    public void RunShutdownScript_WithPath_RunsThatScript()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.RunShutdownScript("shutdown_commands.txt");

        Assert.Equal(new[] { "shutdown_commands.txt" }, ran);
    }

    [Fact]
    public void StartTimerScript_WhenDisabled_StopIsSafe()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.StartTimerScript("disabled", 1200);

        // No timer was created; Stop must not throw.
        sut.Stop();
        Assert.Empty(ran);
    }

    [Fact]
    public void StartTimerScript_WhenEnabled_StopDisposesWithoutThrowing()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.StartTimerScript("commands.txt", 1);

        sut.Stop();
    }

    [Fact]
    public void StartTimerScript_WhenEnabled_StopIsIdempotentAfterDisable()
    {
        var ran = new List<string>();
        var sut = new RegionStartupScriptService(ran.Add);

        sut.StartTimerScript("commands.txt", 1);
        sut.Stop();

        // After Stop the timer is considered "disabled" again; a second Stop is a no-op.
        sut.Stop();
    }
}
