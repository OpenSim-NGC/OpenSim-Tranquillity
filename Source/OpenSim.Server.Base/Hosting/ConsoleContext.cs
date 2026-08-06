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
/// Holds the active <see cref="ICommandConsole"/> for this server process and keeps
/// <see cref="MainConsole.Instance"/> in sync so legacy code that still reads the
/// static singleton continues to work without modification.
/// </summary>
/// <remarks>
/// Register as a singleton in DI.  Server runtimes and hosted services should inject
/// <see cref="IConsoleContext"/> rather than touching <see cref="MainConsole.Instance"/>
/// directly.
/// <para>
/// The console instance is supplied at construction time (typically from
/// <see cref="IConsoleFactory"/>) and is immutable thereafter.  If you need to swap
/// the console during testing, create a fresh <see cref="ConsoleContext"/>.
/// </para>
/// </remarks>
public sealed class ConsoleContext : IConsoleContext
{
    /// <inheritdoc/>
    public ICommandConsole Console { get; }

    /// <summary>
    /// Initialises the context with <paramref name="console"/> and immediately writes
    /// the instance to <see cref="MainConsole.Instance"/> so legacy code sees it.
    /// </summary>
    /// <param name="console">The console to own.  Must not be <see langword="null"/>.</param>
    public ConsoleContext(ICommandConsole console)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));

        // Keep the legacy static singleton in sync for code that has not been
        // migrated to inject IConsoleContext yet.
        MainConsole.Instance = console;
    }
}
