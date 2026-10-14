/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using Nini.Config;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// [InWorldz.Phlox] ScriptEnginesPath: the parent folder of this engine's saved state and bytecode cache, so an
/// operator can keep script state out of the published binary tree. Named as YEngine names its own setting, but
/// read only from this engine's section - moving one engine's files never moves the other's.
/// </summary>
public class ScriptEnginesPathTests
{
    private static readonly string DefaultPath = Path.Combine("ScriptEngines", "Phlox");

    private static IConfig Phlox(string phloxPath, string yenginePath = null, string yengineLegacyPath = null)
    {
        var source = new IniConfigSource();
        var phlox = source.AddConfig("InWorldz.Phlox");
        if (phloxPath != null) phlox.Set("ScriptEnginesPath", phloxPath);
        var yengine = source.AddConfig("YEngine");
        if (yenginePath != null) yengine.Set("ScriptEnginesPath", yenginePath);
        if (yengineLegacyPath != null) yengine.Set("ScriptBasePath", yengineLegacyPath);
        return phlox;
    }

    [Fact]
    public void UnsetIsTheWorkingDirectorysScriptEnginesPhlox()
    {
        Assert.Equal(DefaultPath, PhloxEngine.ResolveScriptEnginePath(Phlox(null)));
        // No [InWorldz.Phlox] section at all is the same default, not a crash.
        Assert.Equal(DefaultPath, PhloxEngine.ResolveScriptEnginePath(null));
    }

    [Fact]
    public void PhloxKeyIsUsedAndPhloxIsAppendedToIt()
    {
        Assert.Equal(Path.Combine("/srv/opensim/data/ScriptEngines", "Phlox"),
            PhloxEngine.ResolveScriptEnginePath(Phlox("/srv/opensim/data/ScriptEngines")));
    }

    [Fact]
    public void YEnginesOwnSettingDoesNotMovePhlox()
    {
        // An operator who moved only YEngine's state keeps Phlox where it was: each engine reads its own
        // section, so neither setting can silently relocate the other engine's database.
        Assert.Equal(DefaultPath,
            PhloxEngine.ResolveScriptEnginePath(Phlox(null, "/srv/yengine", "/srv/older")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankValueCountsAsUnset(string blank)
    {
        Assert.Equal(DefaultPath, PhloxEngine.ResolveScriptEnginePath(Phlox(blank)));
    }

    [Fact]
    public void SpacesAroundTheValueAreTrimmed()
    {
        Assert.Equal(Path.Combine("/srv/phlox", "Phlox"),
            PhloxEngine.ResolveScriptEnginePath(Phlox("  /srv/phlox  ")));
    }

    [Fact]
    public void AnUnconfiguredEnginePointsAtTheOldFixedPaths()
    {
        // The engine before Initialise, and after an Initialise with no setting, keeps the paths Phlox used
        // before the setting existed, so an upgrade with no config change finds its state where it left it.
        var engine = new PhloxEngine();
        Assert.Equal(DefaultPath, engine.ScriptEnginePath);
        Assert.Equal(Path.Combine(DefaultPath, "state", "script_state.db"), engine.StateDbFile);
        Assert.Equal(Path.Combine(DefaultPath, "bytecode"), engine.BytecodeDir);
    }

    /// <summary>
    /// A configured path moves both the state database and the bytecode cache, and a real region start puts the
    /// files there: the folders exist, with the database in them, and nothing is written to the default path.
    /// </summary>
    [Fact]
    public void AConfiguredPathMovesTheStateDatabaseAndBytecodeCache()
    {
        string root = Path.Combine(Path.GetTempPath(), "phlox-enginespath-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ProductionBytecodeDir leaves the harness's rooted-folder test seam unset, so the loader takes the
            // folder the configured path gives it - which is what this test is about.
            using var h = new SchedulerHarness(
                configure: c => c.Configs["InWorldz.Phlox"].Set("ScriptEnginesPath", root),
                bytecodeDir: SchedulerHarness.ProductionBytecodeDir);

            string enginePath = Path.Combine(root, "Phlox");
            Assert.Equal(enginePath, h.Engine.ScriptEnginePath);
            Assert.Equal(Path.Combine(enginePath, "state", "script_state.db"), h.Engine.StateDbFile);
            Assert.Equal(Path.Combine(enginePath, "bytecode"), h.Engine.BytecodeDir);

            Assert.True(File.Exists(Path.Combine(enginePath, "state", "script_state.db")),
                "the state database was not created under the configured path");
            Assert.True(Directory.Exists(Path.Combine(enginePath, "bytecode")),
                "the bytecode cache folder was not created under the configured path");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

}
