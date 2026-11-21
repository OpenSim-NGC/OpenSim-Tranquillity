/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.ClientStack.LindenUDP;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// ScriptRunningReply tells a viewer that asked for it which engine runs a script ("Engine") and which engine is saved
/// with it ("EngineChoice"). A viewer asks by listing the SetScriptEngine capability in its seed request; a viewer that
/// does not is sent exactly the reply it was sent before. The region's SimulatorFeatures say whether it keeps choices.
/// </summary>
// Uses MainServer's port 9999 and OpenSimTestCase, as EventQueueTests does.
[Collection("EventQueueMainServer")]
public class ScriptEngineReplyTests : OpenSimTestCase
{
    private const string Phlox = "InWorldz.Phlox";

    private TestScene m_scene;
    private EventQueueGetModule m_eqgMod;

    /// <summary>A stand-in engine that holds the scripts it is told it holds.</summary>
    private sealed class HoldingEngine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public readonly HashSet<UUID> Holds = new();
        public HoldingEngine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
        bool IScriptModule.HasScript(UUID itemID, out bool running) { running = Holds.Contains(itemID); return running; }
    }

    public override void SetUp()
    {
        base.SetUp();
        uint port = 9999;
        MainServer.Instance.RemoveHttpServer(port);
        MainServer.Instance.AddHttpServer(new BaseHttpServer(port, false, "", "", ""));
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup");
        m_eqgMod = new EventQueueGetModule();
        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, config, new CapabilitiesModule(), m_eqgMod);
    }

    private OSDMap ReadReply(UUID agentId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Hashtable eventsResponse = m_eqgMod.GetEvents(UUID.Zero, agentId);
            if ((int)eventsResponse["int_response_code"] != (int)HttpStatusCode.OK || eventsResponse["bin_response_data"] is null)
                continue;
            string data = Encoding.UTF8.GetString((byte[])eventsResponse["bin_response_data"]);
            foreach (OSD osd in (OSDArray)((OSDMap)OSDParser.DeserializeLLSDXml(data))["events"])
            {
                OSDMap eventOsd = (OSDMap)osd;
                if (eventOsd["message"] == "ScriptRunningReply")
                    return (OSDMap)((OSDArray)((OSDMap)eventOsd["body"])["Script"])[0];
            }
        }
        return null;
    }

    [Fact]
    public void TheEventCarriesTheEnginesWhenGivenThem()
    {
        ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, UUID.Random());
        UUID objectId = UUID.Random(), itemId = UUID.Random();

        m_eqgMod.ScriptRunningEvent(objectId, itemId, true, sp.UUID, ScriptCompileTarget.Unspecified, Phlox, "YEngine");

        OSDMap script = ReadReply(sp.UUID);
        Assert.NotNull(script);
        Assert.Equal(OSDType.String, script["Engine"].Type);
        Assert.Equal(Phlox, script["Engine"].AsString());
        Assert.Equal("YEngine", script["EngineChoice"].AsString());
        // the old fields keep their meaning
        Assert.True(script["Mono"].AsBoolean());
        Assert.False(script.ContainsKey("Luau"));
        Assert.True(script["Running"].AsBoolean());
    }

    [Fact]
    public void AnEmptyChoiceIsSentAsAnEmptyString()
    {
        ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, UUID.Random());

        m_eqgMod.ScriptRunningEvent(UUID.Random(), UUID.Random(), false, sp.UUID, ScriptCompileTarget.Luau, "SLua", string.Empty);

        OSDMap script = ReadReply(sp.UUID);
        Assert.NotNull(script);
        Assert.Equal("SLua", script["Engine"].AsString());
        Assert.True(script.ContainsKey("EngineChoice"));
        Assert.Equal(string.Empty, script["EngineChoice"].AsString());
        Assert.False(script["Mono"].AsBoolean());
        Assert.True(script["Luau"].AsBoolean());
        Assert.True(script["LuauLanguage"].AsBoolean());
    }

    [Fact]
    public void WithoutEnginesTheEventIsTheOldOne()
    {
        ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, UUID.Random());

        m_eqgMod.ScriptRunningEvent(UUID.Random(), UUID.Random(), true, sp.UUID, ScriptCompileTarget.Unspecified, null, null);

        OSDMap script = ReadReply(sp.UUID);
        Assert.NotNull(script);
        Assert.Equal(new[] { "ItemID", "Mono", "ObjectID", "Running" }, script.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    // ---- the client: only a viewer that asked gets the new fields ----------------------------------------------

    private (LLClientView client, SceneObjectPart part, TaskInventoryItem item, HoldingEngine engine) Client(bool askedForEngines)
    {
        var engine = new HoldingEngine(Phlox);
        m_scene.StackModuleInterface<IScriptModule>(engine);
        ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, UUID.Random());
        uint circuitCode = sp.ControllingClient.CircuitCode;
        Caps caps = m_scene.CapsModule.GetCapsForUser(circuitCode);
        Assert.NotNull(caps);
        if (askedForEngines)
            caps.Flags |= BunchOfCaps.FlagForRequestedCap("SetScriptEngine");

        TestLLUDPServer udpServer = new(IPAddress.Loopback, 0, 0, new IniConfigSource(), m_scene.AuthenticateHandler);
        LLUDPClient udpClient = new(udpServer, udpServer.ThrottleRates, udpServer.Throttle, circuitCode, sp.UUID,
            new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        var client = new LLClientView(m_scene, udpServer, udpClient, new AuthenticateResponse { Authorised = true, LoginInfo = new Login() },
            sp.UUID, UUID.Random(), circuitCode);

        SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene).RootPart;
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(m_scene.AssetService, part, "script", "default { state_entry() { } }");
        item.Flags = ScriptEngineChoices.WithChoice(item.Flags, ScriptEngineChoices.YEngine);
        engine.Holds.Add(item.ItemID);
        return (client, part, item, engine);
    }

    [Fact]
    public void SetScriptEngineInTheSeedRequestIsTheOptIn()
    {
        Assert.Equal(Caps.CapsFlags.ScriptEngineChoice, BunchOfCaps.FlagForRequestedCap("SetScriptEngine"));
        Assert.NotEqual(Caps.CapsFlags.None, Caps.CapsFlags.ScriptEngineChoice);
        Assert.Equal(Caps.CapsFlags.None, BunchOfCaps.FlagForRequestedCap("UpdateScriptTask"));
        Assert.Equal(Caps.CapsFlags.None, BunchOfCaps.FlagForRequestedCap("setscriptengine"));
    }

    [Fact]
    public void AViewerThatAskedIsToldWhichEngineRunsTheScriptAndWhichIsSaved()
    {
        var (client, part, item, _) = Client(askedForEngines: true);

        client.SendScriptRunningReply(part.UUID, item.ItemID, true);

        OSDMap script = ReadReply(client.AgentId);
        Assert.NotNull(script);
        Assert.Equal(Phlox, script["Engine"].AsString());
        Assert.Equal("YEngine", script["EngineChoice"].AsString());
        Assert.True(script["Mono"].AsBoolean());
    }

    [Fact]
    public void AViewerThatDidNotAskIsSentTheOldReply()
    {
        var (client, part, item, _) = Client(askedForEngines: false);

        client.SendScriptRunningReply(part.UUID, item.ItemID, true);

        OSDMap script = ReadReply(client.AgentId);
        Assert.NotNull(script);
        Assert.Equal(new[] { "ItemID", "Mono", "ObjectID", "Running" }, script.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheRegionNamesTheEngineThatRunsAScriptAndNothingWhenNoneDoes()
    {
        var (_, part, item, engine) = Client(askedForEngines: true);

        Assert.Equal(Phlox, m_scene.GetScriptEngineRunning(part.UUID, item.ItemID));
        engine.Holds.Clear();
        Assert.Equal(string.Empty, m_scene.GetScriptEngineRunning(part.UUID, item.ItemID));
    }
}
