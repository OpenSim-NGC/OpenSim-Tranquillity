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
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// The SetScriptEngine capability {task_id, item_id, target} chooses a script's engine without saving its text; an empty
/// target clears the choice. It is offered only when the region keeps choices. Each test builds its own scene, listener and
/// caps and touches no process-wide state, so the class runs in parallel.
/// </summary>
public class SetScriptEngineCapTests
{
    private sealed class Engine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public Engine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
        // MockScriptEngine leaves these unimplemented; the region asks every engine when it starts a script.
        System.Collections.ArrayList IScriptModule.GetScriptErrors(UUID itemID) => new();
        bool IScriptModule.HasScript(UUID itemID, out bool running) { running = false; return false; }
        bool IScriptModule.SuspendScript(UUID itemID) => true;
        bool IScriptModule.ResumeScript(UUID itemID) => true;
        bool IScriptModule.GetScriptState(UUID itemID) => false;
        void IScriptModule.SaveAllState() { }
        string IScriptModule.GetXMLState(UUID itemID) => string.Empty;
        bool IScriptModule.SetXMLState(UUID itemID, string xml) => false;
    }

    private sealed class Listener : BaseHttpServer
    {
        public Listener() : base(0) { }
        public ISimpleStreamHandler Handler(string path) => m_simpleStreamHandlers[path];
    }

    /// <summary>A POST carrying an LLSD body.</summary>
    public class PostRequest : DispatchProxy
    {
        public Stream Body;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_HttpMethod": return "POST";
                case "get_InputStream": return Body;
                case "get_ContentType": return "application/llsd+xml";
                case "get_RemoteIPEndPoint": return new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9000);
            }
            Type type = method.ReturnType;
            return type != typeof(void) && type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class Region
    {
        public Scene Scene;
        public Listener Listener = new();
        public Caps Caps;
        public SceneObjectPart Part;
        public TaskInventoryItem Item;

        public Region(string choiceSetting = null, bool withPhlox = true)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("DefaultScriptEngine", "YEngine");
            if (choiceSetting is not null)
                startup.Set("ScriptEngineChoice", choiceSetting);
            Scene = new SceneHelpers().SetupScene("Example Region", UUID.Random(), 1000, 1000, config);
            Scene.StackModuleInterface<IScriptModule>(new Engine("YEngine"));
            if (withPhlox)
                Scene.StackModuleInterface<IScriptModule>(new Engine("InWorldz.Phlox"));
            UUID agent = UUID.Random();
            Caps = new Caps(Listener, "example.org", 9000, "/CAPS/" + UUID.Random(), agent, "Example Region");
            _ = new BunchOfCaps(Scene, agent, Caps, new BunchOfCapsConfigOptions());
            Part = SceneHelpers.AddSceneObject(Scene, "Example Object", agent).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", "default { state_entry() { } }");
        }

        public bool Offered => Caps.CapsHandlers.GetCapsLocalPaths().ContainsKey("SetScriptEngine");

        public OSDMap Post(OSDMap body)
        {
            Assert.True(Caps.CapsHandlers.GetCapsLocalPaths().TryGetValue("SetScriptEngine", out string path), "SetScriptEngine is not offered");
            PostRequest request = DispatchProxy.Create<IOSHttpRequest, PostRequest>() as PostRequest;
            request.Body = new MemoryStream(OSDParser.SerializeLLSDXmlBytes(body));
            var response = new TestOSHttpResponse();
            Listener.Handler(path).Handle((IOSHttpRequest)(object)request, response);
            Assert.Equal(200, response.StatusCode);
            return Assert.IsType<OSDMap>(OSDParser.DeserializeLLSDXml(response.RawBuffer));
        }

        public OSDMap Ask(string target) => Post(new OSDMap
        {
            ["task_id"] = Part.UUID,
            ["item_id"] = Item.ItemID,
            ["target"] = target,
        });

        public int Choice => ScriptEngineChoices.FromFlags(Part.Inventory.GetInventoryItem(Item.ItemID).Flags);
    }

    [Fact]
    public void TheCapabilityIsOfferedWhenTheRegionKeepsChoices()
    {
        Assert.True(new Region().Offered);
        Assert.False(new Region("false").Offered);
    }

    [Fact]
    public void ATargetIsStoredAndTheReplySaysSo()
    {
        var region = new Region();

        OSDMap reply = region.Ask("engine:Phlox");

        Assert.Equal("complete", reply["state"].AsString());
        Assert.Equal("Changed", reply["result"].AsString());
        Assert.Equal("InWorldz.Phlox", reply["EngineChoice"].AsString());
        Assert.Equal(ScriptEngineChoices.Phlox, region.Choice);
    }

    [Fact]
    public void AnEmptyTargetClearsTheChoice()
    {
        var region = new Region();
        region.Ask("YEngine");
        Assert.Equal(ScriptEngineChoices.YEngine, region.Choice);

        OSDMap reply = region.Ask("");

        Assert.Equal("complete", reply["state"].AsString());
        Assert.Equal(string.Empty, reply["EngineChoice"].AsString());
        Assert.Equal(ScriptEngineChoices.None, region.Choice);
    }

    [Fact]
    public void AnUnknownEngineIsAnErrorAndChangesNothing()
    {
        var region = new Region();

        OSDMap reply = region.Ask("engine:NoSuchEngine");

        Assert.Equal("error", reply["state"].AsString());
        Assert.Equal("UnknownEngine", reply["result"].AsString());
        Assert.False(string.IsNullOrEmpty(reply["error"].AsString()));
        Assert.Equal(ScriptEngineChoices.None, region.Choice);
    }

    // A request the region cannot meet is answered with the same error shape as NotAllowed, Disabled and UnknownEngine:
    // state "error", result naming the refusal, error text. The script keeps the choice it had.
    [Fact]
    public void AnEngineThatIsNotLoadedHereIsAnErrorNotSwitchedAndChangesNothing()
    {
        var region = new Region(withPhlox: false);

        OSDMap reply = region.Ask("engine:Phlox");

        Assert.Equal("error", reply["state"].AsString());
        Assert.Equal("NotSwitched", reply["result"].AsString());
        Assert.False(string.IsNullOrEmpty(reply["error"].AsString()));
        Assert.False(reply.ContainsKey("EngineChoice"));
        Assert.Equal(ScriptEngineChoices.None, region.Choice);
    }

    [Fact]
    public void ALuaTargetNoLoadedEngineTakesIsAnErrorNotSwitchedAndChangesNothing()
    {
        var region = new Region();

        OSDMap reply = region.Ask("lsl-luau");

        Assert.Equal("error", reply["state"].AsString());
        Assert.Equal("NotSwitched", reply["result"].AsString());
        Assert.False(string.IsNullOrEmpty(reply["error"].AsString()));
        Assert.Equal(ScriptCompileTarget.Unspecified, ScriptCompileTargets.FromFlags(region.Part.Inventory.GetInventoryItem(region.Item.ItemID).Flags));
    }

    [Fact]
    public void ARequestWithoutIdsIsAnError()
    {
        var region = new Region();

        OSDMap reply = region.Post(new OSDMap { ["target"] = "Phlox" });

        Assert.Equal("error", reply["state"].AsString());
        Assert.Equal(ScriptEngineChoices.None, region.Choice);
    }
}
