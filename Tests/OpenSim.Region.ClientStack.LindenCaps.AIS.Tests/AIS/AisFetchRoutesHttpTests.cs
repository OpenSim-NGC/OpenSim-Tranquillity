using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// Every A1 fetch route driven over HTTP against the handler with an in-memory backend, asserting the envelope
/// the viewer parses (AIS-V3-SPEC.md §1c/§1d) and the shapes the A0 fixtures pin. These are the cases that catch
/// real divergence — links kept out of <c>items</c>, the depth shapes, a subset naming a child that is not there,
/// an empty COF, an unknown id — not just the happy paths.
/// </summary>
[TestFixture]
public class AisFetchRoutesHttpTests
{
    private const string Cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Clothing = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Outfits = new("66666666-6666-4666-8666-666666666666");
    private static readonly UUID Party = new("77777777-7777-4777-8777-777777777771");
    private static readonly UUID Cof = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly UUID Shirt = new("22222222-2222-4222-8222-222222222222");
    private static readonly UUID Pants = new("22222222-2222-4222-8222-222222222223");
    private static readonly UUID LinkToShirt = new("88888888-8888-4888-8888-888888888888");
    private static readonly UUID LinkToPants = new("88888888-8888-4888-8888-888888888889");

    /// <summary>Minimal IOSHttpRequest: only what the handler reads (verb, raw url, body).</summary>
    private sealed class FetchTestRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public FetchTestRequest(string verb, string url) { HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url; }
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
        public IPEndPoint RemoteIPEndPoint => new(IPAddress.Loopback, 1);
        public IPEndPoint LocalIPEndPoint => new(IPAddress.Loopback, 2);
        public string UserAgent => "test";
        public double ArrivalTS => 0;
    }

    private static FakeAisBackend Inventory()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddFolder(Outfits, Clothing, "Outfits", 2);
        b.AddFolder(Party, Outfits, "Party", 5);
        b.AddFolder(Cof, Root, "Current Outfit", 11, (short)FolderType.CurrentOutfit);
        b.CurrentOutfitId = Cof;
        b.AddItem(Shirt, Clothing, "Blue Shirt");
        b.AddItem(Pants, Clothing, "Grey Pants");
        b.AddLink(LinkToShirt, Cof, "Blue Shirt", Shirt);
        b.AddLink(LinkToPants, Cof, "Grey Pants", Pants);
        return b;
    }

    /// <summary>Drives one request end to end through SimpleStreamHandler.Handle and returns status + parsed body.</summary>
    private static (int Status, OSDMap Body) Get(FakeAisBackend backend, string path, AisMode mode = AisMode.Inventory)
    {
        var handler = new AisHandler(Cap, Agent, backend, mode);
        var request = new FetchTestRequest("GET", Cap + path);
        var response = new TestOSHttpResponse();
        handler.Handle(request, response);
        Assert.That(response.ContentType, Is.EqualTo("application/llsd+xml"), $"GET {path}");
        var body = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.That(body, Is.InstanceOf<OSDMap>(), "the viewer forces 500 on a non-map body (llaisapi.cpp:882-885)");
        return (response.StatusCode, (OSDMap)body);
    }

    private static OSDMap Embedded(OSDMap category) => (OSDMap)category["_embedded"];
    private static OSDMap Coll(OSDMap category, string name) => (OSDMap)Embedded(category)[name];

    // ------------------------------------------------------------------ GET /item/{id}

    [Test]
    public void item_route_returns_an_item_and_a_link_route_returns_a_link()
    {
        var b = Inventory();

        var (status, item) = Get(b, $"/item/{Shirt}");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(item["item_id"].AsUUID(), Is.EqualTo(Shirt));
        Assert.That(item["parent_id"].AsUUID(), Is.EqualTo(Clothing));
        Assert.That(item.ContainsKey("linked_id"), Is.False, "a real item must not carry linked_id: that would select parseLink (§1c)");
        Assert.That(item["permissions"], Is.InstanceOf<OSDMap>());
        Assert.That(item["sale_info"], Is.InstanceOf<OSDMap>());

        var (linkStatus, link) = Get(b, $"/item/{LinkToShirt}");
        Assert.That(linkStatus, Is.EqualTo(200));
        Assert.That(link["linked_id"].AsUUID(), Is.EqualTo(Shirt), "a link carries the target id (§1d)");
        Assert.That(link["item_id"].AsUUID(), Is.EqualTo(LinkToShirt));
        Assert.That(link.ContainsKey("permissions"), Is.False, "the viewer overwrites a link's permissions with defaults (llaisapi.cpp:1278-1283)");
    }

    [Test]
    public void an_unknown_item_id_is_404_with_the_error_body_shape()
    {
        var (status, body) = Get(Inventory(), $"/item/{UUID.Random()}");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ GET /category/{id}/children

    [Test]
    public void children_at_depth_0_expands_the_folder_only()
    {
        var (status, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=0");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(cat["category_id"].AsUUID(), Is.EqualTo(Clothing));
        Assert.That(cat["parent_id"].AsUUID(), Is.EqualTo(Root));
        Assert.That(cat["version"].AsInteger(), Is.EqualTo(7), "the folder's version, read fresh (T4)");

        Assert.That(Coll(cat, "items").Count, Is.EqualTo(2));
        Assert.That(Coll(cat, "links").Count, Is.EqualTo(0));
        var categories = Coll(cat, "categories");
        Assert.That(categories.Count, Is.EqualTo(1));
        var child = (OSDMap)categories[Outfits.ToString()];
        Assert.That(child.ContainsKey("_embedded"), Is.False, "at depth 0 a child category is a stub, not expanded");
    }

    [Test]
    public void children_at_depth_1_and_2_expand_one_and_two_generations()
    {
        var b = Inventory();

        var (_, d1) = Get(b, $"/category/{Clothing}/children?depth=1");
        var outfits1 = (OSDMap)Coll(d1, "categories")[Outfits.ToString()];
        Assert.That(outfits1.ContainsKey("_embedded"), Is.True, "depth 1 expands the child");
        var party1 = (OSDMap)Coll(outfits1, "categories")[Party.ToString()];
        Assert.That(party1.ContainsKey("_embedded"), Is.False, "depth 1 stops at the grandchild");

        var (_, d2) = Get(b, $"/category/{Clothing}/children?depth=2");
        var outfits2 = (OSDMap)Coll(d2, "categories")[Outfits.ToString()];
        var party2 = (OSDMap)Coll(outfits2, "categories")[Party.ToString()];
        Assert.That(party2.ContainsKey("_embedded"), Is.True, "depth 2 expands the grandchild");
        Assert.That(Coll(party2, "categories").Count, Is.EqualTo(0));
    }

    [Test]
    public void an_expanded_category_always_carries_all_three_collections()
    {
        // spec §1c: the viewer knows a folder's descendent count only from all three, and versions it only then
        var (_, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=2");
        void AssertAllThree(OSDMap c)
        {
            if (!c.ContainsKey("_embedded")) return;
            var e = Embedded(c);
            Assert.That(e.ContainsKey("categories"), Is.True, $"{c["name"].AsString()}: categories");
            Assert.That(e.ContainsKey("items"), Is.True, $"{c["name"].AsString()}: items");
            Assert.That(e.ContainsKey("links"), Is.True, $"{c["name"].AsString()}: links");
            foreach (var child in ((OSDMap)e["categories"]).Values) AssertAllThree((OSDMap)child);
        }
        AssertAllThree(cat);
    }

    [Test]
    public void an_unknown_category_id_is_404_with_the_error_body_shape()
    {
        var (status, body) = Get(Inventory(), $"/category/{UUID.Random()}/children?depth=1");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ subset

    [Test]
    public void a_subset_returns_only_the_named_children_and_skips_one_that_does_not_exist()
    {
        var absent = UUID.Random();
        var (status, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=1&children={Shirt},{absent},{Outfits}");
        Assert.That(status, Is.EqualTo(200), "a named child that is gone does not fail the request");

        Assert.That(Coll(cat, "items").Keys, Is.EquivalentTo(new[] { Shirt.ToString() }), "Pants was not asked for");
        Assert.That(Coll(cat, "categories").Keys, Is.EquivalentTo(new[] { Outfits.ToString() }));
        Assert.That(Coll(cat, "items").ContainsKey(absent.ToString()), Is.False);
        Assert.That(Coll(cat, "categories").ContainsKey(absent.ToString()), Is.False);
    }

    // ------------------------------------------------------------------ categories

    [Test]
    public void the_categories_route_returns_sub_folders_only_and_no_sibling_collections()
    {
        var (status, cat) = Get(Inventory(), $"/category/{Clothing}/categories?depth=1");
        Assert.That(status, Is.EqualTo(200));
        var e = Embedded(cat);
        Assert.That(((OSDMap)e["categories"]).Keys, Is.EquivalentTo(new[] { Outfits.ToString() }));
        Assert.That(e.ContainsKey("items"), Is.False,
            "a partial view must not carry empty siblings: the viewer would read a descendent count of 1 and version a folder it has not seen (§1c)");
        Assert.That(e.ContainsKey("links"), Is.False);
    }

    // ------------------------------------------------------------------ links

    [Test]
    public void links_are_their_own_collection_and_items_carries_the_link_targets()
    {
        // the COF's links point at items that live in Clothing, not in the COF
        var (status, cat) = Get(Inventory(), $"/category/{Cof}/links");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(cat["category_id"].AsUUID(), Is.EqualTo(Cof));

        var links = Coll(cat, "links");
        Assert.That(links.Keys, Is.EquivalentTo(new[] { LinkToShirt.ToString(), LinkToPants.ToString() }),
            "keyed by the link's own id, not the target's");
        Assert.That(((OSDMap)links[LinkToShirt.ToString()])["linked_id"].AsUUID(), Is.EqualTo(Shirt));

        var items = Coll(cat, "items");
        Assert.That(items.Keys, Is.EquivalentTo(new[] { Shirt.ToString(), Pants.ToString() }),
            "items carries the link TARGETS — the real items the links resolve to (FetchInvDescHandler.cs:429), never the links");
        foreach (var value in items.Values)
            Assert.That(((OSDMap)value).ContainsKey("linked_id"), Is.False, "a link must never appear in the items collection (risk A-R4)");
    }

    [Test]
    public void the_cof_alias_resolves_to_the_current_outfit_folder()
    {
        var b = Inventory();
        var (status, cat) = Get(b, "/category/current/links");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(cat["category_id"].AsUUID(), Is.EqualTo(Cof), "'current' resolved by folder type (T2)");
        Assert.That(b.Calls, Does.Contain("GetInventorySkeleton"), "resolved deterministically over the skeleton (A7)");
        Assert.That(Coll(cat, "links").Count, Is.EqualTo(2));
    }

    [Test]
    public void an_empty_cof_returns_an_empty_links_collection_not_an_error()
    {
        var b = Inventory();
        b.Items.Remove(LinkToShirt);
        b.Items.Remove(LinkToPants);

        var (status, cat) = Get(b, "/category/current/links");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(cat["category_id"].AsUUID(), Is.EqualTo(Cof));
        Assert.That(Coll(cat, "links").Count, Is.EqualTo(0), "an empty outfit is an empty links map, so the viewer can count 0 descendents");
        Assert.That(Coll(cat, "items").Count, Is.EqualTo(0));
    }

    [Test]
    public void an_agent_with_no_cof_at_all_is_404()
    {
        var b = Inventory();
        b.CurrentOutfitId = UUID.Zero;
        b.Folders.Remove(Cof);
        var (status, body) = Get(b, "/category/current/links");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ orphans

    [Test]
    public void the_orphans_route_lists_folders_whose_parent_is_gone()
    {
        var b = Inventory();
        var (status, body) = Get(b, "/orphans");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(body.ContainsKey("category_id"), Is.False, "no top-level object, so the viewer parses _embedded straight (§1c)");
        Assert.That(((OSDMap)Embedded(body)["categories"]).Count, Is.EqualTo(0));

        b.Folders.Remove(Outfits);
        var (_, withOrphan) = Get(b, "/orphans");
        Assert.That(((OSDMap)Embedded(withOrphan)["categories"]).Keys, Is.EquivalentTo(new[] { Party.ToString() }));
    }

    // ------------------------------------------------------------------ tid, library, config

    [Test]
    public void tid_is_echoed_when_the_request_carries_one()
    {
        var tid = UUID.Random();
        var (_, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=0&tid={tid}");
        Assert.That(cat["tid"].AsUUID(), Is.EqualTo(tid));

        var (_, noTid) = Get(Inventory(), $"/category/{Clothing}/children?depth=0");
        Assert.That(noTid.ContainsKey("tid"), Is.False, "nothing is invented when the request had no tid");
    }

    [Test]
    public void the_library_cap_serves_the_same_reads_and_refuses_every_mutation_with_405()
    {
        var b = Inventory();
        var (status, cat) = Get(b, $"/category/{Clothing}/children?depth=0", AisMode.Library);
        Assert.That(status, Is.EqualTo(200), "reads are identical on the library cap");
        Assert.That(cat["category_id"].AsUUID(), Is.EqualTo(Clothing));

        foreach (var (verb, path) in new (string, string)[]
        {
            ("POST", $"/category/{Clothing}"), ("PUT", $"/category/{Clothing}/links"), ("DELETE", $"/category/{Clothing}"),
            ("DELETE", $"/item/{Shirt}"), ("PATCH", $"/category/{Clothing}"), ("PATCH", $"/item/{Shirt}"),
            ("DELETE", $"/category/{Clothing}/children"),
        })
        {
            var handler = new AisHandler(Cap, Agent, b, AisMode.Library);
            var response = new TestOSHttpResponse();
            handler.Handle(new FetchTestRequest(verb, Cap + path), response);
            Assert.That(response.StatusCode, Is.EqualTo((int)HttpStatusCode.MethodNotAllowed), $"{verb} {path} on the library");
            var body = (OSDMap)OSDParser.DeserializeLLSDXml(response.RawBuffer);
            AssertErrorBody(body, 405);
        }
    }

    /// <summary>
    /// The advertisement gate (A5). Both cap names are pinned, the default is off, and the per-region override
    /// resolves independently for each region - a grid-wide flip is unacceptable under risk A-R1, because
    /// turning AIS on hands the LL viewer's whole inventory path to it with no fallback.
    /// </summary>
    [Test]
    public void the_flag_defaults_off_and_resolves_per_region()
    {
        Assert.That(AISv3Module.CapName, Is.EqualTo("InventoryAPIv3"));
        Assert.That(AISv3Module.LibraryCapName, Is.EqualTo("LibraryAPIv3"));

        var module = new AISv3Module();
        module.Initialise(new Nini.Config.IniConfigSource());
        Assert.That(module.Enabled, Is.False, "[AIS] Enabled defaults to false (A-D4, risk A-R1)");
        Assert.DoesNotThrow(() => module.RegionLoaded(null), "a null scene is ignored, not dereferenced");

        var source = new Nini.Config.IniConfigSource();
        source.AddConfig("AIS").Set("Enabled", "true");
        var enabled = new AISv3Module();
        enabled.Initialise(source);
        Assert.That(enabled.Enabled, Is.True);
    }

    /// <summary>
    /// One region can be turned on without touching the others, using the per-region idiom this tree already has
    /// (a [&lt;Region Name&gt;] section, as AutoBackupModule.cs:400-406 reads it). Resolution is static and takes a
    /// plain config source, so it is provable without building a Scene.
    /// </summary>
    [Test]
    public void one_region_can_be_enabled_without_affecting_the_others()
    {
        var sceneConfig = new Nini.Config.IniConfigSource();
        sceneConfig.AddConfig("AIS").Set("Enabled", "false");   // grid default: off
        sceneConfig.AddConfig("Ebony").Set("AIS_Enabled", "true");
        sceneConfig.AddConfig("Transylvania").Set("SomethingElse", "1");

        Assert.That(AISv3Module.ResolveEnabled(false, sceneConfig, "Ebony"), Is.True,
            "the named region opts in");
        Assert.That(AISv3Module.ResolveEnabled(false, sceneConfig, "Transylvania"), Is.False,
            "a region with its own section but no AIS_Enabled key stays on the grid default");
        Assert.That(AISv3Module.ResolveEnabled(false, sceneConfig, "Elm"), Is.False,
            "a region with no section at all stays on the grid default");

        // and the override works downwards too: a grid that is on can exempt one region
        var optOut = new Nini.Config.IniConfigSource();
        optOut.AddConfig("Elm").Set("AIS_Enabled", "false");
        Assert.That(AISv3Module.ResolveEnabled(true, optOut, "Elm"), Is.False);
        Assert.That(AISv3Module.ResolveEnabled(true, optOut, "Ebony"), Is.True);

        Assert.That(AISv3Module.ResolveEnabled(false, null, "Ebony"), Is.False,
            "no scene config at all falls back to the grid default");
    }
    // ------------------------------------------------------------------ shared assertions

    /// <summary>Spec §1f: an error body is a flat map that the viewer's update parser finds nothing to apply in.</summary>
    private static void AssertErrorBody(OSDMap body, int code)
    {
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(code));
        Assert.That(body.ContainsKey("message"), Is.True);
        Assert.That(body.ContainsKey("parent_id"), Is.False);
        Assert.That(body.ContainsKey("item_id"), Is.False);
        Assert.That(body.ContainsKey("category_id"), Is.False);
        Assert.That(body.ContainsKey("_embedded"), Is.False);
    }

    // ------------------------------------------------------------------ conformance with the A0 fixtures

    private static OSDMap LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AIS", "Fixtures", name);
        return (OSDMap)OSDParser.DeserializeLLSDXml(File.ReadAllBytes(path));
    }

    /// <summary>
    /// The live envelopes carry exactly the keys the golden fixtures pin — no extra keys, none missing. The
    /// fixtures were corrected in A1 to the field set A-Q1 resolved: <c>type</c>, <c>inv_type</c> and
    /// <c>sale_type</c> as integers (<c>LLInventoryItem::fromLLSD</c> accepts either, llinventory.cpp:1108-1135,
    /// and integers are what this tree already sends, LLSDInventoryItem.cs:33-68), and no <c>last_owner_id</c>
    /// in <c>permissions</c> for the same reason.
    /// </summary>
    [Test]
    public void live_envelopes_match_the_golden_fixture_key_sets()
    {
        var b = Inventory();

        var (_, item) = Get(b, $"/item/{Shirt}");
        var itemFixture = LoadFixture("item-fetch.xml");
        Assert.That(item.Keys, Is.EquivalentTo(itemFixture.Keys), "item envelope");
        Assert.That(((OSDMap)item["permissions"]).Keys, Is.EquivalentTo(((OSDMap)itemFixture["permissions"]).Keys), "permissions");
        Assert.That(((OSDMap)item["sale_info"]).Keys, Is.EquivalentTo(((OSDMap)itemFixture["sale_info"]).Keys), "sale_info");
        foreach (var key in itemFixture.Keys)
            Assert.That(item[key].Type, Is.EqualTo(itemFixture[key].Type), $"item.{key} LLSD type");

        var (_, cat) = Get(b, $"/category/{Clothing}/children?depth=1");
        var catFixture = LoadFixture("category-fetch.xml");
        Assert.That(cat.Keys, Is.EquivalentTo(catFixture.Keys), "category envelope");
        Assert.That(Embedded(cat).Keys, Is.EquivalentTo(Embedded(catFixture).Keys), "_embedded collections");

        var (_, link) = Get(b, $"/item/{LinkToShirt}");
        var linkFixture = (OSDMap)((OSDMap)Coll(catFixture, "links")).Values.First();
        Assert.That(link.Keys, Is.EquivalentTo(linkFixture.Keys), "link envelope");
    }

    /// <summary>
    /// The depth contract of spec 1c-bis, pinned: N licenses exactly N generations below the requested folder,
    /// an expanded category carries all three collections and a version, and an unexpanded one is a stub. The
    /// viewer versions a category only while its decremented depth is still >= 0 and its descendent count is
    /// known (llaisapi.cpp:1380-1407), so our deepest expanded generation lands on exactly 0 - the last value
    /// that still counts. A requested depth above the viewer own ceiling of 50 is clamped.
    /// </summary>
    [Test]
    public void the_depth_contract_matches_the_viewers_parse()
    {
        var b = Inventory();

        // the two depths the viewer actually sends (llinventorymodelbackgroundfetch.cpp:937, :994 with
        // llaisapi.cpp:463-474): 0 for a plain fetch, 50 for a recursive one
        var (_, d0) = Get(b, $"/category/{Clothing}/children?depth=0");
        Assert.That(((OSDMap)Coll(d0, "categories")[Outfits.ToString()]).ContainsKey("_embedded"), Is.False,
            "depth 0 versions the requested folder only; every child must be a stub so the viewer re-queues it");

        var (_, d50) = Get(b, $"/category/{Clothing}/children?depth=50");
        var outfits = (OSDMap)Coll(d50, "categories")[Outfits.ToString()];
        var party = (OSDMap)Coll(outfits, "categories")[Party.ToString()];
        Assert.That(party.ContainsKey("_embedded"), Is.True, "a recursive fetch expands the whole tree");

        // every expanded category carries all three collections AND a version, or the viewer cannot count its
        // descendents and will never version it (llaisapi.cpp:1466-1482, risk A-R3)
        void AssertExpandedAreCountable(OSDMap c)
        {
            if (!c.ContainsKey("_embedded")) return;
            Assert.That(c.ContainsKey("version"), Is.True, c["name"].AsString());
            var e = Embedded(c);
            foreach (var name in new[] { "categories", "items", "links" })
                Assert.That(e.ContainsKey(name), Is.True, $"{c["name"].AsString()}: {name}");
            foreach (var child in ((OSDMap)e["categories"]).Values) AssertExpandedAreCountable((OSDMap)child);
        }
        AssertExpandedAreCountable(d50);

        // a depth beyond the viewer ceiling is clamped, not honoured literally
        var (status, clamped) = Get(b, $"/category/{Clothing}/children?depth=100000");
        Assert.That(status, Is.EqualTo(200));
        Assert.That(((OSDMap)Coll((OSDMap)Coll(clamped, "categories")[Outfits.ToString()], "categories")[Party.ToString()])
            .ContainsKey("_embedded"), Is.True, "clamping to 50 still covers any real inventory");
    }
}
