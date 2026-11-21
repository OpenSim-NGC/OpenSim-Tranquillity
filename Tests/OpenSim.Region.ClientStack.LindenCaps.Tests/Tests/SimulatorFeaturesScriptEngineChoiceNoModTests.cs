/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// OpenSimExtras["ScriptEngineChoiceNoMod"] is true when an object's owner may choose the engine of a script in it that
/// they cannot edit: the region keeps choices and [Startup] ScriptEngineChoiceNoMod is on. Otherwise it is not sent.
/// Each test builds its own scene and touches no process-wide state, so the class runs in parallel.
/// </summary>
public class SimulatorFeaturesScriptEngineChoiceNoModTests
{
    private static SimulatorFeaturesModule LoadRegion(string choice, string noMod)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        if (choice is not null)
            startup.Set("ScriptEngineChoice", choice);
        if (noMod is not null)
            startup.Set("ScriptEngineChoiceNoMod", noMod);
        Scene scene = new SceneHelpers().SetupScene("Example Region", UUID.Random(), 1000, 1000, config);
        var features = new SimulatorFeaturesModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), features);
        return features;
    }

    [Fact]
    public void TheFlagIsSentWhenOwnersMayChooseForScriptsTheyCannotEdit()
    {
        Assert.True(LoadRegion(null, "true").TryGetOpenSimExtraFeature("ScriptEngineChoiceNoMod", out OSD value));
        Assert.Equal(OSDType.Boolean, value.Type);
        Assert.True(value.AsBoolean());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "false")]
    [InlineData("false", "true")]
    public void OtherwiseTheFlagIsNotSent(string choice, string noMod)
    {
        Assert.False(LoadRegion(choice, noMod).TryGetOpenSimExtraFeature("ScriptEngineChoiceNoMod", out _));
    }
}
