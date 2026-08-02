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
/// Common command-line startup options shared by all three server executables.
/// Derived classes add server-specific switches.
/// </summary>
/// <remarks>
/// These are value-type option captures populated by System.CommandLine before the
/// generic host is built.  They are registered in DI so services can read them at
/// startup time without re-parsing the command line.
/// </remarks>
public class ServerStartupOptions
{
    /// <summary>
    /// Path to the log4net configuration file.
    /// Defaults to the assembly name with a <c>.dll.config</c> extension when empty.
    /// </summary>
    public string LogConfig { get; init; } = string.Empty;

    /// <summary>
    /// Zero or more explicit <c>.ini</c> files to load in addition to the master.
    /// Applied in order after <see cref="IniMaster"/>.
    /// </summary>
    public IReadOnlyList<string> IniFiles { get; init; } = [];

    /// <summary>
    /// Path to the master ini file, which is loaded first and provides baseline defaults.
    /// </summary>
    public string IniMaster { get; init; } = string.Empty;

    /// <summary>
    /// Directory from which all <c>*.ini</c> files are loaded last (sorted by name).
    /// Ignored when the directory does not exist.
    /// </summary>
    public string IniDirectory { get; init; } = "config";

    /// <summary>
    /// Console type to instantiate.  Valid values: <c>basic</c>, <c>local</c>, <c>rest</c>, <c>mock</c>.
    /// Defaults to <c>local</c>.
    /// </summary>
    public string ConsoleType { get; init; } = "local";
}
