/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Server.GridServer;

/// <summary>
/// Runtime coordinator for GridServer startup infrastructure and shutdown behaviour.
/// </summary>
/// <remarks>
/// Owns the HTTP listener bootstrap, service connector activation and plugin loader
/// setup that previously lived in the GridServer boot routine. Separating this from
/// <see cref="GridService"/> keeps the hosted service focused on host-lifetime
/// orchestration.
/// </remarks>
public interface IGridServerRuntime
{
    /// <summary>
    /// Boots the HTTP listeners, activates the service connectors and prepares the
    /// plugin loader. Safe to call multiple times; subsequent calls are ignored.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Performs runtime shutdown for GridServer infrastructure.
    /// </summary>
    void Stop();
}
