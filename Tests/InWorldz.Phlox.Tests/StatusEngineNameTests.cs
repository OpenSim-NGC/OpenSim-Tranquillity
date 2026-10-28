/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The engine the status command names for a script it does not run is the engine the region chose for it
/// (Scene.ResolveScriptEngine: header, saved state, claim, default), not a guess from the first line alone.
/// </summary>
public class StatusEngineNameTests
{
    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases { get; } = Array.Empty<string>();
        public bool Claims { get; set; }
        public bool HoldsState { get; set; }
        public Engine(string name) { ScriptEngineName = name; }
        public bool ClaimsScript(string scriptSource) => Claims && scriptSource.StartsWith("--!slua");
        public bool HasSavedScriptState(UUID itemID, UUID assetID) => HoldsState;
    }

    private static (Scene scene, TaskInventoryItem item) Region(string source, Engine phlox, Engine other)
    {
        Scene scene = new SceneHelpers().SetupScene();
        // The test scene reads no [Startup] section, so it has no default engine until one is set.
        typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(scene, phlox.ScriptEngineName);
        scene.StackModuleInterface<IScriptModule>(phlox);
        scene.StackModuleInterface<IScriptModule>(other);
        SceneObjectGroup group = SceneHelpers.AddSceneObject(scene);
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart, "probe", source);
        return (scene, item);
    }

    [Fact]
    public void TheStatusNamesTheEngineTheRegionChoseNotJustTheOneTheHeaderSuggests()
    {
        var phlox = new Engine("YEngine");
        var other = new Engine("SLua") { Claims = true };
        var (scene, item) = Region("--!slua\nprint(1)", phlox, other);

        Assert.Equal("SLua", PhloxEngine.ChosenEngineName(scene, item));          // claimed
        other.Claims = false;
        Assert.Equal("YEngine", PhloxEngine.ChosenEngineName(scene, item)); // not claimed: the default
    }

    [Fact]
    public void TheStatusNamesTheEngineThatHoldsTheScriptsSavedState()
    {
        var phlox = new Engine("YEngine");
        var other = new Engine("SLua") { Claims = true };
        var (scene, item) = Region("--!slua\nprint(1)", phlox, other);

        Assert.Equal("SLua", PhloxEngine.ChosenEngineName(scene, item));
        phlox.HoldsState = true;
        Assert.Equal("YEngine", PhloxEngine.ChosenEngineName(scene, item));
    }

    [Fact]
    public void AScriptTheRegionHasNoEngineForIsNamedUnknown()
    {
        var phlox = new Engine("YEngine");
        var other = new Engine("SLua") { Claims = true };
        var (scene, item) = Region("--!slua\nprint(1)", phlox, other);

        Assert.Equal("SLua", PhloxEngine.ChosenEngineName(scene, item));
        Assert.Equal("(unknown)", PhloxEngine.ChosenEngineName(scene, null));
    }
}
