/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// What each kind of viewer's script save does to a script whose engine was chosen, through the region's real save path
/// (Scene.CapsUpdateTaskInventoryScriptAsset, as UpdateScriptTask calls it). The "target" values are the ones each
/// viewer's editor sends:
/// <list type="bullet">
/// <item>a viewer with only a Mono checkbox (Firestorm releases up to August 2026, and other viewers of that age):
/// "mono" or "lsl2";</item>
/// <item>a viewer with the four-way compiler list (Second Life's viewer since its Lua editor, current Firestorm, Aurora):
/// "lsl2", "mono", "lsl-luau" or "luau";</item>
/// <item>a viewer that names engines: "engine:&lt;Name&gt;";</item>
/// <item>a viewer that sends no target at all.</item>
/// </list>
/// No save that cannot name an engine wipes a saved one; choosing Lua does, as it is a choice of engine too.
/// </summary>
public class ScriptEngineChoiceViewerSaveTests : OpenSimTestCase
{
    private const string YEngine = "YEngine", Phlox = "InWorldz.Phlox";

    private sealed class Engine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public readonly List<UUID> Started = new();
        public Engine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
        ArrayList IScriptModule.GetScriptErrors(UUID itemID) => new();
        bool IScriptModule.HasScript(UUID itemID, out bool running) { running = false; return false; }
        bool IScriptModule.SuspendScript(UUID itemID) => true;
        bool IScriptModule.ResumeScript(UUID itemID) => true;
        bool IScriptModule.GetScriptState(UUID itemID) => false;
        void IScriptModule.SaveAllState() { }
        string IScriptModule.GetXMLState(UUID itemID) => string.Empty;
        bool IScriptModule.SetXMLState(UUID itemID, string xml) => false;

        public void Attach(Scene scene)
            => scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) =>
            {
                if (engine == m_name)
                    Started.Add(itemID);
            };
    }

    private sealed class Rig
    {
        public Scene Scene;
        public Engine Y = new(YEngine), P = new(Phlox);
        public SceneObjectPart Part;
        public TaskInventoryItem Item;
        public TestClient Client;

        public Rig()
        {
            var config = new IniConfigSource();
            config.AddConfig("Startup").Set("DefaultScriptEngine", YEngine);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            Scene.StackModuleInterface<IScriptModule>(Y);
            Scene.StackModuleInterface<IScriptModule>(P);
            Y.Attach(Scene);
            P.Attach(Scene);
            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", "default { state_entry() { } }");
            Client = new TestClient(SceneHelpers.GenerateAgentData(UUID.Random()), Scene);
        }

        public void Save(string target)
            => Scene.CapsUpdateTaskInventoryScriptAsset(Client, Item.ItemID, Part.UUID, true, UUID.Zero,
                Encoding.UTF8.GetBytes("default { state_entry() { llOwnerSay(\"saved\"); } }"), target);

        public uint Flags => Part.Inventory.GetInventoryItem(Item.ItemID).Flags;
        public string Chosen => ScriptEngineChoices.NameFor(ScriptEngineChoices.FromFlags(Flags));
        public ScriptCompileTarget LuaTarget => ScriptCompileTargets.FromFlags(Flags);
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    public void AMonoCheckboxViewerKeepsTheEngine(string target)
    {
        var rig = new Rig();
        rig.Save("engine:Phlox");
        rig.P.Started.Clear();

        rig.Save(target);
        rig.Save(target);

        Assert.Equal(Phlox, rig.Chosen);
        Assert.Equal(new[] { rig.Item.ItemID, rig.Item.ItemID }, rig.P.Started);
        Assert.Empty(rig.Y.Started);
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    public void AFourWayViewerSavingLslKeepsTheEngine(string target)
    {
        var rig = new Rig();
        rig.Save("engine:YEngine");

        rig.Save(target);

        Assert.Equal(YEngine, rig.Chosen);
        Assert.Equal(ScriptCompileTarget.Unspecified, rig.LuaTarget);
    }

    [Theory]
    [InlineData("lsl-luau", ScriptCompileTarget.LslOnLuau)]
    [InlineData("luau", ScriptCompileTarget.Luau)]
    public void AFourWayViewerChoosingLuaReplacesTheEngineWithTheLuaTarget(string target, ScriptCompileTarget expected)
    {
        var rig = new Rig();
        rig.Save("engine:Phlox");

        rig.Save(target);

        Assert.Null(rig.Chosen);
        Assert.Equal(expected, rig.LuaTarget);
    }

    [Fact]
    public void AViewerThatSendsNoTargetKeepsEverything()
    {
        var rig = new Rig();
        rig.Save("engine:Phlox");

        rig.Save(null);

        Assert.Equal(Phlox, rig.Chosen);
    }

    /// <summary>
    /// A save that carries no target (a viewer that cannot tell which target the script has) changes neither the saved
    /// engine nor a saved Lua target, and every other flag bit stays as it was.
    /// </summary>
    [Theory]
    [InlineData("engine:Phlox", Phlox, ScriptCompileTarget.Unspecified)]
    [InlineData("engine:YEngine", YEngine, ScriptCompileTarget.Unspecified)]
    [InlineData("luau", null, ScriptCompileTarget.Luau)]
    [InlineData("lsl-luau", null, ScriptCompileTarget.LslOnLuau)]
    public void ASaveWithNoTargetKeepsTheSavedEngineAndTheSavedLuaTarget(string first, string engine, ScriptCompileTarget lua)
    {
        var rig = new Rig();
        rig.Save(first);
        uint before = rig.Flags;

        rig.Save(null);
        rig.Save(null);

        Assert.Equal(before, rig.Flags);
        Assert.Equal(engine, rig.Chosen);
        Assert.Equal(lua, rig.LuaTarget);
    }

    [Fact]
    public void AnEngineNamingViewerChoosesAndClears()
    {
        var rig = new Rig();

        rig.Save("engine:Phlox");
        Assert.Equal(Phlox, rig.Chosen);
        rig.Save("engine:YEngine");
        Assert.Equal(YEngine, rig.Chosen);
        rig.Save("engine:");
        Assert.Null(rig.Chosen);
        Assert.Equal(ScriptCompileTarget.Unspecified, rig.LuaTarget);
    }

    [Fact]
    public void AnEngineNamingViewerReplacesALuaTarget()
    {
        var rig = new Rig();
        rig.Save("luau");

        rig.Save("engine:Phlox");

        Assert.Equal(Phlox, rig.Chosen);
        Assert.Equal(ScriptCompileTarget.Unspecified, rig.LuaTarget);
    }
}
