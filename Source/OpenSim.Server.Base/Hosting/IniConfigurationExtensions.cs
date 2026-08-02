/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Configuration;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Extension methods that add OpenSimulator ini file sources to an
/// <see cref="IConfigurationBuilder"/> using the load-order rules defined by
/// <see cref="ServerStartupOptions"/>.
/// </summary>
/// <remarks>
/// Precedence (lowest → highest):
/// <list type="number">
///   <item><see cref="ServerStartupOptions.IniMaster"/></item>
///   <item>Explicit files in <see cref="ServerStartupOptions.IniFiles"/> (in order)</item>
///   <item>All <c>*.ini</c> files found in <see cref="ServerStartupOptions.IniDirectory"/> (sorted)</item>
/// </list>
/// Each source is registered as optional so a missing file is silently skipped rather
/// than crashing startup.  The <c>reloadOnChange</c> flag is intentionally left
/// <see langword="false"/> at this layer because live ini reload is not yet supported by
/// most of the legacy runtime; set it explicitly if you need it for a specific source.
/// </remarks>
public static class IniConfigurationExtensions
{
    /// <summary>
    /// Adds OpenSimulator ini file sources to <paramref name="builder"/> using the
    /// precedence rules encoded in <paramref name="options"/>.
    /// </summary>
    /// <param name="builder">The configuration builder to add sources to.</param>
    /// <param name="options">Startup options that describe which files to load.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static IConfigurationBuilder AddOpenSimIniFiles(
        this IConfigurationBuilder builder,
        ServerStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // 1. Master ini — baseline defaults, loaded first.
        if (!string.IsNullOrWhiteSpace(options.IniMaster))
            builder.AddIniFile(options.IniMaster, optional: true, reloadOnChange: false);

        // 2. Explicit additional files, in the order the caller listed them.
        foreach (string file in options.IniFiles)
        {
            if (!string.IsNullOrWhiteSpace(file))
                builder.AddIniFile(file, optional: true, reloadOnChange: false);
        }

        // 3. Directory-based wildcard, sorted so load order is deterministic.
        if (!string.IsNullOrWhiteSpace(options.IniDirectory)
            && Directory.Exists(options.IniDirectory))
        {
            foreach (string file in Directory.GetFiles(options.IniDirectory, "*.ini")
                                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                builder.AddIniFile(file, optional: true, reloadOnChange: false);
            }
        }

        return builder;
    }
}
