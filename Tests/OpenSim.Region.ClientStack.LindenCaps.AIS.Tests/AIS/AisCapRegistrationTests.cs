using System;
using System.Collections.Generic;
using System.Net;
using System.Xml;
using Nwc.XmlRpc;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The wiring, not the handler. These are the tests that would have caught the A6 live failure: AIS advertised its
/// caps correctly and then every request 404'd inside the HTTP server, because a capability whose URLs carry
/// sub-paths must be registered as a **variable-path** handler and AIS was not
/// (Docs/feature/ais-v3/A6-LIVE-FAILURE.md).
///
/// <para>The existing 114 tests all passed while this was broken: they call
/// <c>AisHandler.Handle(request, response)</c> directly, so they exercise routing, envelopes and every operation,
/// and never observe how the handler is bound to a URL. The bug lived entirely between <c>RegisterCaps</c> and the
/// listener.</para>
/// </summary>
[TestFixture]
public class AisCapRegistrationTests
{
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    /// <summary>Records what was registered on the listener, and nothing else.</summary>
    private sealed class RecordingHttpServer : IHttpServer
    {
        public readonly List<(ISimpleStreamHandler Handler, bool VarPath)> SimpleStreamHandlers = new();

        public void AddSimpleStreamHandler(ISimpleStreamHandler handler, bool varPath = false)
            => SimpleStreamHandlers.Add((handler, varPath));

        public uint SSLPort => 0;
        public string SSLCommonName => "";
        public uint Port => 9000;
        public bool UseSSL => false;
        public IPAddress ListenIPAddress { get; set; } = IPAddress.Loopback;
        public int DebugLevel { get; set; }

        public bool AddHTTPHandler(string methodName, GenericHTTPMethod handler) => true;
        public bool AddPollServiceHTTPHandler(string uripath, PollServiceEventArgs args) => true;
        public bool AddPollServiceHTTPHandler(PollServiceEventArgs args) => true;
        public bool AddPollServiceHTTPHandlerVarPath(PollServiceEventArgs args) => true;
        public void RemovePollServiceHTTPHandler(string url, string path) { }
        public void RemovePollServiceHTTPHandler(string path) { }
        public bool AddLLSDHandler(string path, LLSDMethod handler) => true;
        public void AddGlobalMethodHandler(string key, SimpleStreamMethod sh) { }
        public void AddStreamHandler(IRequestHandler handler) { }
        public bool AddXmlRPCHandler(string method, XmlRpcMethod handler) => true;
        public bool AddXmlRPCHandler(string method, XmlRpcMethod handler, bool keepAlive) => true;
        public void HandleXmlRpcRequests(OSHttpRequest request, OSHttpResponse response) { }
        public void HandleXmlRpcRequests(OSHttpRequest request, OSHttpResponse response, Dictionary<string, XmlRpcMethod> rpcHandlers) { }
        public bool AddJsonRPCHandler(string method, JsonRPCMethod handler) => true;
        public void AddWebSocketHandler(string servicepath, BaseHttpServer.WebSocketRequestDelegate handler) { }
        public void RemoveWebSocketHandler(string servicepath) { }
        public XmlRpcMethod GetXmlRPCHandler(string method) => null;
        public bool SetDefaultLLSDHandler(DefaultLLSDMethod handler) => true;
        public void RemoveHTTPHandler(string httpMethod, string path) { }
        public bool RemoveLLSDHandler(string path, LLSDMethod handler) => true;
        public void RemoveStreamHandler(string httpMethod, string path) { }
        public void RemoveSimpleStreamHandler(string path) { }
        public void RemoveXmlRPCHandler(string method) { }
        public void RemoveJsonRPCHandler(string method) { }
        public string GetHTTP404() => "";
        public void AddIndexPHPMethodHandler(string key, SimpleStreamMethod sh) { }
        public void RemoveIndexPHPMethodHandler(string key) { }
        public SimpleStreamMethod TryGetIndexPHPMethodHandler(string key) => null;
        public void Start() { }
        public void Stop() { }
    }

    /// <summary>
    /// Registers both AIS caps through the real <see cref="Caps"/> exactly as <c>AISv3Module.RegisterCaps</c> does,
    /// and returns the listener that recorded it plus the caps object.
    /// </summary>
    private static (RecordingHttpServer Server, Caps Caps, AisHandler Inventory, AisHandler Library) Register()
    {
        var server = new RecordingHttpServer();
        var caps = new Caps(server, "localhost", 9000, "/CAPS/" + UUID.Random(), Agent, "Ebony");

        var backend = new FakeAisBackend(Agent);
        var inventory = new AisHandler("/" + UUID.Random(), Agent, backend);
        caps.RegisterSimpleHandler(AISv3Module.CapName, inventory, varPath: AISv3Module.VarPath);

        var library = new AisHandler("/" + UUID.Random(), Agent, backend, AisMode.Library, backend, Agent);
        caps.RegisterSimpleHandler(AISv3Module.LibraryCapName, library, varPath: AISv3Module.VarPath);

        return (server, caps, inventory, library);
    }

    [Test]
    public void both_caps_are_registered_on_the_listener_as_variable_path_handlers()
    {
        var (server, _, inventory, library) = Register();

        Assert.That(server.SimpleStreamHandlers.Count, Is.EqualTo(2), "both caps must reach the listener");
        foreach (var (handler, varPath) in server.SimpleStreamHandlers)
        {
            Assert.That(varPath, Is.True,
                $"{handler.Path} was registered as an exact-path handler. BaseHttpServer keeps exact and "
                + "variable-path handlers in separate dictionaries and only matches the latter by prefix, so every "
                + "AIS sub-path request would 404 before the handler is entered (A6 live failure).");
        }
        Assert.That(server.SimpleStreamHandlers.ConvertAll(h => h.Handler.Path),
            Is.EquivalentTo(new[] { inventory.CapPath, library.CapPath }));
    }

    [Test]
    public void both_caps_appear_in_the_seed_response_with_their_urls()
    {
        var (_, caps, inventory, library) = Register();

        // SeedCapRequest emits a URL for a requested name only when a handler is registered under it
        // (CapsHandlers.GetCapsDetailsLLSDxml). ContainsCap is the same lookup over the same two dictionaries.
        Assert.That(caps.CapsHandlers.ContainsCap(AISv3Module.CapName), Is.True, "InventoryAPIv3 must be advertised");
        Assert.That(caps.CapsHandlers.ContainsCap(AISv3Module.LibraryCapName), Is.True, "LibraryAPIv3 must be advertised");
        Assert.That(caps.CapsHandlers.ContainsCap("FetchInventory2"), Is.False,
            "a requested name with no handler registered gets no URL, which is why an absent cap is harmless");

        // and the URL a viewer would be given is the cap path the handler was registered under
        Assert.That(inventory.CapPath, Does.StartWith("/"));
        Assert.That(library.CapPath, Does.StartWith("/"));
        Assert.That(inventory.CapPath, Is.Not.EqualTo(library.CapPath), "each cap gets its own path");
    }
    /// <summary>
    /// Why variable-path registration is required, expressed as the listener's own rule rather than as a repeat of
    /// the assertion above. <c>BaseHttpServer.TryGetSimpleStreamHandler</c> tries an exact dictionary lookup on the
    /// request's URI path and otherwise looks up only the segment before the second slash, in the variable-path
    /// dictionary. Every AIS URL has segments after the cap path, so the exact lookup can never match.
    /// </summary>
    [TestCase("/item/22222222-2222-4222-8222-222222222222")]
    [TestCase("/category/11111111-1111-4111-8111-111111111111/children")]
    [TestCase("/category/current/links")]
    [TestCase("/orphans")]
    public void every_ais_url_is_a_sub_path_that_only_the_variable_path_rule_can_match(string suffix)
    {
        var capPath = "/" + UUID.Random();
        var uriPath = capPath + suffix;

        Assert.That(uriPath, Is.Not.EqualTo(capPath),
            "if an AIS URL were ever equal to its cap path, exact registration would have worked");

        // the exact-match branch: m_simpleStreamHandlers.TryGetValue(uripath)
        Assert.That(uriPath.Equals(capPath, StringComparison.Ordinal), Is.False, "exact match cannot hit");

        // the variable-path branch: m_simpleStreamVarPath.TryGetValue(uripath[..uripath.IndexOf('/', 2)])
        var indx = uriPath.IndexOf('/', 2);
        Assert.That(indx, Is.GreaterThan(0), "there must be a second slash for the var-path rule to fire");
        Assert.That(indx, Is.Not.EqualTo(uriPath.Length - 1), "and it must not be the last character");
        Assert.That(uriPath[..indx], Is.EqualTo(capPath),
            "the key the listener looks up is exactly the path the cap was registered under");
    }

    /// <summary>The router still resolves the operation once the request reaches us, for each of those URLs.</summary>
    [Test]
    public void the_sub_path_urls_resolve_to_real_operations()
    {
        var capPath = "/" + UUID.Random();
        foreach (var (verb, suffix, expected) in new (string, string, AisOperation)[]
        {
            ("GET", "/item/22222222-2222-4222-8222-222222222222", AisOperation.FetchItem),
            ("GET", "/category/11111111-1111-4111-8111-111111111111/children?depth=50", AisOperation.FetchCategoryChildren),
            ("GET", "/category/current/links", AisOperation.FetchCOF),
            ("GET", "/orphans", AisOperation.FetchOrphans),
            ("PUT", "/category/11111111-1111-4111-8111-111111111111/links", AisOperation.SlamFolder),
        })
        {
            var route = AisRouter.Parse(verb, capPath + suffix, capPath);
            Assert.That(route.Operation, Is.EqualTo(expected), $"{verb} {suffix}");
        }
    }
}
