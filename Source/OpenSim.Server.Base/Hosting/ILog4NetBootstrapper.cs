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
/// Provides a shared bootstrap path for log4net configuration selection and initialization.
/// </summary>
public interface ILog4NetBootstrapper
{
    string LogPath { get; set; }
    
    /// <summary>
    /// Resolves the log4net config path using a configured path with fallback to a server default.
    /// </summary>
    string ResolveConfigPath(string configuredPath, string defaultPath);

    /// <summary>
    /// Configures log4net and returns the effective config path used.
    /// </summary>
    string Configure(string configuredPath, string defaultPath);
}
