/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="RegionReadyStatusMonitor"/>, the extracted "enable watchdogs
/// only when all regions are ready" policy. A fake <see cref="IRuntimeMonitoringController"/>
/// records the watchdog calls so the policy can be asserted without real watchdog timers
/// (which throw when enabled before initialization).
/// </summary>
public sealed class RegionReadyStatusMonitorTests
{
    private sealed class FakeMonitoringController : IRuntimeMonitoringController
    {
        public List<string> Calls { get; } = new();

        public void EnableWatchdog() => Calls.Add(nameof(EnableWatchdog));
        public void DisableWatchdog() => Calls.Add(nameof(DisableWatchdog));
        public void EnableMemoryWatchdog() => Calls.Add(nameof(EnableMemoryWatchdog));
        public void DisableMemoryWatchdog() => Calls.Add(nameof(DisableMemoryWatchdog));
        public void StopWorkManager() => Calls.Add(nameof(StopWorkManager));
    }

    [Fact]
    public void OnRegionsReadyStatusChanged_WhenReady_EnablesWatchdogsInOrder()
    {
        var fake = new FakeMonitoringController();
        var sut = new RegionReadyStatusMonitor(fake);

        sut.OnRegionsReadyStatusChanged(true);

        Assert.Equal(
            new[] { nameof(FakeMonitoringController.EnableMemoryWatchdog), nameof(FakeMonitoringController.EnableWatchdog) },
            fake.Calls);
    }

    [Fact]
    public void OnRegionsReadyStatusChanged_WhenNotReady_DisablesWatchdogsInOrder()
    {
        var fake = new FakeMonitoringController();
        var sut = new RegionReadyStatusMonitor(fake);

        sut.OnRegionsReadyStatusChanged(false);

        Assert.Equal(
            new[] { nameof(FakeMonitoringController.DisableMemoryWatchdog), nameof(FakeMonitoringController.DisableWatchdog) },
            fake.Calls);
    }
}
