/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Builds a legacy Nini <see cref="IConfigSource"/> from <see cref="ServerStartupOptions"/>
/// with the same precedence as <see cref="IniConfigurationExtensions.AddOpenSimIniFiles"/>.
/// </summary>
public sealed class LegacyIniConfigSourceAccessor : ILegacyConfigSourceAccessor
{
    /// <inheritdoc/>
    public IConfigSource ConfigSource { get; }

    public LegacyIniConfigSourceAccessor(ServerStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> existingIniFiles = GetOrderedIniFiles(options)
            .Where(File.Exists)
            .ToList();

        if (existingIniFiles.Count == 0)
        {
            ConfigSource = new IniConfigSource();
            return;
        }

        // Start with the lowest-precedence file, then merge upwards.
        IniConfigSource merged = new IniConfigSource(existingIniFiles[0]);
        for (int i = 1; i < existingIniFiles.Count; i++)
            merged.Merge(new IniConfigSource(existingIniFiles[i]));

        ConfigSource = merged;
    }

    private static IEnumerable<string> GetOrderedIniFiles(ServerStartupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.IniMaster))
            yield return options.IniMaster;

        foreach (string file in options.IniFiles)
        {
            if (!string.IsNullOrWhiteSpace(file))
                yield return file;
        }

        if (!string.IsNullOrWhiteSpace(options.IniDirectory)
            && Directory.Exists(options.IniDirectory))
        {
            foreach (string file in Directory.GetFiles(options.IniDirectory, "*.ini")
                                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }
}
