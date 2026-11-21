/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse.StructuredData;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// A script engine may mark itself early access (<see cref="IScriptModule.EarlyAccess"/>, false unless the engine says
/// otherwise). The region lists the early-access engines it runs, by ScriptEngineName, under
/// OpenSimExtras["ScriptEngineEarlyAccess"]; when it runs none, the key is not sent, so the reply is as before.
/// Each test builds its own scene and modules and touches no process-wide state, so the class runs in parallel.
/// </summary>
public class SimulatorFeaturesScriptEngineEarlyAccessTests
{
    /// <summary>An engine that says nothing about early access.</summary>
    private sealed class PlainEngine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public PlainEngine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
    }

    /// <summary>An engine that marks itself early access.</summary>
    private sealed class EarlyEngine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public EarlyEngine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
        bool IScriptModule.EarlyAccess => true;
    }

    private static SimulatorFeaturesModule LoadRegion(params MockScriptEngine[] engines)
    {
        Scene scene = new SceneHelpers().SetupScene();
        foreach (MockScriptEngine engine in engines)
        {
            engine.Initialise(new IniConfigSource());
            engine.AddRegion(scene);
        }
        SimulatorFeaturesModule features = new SimulatorFeaturesModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), features);
        return features;
    }

    private static string[] Names(OSD list)
    {
        OSDArray array = Assert.IsType<OSDArray>(list);
        return array.Select(v => v.AsString()).ToArray();
    }

    [Fact]
    public void AnEngineIsNotEarlyAccessUnlessItSaysSo()
    {
        Assert.False(((IScriptModule)new PlainEngine("Example.Engine")).EarlyAccess);
        Assert.True(((IScriptModule)new EarlyEngine("Example.Engine")).EarlyAccess);
    }

    [Fact]
    public void TheRegionListsItsEarlyAccessEnginesByName()
    {
        SimulatorFeaturesModule features = LoadRegion(
            new PlainEngine("Example.Final"), new EarlyEngine("SLua"), new EarlyEngine("Example.Early"));

        Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngineEarlyAccess", out OSD list));
        Assert.Equal(new[] { "SLua", "Example.Early" }, Names(list));
        // every engine is still in the engine list
        Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngines", out OSD all));
        Assert.Equal(new[] { "Example.Final", "SLua", "Example.Early" }, Names(all));
    }

    [Fact]
    public void ARegionWithNoEarlyAccessEngineSendsNoKey()
    {
        SimulatorFeaturesModule features = LoadRegion(new PlainEngine("YEngine"), new PlainEngine("InWorldz.Phlox"));

        Assert.False(features.TryGetOpenSimExtraFeature("ScriptEngineEarlyAccess", out _));
        Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngines", out _));
    }
}
