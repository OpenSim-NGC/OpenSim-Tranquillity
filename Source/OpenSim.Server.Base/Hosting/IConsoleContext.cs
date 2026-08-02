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
/// Owns the process-wide <see cref="ICommandConsole"/> instance.
/// </summary>
/// <remarks>
/// Injecting this interface rather than reading <c>MainConsole.Instance</c> directly
/// makes the console dependency explicit and testable.  The implementation is
/// responsible for keeping <see cref="Framework.MainConsole.Instance"/> in sync.
/// </remarks>
public interface IConsoleContext
{
    /// <summary>The active command console for this server process.</summary>
    ICommandConsole Console { get; }
}
