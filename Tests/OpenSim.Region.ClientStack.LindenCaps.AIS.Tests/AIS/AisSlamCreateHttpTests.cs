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
/// SlamFolder and CreateInventory over HTTP. The tests that earn their keep here are the failure ones: a slam is
/// several independent writes with no transaction underneath (Ledger A-R2), so what matters is exactly what the
/// folder looks like when one of them fails.
/// </summary>
[TestFixture]
public class AisSlamCreateHttpTests
{
    private const string Cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Clothing = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Cof = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");

    // five targets to link to, plus the links currently in the COF
    private static readonly UUID[] Targets =
    {
        new("22222222-2222-4222-8222-222222222221"), new("22222222-2222-4222-8222-222222222222"),
        new("22222222-2222-4222-8222-222222222223"), new("22222222-2222-4222-8222-222222222224"),
        new("22222222-2222-4222-8222-222222222225"),
    };
    private static readonly UUID OldLinkA = new("88888888-8888-4888-8888-888888888881");
    private static readonly UUID OldLinkB = new("88888888-8888-4888-8888-888888888882");
    private static readonly UUID PlainItem = new("99999999-9999-4999-8999-999999999991");

    private sealed class SlamTestRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public SlamTestRequest(string verb, string url, OSD body)
        {
            HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url;
            InputStream = body is null ? new MemoryStream() : new MemoryStream(OSDParser.SerializeLLSDXmlBytes(body));
        }

        /// <summary>
        /// AIS-SEC-2: a body the serialiser would never produce — truncated XML, XML that is not LLSD, or simply
        /// too much of it. These are the shapes a real client hits on a dropped connection and an attacker sends
        /// on purpose, and neither can be expressed through the <see cref="OSD"/> constructor above.
        /// </summary>
        public SlamTestRequest(string verb, string url, byte[] raw)
        {
            HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url;
            InputStream = new MemoryStream(raw ?? Array.Empty<byte>());
        }
        public string HttpMethod { get; }
        public Uri Url { get; }
        public string RawUrl { get; }
        public string UriPath => Url.AbsolutePath;
        public Stream InputStream { get; set; }
        public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
        public bool HasEntityBody => InputStream.Length > 0;
        public long ContentLength => InputStream.Length;
        public long ContentLength64 => InputStream.Length;
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

    /// <summary>A COF holding two links and one ordinary (non-link) item, plus five link targets in Clothing.</summary>
    private static FakeAisBackend Inventory()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddFolder(Cof, Root, "Current Outfit", 11, (short)FolderType.CurrentOutfit);
        b.CurrentOutfitId = Cof;
        for (var i = 0; i < Targets.Length; i++) b.AddItem(Targets[i], Clothing, $"Target {i}");
        b.AddLink(OldLinkA, Cof, "old A", Targets[0]);
        b.AddLink(OldLinkB, Cof, "old B", Targets[1]);
        b.AddItem(PlainItem, Cof, "a note that is not a link");
        return b;
    }

    private static OSDArray Body(params UUID[] targets)
    {
        var array = new OSDArray();
        foreach (var t in targets)
            array.Add(new OSDMap
            {
                ["name"] = "link to " + t,
                ["desc"] = "",
                ["linked_id"] = t,
                ["type"] = (int)AssetType.Link,
            });
        return array;
    }

    private static (int Status, OSDMap Body) Send(FakeAisBackend backend, string verb, string path, OSD body, AisMode mode = AisMode.Inventory)
    {
        var handler = new AisHandler(Cap, Agent, backend, mode);
        var response = new TestOSHttpResponse();
        handler.Handle(new SlamTestRequest(verb, Cap + path, body), response);
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.That(parsed, Is.InstanceOf<OSDMap>());
        return (response.StatusCode, (OSDMap)parsed);
    }

    /// <summary>AIS-SEC-2: the same round trip, but with the request body given as raw bytes.</summary>
    private static (int Status, OSDMap Body) SendRaw(FakeAisBackend backend, string verb, string path, byte[] raw)
    {
        var handler = new AisHandler(Cap, Agent, backend);
        var response = new TestOSHttpResponse();
        handler.Handle(new SlamTestRequest(verb, Cap + path, raw), response);
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.That(parsed, Is.InstanceOf<OSDMap>());
        return (response.StatusCode, (OSDMap)parsed);
    }

    /// <summary>The ids of the link rows currently in a folder, which is what a slam is supposed to replace.</summary>
    private static HashSet<UUID> LinkIds(FakeAisBackend b, UUID folder)
        => b.Items.Values.Where(i => i.Folder == folder && (i.AssetType == (int)AssetType.Link || i.AssetType == (int)AssetType.LinkFolder))
                         .Select(i => i.ID).ToHashSet();

    /// <summary>The link targets in a folder — what the outfit actually is, independent of link row ids.</summary>
    private static HashSet<UUID> LinkTargets(FakeAisBackend b, UUID folder)
        => b.Items.Values.Where(i => i.Folder == folder && i.AssetType == (int)AssetType.Link)
                         .Select(i => i.AssetID).ToHashSet();

    // ------------------------------------------------------------------ the happy paths

    [Test]
    public void a_slam_replaces_the_links_and_reports_created_removed_and_the_new_version()
    {
        var b = Inventory();
        var before = b.Folders[Cof].Version;
        var oldIds = LinkIds(b, Cof);

        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", Body(Targets[2], Targets[3], Targets[4]));

        Assert.That(status, Is.EqualTo(200));
        Assert.That(LinkTargets(b, Cof), Is.EquivalentTo(new[] { Targets[2], Targets[3], Targets[4] }));
        Assert.That(LinkIds(b, Cof).Intersect(oldIds), Is.Empty, "every old link row is gone");

        var created = ((OSDArray)body["_created_items"]).Select(o => o.AsUUID()).ToList();
        Assert.That(created, Is.EquivalentTo(LinkIds(b, Cof)));
        var embeddedLinks = (OSDMap)((OSDMap)body["_embedded"])["links"];
        Assert.That(embeddedLinks.Keys, Is.EquivalentTo(created.Select(c => c.ToString())),
            "the created links must ride in _embedded.links, and the viewer only accepts embedded objects listed in _created_items");
        Assert.That(((OSDArray)body["_removed_items"]).Select(o => o.AsUUID()), Is.EquivalentTo(oldIds));
        Assert.That(((OSDMap)body["_updated_category_versions"])[Cof.ToString()].AsInteger(),
            Is.EqualTo(b.Folders[Cof].Version), "the slammed folder's version, read fresh");
        Assert.That(b.Folders[Cof].Version, Is.GreaterThan(before));
    }

    [Test]
    public void a_slam_of_an_empty_array_empties_the_folders_links()
    {
        var b = Inventory();
        var oldIds = LinkIds(b, Cof);

        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", new OSDArray());

        Assert.That(status, Is.EqualTo(200));
        Assert.That(LinkIds(b, Cof), Is.Empty);
        Assert.That(body.ContainsKey("_created_items"), Is.False, "nothing was created, so the key is absent (an absent key and an empty one are identical to the viewer)");
        Assert.That(((OSDArray)body["_removed_items"]).Select(o => o.AsUUID()), Is.EquivalentTo(oldIds));
        Assert.That(body.ContainsKey("_updated_category_versions"), Is.True);
    }

    [Test]
    public void the_cof_alias_slams_the_current_outfit_folder()
    {
        var b = Inventory();
        var (status, _) = Send(b, "PUT", "/category/current/links", Body(Targets[4]));
        Assert.That(status, Is.EqualTo(200));
        Assert.That(LinkTargets(b, Cof), Is.EquivalentTo(new[] { Targets[4] }));
        Assert.That(b.Calls, Does.Contain("GetInventorySkeleton"), "resolved deterministically over the skeleton (A7)");
    }

    [Test]
    public void a_slam_leaves_non_link_items_in_the_folder_alone()
    {
        // the viewer builds a slam body from AT_LINK / AT_LINK_FOLDER rows only (llappearancemgr.cpp:1795-1833),
        // so a slam has nothing to say about ordinary items that happen to live in the folder
        var b = Inventory();
        var (status, _) = Send(b, "PUT", $"/category/{Cof}/links", Body(Targets[2]));
        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items.ContainsKey(PlainItem), Is.True, "the non-link item must survive a slam");
        Assert.That(b.Items[PlainItem].Folder, Is.EqualTo(Cof));
    }

    [Test]
    public void the_cof_is_slammable_even_though_it_is_a_protected_folder()
    {
        // lookupIsProtectedType governs move/delete/retype (llfoldertype.cpp:151-153); FT_CURRENT_OUTFIT is
        // protected AND is the folder the viewer slams constantly (llappearancemgr.cpp:2251)
        var b = Inventory();
        Assert.That(AisHandler.IsProtected(b.Folders[Cof]), Is.True, "the COF is protected against deletion");
        var (status, _) = Send(b, "PUT", $"/category/{Cof}/links", Body(Targets[3]));
        Assert.That(status, Is.EqualTo(200), "but a slam is not a delete");
    }

    // ------------------------------------------------------------------ fault injection

    [Test]
    public void a_failure_on_the_third_of_five_creations_leaves_the_folder_exactly_as_it_was()
    {
        var b = Inventory();
        var beforeIds = LinkIds(b, Cof);
        var beforeTargets = LinkTargets(b, Cof);
        var adds = 0;
        b.AddItemGate = _ => ++adds != 3;   // the third creation fails

        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links",
            Body(Targets[0], Targets[1], Targets[2], Targets[3], Targets[4]));

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.InternalServerError));
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(500));

        // the whole point: the folder's link contents are byte-for-byte what they were
        Assert.That(LinkIds(b, Cof), Is.EquivalentTo(beforeIds), "no link row was added or lost");
        Assert.That(LinkTargets(b, Cof), Is.EquivalentTo(beforeTargets), "and the outfit is unchanged");
        Assert.That(b.Items.ContainsKey(PlainItem), Is.True);
        Assert.That(body.ContainsKey("_removed_items"), Is.False, "nothing was removed, so nothing is reported removed");
        Assert.That(body.ContainsKey("_created_items"), Is.False);
    }

    [Test]
    public void a_failure_during_the_rollback_itself_leaves_extra_links_never_fewer_and_says_so()
    {
        // This documents the worst case rather than pretending it cannot happen: the creation fails AND the
        // compensating delete fails too. The folder then holds its original links PLUS the ones that could not be
        // rolled back - more than it started with, never fewer, so the avatar is never stripped.
        var b = Inventory();
        var beforeIds = LinkIds(b, Cof);
        var adds = 0;
        b.AddItemGate = _ => ++adds != 3;
        b.DeleteItemsGate = _ => false;     // the rollback cannot complete either

        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links",
            Body(Targets[0], Targets[1], Targets[2], Targets[3], Targets[4]));

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.InternalServerError));
        Assert.That(body["message"].AsString(), Does.Contain("rollback also failed"),
            "the response must name the state it left behind");

        var after = LinkIds(b, Cof);
        Assert.That(after.IsSupersetOf(beforeIds), Is.True, "every original link is still there");
        Assert.That(after.Count, Is.EqualTo(beforeIds.Count + 2), "plus the two that were created before the failure");
        Assert.That(LinkTargets(b, Cof).IsSupersetOf(new[] { Targets[0], Targets[1] }), Is.True,
            "the leftovers are duplicates of the first two links, recoverable by the next slam");
    }

    [Test]
    public void a_removal_failure_after_every_creation_keeps_both_sets_and_reports_the_failure()
    {
        var b = Inventory();
        var beforeIds = LinkIds(b, Cof);
        b.DeleteItemsGate = _ => false;     // creations all succeed; the removal of the old links fails

        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", Body(Targets[2], Targets[3]));

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.InternalServerError));
        Assert.That(body["message"].AsString(), Does.Contain("could not be removed"));
        var after = LinkIds(b, Cof);
        Assert.That(after.IsSupersetOf(beforeIds), Is.True, "nothing was lost");
        Assert.That(after.Count, Is.EqualTo(beforeIds.Count + 2), "and the new links are there too");
    }

    // ------------------------------------------------------------------ create

    [Test]
    public void create_makes_categories_and_links_with_the_right_deltas()
    {
        var b = Inventory();
        var before = b.Folders[Clothing].Version;

        // the shapes the viewer really sends: a categories array (llinventorymodel.cpp:1036-1041) and a links
        // array whose entries are linked_id / type / inv_type / name / desc (llviewerinventory.cpp:1352-1370)
        var (status, body) = Send(b, "POST", $"/category/{Clothing}?tid={UUID.Random()}", new OSDMap
        {
            ["categories"] = new OSDArray { new OSDMap { ["name"] = "New Folder", ["type_default"] = -1 } },
            ["links"] = new OSDArray
            {
                new OSDMap
                {
                    ["linked_id"] = Targets[0],
                    ["type"] = (int)AssetType.Link,
                    ["inv_type"] = (int)InventoryType.Wearable,
                    ["name"] = "a link",
                    ["desc"] = "",
                },
            },
        });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(((OSDArray)body["_created_categories"]).Count, Is.EqualTo(1));
        Assert.That(((OSDArray)body["_created_items"]).Count, Is.EqualTo(1), "a link is a created item");

        var embedded = (OSDMap)body["_embedded"];
        Assert.That(embedded.ContainsKey("categories"), Is.True);
        Assert.That(embedded.ContainsKey("links"), Is.True, "links are their own collection, never items");
        Assert.That(embedded.ContainsKey("items"), Is.False);
        var link = (OSDMap)((OSDMap)embedded["links"]).Values.First();
        Assert.That(link["linked_id"].AsUUID(), Is.EqualTo(Targets[0]));
        Assert.That(link["inv_type"].AsInteger(), Is.EqualTo((int)InventoryType.Wearable),
            "the body's inv_type is the target's and is kept");

        Assert.That(((OSDMap)body["_updated_category_versions"])[Clothing.ToString()].AsInteger(),
            Is.EqualTo(b.Folders[Clothing].Version));
        Assert.That(b.Folders[Clothing].Version, Is.GreaterThan(before));
        Assert.That(body["tid"].Type, Is.EqualTo(OSDType.UUID), "tid is echoed");
        Assert.That(b.Folders.Values.Count(f => f.ParentID == Clothing && f.Name == "New Folder"), Is.EqualTo(1));
    }

    /// <summary>
    /// A4: an items array is refused with 501 rather than creating an item with no asset behind it. The viewer's
    /// own builder wraps the item's asLLSD with a null asset_id for the server to fill
    /// (llviewerinventory.cpp:1124-1157) and is compiled out behind USE_AIS_FOR_NC, above the comment "not yet
    /// implemented within AIS3" (:1120-1121). A3 guessed and created assetless items; this replaces that.
    /// </summary>
    [Test]
    public void an_items_create_array_is_refused_before_anything_is_written()
    {
        var b = Inventory();
        var foldersBefore = b.Folders.Count;
        var itemsBefore = b.Items.Count;

        var (status, body) = Send(b, "POST", $"/category/{Clothing}", new OSDMap
        {
            // a mixed body: the categories must NOT be created either
            ["categories"] = new OSDArray { new OSDMap { ["name"] = "should not appear" } },
            ["items"] = new OSDArray { new OSDMap { ["name"] = "New Note", ["type"] = (int)AssetType.Notecard } },
        });

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotImplemented));
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(501));
        Assert.That(b.Folders.Count, Is.EqualTo(foldersBefore), "the refusal happens before any write");
        Assert.That(b.Items.Count, Is.EqualTo(itemsBefore));
    }

    /// <summary>An empty items array is not a request to create items, so it does not trip the refusal.</summary>
    [Test]
    public void an_empty_items_array_is_not_refused()
    {
        var b = Inventory();
        var (status, _) = Send(b, "POST", $"/category/{Clothing}", new OSDMap
        {
            ["items"] = new OSDArray(),
            ["categories"] = new OSDArray { new OSDMap { ["name"] = "fine" } },
        });
        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Folders.Values.Any(f => f.Name == "fine"), Is.True);
    }
    [Test]
    public void creating_a_second_object_with_the_same_name_is_allowed()
    {
        // inventory names are not unique in SL or in this tree; a duplicate is a normal outcome, not an error
        var b = Inventory();
        var body = new OSDMap { ["categories"] = new OSDArray { new OSDMap { ["name"] = "Twin", ["type_default"] = -1 } } };

        var (first, _) = Send(b, "POST", $"/category/{Clothing}", body);
        var (second, _) = Send(b, "POST", $"/category/{Clothing}", body);

        Assert.That(first, Is.EqualTo(200));
        Assert.That(second, Is.EqualTo(200));
        Assert.That(b.Folders.Values.Count(f => f.ParentID == Clothing && f.Name == "Twin"), Is.EqualTo(2),
            "two folders of the same name, with different ids");
    }

    [Test]
    public void creating_a_link_to_a_target_that_does_not_exist_succeeds_as_a_broken_link()
    {
        // broken links are a normal inventory state - the viewer has a whole delta key for removing them
        // (_broken_links_removed) - so creating one is not an error
        var b = Inventory();
        var missing = UUID.Random();

        var (status, body) = Send(b, "POST", $"/category/{Clothing}", new OSDMap
        {
            ["links"] = new OSDArray { new OSDMap { ["name"] = "dangling", ["linked_id"] = missing, ["type"] = (int)AssetType.Link } },
        });

        Assert.That(status, Is.EqualTo(200));
        var created = ((OSDArray)body["_created_items"]).Single().AsUUID();
        Assert.That(b.Items[created].AssetID, Is.EqualTo(missing));
        Assert.That(b.Items[created].AssetType, Is.EqualTo((int)AssetType.Link));
    }

    [Test]
    public void creating_in_an_unknown_category_is_404()
    {
        var (status, body) = Send(Inventory(), "POST", $"/category/{UUID.Random()}", new OSDMap());
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(404));
    }

    [Test]
    public void slam_and_create_through_the_library_cap_are_405()
    {
        foreach (var (verb, path, body) in new (string, string, OSD)[]
        {
            ("PUT", $"/category/{Cof}/links", Body(Targets[2])),
            ("POST", $"/category/{Clothing}", new OSDMap()),
        })
        {
            var b = Inventory();
            var (status, _) = Send(b, verb, path, body, AisMode.Library);
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.MethodNotAllowed), $"{verb} {path}");
            Assert.That(LinkTargets(b, Cof), Is.EquivalentTo(new[] { Targets[0], Targets[1] }), "nothing changed");
        }
    }

    [Test]
    public void a_slam_body_that_is_not_an_array_is_400()
    {
        var (status, body) = Send(Inventory(), "PUT", $"/category/{Cof}/links", new OSDMap { ["nonsense"] = 1 });
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.BadRequest));
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(400));
    }

    /// <summary>
    /// A5: the exact map asAISCreateCatLLSD sends (llinventory.cpp:1256-1276) - category_id null, parent_id,
    /// type_default as an integer, name, and optionally thumbnail and favorite. Everything is accepted; the two
    /// optional ones have no column in this tree and are dropped.
    /// </summary>
    [Test]
    public void the_categories_create_map_is_accepted_exactly_as_the_viewer_sends_it()
    {
        var b = Inventory();

        var (status, body) = Send(b, "POST", $"/category/{Clothing}", new OSDMap
        {
            ["categories"] = new OSDArray
            {
                new OSDMap
                {
                    ["category_id"] = UUID.Zero,          // null on a create: the server assigns it
                    ["parent_id"] = Clothing,
                    ["type_default"] = (int)FolderType.Outfit,
                    ["name"] = "Beach Outfit",
                    ["thumbnail"] = new OSDMap { ["asset_id"] = UUID.Random() },
                    ["favorite"] = new OSDMap { ["toggled"] = true },
                },
            },
        });

        Assert.That(status, Is.EqualTo(200), "thumbnail and favorite must not make the create fail");
        var created = ((OSDArray)body["_created_categories"]).Single().AsUUID();
        Assert.That(created, Is.Not.EqualTo(UUID.Zero), "the server assigned an id rather than echoing the null one");

        var folder = b.Folders[created];
        Assert.That(folder.Name, Is.EqualTo("Beach Outfit"));
        Assert.That(folder.Type, Is.EqualTo((short)FolderType.Outfit), "type_default is the integer folder type");
        Assert.That(folder.ParentID, Is.EqualTo(Clothing));
        Assert.That(folder.Owner, Is.EqualTo(Agent));
    }

    // ================================================================== AIS-SEC-2
    //
    // A slam has REPLACEMENT semantics, so "I could not understand this body" and "this body asks for no links"
    // are one keystroke apart in effect and a universe apart in meaning. Before this, ReadBodyOsd ended in
    // `catch { return new OSDMap(); }` and ParseBody treated an empty map as an empty slam, so a truncated
    // PUT /category/{COF}/links — a dropped connection is enough — was read as "remove every link" and emptied
    // the wearer's Current Outfit.
    //
    // Every case below asserts the same two things: the status, and that NOT ONE backend write was attempted.
    // The second is the real assertion. A 400 that had already called DeleteItems would be no fix at all.

    /// <summary>
    /// The body ceiling the handler is required to enforce. Step 2 adds a cross-check against
    /// <c>AisHandler.MaxBodyBytes</c> inside the 413 test, so the two cannot drift apart.
    /// </summary>
    private const int BodyCeiling = 1024 * 1024;

    /// <summary>The COF's link rows, id -> (name, target), so a test can prove they are untouched field by field.</summary>
    private static Dictionary<UUID, (string Name, UUID Target)> LinkRows(FakeAisBackend b, UUID folder)
        => b.Items.Values
            .Where(i => i.Folder == folder && (i.AssetType == (int)AssetType.Link || i.AssetType == (int)AssetType.LinkFolder))
            .ToDictionary(i => i.ID, i => (i.Name, i.AssetID));

    /// <summary>A well-formed slam body, so the invalid cases differ from a good one in exactly one respect.</summary>
    private static OSDMap GoodLink(UUID target) => new()
    {
        ["name"] = "link to " + target,
        ["desc"] = "",
        ["linked_id"] = target,
        ["type"] = (int)AssetType.Link,
    };

    private static void AssertRefused(FakeAisBackend b, Dictionary<UUID, (string Name, UUID Target)> before,
        int status, OSDMap body, int expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(expected));
            Assert.That(body["error_code"].AsInteger(), Is.EqualTo(expected));
            Assert.That(b.Writes, Is.Empty, "a refused body must not reach a single backend write");
            Assert.That(LinkRows(b, Cof), Is.EqualTo(before), "the COF's link rows must be byte-for-byte what they were");
            Assert.That(body.ContainsKey("_removed_items"), Is.False, "and nothing may be reported as removed");
            Assert.That(body.ContainsKey("_updated_category_versions"), Is.False,
                "no version delta either, or the viewer advances past a state the server never reached");
        });
    }

    [Test]
    public void a_slam_body_of_truncated_xml_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var raw = System.Text.Encoding.UTF8.GetBytes("<llsd><array><map>");
        var (status, body) = SendRaw(b, "PUT", $"/category/{Cof}/links", raw);
        AssertRefused(b, before, status, body, 400);
    }

    [Test]
    public void a_slam_body_of_valid_xml_that_is_not_llsd_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var raw = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><outfit><wear id=\"x\"/></outfit>");
        var (status, body) = SendRaw(b, "PUT", $"/category/{Cof}/links", raw);
        AssertRefused(b, before, status, body, 400);
    }

    /// <summary>
    /// No body at all. This is the one that made the defect reachable without malice: an interrupted PUT arrives
    /// with nothing in it, and "nothing" used to mean "empty slam".
    /// </summary>
    [Test]
    public void a_slam_with_no_body_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var (status, body) = SendRaw(b, "PUT", $"/category/{Cof}/links", Array.Empty<byte>());
        AssertRefused(b, before, status, body, 400);
    }

    /// <summary>
    /// An empty MAP is not an empty slam. The viewer sends a bare LLSD <b>array</b> and never <c>{}</c>
    /// (spec A-Q3, <c>llappearancemgr.cpp:2209-2245</c>), so <c>{}</c> can only be a client we do not know or a
    /// body that arrived damaged — and under replacement semantics the safe reading of both is "refuse".
    /// </summary>
    [Test]
    public void a_slam_body_of_an_empty_map_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", new OSDMap());
        AssertRefused(b, before, status, body, 400);
    }

    /// <summary>
    /// All-or-nothing. Accepting the good entries and dropping the bad one would delete the old links the viewer
    /// meant to keep, which is the same outfit loss by a quieter route.
    /// </summary>
    [Test]
    public void a_slam_array_holding_a_non_map_entry_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var slam = new OSDArray { GoodLink(Targets[0]), OSD.FromString("not a link map"), GoodLink(Targets[1]) };
        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", slam);
        AssertRefused(b, before, status, body, 400);
    }

    [Test]
    public void a_slam_link_with_a_zero_linked_id_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var bad = GoodLink(Targets[0]);
        bad["linked_id"] = UUID.Zero;
        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", new OSDArray { GoodLink(Targets[1]), bad });
        AssertRefused(b, before, status, body, 400);
    }

    /// <summary>
    /// A slam body describes links and nothing else: both builders switch on <c>AT_LINK</c> / <c>AT_LINK_FOLDER</c>
    /// (<c>llappearancemgr.cpp:1795-1833</c>). A type-0 (Texture) entry would have been stored as a link row with
    /// an asset type no fetch route knows how to present.
    /// </summary>
    [Test]
    public void a_slam_link_of_a_type_that_is_not_a_link_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        var bad = GoodLink(Targets[0]);
        bad["type"] = (int)AssetType.Texture;   // 0
        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", new OSDArray { GoodLink(Targets[1]), bad });
        AssertRefused(b, before, status, body, 400);
    }

    /// <summary>
    /// The ceiling is enforced while the stream is copied, so an oversized body is refused without ever being
    /// held in memory — the point being that a body too big to trust is also a body too big to buffer.
    /// </summary>
    [Test]
    public void a_slam_body_over_the_size_ceiling_is_413_and_writes_nothing()
    {
        var b = Inventory();
        var before = LinkRows(b, Cof);
        Assert.That(AisHandler.MaxBodyBytes, Is.EqualTo(BodyCeiling),
            "the handler's ceiling and the one this test exercises must be the same number");
        var raw = new byte[BodyCeiling + 1];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)'a';
        var (status, body) = SendRaw(b, "PUT", $"/category/{Cof}/links", raw);
        AssertRefused(b, before, status, body, 413);
    }

    /// <summary>
    /// Positive control. An empty ARRAY stays an intentional empty slam — that is how the viewer takes off the
    /// last garment — and is the case the negative ones must not sweep up with them.
    /// (<c>a_slam_of_an_empty_array_empties_the_folders_links</c> is the same assertion from A3's side.)
    /// </summary>
    [Test]
    public void the_empty_array_is_still_an_intentional_empty_slam()
    {
        var b = Inventory();
        var oldIds = LinkIds(b, Cof);
        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links", new OSDArray());

        Assert.That(status, Is.EqualTo(200));
        Assert.That(LinkIds(b, Cof), Is.Empty);
        Assert.That(((OSDArray)body["_removed_items"]).Select(o => o.AsUUID()), Is.EquivalentTo(oldIds));
        Assert.That(b.Writes, Is.Not.Empty, "an intentional empty slam DOES write - it removes the old links");
    }

    /// <summary>Positive control: an ordinary two-link slam still replaces the folder's links.</summary>
    [Test]
    public void a_valid_two_link_slam_still_replaces_the_links()
    {
        var b = Inventory();
        var oldIds = LinkIds(b, Cof);
        var (status, body) = Send(b, "PUT", $"/category/{Cof}/links",
            new OSDArray { GoodLink(Targets[2]), GoodLink(Targets[3]) });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(200));
            Assert.That(LinkTargets(b, Cof), Is.EquivalentTo(new[] { Targets[2], Targets[3] }));
            Assert.That(LinkIds(b, Cof).Intersect(oldIds), Is.Empty, "the previous link rows are gone");
            Assert.That(((OSDArray)body["_created_items"]), Has.Count.EqualTo(2));
            Assert.That(((OSDArray)body["_removed_items"]).Select(o => o.AsUUID()), Is.EquivalentTo(oldIds));
        });
    }

    // ------------------------------------------------------------------ the other body-carrying mutations

    [Test]
    public void a_PATCH_item_with_a_malformed_body_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var nameBefore = b.Items[PlainItem].Name;
        var raw = System.Text.Encoding.UTF8.GetBytes("<llsd><map><key>name</key>");
        var (status, body) = SendRaw(b, "PATCH", $"/item/{PlainItem}", raw);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(400));
            Assert.That(body["error_code"].AsInteger(), Is.EqualTo(400));
            Assert.That(b.Writes, Is.Empty);
            Assert.That(b.Items[PlainItem].Name, Is.EqualTo(nameBefore));
        });
    }

    [Test]
    public void a_PATCH_category_with_a_malformed_body_is_400_and_writes_nothing()
    {
        var b = Inventory();
        var nameBefore = b.Folders[Clothing].Name;
        var versionBefore = b.Folders[Clothing].Version;
        var raw = System.Text.Encoding.UTF8.GetBytes("<llsd><map><key>name</key><string>x");
        var (status, body) = SendRaw(b, "PATCH", $"/category/{Clothing}", raw);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(400));
            Assert.That(body["error_code"].AsInteger(), Is.EqualTo(400));
            Assert.That(b.Writes, Is.Empty);
            Assert.That(b.Folders[Clothing].Name, Is.EqualTo(nameBefore));
            Assert.That(b.Folders[Clothing].Version, Is.EqualTo(versionBefore));
        });
    }
}
