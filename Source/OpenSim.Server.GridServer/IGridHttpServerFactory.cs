/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Creates and starts the GridServer HTTP listeners described by the
/// <c>[Network]</c> configuration section.
/// </summary>
/// <remarks>
/// This contains the listener-creation logic that previously lived in
/// <c>HttpServerBase.ReadConfig</c>/<c>Initialise</c>; the behaviour is preserved
/// and merely composed out of the inheritance chain into a DI service.
/// </remarks>
public interface IGridHttpServerFactory
{
    /// <summary>
    /// Builds the HTTP server(s) from the <c>[Network]</c> section, starts them,
    /// registers the HTTP console commands and wires the console to the server.
    /// </summary>
    /// <param name="config">The fully merged configuration source.</param>
    /// <param name="console">The console to wire to the default/console server.</param>
    void CreateAndStart(IConfigSource config, ICommandConsole console);
}
