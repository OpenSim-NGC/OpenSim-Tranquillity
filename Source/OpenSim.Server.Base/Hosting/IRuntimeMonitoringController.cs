/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Server.Base.Hosting;

public interface IRuntimeMonitoringController
{
    void EnableWatchdog();
    void DisableWatchdog();
    void EnableMemoryWatchdog();
    void DisableMemoryWatchdog();
    void StopWorkManager();
}
