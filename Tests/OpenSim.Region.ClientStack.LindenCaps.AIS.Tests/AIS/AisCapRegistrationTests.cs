/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Xml;
using Nwc.XmlRpc;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The wiring, not the handler. These are the tests that would have caught the first live failure: AIS advertised
/// its caps correctly and then every request 404'd inside the HTTP server, because a capability whose URLs carry
/// sub-paths must be registered as a **variable-path** handler and AIS was not.
///
/// <para>The existing 114 tests all passed while this was broken: they call
/// <c>AisHandler.Handle(request, response)</c> directly, so they exercise routing, envelopes and every operation,
/// and never observe how the handler is bound to a URL. The bug lived entirely between <c>RegisterCaps</c> and the
/// listener.</para>
/// </summary>
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

    [Fact]
    public void both_caps_are_registered_on_the_listener_as_variable_path_handlers()
    {
        var (server, _, inventory, library) = Register();

        Assert.Equal(2, server.SimpleStreamHandlers.Count);
        foreach (var (handler, varPath) in server.SimpleStreamHandlers)
        {
            Assert.True(varPath, $"{handler.Path} was registered as an exact-path handler. BaseHttpServer keeps exact and "
                + "variable-path handlers in separate dictionaries and only matches the latter by prefix, so every "
                + "AIS sub-path request would 404 before the handler is entered (A6 live failure).");
        }
        Assert.Equivalent(new[] { inventory.CapPath, library.CapPath }, server.SimpleStreamHandlers.ConvertAll(h => h.Handler.Path), true);
    }

    [Fact]
    public void both_caps_appear_in_the_seed_response_with_their_urls()
    {
        var (_, caps, inventory, library) = Register();

        // SeedCapRequest emits a URL for a requested name only when a handler is registered under it
        // (CapsHandlers.GetCapsDetailsLLSDxml). ContainsCap is the same lookup over the same two dictionaries.
        Assert.True(caps.CapsHandlers.ContainsCap(AISv3Module.CapName), "InventoryAPIv3 must be advertised");
        Assert.True(caps.CapsHandlers.ContainsCap(AISv3Module.LibraryCapName), "LibraryAPIv3 must be advertised");
        Assert.False(caps.CapsHandlers.ContainsCap("FetchInventory2"), "a requested name with no handler registered gets no URL, which is why an absent cap is harmless");

        // and the URL a viewer would be given is the cap path the handler was registered under
        Assert.StartsWith("/", inventory.CapPath);
        Assert.StartsWith("/", library.CapPath);
        Assert.NotEqual(library.CapPath, inventory.CapPath);
    }
    /// <summary>
    /// Why variable-path registration is required, expressed as the listener's own rule rather than as a repeat of
    /// the assertion above. <c>BaseHttpServer.TryGetSimpleStreamHandler</c> tries an exact dictionary lookup on the
    /// request's URI path and otherwise looks up only the segment before the second slash, in the variable-path
    /// dictionary. Every AIS URL has segments after the cap path, so the exact lookup can never match.
    /// </summary>
    [Theory]
    [InlineData("/item/22222222-2222-4222-8222-222222222222")]
    [InlineData("/category/11111111-1111-4111-8111-111111111111/children")]
    [InlineData("/category/current/links")]
    [InlineData("/orphans")]
    public void every_ais_url_is_a_sub_path_that_only_the_variable_path_rule_can_match(string suffix)
    {
        var capPath = "/" + UUID.Random();
        var uriPath = capPath + suffix;

        Assert.NotEqual(capPath, uriPath);

        // the exact-match branch: m_simpleStreamHandlers.TryGetValue(uripath)
        Assert.False(uriPath.Equals(capPath, StringComparison.Ordinal), "exact match cannot hit");

        // the variable-path branch: m_simpleStreamVarPath.TryGetValue(uripath[..uripath.IndexOf('/', 2)])
        var indx = uriPath.IndexOf('/', 2);
        Assert.True(indx > 0, "there must be a second slash for the var-path rule to fire");
        Assert.NotEqual(uriPath.Length - 1, indx);
        Assert.Equal(capPath, uriPath[..indx]);
    }

    /// <summary>The router still resolves the operation once the request reaches us, for each of those URLs.</summary>
    [Fact]
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
            Assert.Equal(expected, route.Operation);
        }
    }
}
