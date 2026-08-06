/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Creates an <see cref="ICommandConsole"/> for a given console-type string.
/// </summary>
/// <remarks>
/// Centralises the <c>switch</c> on the console-type string that was previously
/// duplicated in each server's <c>Program.cs</c>.  Register this as a singleton
/// in DI and inject it wherever a console instance needs to be created.
/// </remarks>
public interface IConsoleFactory
{
    /// <summary>
    /// Creates and returns an <see cref="ICommandConsole"/> matching
    /// <paramref name="consoleType"/>.
    /// </summary>
    /// <param name="consoleType">
    /// One of <c>basic</c>, <c>local</c>, <c>rest</c>, or <c>mock</c>.
    /// Any unrecognised value falls back to <c>local</c>.
    /// </param>
    /// <param name="prompt">The prompt string shown before each input line.</param>
    ICommandConsole Create(string consoleType, string prompt);
}
