/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionReadyStatusMonitor"/> implementation. Contains the
/// "only enable the watchdogs when all regions are ready" wiring that previously lived
/// inline in <see cref="OpenSimBase.Initialize"/>; behavior is preserved verbatim and
/// merely composed out of the inheritance chain. Watchdog operations are delegated to
/// the shared <see cref="IRuntimeMonitoringController"/> so the policy can be tested
/// with a fake controller.
/// </summary>
public sealed class RegionReadyStatusMonitor : IRegionReadyStatusMonitor
{
    private readonly IRuntimeMonitoringController _monitoring;

    public RegionReadyStatusMonitor()
        : this(new RuntimeMonitoringController())
    {
    }

    public RegionReadyStatusMonitor(IRuntimeMonitoringController monitoring)
    {
        _monitoring = monitoring;
    }

    public void Attach(SceneManager sceneManager)
    {
        // Only enable the watchdogs when all regions are ready.  Otherwise we get false
        // positives when cpu is heavily used during initial startup.
        //
        // FIXME: It's also possible that region ready status should be flipped during an
        // OAR load since this also makes heavy use of the CPU.
        sceneManager.OnRegionsReadyStatusChange += sm => OnRegionsReadyStatusChanged(sm.AllRegionsReady);
    }

    public void OnRegionsReadyStatusChanged(bool allRegionsReady)
    {
        if (allRegionsReady)
        {
            _monitoring.EnableMemoryWatchdog();
            _monitoring.EnableWatchdog();
        }
        else
        {
            _monitoring.DisableMemoryWatchdog();
            _monitoring.DisableWatchdog();
        }
    }
}
