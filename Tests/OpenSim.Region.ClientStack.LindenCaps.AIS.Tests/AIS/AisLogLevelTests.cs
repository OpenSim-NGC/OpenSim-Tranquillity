/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Nini.Config;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// What AIS writes to the log on a normal run, and at which level. Each region logs its set-up once and an agent
/// whose inventory holds duplicate system folders is looked up on every login, so the set-up lines are Debug and the
/// duplicate-folder warning is written once per agent for the life of the process.
///
/// <para>The logger factory is process-wide; this project's tests run one at a time.</para>
/// </summary>
public class AisLogLevelTests
{
    private static readonly UUID Agent = new("b1e0b6a0-5d0e-4a46-9f5e-3b0f6a6b0c11");
    private static readonly UUID OtherAgent = new("c2f1c7b1-6e1f-4b57-8a6f-4c1a7b7c1d22");

    public AisLogLevelTests() => AisInventory.ResetDuplicateWarnings();

    [Fact]
    public void a_regions_start_up_lines_are_debug_and_the_global_line_stays_information()
    {
        using var log = new CapturedLog();
        var scene = new SceneHelpers().SetupScene("QuietAis" + Guid.NewGuid().ToString("N"), UUID.Random(), 1000, 1000);
        var config = new IniConfigSource();
        config.AddConfig(AISv3Module.ConfigSection).Set("Enabled", "true");
        SceneHelpers.SetupSceneModules(scene, config, new AISv3Module());

        Assert.True(log.Messages(LogLevel.Information).Any(m => m.Contains("Enabled = true")), "the one line that reports the global setting is kept at Information");

        var regionLines = new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Error }
            .SelectMany(level => log.Messages(level)).Where(m => m.Contains("[AIS]") && m.Contains(scene.Name)).ToList();
        Assert.Empty(regionLines);

        var debug = log.Messages(LogLevel.Debug).Where(m => m.Contains("[AIS]") && m.Contains(scene.Name)).ToList();
        Assert.Equal(2, debug.Count);
    }

    private static FakeAisBackend TwoCurrentOutfits(UUID agent)
    {
        var backend = new FakeAisBackend(agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
        var first = UUID.Random();
        var second = UUID.Random();
        backend.AddFolder(first, root, "Current Outfit", 1, (short)FolderType.CurrentOutfit);
        backend.AddFolder(second, root, "Current Outfit", 5, (short)FolderType.CurrentOutfit);
        backend.CurrentOutfitId = first;
        return backend;
    }

    [Fact]
    public void the_duplicate_folder_warning_is_written_once_per_agent_and_repeats_are_debug()
    {
        var backend = TwoCurrentOutfits(Agent);

        using var log = new CapturedLog();
        AisInventory.GetCurrentOutfit(backend, Agent);
        AisInventory.GetCurrentOutfit(backend, Agent);
        AisInventory.GetCurrentOutfit(backend, Agent);

        var warnings = log.Warnings.Where(w => w.Contains("folders of type")).ToList();
        var repeats = log.Messages(LogLevel.Debug).Where(m => m.Contains("folders of type")).ToList();
        Assert.Single(warnings);
        Assert.Equal(2, repeats.Count);
        Assert.Equal(warnings[0], repeats[0]);
    }

    [Fact]
    public void a_second_agent_with_duplicates_gets_its_own_warning()
    {
        using var log = new CapturedLog();
        AisInventory.GetCurrentOutfit(TwoCurrentOutfits(Agent), Agent);
        AisInventory.GetCurrentOutfit(TwoCurrentOutfits(OtherAgent), OtherAgent);

        var warnings = log.Warnings.Where(w => w.Contains("folders of type")).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains(Agent.ToString()));
        Assert.Contains(warnings, w => w.Contains(OtherAgent.ToString()));
    }
}
