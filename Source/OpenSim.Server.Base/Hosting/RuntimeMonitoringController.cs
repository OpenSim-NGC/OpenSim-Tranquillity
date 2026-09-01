/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework.Monitoring;

namespace OpenSim.Server.Base.Hosting;

public sealed class RuntimeMonitoringController : IRuntimeMonitoringController
{
    public void EnableWatchdog() => Watchdog.Enabled = true;
    public void DisableWatchdog() => Watchdog.Enabled = false;

    public void EnableMemoryWatchdog() => MemoryWatchdog.Enabled = true;
    public void DisableMemoryWatchdog() => MemoryWatchdog.Enabled = false;

    public void StopWorkManager() => WorkManager.Stop();
}
