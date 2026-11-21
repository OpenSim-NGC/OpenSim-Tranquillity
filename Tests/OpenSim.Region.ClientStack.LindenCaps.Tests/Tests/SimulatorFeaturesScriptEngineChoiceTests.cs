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
/// OpenSimExtras["ScriptEngineChoice"] is true when the region keeps a script engine chosen for a script, and is not
/// sent when it does not. Each test builds its own scene and touches no process-wide state, so the class runs in parallel.
/// </summary>
public class SimulatorFeaturesScriptEngineChoiceTests
{
    private static SimulatorFeaturesModule LoadRegion(string choiceSetting)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        if (choiceSetting is not null)
            startup.Set("ScriptEngineChoice", choiceSetting);
        Scene scene = new SceneHelpers().SetupScene("Example Region", UUID.Random(), 1000, 1000, config);
        var features = new SimulatorFeaturesModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), features);
        return features;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void ARegionThatKeepsChoicesSaysSo(string setting)
    {
        SimulatorFeaturesModule features = LoadRegion(setting);

        Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngineChoice", out OSD value));
        Assert.Equal(OSDType.Boolean, value.Type);
        Assert.True(value.AsBoolean());
    }

    [Fact]
    public void ARegionThatKeepsNoChoicesSendsNoFlag()
    {
        SimulatorFeaturesModule features = LoadRegion("false");

        Assert.False(features.TryGetOpenSimExtraFeature("ScriptEngineChoice", out _));
        // the engine list is sent as before
        Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngines", out _));
    }
}
