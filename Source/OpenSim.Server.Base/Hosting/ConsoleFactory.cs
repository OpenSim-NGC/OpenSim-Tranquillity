/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Framework.Console;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Default implementation of <see cref="IConsoleFactory"/>.
/// </summary>
public sealed class ConsoleFactory : IConsoleFactory
{
    /// <inheritdoc/>
    public ICommandConsole Create(string consoleType, string prompt)
    {
        return (consoleType ?? "local").Trim().ToLowerInvariant() switch
        {
            "basic" => new CommandConsole(prompt),
            "rest"  => new RemoteConsole(prompt),
            "mock"  => new MockConsole(),
            _       => new LocalConsole(prompt),   // "local" or any unrecognised value
        };
    }
}
