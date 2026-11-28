/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace OpenSim.Server.Base.Tests;

/// <summary>
/// Guards runtimeconfig.template.json against the mistake found in F-1351: the .NET SDK already
/// wraps a template's top-level properties in "runtimeOptions" when it writes the built
/// *.runtimeconfig.json, so a template that itself starts with a "runtimeOptions" wrapper ends up
/// doubly nested (runtimeOptions.runtimeOptions.configProperties) and the host never reads it
/// (https://learn.microsoft.com/en-us/dotnet/core/runtime-config/#runtimeconfigtemplatejson).
/// GC and thread-pool keys must sit directly under a top-level "configProperties", or the process
/// silently keeps the runtime's defaults instead of the configured ones.
/// </summary>
public sealed class RuntimeConfigTemplateTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    public static IEnumerable<object[]> TemplatePaths()
    {
        yield return new object[] { Path.Combine("Source", "OpenSim.Server.RegionServer", "runtimeconfig.template.json") };
        yield return new object[] { Path.Combine("Source", "OpenSim.Server.GridServer", "runtimeconfig.template.json") };
        yield return new object[] { Path.Combine("Source", "OpenSim.Server.MoneyServer", "runtimeconfig.template.json") };
    }

    [Theory]
    [MemberData(nameof(TemplatePaths))]
    public void Template_has_no_runtimeOptions_wrapper(string relativePath)
    {
        string path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"template not found: {path}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

        Assert.False(
            doc.RootElement.TryGetProperty("runtimeOptions", out _),
            $"{relativePath}: a top-level \"runtimeOptions\" wrapper doubly-nests every setting inside it " +
            "(runtimeOptions.runtimeOptions.configProperties in the built file), so the host never reads them. " +
            "Settings go directly under a top-level \"configProperties\".");
    }

    [Theory]
    [MemberData(nameof(TemplatePaths))]
    public void GC_and_threadpool_settings_sit_directly_under_configProperties(string relativePath)
    {
        string path = Path.Combine(RepoRoot(), relativePath);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

        List<string> badPaths = new();
        FindMisplacedSettings(doc.RootElement, new Stack<string>(), badPaths);

        Assert.True(
            badPaths.Count == 0,
            $"{relativePath}: GC/ThreadPool settings not directly under a top-level \"configProperties\" " +
            $"(the host reads only that path): {string.Join(", ", badPaths)}");
    }

    // Walks the whole document for any "System.GC.*" or "System.Threading.ThreadPool.*" key and
    // records it as misplaced unless its path from the document root is exactly
    // ["configProperties", "<key>"].
    private static void FindMisplacedSettings(JsonElement element, Stack<string> path, List<string> badPaths)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty prop in element.EnumerateObject())
        {
            bool isGuardedSetting = prop.Name.StartsWith("System.GC.", StringComparison.Ordinal)
                || prop.Name.StartsWith("System.Threading.ThreadPool.", StringComparison.Ordinal);

            if (isGuardedSetting)
            {
                bool atExpectedDepth = path.Count == 1 && path.Peek() == "configProperties";
                if (!atExpectedDepth)
                {
                    string fullPath = string.Join(".", path.Reverse().Append(prop.Name));
                    badPaths.Add(fullPath);
                }
            }

            path.Push(prop.Name);
            FindMisplacedSettings(prop.Value, path, badPaths);
            path.Pop();
        }
    }
}
