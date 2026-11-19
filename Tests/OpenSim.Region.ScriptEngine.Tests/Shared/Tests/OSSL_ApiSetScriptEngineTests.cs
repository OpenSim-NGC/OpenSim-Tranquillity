/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests;

/// <summary>
/// osSetScriptEngine(scriptName, target) chooses the engine of a script in the caller's own prim, for the prim's owner,
/// at threat level High. It answers 1 when the choice is in place and 0 when it is not.
/// </summary>
public class OSSL_ApiSetScriptEngineTests : OpenSimTestCase
{
    private sealed class NamedEngine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public NamedEngine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
    }

    private Scene m_scene;
    private MockScriptEngine m_engine;
    private SceneObjectGroup m_object;
    private TaskInventoryItem m_caller, m_other;

    private OSSL_Api Api(string threatLevel)
    {
        IConfigSource config = new IniConfigSource();
        IConfig oconfig = config.AddConfig("OSSL");
        oconfig.Set("DebuggerSafe", false);
        oconfig.Set("AllowOSFunctions", "true");
        oconfig.Set("OSFunctionThreatLevel", threatLevel);
        config.AddConfig("XEngine").Set("Enabled", "true");
        config.AddConfig("Startup").Set("DefaultScriptEngine", "YEngine");

        m_scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
        m_engine = new MockScriptEngine();
        m_engine.Initialise(config);
        m_engine.AddRegion(m_scene);
        m_scene.StackModuleInterface<IScriptModule>(new NamedEngine("YEngine"));
        m_scene.StackModuleInterface<IScriptModule>(new NamedEngine("InWorldz.Phlox"));

        UserAccount owner = UserAccountHelpers.CreateUserWithInventory(m_scene, 0x1);
        m_object = SceneHelpers.AddSceneObject(m_scene, "Example Object", owner.PrincipalID);
        m_caller = TaskInventoryHelpers.AddScript(m_scene.AssetService, m_object.RootPart, "caller", "default { state_entry() { } }");
        m_other = TaskInventoryHelpers.AddScript(m_scene.AssetService, m_object.RootPart, "other", "default { state_entry() { } }");

        var lsl = new LSL_Api();
        lsl.Initialize(m_engine, m_object.RootPart, m_caller);
        m_engine.RegisterApi(m_caller.ItemID, "LSL", lsl);
        var ossl = new OSSL_Api();
        ossl.Initialize(m_engine, m_object.RootPart, m_caller);
        return ossl;
    }

    private int ChoiceOf(TaskInventoryItem item)
        => ScriptEngineChoices.FromFlags(m_object.RootPart.Inventory.GetInventoryItem(item.ItemID).Flags);

    [Theory]
    [InlineData("Phlox", ScriptEngineChoices.Phlox)]
    [InlineData("engine:YEngine", ScriptEngineChoices.YEngine)]
    public void TheOwnersScriptChoosesTheEngineOfAnotherScriptInItsPrim(string target, int expected)
    {
        OSSL_Api api = Api("High");

        Assert.Equal(1, (int)api.osSetScriptEngine("other", target));

        Assert.Equal(expected, ChoiceOf(m_other));
        Assert.Equal(ScriptEngineChoices.None, ChoiceOf(m_caller));
    }

    [Fact]
    public void AnEmptyTargetClearsTheChoice()
    {
        OSSL_Api api = Api("High");
        Assert.Equal(1, (int)api.osSetScriptEngine("other", "Phlox"));

        Assert.Equal(1, (int)api.osSetScriptEngine("other", ""));

        Assert.Equal(ScriptEngineChoices.None, ChoiceOf(m_other));
    }

    [Fact]
    public void AnswersZeroForAScriptThatIsNotThereAnUnknownEngineOrItself()
    {
        OSSL_Api api = Api("High");

        Assert.Equal(0, (int)api.osSetScriptEngine("no such script", "Phlox"));
        Assert.Equal(0, (int)api.osSetScriptEngine("other", "NoSuchEngine"));
        Assert.Equal(0, (int)api.osSetScriptEngine("caller", "Phlox"));
        Assert.Equal(ScriptEngineChoices.None, ChoiceOf(m_other));
        Assert.Equal(ScriptEngineChoices.None, ChoiceOf(m_caller));
    }

    [Fact]
    public void ItIsAHighThreatFunction()
    {
        OSSL_Api api = Api("Moderate");

        Assert.ThrowsAny<Exception>(() => api.osSetScriptEngine("other", "Phlox"));
        Assert.Equal(ScriptEngineChoices.None, ChoiceOf(m_other));
    }
}
