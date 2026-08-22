/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Carries the already-parsed command-line options into the host-managed
/// RegionServer runtime services.
/// </summary>
/// <remarks>
/// The values here come from the System.CommandLine parse in <c>Program</c>; the
/// region runtime turns them into a Nini <c>[Startup]</c> section rather than
/// re-parsing the raw process arguments, so <c>ConfigurationLoader</c> still
/// performs ini loading, <c>Include-*</c> expansion and key substitution.
/// </remarks>
public sealed class RegionHostOptions
{
    public RegionHostOptions(
        IReadOnlyList<string> iniFiles,
        string iniMaster,
        string iniDirectory,
        string consoleType,
        bool background)
    {
        IniFiles = iniFiles ?? Array.Empty<string>();
        IniMaster = iniMaster;
        IniDirectory = iniDirectory;
        ConsoleType = consoleType;
        Background = background;
    }

    /// <summary>Ini files supplied via <c>--inifile</c>, in load order (may be empty).</summary>
    public IReadOnlyList<string> IniFiles { get; }

    /// <summary>Master ini file (<c>--inimaster</c>).</summary>
    public string IniMaster { get; }

    /// <summary>Directory scanned for override ini files (<c>--inidirectory</c>).</summary>
    public string IniDirectory { get; }

    /// <summary>Console type (<c>--console</c>).</summary>
    public string ConsoleType { get; }

    /// <summary>
    /// True when the server runs without an interactive console prompt loop.
    /// In this mode host lifetime keeps the process alive rather than a blocking
    /// <c>ManualResetEvent</c>.
    /// </summary>
    public bool Background { get; }
}
