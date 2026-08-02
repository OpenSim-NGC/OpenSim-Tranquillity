/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Runs the region's startup and shutdown console-command scripts and manages the
/// optional periodic "timed script". Extracted from <see cref="OpenSim"/> so the
/// script orchestration can be composed and tested independently of the startup
/// inheritance chain.
/// </summary>
public interface IRegionStartupScriptService
{
    /// <summary>
    /// Runs the startup command script, if one is configured.
    /// </summary>
    void RunStartupScript(string startupCommandsFile);

    /// <summary>
    /// Starts the periodic timed script, if one is configured (i.e. not "disabled").
    /// </summary>
    void StartTimerScript(string timedScript, int timeIntervalSeconds);

    /// <summary>
    /// Runs the shutdown command script, if one is configured.
    /// </summary>
    void RunShutdownScript(string shutdownCommandsFile);

    /// <summary>
    /// Stops and disposes the periodic timed script if it is running.
    /// </summary>
    void Stop();
}
