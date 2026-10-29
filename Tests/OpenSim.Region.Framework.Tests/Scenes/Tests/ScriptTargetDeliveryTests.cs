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
/// A script saved with a Luau compile target is run by the engine the region chose for it and by no other. The region
/// chooses the engine; an engine that can read the target itself (it implements <see cref="IScriptEngineSelection"/>)
/// hears every rez and lets go of a script that is not its own, and an engine that cannot read it (it reads only the
/// script's first line, as YEngine does) is not told about a script that carries a target unless it is the engine chosen.
/// </summary>
public class ScriptTargetDeliveryTests : OpenSimTestCase
{
    private const string Default = "Alpha", Other = "Beta", Plain = "Yeng";

    /// <summary>An engine that reads only the script's first line to decide whether a rez is its own, as YEngine does.</summary>
    private class FirstLineEngine : MockScriptEngine, IScriptModule, IScriptStateProvider
    {
        public new string ScriptEngineName { get; }
        public readonly List<UUID> Heard = new();
        public readonly List<UUID> Started = new();

        public FirstLineEngine(string name) { ScriptEngineName = name; }

        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool SuspendScript(UUID itemID) => true;
        public bool ResumeScript(UUID itemID) => true;
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public bool GetScriptState(UUID itemID) => false;
        public void SetScriptState(UUID itemID, bool state, bool self) { }
        public void SaveAllState() { }
        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool HasSavedScriptState(UUID itemID, UUID assetID) => false;

        /// <summary>True for an engine that, like SLua and Phlox, puts a script's compile target ahead of its first line.</summary>
        protected bool ReadsTarget { get; init; }

        private Scene m_scene;

        public void Attach(Scene scene)
        {
            m_scene = scene;
            scene.EventManager.OnRezScript += OnRez;
        }

        private void OnRez(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
        {
            Heard.Add(itemID);
            // the first line names an engine, otherwise the engine the region passed is meant
            string named = engine;
            TaskInventoryItem item = m_scene.GetSceneObjectPart(localID)?.Inventory.GetInventoryItem(itemID);
            bool targeted = ReadsTarget && item is not null && ScriptCompileTargets.FromFlags(item.Flags) != ScriptCompileTarget.Unspecified;
            if (!targeted && script.StartsWith("//") && script.IndexOf('\n') > 5)
            {
                string line = script[2..script.IndexOf('\n')].Trim();
                int colon = line.IndexOf(':');
                if (colon >= 3)
                    named = line[..colon].TrimEnd();
            }
            if (named == ScriptEngineName)
                Started.Add(itemID);
        }
    }

    /// <summary>An engine that can read the target: it starts what the region selected for it and lets go of the rest.</summary>
    private sealed class TargetAwareEngine : FirstLineEngine, IScriptEngineSelection
    {
        public IReadOnlyList<string> HeaderAliases => Array.Empty<string>();
        public bool AcceptsTargets { get; set; }

        public TargetAwareEngine(string name) : base(name) { ReadsTarget = true; }

        public bool ClaimsScript(string scriptSource) => false;
        public bool AcceptsCompileTarget(ScriptCompileTarget target) => AcceptsTargets;
    }

    private sealed class Rig
    {
        public readonly Scene Scene;
        public readonly TargetAwareEngine Alpha = new(Default);
        public readonly TargetAwareEngine Beta = new(Other) { AcceptsTargets = true };
        public readonly FirstLineEngine Yeng = new(Plain);
        public readonly SceneObjectPart Part;
        public readonly TaskInventoryItem Item;
        public readonly TestClient Client;
        public readonly List<UUID> OtherSubscriberHeard = new();

        public Rig(string source, bool betaAcceptsTargets = true)
        {
            Beta.AcceptsTargets = betaAcceptsTargets;
            var config = new IniConfigSource();
            config.AddConfig("Startup").Set("DefaultScriptEngine", Default);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            Scene.StackModuleInterface<IScriptModule>(Alpha);
            Scene.StackModuleInterface<IScriptModule>(Beta);
            Scene.StackModuleInterface<IScriptModule>(Yeng);
            Alpha.Attach(Scene);
            Beta.Attach(Scene);
            Yeng.Attach(Scene);
            // something that listens to rez events and is not a script engine
            Scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) => OtherSubscriberHeard.Add(itemID);
            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", source);
            Client = new TestClient(SceneHelpers.GenerateAgentData(UUID.Random()), Scene);
        }

        public void WithTarget(ScriptCompileTarget target)
        {
            Item.Flags = ScriptCompileTargets.WithTarget(Item.Flags, target);
            Part.Inventory.GetInventoryItem(Item.ItemID).Flags = Item.Flags;   // the part's own copy, which the engines read
        }

        public void Start() => Assert.True(Part.Inventory.CreateScriptInstance(Item, 0, false, Default, 0));

        public void Save(string source, string target)
            => Scene.CapsUpdateTaskInventoryScriptAsset(Client, Item.ItemID, Part.UUID, true, UUID.Zero, Encoding.UTF8.GetBytes(source), target);
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    public void AnEngineThatReadsOnlyTheFirstLineIsNotToldOfAScriptWithATargetItsFirstLineNames(ScriptCompileTarget target)
    {
        var rig = new Rig("//" + Plain + ":\nx");
        rig.WithTarget(target);

        rig.Start();

        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);
        Assert.Empty(rig.Yeng.Heard);
        Assert.Empty(rig.Yeng.Started);
        Assert.Empty(rig.Alpha.Started);
    }

    [Fact]
    public void AScriptSavedWithLuauWhoseFirstLineNamesThatEngineRunsOnTheTargetsEngineOnly()
    {
        var rig = new Rig("x");

        rig.Save("//" + Plain + ":\ny", "luau");

        Assert.Equal(ScriptCompileTarget.Luau, ScriptCompileTargets.FromFlags(rig.Part.Inventory.GetInventoryItem(rig.Item.ItemID).Flags));
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);
        Assert.Empty(rig.Yeng.Heard);
        Assert.Empty(rig.Yeng.Started);
    }

    [Fact]
    public void AnEngineThatCanReadTheTargetStillHearsOfTheScriptSoItCanLetGo()
    {
        var rig = new Rig("//" + Default + ":\nx");
        rig.WithTarget(ScriptCompileTarget.Luau);

        rig.Start();

        // Alpha heard it (and would drop its own record); it is not the engine chosen, and its own first line is overruled
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Alpha.Heard);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);
    }

    [Fact]
    public void WithNoTargetTheEngineThatReadsTheFirstLineHearsAndStartsItAsBefore()
    {
        var rig = new Rig("//" + Plain + ":\nx");

        rig.Start();

        Assert.Equal(new[] { rig.Item.ItemID }, rig.Yeng.Heard);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Yeng.Started);
        Assert.Empty(rig.Beta.Started);
    }

    [Fact]
    public void WithNoTargetAnEngineThatIsNotTheOneChosenStillHearsTheRezAsBefore()
    {
        var rig = new Rig("//" + Other + ":\nx");

        rig.Start();

        // the first line names Beta, which starts it; Yeng hears it too, as every engine did before targets existed
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Yeng.Heard);
        Assert.Empty(rig.Yeng.Started);
    }

    [Fact]
    public void WithATargetNoEngineTakesTheEngineTheFirstLineNamesIsTheOneChosenAndHearsIt()
    {
        var rig = new Rig("//" + Plain + ":\nx", betaAcceptsTargets: false);
        rig.WithTarget(ScriptCompileTarget.Luau);

        rig.Start();

        Assert.Equal(new[] { rig.Item.ItemID }, rig.Yeng.Heard);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Yeng.Started);
    }

    [Fact]
    public void SomethingThatListensToRezEventsAndIsNotAnEngineHearsEveryRez()
    {
        var rig = new Rig("//" + Plain + ":\nx");
        rig.WithTarget(ScriptCompileTarget.Luau);

        rig.Start();

        Assert.Equal(new[] { rig.Item.ItemID }, rig.OtherSubscriberHeard);
    }
}
