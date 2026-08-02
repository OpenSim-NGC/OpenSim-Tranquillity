/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Owns the periodic diagnostics log timer that was previously embedded in
/// <c>BaseOpenSimServer</c>.
/// </summary>
/// <remarks>
/// Extracting this concern gives the region runtime an explicit seam for the
/// periodic "show stats" diagnostics output instead of it being started and stopped
/// implicitly inside the startup inheritance chain.
/// </remarks>
public interface IRegionDiagnosticsService
{
    /// <summary>
    /// Starts the periodic diagnostics timer using the <c>LogShowStatsSeconds</c>
    /// value from the supplied <c>[Startup]</c> configuration. A value of zero
    /// disables the timer.
    /// </summary>
    /// <param name="startupConfig">The <c>[Startup]</c> configuration section (may be null).</param>
    /// <param name="uptimeReport">Provides the current uptime report text.</param>
    /// <param name="threadsReport">Provides the current threads report text.</param>
    void Start(IConfig startupConfig, Func<string> uptimeReport, Func<string> threadsReport);

    /// <summary>
    /// Stops the periodic diagnostics timer. Safe to call when never started.
    /// </summary>
    void Stop();
}
