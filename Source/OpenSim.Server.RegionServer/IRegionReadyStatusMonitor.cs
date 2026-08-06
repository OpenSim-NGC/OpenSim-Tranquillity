/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Enables the watchdog and memory watchdog only once all regions report ready, and
/// disables them otherwise. Extracted from <see cref="OpenSimBase.Initialize"/> so the
/// "regions ready" gating of the monitoring watchdogs can be composed and tested
/// independently of the startup inheritance chain.
/// </summary>
public interface IRegionReadyStatusMonitor
{
    /// <summary>
    /// Subscribes to <see cref="SceneManager.OnRegionsReadyStatusChange"/> so the
    /// watchdogs are toggled as the regions become ready or not-ready.
    /// </summary>
    void Attach(SceneManager sceneManager);

    /// <summary>
    /// Applies the watchdog enable/disable policy for the supplied readiness state.
    /// </summary>
    void OnRegionsReadyStatusChanged(bool allRegionsReady);
}
