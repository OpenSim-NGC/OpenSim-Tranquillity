using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using NUnit.Framework;
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
[TestFixture]
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
    [Test]
    public void copy_on_the_inventory_cap_is_the_only_501_left()
    {
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var handler = new AisHandler(cap, Agent, new ExplodingBackend());
        var path = $"/category/{Cat}?tid={UUID.Random()},depth=0";
        var route = AisRouter.Parse("COPY", cap + path, cap);
        Assert.That(route.Operation, Is.EqualTo(AisOperation.CopyCategory));

        var (req, resp) = Http("COPY", cap + path);
        handler.Dispatch(route, req, resp);

        Assert.That(resp.StatusCode, Is.EqualTo((int)HttpStatusCode.NotImplemented));
        Assert.That(resp.ContentType, Is.EqualTo("application/llsd+xml"));
        var map = (OSDMap)OSDParser.DeserializeLLSDXml(resp.RawBuffer);
        Assert.That(map["error_code"].AsInteger(), Is.EqualTo(501));
        Assert.That(map["operation"].AsString(), Is.EqualTo(nameof(AisOperation.CopyCategory)));
        // spec 1f: an error body must not look like content the viewer would apply
        Assert.That(map.ContainsKey("parent_id"), Is.False);
        Assert.That(map.ContainsKey("item_id"), Is.False);
        Assert.That(map.ContainsKey("category_id"), Is.False);
        Assert.That(map.ContainsKey("_embedded"), Is.False);
    }
    [Test]
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
        Assert.That(map["operation"].AsString(), Is.EqualTo(nameof(AisOperation.PurgeDescendents)));
        Assert.That(map["path"].AsString(), Is.EqualTo($"/category/{Cat}/children"));
    }

    [Test]
    public void an_unknown_route_returns_404_with_the_same_body_shape()
    {
        var cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
        var handler = new AisHandler(cap, Agent, new ExplodingBackend());
        var route = AisRouter.Parse("GET", cap + $"/category/{Cat}", cap);
        var (req, resp) = Http("GET", cap + $"/category/{Cat}");
        handler.Dispatch(route, req, resp);
        Assert.That(resp.StatusCode, Is.EqualTo((int)HttpStatusCode.NotFound));
        var map = (OSDMap)OSDParser.DeserializeLLSDXml(resp.RawBuffer);
        Assert.That(map["error_code"].AsInteger(), Is.EqualTo(404));
    }

    // ------------------------------------------------------------------ golden envelope fixtures (spec §1c)

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "AIS", "Fixtures");

    private static OSDMap LoadFixture(string name)
    {
        var path = Path.Combine(FixturesDir, name);
        Assert.That(File.Exists(path), Is.True, $"fixture {path} must be copied to the output directory");
        var osd = OSDParser.DeserializeLLSDXml(File.ReadAllText(path));
        Assert.That(osd, Is.InstanceOf<OSDMap>(), name);
        return (OSDMap)osd;
    }

    private static void AssertUuidArray(OSDMap map, string key)
    {
        Assert.That(map.ContainsKey(key), Is.True, key);
        Assert.That(map[key], Is.InstanceOf<OSDArray>(), key);
        foreach (var e in (OSDArray)map[key]) Assert.That(e.Type, Is.EqualTo(OSDType.UUID), $"{key} entries are uuids");
    }

    private static void AssertEmbedded(OSDMap category, bool requireAllThree)
    {
        Assert.That(category.ContainsKey("_embedded"), Is.True, "_embedded");
        var embedded = (OSDMap)category["_embedded"];
        foreach (var coll in new[] { "categories", "items", "links" })
        {
            if (requireAllThree) Assert.That(embedded.ContainsKey(coll), Is.True, $"_embedded.{coll} must be present even when empty (spec §1c: descendent count needs all three)");
            if (embedded.ContainsKey(coll)) Assert.That(embedded[coll], Is.InstanceOf<OSDMap>(), $"_embedded.{coll} is a map keyed by uuid string");
        }
    }

    [Test]
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
        Assert.That(m["_updated_category_versions"], Is.InstanceOf<OSDMap>());
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)m["_updated_category_versions"])
        {
            Assert.That(UUID.TryParse(kv.Key, out _), Is.True, "keys are category uuids");
            Assert.That(kv.Value.Type, Is.EqualTo(OSDType.Integer), "values are integer versions");
        }
        Assert.That(m["_embedded"], Is.InstanceOf<OSDMap>());
    }

    [Test]
    public void category_fetch_fixture_is_a_category_with_all_three_embedded_collections()
    {
        // spec §1c content keys, llaisapi.cpp:1203-1206, :1466-1482
        var c = LoadFixture("category-fetch.xml");
        Assert.That(c["category_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(c["parent_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(c["agent_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(c["version"].Type, Is.EqualTo(OSDType.Integer));
        AssertEmbedded(c, requireAllThree: true);
        var embedded = (OSDMap)c["_embedded"];
        // links are a separate collection, not items
        Assert.That(((OSDMap)embedded["links"]).Count, Is.EqualTo(1));
        Assert.That(((OSDMap)embedded["items"]).Count, Is.EqualTo(1));
        Assert.That(((OSDMap)embedded["categories"]).Count, Is.EqualTo(1));
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["links"])
        {
            var link = (OSDMap)kv.Value;
            Assert.That(link["linked_id"].Type, Is.EqualTo(OSDType.UUID), "a link carries linked_id (llaisapi.cpp:1185)");
            Assert.That(link["item_id"].AsString(), Is.EqualTo(kv.Key));
            Assert.That(link["parent_id"].AsUUID(), Is.EqualTo(c["category_id"].AsUUID()));
        }
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["items"])
        {
            var item = (OSDMap)kv.Value;
            Assert.That(item.ContainsKey("linked_id"), Is.False, "an item is not a link");
            Assert.That(item["item_id"].AsString(), Is.EqualTo(kv.Key));
            Assert.That(item["parent_id"].Type, Is.EqualTo(OSDType.UUID));
        }
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["categories"])
        {
            var sub = (OSDMap)kv.Value;
            Assert.That(sub["category_id"].AsString(), Is.EqualTo(kv.Key));
            Assert.That(sub["version"].Type, Is.EqualTo(OSDType.Integer));
            AssertEmbedded(sub, requireAllThree: true);
        }
    }

    [Test]
    public void cof_links_fixture_is_a_links_only_category()
    {
        // spec §1c: FT_CURRENT_OUTFIT may carry links alone (llaisapi.cpp:1477-1481); a link may embed its target
        var c = LoadFixture("cof-links.xml");
        Assert.That(c["category_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(c["type_default"].AsInteger(), Is.EqualTo((int)FolderType.CurrentOutfit));
        var embedded = (OSDMap)c["_embedded"];
        Assert.That(embedded.ContainsKey("links"), Is.True);
        Assert.That(embedded.ContainsKey("items"), Is.False);
        foreach (KeyValuePair<string, OSD> kv in (OSDMap)embedded["links"])
        {
            var link = (OSDMap)kv.Value;
            Assert.That(link["linked_id"].Type, Is.EqualTo(OSDType.UUID));
            var linkEmbedded = (OSDMap)link["_embedded"];
            Assert.That(linkEmbedded["item"], Is.InstanceOf<OSDMap>(), "_embedded.item inside a link (llaisapi.cpp:1496)");
            Assert.That(((OSDMap)linkEmbedded["item"])["item_id"].AsUUID(), Is.EqualTo(link["linked_id"].AsUUID()));
        }
    }

    [Test]
    public void item_fetch_fixture_is_an_item()
    {
        var i = LoadFixture("item-fetch.xml");
        Assert.That(i["item_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(i["parent_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(i.ContainsKey("linked_id"), Is.False);
        Assert.That(i["permissions"], Is.InstanceOf<OSDMap>());
        Assert.That(i["sale_info"], Is.InstanceOf<OSDMap>());
    }

    [Test]
    public void link_fetch_fixture_is_a_link_with_an_embedded_target()
    {
        var l = LoadFixture("link-fetch.xml");
        Assert.That(l["linked_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(l["item_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(l["parent_id"].Type, Is.EqualTo(OSDType.UUID));
        Assert.That(((OSDMap)l["_embedded"])["item"], Is.InstanceOf<OSDMap>());
    }

    [Test]
    public void error_fixture_is_a_flat_map_the_viewer_ignores()
    {
        // spec §1f
        var e = LoadFixture("error.xml");
        Assert.That(e["error_code"].Type, Is.EqualTo(OSDType.Integer));
        Assert.That(e["error_description"].Type, Is.EqualTo(OSDType.String));
        Assert.That(e["message"].Type, Is.EqualTo(OSDType.String));
        Assert.That(e.ContainsKey("parent_id"), Is.False);
        Assert.That(e.ContainsKey("_embedded"), Is.False);
        var live = AisHandler.ErrorBody(HttpStatusCode.NotImplemented, "x", AisRoute.None);
        foreach (var key in new[] { "error_code", "error_description", "message" })
            Assert.That(live[key].Type, Is.EqualTo(e[key].Type), $"live error body and fixture agree on {key}");
    }
}
