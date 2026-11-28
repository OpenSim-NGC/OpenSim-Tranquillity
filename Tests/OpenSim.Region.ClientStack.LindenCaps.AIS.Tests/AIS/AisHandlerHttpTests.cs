/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// HTTP-level behaviour of the A0 handler (501 on every route with an LLSD error map, spec §1f) and the golden
/// envelope fixtures under AIS/Fixtures (spec §1c): every key present with the right LLSD type.
/// </summary>
public class AisHandlerHttpTests
{
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly UUID Cat = new("11111111-1111-4111-8111-111111111111");

    /// <summary>A backend that must never be reached in A0.</summary>
    private sealed class ExplodingBackend : IAisInventoryBackend
    {
        private static Exception Boom() => new InvalidOperationException("A0 handler must not call the backend");
        public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type) => throw Boom();
        public InventoryFolderBase GetFolder(UUID agentId, UUID folderId) => throw Boom();
        public InventoryCollection GetFolderContent(UUID agentId, UUID folderId) => throw Boom();
        public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds) => throw Boom();
        public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId) => throw Boom();
        public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId) => throw Boom();
        public InventoryItemBase GetItem(UUID agentId, UUID itemId) => throw Boom();
        public bool AddFolder(InventoryFolderBase folder) => throw Boom();
        public bool AddItem(InventoryItemBase item) => throw Boom();
        public bool UpdateItem(InventoryItemBase item) => throw Boom();
        public AisAssetTransaction ApplyAssetTransaction(UUID agentId, UUID transactionId, InventoryItemBase item) => throw Boom();
        public void OnItemAssetChanged(UUID agentId, UUID itemId, UUID newAssetId) => throw Boom();
        public bool MayUseAsset(UUID agentId, UUID assetId, sbyte assetType) => throw Boom();
        public bool UpdateFolder(InventoryFolderBase folder) => throw Boom();
        public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds) => throw Boom();
        public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash) => throw Boom();
        public bool PurgeFolder(InventoryFolderBase folder) => throw Boom();
    }

    /// <summary>
    /// The shared TestOSHttpRequest mock throws on HttpMethod, so the end-to-end test uses this stub: only the
    /// members the handler reads are implemented (verb, raw url, path, body); everything else throws.
    /// </summary>
    private sealed class AisTestRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public AisTestRequest(string verb, string url) { HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url; }
        public string HttpMethod { get; }
        public Uri Url { get; }
        public string RawUrl { get; }
        public string UriPath => Url.AbsolutePath;
        public Stream InputStream { get; set; } = new MemoryStream();
        public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
        public bool HasEntityBody => false;
        public long ContentLength => 0;
        public long ContentLength64 => 0;
        public string ContentType => "application/llsd+xml";
        public string[] AcceptTypes => Array.Empty<string>();
        public System.Text.Encoding ContentEncoding => System.Text.Encoding.UTF8;
        public bool IsSecured => false;
        public bool KeepAlive => false;
        public System.Collections.Specialized.NameValueCollection QueryString => throw new NotImplementedException();
        public System.Collections.Hashtable Query => throw new NotImplementedException();
        public HashSet<string> QueryFlags => throw new NotImplementedException();
        public Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
        public System.Net.IPEndPoint RemoteIPEndPoint => new(System.Net.IPAddress.Loopback, 1);
        public System.Net.IPEndPoint LocalIPEndPoint => new(System.Net.IPAddress.Loopback, 2);
        public string UserAgent => "test";
        public double ArrivalTS => 0;
    }

    private static (AisTestRequest Request, TestOSHttpResponse Response) Http(string verb, string url)
        => (new AisTestRequest(verb, url), new TestOSHttpResponse());

    /// <summary>
    /// After A4 every operation in the spec is implemented, so the only 501 left is COPY arriving on the
    /// **inventory** cap: it is a LibraryAPIv3 operation by design (spec 1a row 5 sends it to {lib}), and the
    /// inventory handler has no library source or destination to serve it with.
    /// </summary>
    [Fact]
    public void copy_on_the_inventory_cap_is_the_only_501_left()
    {
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var handler = new AisHandler(cap, Agent, new ExplodingBackend());
        var path = $"/category/{Cat}?tid={UUID.Random()},depth=0";
        var route = AisRouter.Parse("COPY", cap + path, cap);
        Assert.Equal(AisOperation.CopyCategory, route.Operation);

        var (req, resp) = Http("COPY", cap + path);
        handler.Dispatch(route, req, resp);

        Assert.Equal((int)HttpStatusCode.NotImplemented, resp.StatusCode);
        Assert.Equal("application/llsd+xml", resp.ContentType);
        var map = (OSDMap)OSDParser.DeserializeLLSDXml(resp.RawBuffer);
        Assert.Equal(501, map["error_code"].AsInteger());
        Assert.Equal(nameof(AisOperation.CopyCategory), map["operation"].AsString());
        // spec 1f: an error body must not look like content the viewer would apply
        Assert.False(map.ContainsKey("parent_id"));
        Assert.False(map.ContainsKey("item_id"));
        Assert.False(map.ContainsKey("category_id"));
        Assert.False(map.ContainsKey("_embedded"));
    }
    [Fact]
    public void the_handler_parses_verb_and_path_from_the_request_itself()
    {
        // through SimpleStreamHandler.Handle -> ProcessRequest -> AisRouter.Parse(HttpMethod, RawUrl, capPath)
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var handler = new AisHandler(cap, Agent, new ExplodingBackend());
        var (req, resp) = Http("DELETE", cap + $"/category/{Cat}/children");
        handler.Handle(req, resp);
        // every operation is implemented as of A4, so this backend throws rather than being left untouched; what
        // the test is actually about is that the verb and the path came off the request and reached the router
        var map = (OSDMap)OSDParser.DeserializeLLSDXml(resp.RawBuffer);
        Assert.Equal(nameof(AisOperation.PurgeDescendents), map["operation"].AsString());
        Assert.Equal($"/category/{Cat}/children", map["path"].AsString());
    }

    [Fact]
    public void an_unknown_route_returns_404_with_the_same_body_shape()
    {
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var handler = new AisHandler(cap, Agent, new ExplodingBackend());
        var route = AisRouter.Parse("GET", cap + $"/category/{Cat}", cap);
        var (req, resp) = Http("GET", cap + $"/category/{Cat}");
        handler.Dispatch(route, req, resp);
        Assert.Equal((int)HttpStatusCode.NotFound, resp.StatusCode);
        var map = (OSDMap)OSDParser.DeserializeLLSDXml(resp.RawBuffer);
        Assert.Equal(404, map["error_code"].AsInteger());
    }

    // ------------------------------------------------------------------ golden envelope fixtures (spec §1c)

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "AIS", "Fixtures");

    private static OSDMap LoadFixture(string name)
    {
        var path = Path.Combine(FixturesDir, name);
        Assert.True(File.Exists(path), $"fixture {path} must be copied to the output directory");
        var osd = OSDParser.DeserializeLLSDXml(File.ReadAllText(path));
        Assert.True(osd is OSDMap, name);
        return (OSDMap)osd;
    }

    private static void AssertUuidArray(OSDMap map, string key)
    {
        Assert.True(map.ContainsKey(key), key);
        Assert.True(map[key] is OSDArray, key);
        foreach (var e in (OSDArray)map[key]) Assert.Equal(OSDType.UUID, e.Type);
    }

    private static void AssertEmbedded(OSDMap category, bool requireAllThree)
    {
        Assert.True(category.ContainsKey("_embedded"), "_embedded");
        var embedded = (OSDMap)category["_embedded"];
        foreach (var coll in new[] { "categories", "items", "links" })
        {
            if (requireAllThree) Assert.True(embedded.ContainsKey(coll), $"_embedded.{coll} must be present even when empty (spec §1c: descendent count needs all three)");
            if (embedded.ContainsKey(coll)) Assert.True(embedded[coll] is OSDMap, $"_embedded.{coll} is a map keyed by uuid string");
        }
    }

    [Fact]
    public void mutation_envelope_fixture_has_every_meta_key_with_the_right_type()
    {
        // spec §1c meta keys, llaisapi.cpp:1101-1177
        var m = LoadFixture("mutation-envelope.xml");
        AssertUuidArray(m, "_categories_removed");
        AssertUuidArray(m, "_category_items_removed");
        AssertUuidArray(m, "_removed_items");
        AssertUuidArray(m, "_broken_links_removed");
        AssertUuidArray(m, "_created_items");
        AssertUuidArray(m, "_created_categories");
        Assert.IsAssignableFrom<OSDMap>(m["_updated_category_versions"]);
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)m["_updated_category_versions"])
        {
            Assert.True(UUID.TryParse(kv.Key, out _), "keys are category uuids");
            Assert.Equal(OSDType.Integer, kv.Value.Type);
        }
        Assert.IsAssignableFrom<OSDMap>(m["_embedded"]);
    }

    [Fact]
    public void category_fetch_fixture_is_a_category_with_all_three_embedded_collections()
    {
        // spec §1c content keys, llaisapi.cpp:1203-1206, :1466-1482
        var c = LoadFixture("category-fetch.xml");
        Assert.Equal(OSDType.UUID, c["category_id"].Type);
        Assert.Equal(OSDType.UUID, c["parent_id"].Type);
        Assert.Equal(OSDType.UUID, c["agent_id"].Type);
        Assert.Equal(OSDType.Integer, c["version"].Type);
        AssertEmbedded(c, requireAllThree: true);
        var embedded = (OSDMap)c["_embedded"];
        // links are a separate collection, not items
        Assert.True(((OSDMap)embedded["links"]).Count == 1);
        Assert.True(((OSDMap)embedded["items"]).Count == 1);
        Assert.True(((OSDMap)embedded["categories"]).Count == 1);
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["links"])
        {
            var link = (OSDMap)kv.Value;
            Assert.Equal(OSDType.UUID, link["linked_id"].Type);
            Assert.Equal(kv.Key, link["item_id"].AsString());
            Assert.Equal(c["category_id"].AsUUID(), link["parent_id"].AsUUID());
        }
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["items"])
        {
            var item = (OSDMap)kv.Value;
            Assert.False(item.ContainsKey("linked_id"), "an item is not a link");
            Assert.Equal(kv.Key, item["item_id"].AsString());
            Assert.Equal(OSDType.UUID, item["parent_id"].Type);
        }
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["categories"])
        {
            var sub = (OSDMap)kv.Value;
            Assert.Equal(kv.Key, sub["category_id"].AsString());
            Assert.Equal(OSDType.Integer, sub["version"].Type);
            AssertEmbedded(sub, requireAllThree: true);
        }
    }

    [Fact]
    public void cof_links_fixture_is_a_links_only_category()
    {
        // spec §1c: FT_CURRENT_OUTFIT may carry links alone (llaisapi.cpp:1477-1481); a link may embed its target
        var c = LoadFixture("cof-links.xml");
        Assert.Equal(OSDType.UUID, c["category_id"].Type);
        Assert.Equal((int)FolderType.CurrentOutfit, c["type_default"].AsInteger());
        var embedded = (OSDMap)c["_embedded"];
        Assert.True(embedded.ContainsKey("links"));
        Assert.False(embedded.ContainsKey("items"));
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["links"])
        {
            var link = (OSDMap)kv.Value;
            Assert.Equal(OSDType.UUID, link["linked_id"].Type);
            var linkEmbedded = (OSDMap)link["_embedded"];
            Assert.True(linkEmbedded["item"] is OSDMap, "_embedded.item inside a link (llaisapi.cpp:1496)");
            Assert.Equal(link["linked_id"].AsUUID(), ((OSDMap)linkEmbedded["item"])["item_id"].AsUUID());
        }
    }

    [Fact]
    public void item_fetch_fixture_is_an_item()
    {
        var i = LoadFixture("item-fetch.xml");
        Assert.Equal(OSDType.UUID, i["item_id"].Type);
        Assert.Equal(OSDType.UUID, i["parent_id"].Type);
        Assert.False(i.ContainsKey("linked_id"));
        Assert.IsAssignableFrom<OSDMap>(i["permissions"]);
        Assert.IsAssignableFrom<OSDMap>(i["sale_info"]);
    }

    [Fact]
    public void link_fetch_fixture_is_a_link_with_an_embedded_target()
    {
        var l = LoadFixture("link-fetch.xml");
        Assert.Equal(OSDType.UUID, l["linked_id"].Type);
        Assert.Equal(OSDType.UUID, l["item_id"].Type);
        Assert.Equal(OSDType.UUID, l["parent_id"].Type);
        Assert.IsAssignableFrom<OSDMap>(((OSDMap)l["_embedded"])["item"]);
    }

    [Fact]
    public void error_fixture_is_a_flat_map_the_viewer_ignores()
    {
        // spec §1f
        var e = LoadFixture("error.xml");
        Assert.Equal(OSDType.Integer, e["error_code"].Type);
        Assert.Equal(OSDType.String, e["error_description"].Type);
        Assert.Equal(OSDType.String, e["message"].Type);
        Assert.False(e.ContainsKey("parent_id"));
        Assert.False(e.ContainsKey("_embedded"));
        var live = AisHandler.ErrorBody(HttpStatusCode.NotImplemented, "x", AisRoute.None);
        foreach (var key in new[] { "error_code", "error_description", "message" })
            Assert.Equal(e[key].Type, live[key].Type);
    }
}
