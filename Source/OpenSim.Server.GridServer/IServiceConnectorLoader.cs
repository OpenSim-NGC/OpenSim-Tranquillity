/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Loads and activates the GridServer service connectors described in the
/// legacy <c>[Startup]</c> / <c>[ServiceList]</c> configuration sections.
/// </summary>
/// <remarks>
/// Connector loading was previously performed inline in the GridServer boot
/// routine. Extracting it into an injectable service keeps connector activation
/// separate from HTTP listener startup and plugin loading.
/// </remarks>
public interface IServiceConnectorLoader
{
    /// <summary>
    /// Reads the connector list from <paramref name="config"/>, activates each
    /// connector against the appropriate HTTP listener and returns the loaded
    /// connectors.
    /// </summary>
    /// <param name="config">The legacy Nini configuration source.</param>
    /// <returns>The connectors that were loaded successfully.</returns>
    IReadOnlyList<IServiceConnector> LoadConnectors(IConfigSource config);
}
