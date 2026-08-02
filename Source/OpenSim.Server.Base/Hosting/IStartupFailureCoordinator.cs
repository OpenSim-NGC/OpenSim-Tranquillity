/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Centralised policy for handling fatal startup failures and controlled host stop requests.
/// </summary>
public interface IStartupFailureCoordinator
{
    /// <summary>
    /// Records a fatal startup failure and throws an exception so host startup fails.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    void ThrowFatal(string message, Exception exception = null);

    /// <summary>
    /// Requests graceful host shutdown without forcing process termination.
    /// </summary>
    void RequestStop(string message, Exception exception = null);
}
