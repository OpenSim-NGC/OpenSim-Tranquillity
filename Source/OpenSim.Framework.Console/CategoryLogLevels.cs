/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;

#nullable enable

namespace OpenSim.Framework.Console;

/// <summary>
/// Per-category log thresholds read from the <c>Logging:LogLevel</c> settings, applied the way
/// Microsoft.Extensions.Logging applies them: the longest category prefix wins, matched without regard to case.
/// </summary>
/// <remarks>
/// The console and the log file both write through sinks that do their own level filtering, so each applies this
/// table itself. The <c>Default</c> entry is kept apart from the categories because the two sinks treat it
/// differently: the console takes it as its starting level, the log file ignores it.
/// </remarks>
public sealed class CategoryLogLevels
{
    /// <summary>A table with no entries.</summary>
    public static readonly CategoryLogLevels Empty = new(null, Array.Empty<KeyValuePair<string, LogLevel>>());

    // Longest prefix first, so the first match is the most specific one.
    private readonly KeyValuePair<string, LogLevel>[] _categories;

    private CategoryLogLevels(LogLevel? defaultLevel, KeyValuePair<string, LogLevel>[] categories)
    {
        Default = defaultLevel;
        _categories = categories;
    }

    /// <summary>The <c>Default</c> entry, or null when there is none.</summary>
    public LogLevel? Default { get; }

    /// <summary>Number of category entries, not counting <c>Default</c>.</summary>
    public int Count => _categories.Length;

    /// <summary>
    /// Builds a table from name/value pairs such as the children of the <c>Logging:LogLevel</c> section. A later pair
    /// replaces an earlier one with the same name. Pairs whose value is not a log level name are skipped.
    /// A trailing <c>*</c> on a name is dropped, so <c>Microsoft.*</c> means the same as <c>Microsoft.</c>.
    /// </summary>
    public static CategoryLogLevels FromSettings(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LogLevel? defaultLevel = null;
        Dictionary<string, LogLevel> categories = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> pair in settings)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || !Enum.TryParse(pair.Value?.Trim(), true, out LogLevel level))
                continue;

            string name = pair.Key.Trim();

            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                defaultLevel = level;
                continue;
            }

            name = name.TrimEnd('*');

            if (name.Length > 0)
                categories[name] = level;
        }

        KeyValuePair<string, LogLevel>[] ordered = categories
            .OrderByDescending(pair => pair.Key.Length)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CategoryLogLevels(defaultLevel, ordered);
    }

    /// <summary>
    /// Finds the entry for a category: the longest configured name that the category starts with.
    /// </summary>
    public bool TryGetLevel(string? category, out LogLevel level)
    {
        if (!string.IsNullOrEmpty(category))
        {
            foreach (KeyValuePair<string, LogLevel> entry in _categories)
            {
                if (category.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    level = entry.Value;
                    return true;
                }
            }
        }

        level = default;
        return false;
    }

    /// <summary>
    /// True when a message at <paramref name="level"/> passes the entry for the category; true when no entry matches.
    /// </summary>
    public bool Allows(string? category, LogLevel level) =>
        !TryGetLevel(category, out LogLevel minimum) || (minimum != LogLevel.None && level >= minimum);
}
