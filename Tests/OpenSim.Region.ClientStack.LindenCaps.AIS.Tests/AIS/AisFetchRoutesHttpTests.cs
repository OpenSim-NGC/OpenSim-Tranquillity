/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// Every fetch route driven over HTTP against the handler with an in-memory backend, asserting the envelope
/// the viewer parses (AIS-V3-SPEC.md §1c/§1d) and the shapes the golden fixtures pin. These are the cases that catch
/// real divergence — links kept out of <c>items</c>, the depth shapes, a subset naming a child that is not there,
/// an empty COF, an unknown id — not just the happy paths.
/// </summary>
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
        Assert.Equal("application/llsd+xml", response.ContentType);
        var body = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.True(body is OSDMap, "the viewer forces 500 on a non-map body (llaisapi.cpp:882-885)");
        return (response.StatusCode, (OSDMap)body);
    }

    private static OSDMap Embedded(OSDMap category) => (OSDMap)category["_embedded"];
    private static OSDMap Coll(OSDMap category, string name) => (OSDMap)Embedded(category)[name];

    // ------------------------------------------------------------------ GET /item/{id}

    [Fact]
    public void item_route_returns_an_item_and_a_link_route_returns_a_link()
    {
        var b = Inventory();

        var (status, item) = Get(b, $"/item/{Shirt}");
        Assert.Equal(200, status);
        Assert.Equal(Shirt, item["item_id"].AsUUID());
        Assert.Equal(Clothing, item["parent_id"].AsUUID());
        Assert.False(item.ContainsKey("linked_id"), "a real item must not carry linked_id: that would select parseLink (§1c)");
        Assert.IsAssignableFrom<OSDMap>(item["permissions"]);
        Assert.IsAssignableFrom<OSDMap>(item["sale_info"]);

        var (linkStatus, link) = Get(b, $"/item/{LinkToShirt}");
        Assert.Equal(200, linkStatus);
        Assert.Equal(Shirt, link["linked_id"].AsUUID());
        Assert.Equal(LinkToShirt, link["item_id"].AsUUID());
        Assert.False(link.ContainsKey("permissions"), "the viewer overwrites a link's permissions with defaults (llaisapi.cpp:1278-1283)");
    }

    [Fact]
    public void an_unknown_item_id_is_404_with_the_error_body_shape()
    {
        var (status, body) = Get(Inventory(), $"/item/{UUID.Random()}");
        Assert.Equal((int)HttpStatusCode.NotFound, status);
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ GET /category/{id}/children

    [Fact]
    public void children_at_depth_0_expands_the_folder_only()
    {
        var (status, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=0");
        Assert.Equal(200, status);
        Assert.Equal(Clothing, cat["category_id"].AsUUID());
        Assert.Equal(Root, cat["parent_id"].AsUUID());
        Assert.Equal(7, cat["version"].AsInteger());

        Assert.Equal(2, Coll(cat, "items").Count);
        Assert.Empty(Coll(cat, "links"));
        var categories = Coll(cat, "categories");
        Assert.True(categories.Count == 1);
        var child = (OSDMap)categories[Outfits.ToString()];
        Assert.False(child.ContainsKey("_embedded"), "at depth 0 a child category is a stub, not expanded");
    }

    [Fact]
    public void children_at_depth_1_and_2_expand_one_and_two_generations()
    {
        var b = Inventory();

        var (_, d1) = Get(b, $"/category/{Clothing}/children?depth=1");
        var outfits1 = (OSDMap)Coll(d1, "categories")[Outfits.ToString()];
        Assert.True(outfits1.ContainsKey("_embedded"), "depth 1 expands the child");
        var party1 = (OSDMap)Coll(outfits1, "categories")[Party.ToString()];
        Assert.False(party1.ContainsKey("_embedded"), "depth 1 stops at the grandchild");

        var (_, d2) = Get(b, $"/category/{Clothing}/children?depth=2");
        var outfits2 = (OSDMap)Coll(d2, "categories")[Outfits.ToString()];
        var party2 = (OSDMap)Coll(outfits2, "categories")[Party.ToString()];
        Assert.True(party2.ContainsKey("_embedded"), "depth 2 expands the grandchild");
        Assert.Empty(Coll(party2, "categories"));
    }

    [Fact]
    public void an_expanded_category_always_carries_all_three_collections()
    {
        // spec §1c: the viewer knows a folder's descendent count only from all three, and versions it only then
        var (_, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=2");
        void AssertAllThree(OSDMap c)
        {
            if (!c.ContainsKey("_embedded")) return;
            var e = Embedded(c);
            Assert.True(e.ContainsKey("categories"), $"{c["name"].AsString()}: categories");
            Assert.True(e.ContainsKey("items"), $"{c["name"].AsString()}: items");
            Assert.True(e.ContainsKey("links"), $"{c["name"].AsString()}: links");
            foreach (var child in ((OSDMap)e["categories"]).Values) AssertAllThree((OSDMap)child);
        }
        AssertAllThree(cat);
    }

    [Fact]
    public void an_unknown_category_id_is_404_with_the_error_body_shape()
    {
        var (status, body) = Get(Inventory(), $"/category/{UUID.Random()}/children?depth=1");
        Assert.Equal((int)HttpStatusCode.NotFound, status);
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ subset

    [Fact]
    public void a_subset_returns_only_the_named_children_and_skips_one_that_does_not_exist()
    {
        var absent = UUID.Random();
        var (status, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=1&children={Shirt},{absent},{Outfits}");
        Assert.Equal(200, status);

        Assert.Equivalent(new[] { Shirt.ToString() }, Coll(cat, "items").Keys, true);
        Assert.Equivalent(new[] { Outfits.ToString() }, Coll(cat, "categories").Keys, true);
        Assert.False(Coll(cat, "items").ContainsKey(absent.ToString()));
        Assert.False(Coll(cat, "categories").ContainsKey(absent.ToString()));
    }

    // ------------------------------------------------------------------ categories

    [Fact]
    public void the_categories_route_returns_sub_folders_only_and_no_sibling_collections()
    {
        var (status, cat) = Get(Inventory(), $"/category/{Clothing}/categories?depth=1");
        Assert.Equal(200, status);
        var e = Embedded(cat);
        Assert.Equivalent(new[] { Outfits.ToString() }, ((OSDMap)e["categories"]).Keys, true);
        Assert.False(e.ContainsKey("items"), "a partial view must not carry empty siblings: the viewer would read a descendent count of 1 and version a folder it has not seen (§1c)");
        Assert.False(e.ContainsKey("links"));
    }

    // ------------------------------------------------------------------ links

    [Fact]
    public void links_are_their_own_collection_and_items_carries_the_link_targets()
    {
        // the COF's links point at items that live in Clothing, not in the COF
        var (status, cat) = Get(Inventory(), $"/category/{Cof}/links");
        Assert.Equal(200, status);
        Assert.Equal(Cof, cat["category_id"].AsUUID());

        var links = Coll(cat, "links");
        Assert.Equivalent(new[] { LinkToShirt.ToString(), LinkToPants.ToString() }, links.Keys, true);
        Assert.Equal(Shirt, ((OSDMap)links[LinkToShirt.ToString()])["linked_id"].AsUUID());

        var items = Coll(cat, "items");
        Assert.Equivalent(new[] { Shirt.ToString(), Pants.ToString() }, items.Keys, true);
        foreach (var value in items.Values)
            Assert.False(((OSDMap)value).ContainsKey("linked_id"), "a link must never appear in the items collection (risk A-R4)");
    }

    [Fact]
    public void the_cof_alias_resolves_to_the_current_outfit_folder()
    {
        var b = Inventory();
        var (status, cat) = Get(b, "/category/current/links");
        Assert.Equal(200, status);
        Assert.Equal(Cof, cat["category_id"].AsUUID());
        Assert.Contains("GetInventorySkeleton", b.Calls);
        Assert.Equal(2, Coll(cat, "links").Count);
    }

    [Fact]
    public void an_empty_cof_returns_an_empty_links_collection_not_an_error()
    {
        var b = Inventory();
        b.Items.Remove(LinkToShirt);
        b.Items.Remove(LinkToPants);

        var (status, cat) = Get(b, "/category/current/links");
        Assert.Equal(200, status);
        Assert.Equal(Cof, cat["category_id"].AsUUID());
        Assert.Empty(Coll(cat, "links"));
        Assert.Empty(Coll(cat, "items"));
    }

    [Fact]
    public void an_agent_with_no_cof_at_all_is_404()
    {
        var b = Inventory();
        b.CurrentOutfitId = UUID.Zero;
        b.Folders.Remove(Cof);
        var (status, body) = Get(b, "/category/current/links");
        Assert.Equal((int)HttpStatusCode.NotFound, status);
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ orphans

    [Fact]
    public void the_orphans_route_lists_folders_whose_parent_is_gone()
    {
        var b = Inventory();
        var (status, body) = Get(b, "/orphans");
        Assert.Equal(200, status);
        Assert.False(body.ContainsKey("category_id"), "no top-level object, so the viewer parses _embedded straight (§1c)");
        Assert.Empty((OSDMap)Embedded(body)["categories"]);

        b.Folders.Remove(Outfits);
        var (_, withOrphan) = Get(b, "/orphans");
        Assert.Equivalent(new[] { Party.ToString() }, ((OSDMap)Embedded(withOrphan)["categories"]).Keys, true);
    }

    // ------------------------------------------------------------------ tid, library, config

    [Fact]
    public void tid_is_echoed_when_the_request_carries_one()
    {
        var tid = UUID.Random();
        var (_, cat) = Get(Inventory(), $"/category/{Clothing}/children?depth=0&tid={tid}");
        Assert.Equal(tid, cat["tid"].AsUUID());

        var (_, noTid) = Get(Inventory(), $"/category/{Clothing}/children?depth=0");
        Assert.False(noTid.ContainsKey("tid"), "nothing is invented when the request had no tid");
    }

    [Fact]
    public void the_library_cap_serves_the_same_reads_and_refuses_every_mutation_with_405()
    {
        var b = Inventory();
        var (status, cat) = Get(b, $"/category/{Clothing}/children?depth=0", AisMode.Library);
        Assert.Equal(200, status);
        Assert.Equal(Clothing, cat["category_id"].AsUUID());

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
            Assert.Equal((int)HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var body = (OSDMap)OSDParser.DeserializeLLSDXml(response.RawBuffer);
            AssertErrorBody(body, 405);
        }
    }

    /// <summary>
    /// The advertisement gate (A5). Both cap names are pinned, the default is off, and the per-region override
    /// resolves independently for each region - a grid-wide flip is unacceptable under risk A-R1, because
    /// turning AIS on hands the LL viewer's whole inventory path to it with no fallback.
    /// </summary>
    [Fact]
    public void the_flag_defaults_off_and_resolves_per_region()
    {
        Assert.Equal("InventoryAPIv3", AISv3Module.CapName);
        Assert.Equal("LibraryAPIv3", AISv3Module.LibraryCapName);

        var module = new AISv3Module();
        module.Initialise(new Nini.Config.IniConfigSource());
        Assert.False(module.Enabled, "[AIS] Enabled defaults to false (A-D4, risk A-R1)");
        Assert.True(Record.Exception(() => module.RegionLoaded(null)) is null, "a null scene is ignored, not dereferenced");

        var source = new Nini.Config.IniConfigSource();
        source.AddConfig("AIS").Set("Enabled", "true");
        var enabled = new AISv3Module();
        enabled.Initialise(source);
        Assert.True(enabled.Enabled);
    }

    /// <summary>
    /// One region can be turned on without touching the others, using the per-region idiom this tree already has
    /// (a [&lt;Region Name&gt;] section, as AutoBackupModule.cs:400-406 reads it). Resolution is static and takes a
    /// plain config source, so it is provable without building a Scene.
    /// </summary>
    [Fact]
    public void one_region_can_be_enabled_without_affecting_the_others()
    {
        var sceneConfig = new Nini.Config.IniConfigSource();
        sceneConfig.AddConfig("AIS").Set("Enabled", "false");   // grid default: off
        sceneConfig.AddConfig("Ebony").Set("AIS_Enabled", "true");
        sceneConfig.AddConfig("Transylvania").Set("SomethingElse", "1");

        Assert.True(AISv3Module.ResolveEnabled(false, sceneConfig, "Ebony"), "the named region opts in");
        Assert.False(AISv3Module.ResolveEnabled(false, sceneConfig, "Transylvania"), "a region with its own section but no AIS_Enabled key stays on the grid default");
        Assert.False(AISv3Module.ResolveEnabled(false, sceneConfig, "Elm"), "a region with no section at all stays on the grid default");

        // and the override works downwards too: a grid that is on can exempt one region
        var optOut = new Nini.Config.IniConfigSource();
        optOut.AddConfig("Elm").Set("AIS_Enabled", "false");
        Assert.False(AISv3Module.ResolveEnabled(true, optOut, "Elm"));
        Assert.True(AISv3Module.ResolveEnabled(true, optOut, "Ebony"));

        Assert.False(AISv3Module.ResolveEnabled(false, null, "Ebony"), "no scene config at all falls back to the grid default");
    }
    // ------------------------------------------------------------------ shared assertions

    /// <summary>Spec §1f: an error body is a flat map that the viewer's update parser finds nothing to apply in.</summary>
    private static void AssertErrorBody(OSDMap body, int code)
    {
        Assert.Equal(code, body["error_code"].AsInteger());
        Assert.True(body.ContainsKey("message"));
        Assert.False(body.ContainsKey("parent_id"));
        Assert.False(body.ContainsKey("item_id"));
        Assert.False(body.ContainsKey("category_id"));
        Assert.False(body.ContainsKey("_embedded"));
    }

    // ------------------------------------------------------------------ conformance with the A0 fixtures

    private static OSDMap LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AIS", "Fixtures", name);
        return (OSDMap)OSDParser.DeserializeLLSDXml(File.ReadAllBytes(path));
    }

    /// <summary>
    /// The live envelopes carry exactly the keys the golden fixtures pin — no extra keys, none missing. The
    /// fixtures carry the field set the viewer reads: <c>type</c>, <c>inv_type</c> and
    /// <c>sale_type</c> as integers (<c>LLInventoryItem::fromLLSD</c> accepts either, llinventory.cpp:1108-1135,
    /// and integers are what this tree already sends, LLSDInventoryItem.cs:33-68), and no <c>last_owner_id</c>
    /// in <c>permissions</c> for the same reason.
    /// </summary>
    [Fact]
    public void live_envelopes_match_the_golden_fixture_key_sets()
    {
        var b = Inventory();

        var (_, item) = Get(b, $"/item/{Shirt}");
        var itemFixture = LoadFixture("item-fetch.xml");
        Assert.Equivalent(itemFixture.Keys, item.Keys, true);
        Assert.Equivalent(((OSDMap)itemFixture["permissions"]).Keys, ((OSDMap)item["permissions"]).Keys, true);
        Assert.Equivalent(((OSDMap)itemFixture["sale_info"]).Keys, ((OSDMap)item["sale_info"]).Keys, true);
        foreach (var key in itemFixture.Keys)
            Assert.Equal(itemFixture[key].Type, item[key].Type);

        var (_, cat) = Get(b, $"/category/{Clothing}/children?depth=1");
        var catFixture = LoadFixture("category-fetch.xml");
        Assert.Equivalent(catFixture.Keys, cat.Keys, true);
        Assert.Equivalent(Embedded(catFixture).Keys, Embedded(cat).Keys, true);

        var (_, link) = Get(b, $"/item/{LinkToShirt}");
        var linkFixture = (OSDMap)((OSDMap)Coll(catFixture, "links")).Values.First();
        Assert.Equivalent(linkFixture.Keys, link.Keys, true);
    }

    /// <summary>
    /// The depth contract of spec 1c-bis, pinned: N licenses exactly N generations below the requested folder,
    /// an expanded category carries all three collections and a version, and an unexpanded one is a stub. The
    /// viewer versions a category only while its decremented depth is still >= 0 and its descendent count is
    /// known (llaisapi.cpp:1380-1407), so our deepest expanded generation lands on exactly 0 - the last value
    /// that still counts. A requested depth above the viewer own ceiling of 50 is clamped.
    /// </summary>
    [Fact]
    public void the_depth_contract_matches_the_viewers_parse()
    {
        var b = Inventory();

        // the two depths the viewer actually sends (llinventorymodelbackgroundfetch.cpp:937, :994 with
        // llaisapi.cpp:463-474): 0 for a plain fetch, 50 for a recursive one
        var (_, d0) = Get(b, $"/category/{Clothing}/children?depth=0");
        Assert.False(((OSDMap)Coll(d0, "categories")[Outfits.ToString()]).ContainsKey("_embedded"), "depth 0 versions the requested folder only; every child must be a stub so the viewer re-queues it");

        var (_, d50) = Get(b, $"/category/{Clothing}/children?depth=50");
        var outfits = (OSDMap)Coll(d50, "categories")[Outfits.ToString()];
        var party = (OSDMap)Coll(outfits, "categories")[Party.ToString()];
        Assert.True(party.ContainsKey("_embedded"), "a recursive fetch expands the whole tree");

        // every expanded category carries all three collections AND a version, or the viewer cannot count its
        // descendents and will never version it (llaisapi.cpp:1466-1482, risk A-R3)
        void AssertExpandedAreCountable(OSDMap c)
        {
            if (!c.ContainsKey("_embedded")) return;
            Assert.True(c.ContainsKey("version"), c["name"].AsString());
            var e = Embedded(c);
            foreach (var name in new[] { "categories", "items", "links" })
                Assert.True(e.ContainsKey(name), $"{c["name"].AsString()}: {name}");
            foreach (var child in ((OSDMap)e["categories"]).Values) AssertExpandedAreCountable((OSDMap)child);
        }
        AssertExpandedAreCountable(d50);

        // a depth beyond the viewer ceiling is clamped, not honoured literally
        var (status, clamped) = Get(b, $"/category/{Clothing}/children?depth=100000");
        Assert.Equal(200, status);
        Assert.True(((OSDMap)Coll((OSDMap)Coll(clamped, "categories")[Outfits.ToString()], "categories")[Party.ToString()])
            .ContainsKey("_embedded"), "clamping to 50 still covers any real inventory");
    }
}
