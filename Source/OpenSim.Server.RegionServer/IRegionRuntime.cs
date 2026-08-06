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
/// Adapter that owns the legacy region runtime (<see cref="OpenSim"/>) lifecycle
/// on behalf of the generic host.
/// </summary>
/// <remarks>
/// This indirection lets the host start and stop the region simulator without the
/// process lifetime being owned by a static <c>Application.Main()</c> loop or by a
/// blocking <c>ManualResetEvent</c> inside <c>OpenSimBackground</c>.
/// </remarks>
public interface IRegionRuntime
{
    /// <summary>
    /// Builds the legacy configuration source and starts the region simulator.
    /// This call is non-blocking: it returns once startup has been initiated.
    /// Idempotent — subsequent calls are ignored.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Shuts the region simulator down. Safe to call when the runtime was never
    /// initialized or has already stopped.
    /// </summary>
    void Stop();
}
