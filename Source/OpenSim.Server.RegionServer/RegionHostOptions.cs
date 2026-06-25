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
/// Carries the raw command-line arguments and the resolved run mode into the
/// host-managed RegionServer runtime services.
/// </summary>
/// <remarks>
/// During the host-adoption migration the legacy region runtime still consumes a
/// Nini <c>ArgvConfigSource</c> built from the original process arguments. This
/// options object preserves those arguments and the foreground/background decision
/// so that lifetime ownership can move into hosted services instead of
/// <c>Application.Main()</c>.
/// </remarks>
public sealed class RegionHostOptions
{
    public RegionHostOptions(string[] args, bool background)
    {
        Args = args ?? Array.Empty<string>();
        Background = background;
    }

    /// <summary>The original command-line arguments (excluding the executable path).</summary>
    public string[] Args { get; }

    /// <summary>
    /// True when the server runs without an interactive console prompt loop.
    /// In this mode host lifetime keeps the process alive rather than a blocking
    /// <c>ManualResetEvent</c>.
    /// </summary>
    public bool Background { get; }
}
