using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

public class EngineStateSelectionTests
{
    private sealed class StateEngine : MockScriptEngine, IScriptModule, IScriptStateProvider
    {
        private readonly List<string> m_Probes;
        public new string ScriptEngineName { get; }
        public bool Saved { get; set; }
        public UUID ExpectedItem { get; set; }
        public UUID ExpectedAsset { get; set; }

        public StateEngine(string name, List<string> probes)
        {
            ScriptEngineName = name;
            m_Probes = probes;
        }

        public bool HasSavedScriptState(UUID itemID, UUID assetID)
        {
            Assert.Equal(ExpectedItem, itemID);
            Assert.Equal(ExpectedAsset, assetID);
            m_Probes.Add(ScriptEngineName);
            return Saved;
        }
    }

    [Theory]
    [InlineData(true, true, true, "default", "default")]
    [InlineData(false, true, true, "first", "default,first")]
    [InlineData(false, false, true, "second", "default,first,second")]
    [InlineData(false, false, false, "default", "default,first,second")]
    public void DefaultStateIsCheckedFirstAndDefaultCompileIsTheFinalFallback(
        bool defaultSaved, bool firstSaved, bool secondSaved, string expected, string probes)
    {
        Scene scene = new SceneHelpers().SetupScene();
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        var seen = new List<string>();
        foreach (var spec in new[] { ("first", firstSaved), ("default", defaultSaved), ("second", secondSaved) })
        {
            scene.StackModuleInterface<IScriptModule>(new StateEngine(spec.Item1, seen)
            {
                Saved = spec.Item2, ExpectedItem = item.ItemID, ExpectedAsset = item.AssetID
            });
        }

        Assert.Equal(expected, scene.ResolveScriptEngine("default", "default {}", item)?.ScriptEngineName);
        Assert.Equal(probes.Split(','), seen);
    }

    [Fact]
    public void ExplicitHeaderDoesNotProbeSavedState()
    {
        Scene scene = new SceneHelpers().SetupScene();
        var seen = new List<string>();
        var engine = new StateEngine("chosen", seen);
        scene.StackModuleInterface<IScriptModule>(engine);
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        Assert.Same(engine, scene.ResolveScriptEngine("default", "//chosen:\ndefault {}", item));
        Assert.Empty(seen);
    }

    [Fact]
    public void DisabledHeaderFallsThroughToLoadedEnginesSavedState()
    {
        Scene scene = new SceneHelpers().SetupScene();
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        var seen = new List<string>();
        var engine = new StateEngine("saved", seen)
        {
            Saved = true, ExpectedItem = item.ItemID, ExpectedAsset = item.AssetID
        };
        scene.StackModuleInterface<IScriptModule>(engine);
        Assert.Same(engine, scene.ResolveScriptEngine("default", "//disabled:\ndefault {}", item));
        Assert.Equal(new[] { "saved" }, seen);
    }
}
