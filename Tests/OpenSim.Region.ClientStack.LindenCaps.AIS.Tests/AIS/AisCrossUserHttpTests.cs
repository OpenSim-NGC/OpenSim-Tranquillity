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
/// AIS-SEC-1. Alice's cap, handed Bob's UUIDs, over the <b>real</b>
/// <see cref="AISv3Module.InventoryServiceBackend"/> sitting on <see cref="PrincipalIgnoringInventoryService"/> -
/// a double that reproduces <c>XInventoryService</c>'s habit of looking rows up by UUID and disregarding the
/// principal it was handed.
///
/// <para><b>Why not <see cref="FakeAisBackend"/>.</b> Every other HTTP fixture in this project runs on that fake,
/// which enforces owner scoping itself (<c>if (agentId != Owner) return null;</c> in each read). So the whole
/// suite was green while the shipped backend did no scoping at all: it was a pass-through to
/// <c>IInventoryService</c>, and a valid AIS cap plus another resident's item or folder UUID could read, rename,
/// delete, purge, slam and create across the boundary. These tests are built so that they cannot be satisfied by
/// anything except a check inside the backend.</para>
///
/// <para><b>The status chosen for a foreign object is 404</b>, uniformly, for reads and mutations alike - not 403.
/// Three reasons, in order of weight:</para>
/// <list type="number">
///   <item>It is what the scoping <i>means</i>. The backend's contract is that a foreign object is not visible to
///   this cap, so the handler's pre-existing not-found paths are the correct ones and no status mapping had to be
///   invented for the security case.</item>
///   <item>403 would be an oracle: it distinguishes "this UUID belongs to someone else" from "this UUID does not
///   exist", which hands an attacker a membership test over the whole inventory keyspace. 404 says the same thing
///   to both.</item>
///   <item>The viewer treats any non-2xx the same way here - it logs the status and still runs
///   <c>onUpdateReceived</c> without reverting the local edit (<c>llaisapi.cpp:851-951</c>) - so nothing client
///   side turns on the choice. What matters is that the error body carries no
///   <c>_updated_category_versions</c>, which is true of every <c>WriteError</c> path.</item>
/// </list>
/// <para>The one exception is <c>POST /category/{parent}</c> whose <b>body</b> names a different
/// <c>parent_id</c>: that is a malformed request rather than a missing object, and it answers <b>400</b>. The URL
/// is the authority (<c>AISAPI::CreateInventory</c> builds <c>{inv}/category/{parentId}</c>,
/// <c>llaisapi.cpp:115</c>), and the viewer's own body repeats the same id
/// (<c>LLInventoryCategory::asAISCreateCatLLSD</c>, <c>llinventory.cpp:1256-1276</c>), so a mismatch is never
/// something a viewer sends.</para>
///
/// <para>Every cross-user case asserts <b>both</b> halves: the status, and that Bob's rows are byte-for-byte where
/// they were. A 404 that had already written is not a fix.</para>
/// </summary>
[TestFixture]
public class AisCrossUserHttpTests
{
    private const string Cap = "/CAP/5ec0de00-0000-4000-8000-000000000000";

    /// <summary>One resident's inventory shape: root, COF, Clothing, a saved Outfit, a wearable and a COF link.</summary>
    private sealed record Person(
        UUID Agent, UUID Root, UUID Cof, UUID Clothing, UUID Outfit,
        UUID Wearable, UUID OutfitItem, UUID CofLink);

    private static readonly Person Alice = new(
        new("a0000000-0000-4000-8000-000000000001"), new("a0000000-0000-4000-8000-000000000002"),
        new("a0000000-0000-4000-8000-000000000003"), new("a0000000-0000-4000-8000-000000000004"),
        new("a0000000-0000-4000-8000-000000000005"), new("a0000000-0000-4000-8000-000000000006"),
        new("a0000000-0000-4000-8000-000000000007"), new("a0000000-0000-4000-8000-000000000008"));

    private static readonly Person Bob = new(
        new("b0000000-0000-4000-8000-000000000001"), new("b0000000-0000-4000-8000-000000000002"),
        new("b0000000-0000-4000-8000-000000000003"), new("b0000000-0000-4000-8000-000000000004"),
        new("b0000000-0000-4000-8000-000000000005"), new("b0000000-0000-4000-8000-000000000006"),
        new("b0000000-0000-4000-8000-000000000007"), new("b0000000-0000-4000-8000-000000000008"));

    private sealed class CrossUserRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public CrossUserRequest(string verb, string url, OSD body)
        {
            HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url;
            InputStream = body is null ? new MemoryStream() : new MemoryStream(OSDParser.SerializeLLSDXmlBytes(body));
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

    /// <summary>Alice and Bob, same shape, one store, nothing scoped by the service underneath.</summary>
    private static PrincipalIgnoringInventoryService World()
    {
        var svc = new PrincipalIgnoringInventoryService();
        foreach (var p in new[] { Alice, Bob })
        {
            svc.Seed(p.Root, p.Agent, UUID.Zero, "My Inventory", (short)FolderType.Root, 3);
            svc.Seed(p.Cof, p.Agent, p.Root, "Current Outfit", (short)FolderType.CurrentOutfit, 11);
            svc.Seed(p.Clothing, p.Agent, p.Root, "Clothing", (short)FolderType.Clothing, 7);
            // an ordinary saved outfit: FolderType.Outfit is the one system type the delete route does NOT
            // protect, so a cross-user DELETE really would go through if nothing else stopped it
            svc.Seed(p.Outfit, p.Agent, p.Clothing, "Beach Outfit", (short)FolderType.Outfit, 2);
            svc.SeedItem(p.Wearable, p.Agent, p.Clothing, "a shirt");
            svc.SeedItem(p.OutfitItem, p.Agent, p.Outfit, "a note in the outfit");
            svc.SeedLink(p.CofLink, p.Agent, p.Cof, "link to the shirt", p.Wearable);
        }
        return svc;
    }

    /// <summary>
    /// The real region backend, owner-bound. Before AIS-SEC-1 the constructor took no owner at all and this call
    /// is the whole of the defect: nothing downstream of it knew whose cap this was.
    /// </summary>
    private static IAisInventoryBackend Backend(PrincipalIgnoringInventoryService service, UUID owner)
        => new AISv3Module.InventoryServiceBackend(service, owner);

    private static (int Status, OSDMap Body) AsAlice(PrincipalIgnoringInventoryService svc, string verb, string path, OSD body = null)
    {
        var handler = new AisHandler(Cap, Alice.Agent, Backend(svc, Alice.Agent));
        var response = new TestOSHttpResponse();
        handler.Handle(new CrossUserRequest(verb, Cap + path, body), response);
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.That(parsed, Is.InstanceOf<OSDMap>());
        return (response.StatusCode, (OSDMap)parsed);
    }

    private static IReadOnlyList<UUID> ChildFolders(PrincipalIgnoringInventoryService svc, UUID parent)
        => svc.Folders.Values.Where(f => f.ParentID == parent).Select(f => f.ID).OrderBy(i => i).ToList();

    private static IReadOnlyList<UUID> ChildItems(PrincipalIgnoringInventoryService svc, UUID parent)
        => svc.Items.Values.Where(i => i.Folder == parent).Select(i => i.ID).OrderBy(i => i).ToList();

    // ------------------------------------------------------------------ reads

    [Test]
    public void GET_item_of_another_resident_is_404()
    {
        var svc = World();
        var (status, body) = AsAlice(svc, "GET", $"/item/{Bob.Wearable}");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(body.ContainsKey("item_id"), Is.False, "no part of Bob's item may travel");
        Assert.That(svc.Items[Bob.Wearable].Name, Is.EqualTo("a shirt"));
    }

    [Test]
    public void GET_category_children_of_another_resident_is_404()
    {
        var svc = World();
        var (status, body) = AsAlice(svc, "GET", $"/category/{Bob.Outfit}/children");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(body.ContainsKey("_embedded"), Is.False, "Bob's children may not be enumerated");
    }

    [Test]
    public void GET_category_links_of_another_resident_is_404()
    {
        var svc = World();
        var (status, body) = AsAlice(svc, "GET", $"/category/{Bob.Cof}/links");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(body.ContainsKey("_embedded"), Is.False, "Bob's outfit may not be enumerated");
    }

    // ------------------------------------------------------------------ updates

    [Test]
    public void PATCH_item_of_another_resident_is_refused_and_changes_nothing()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "PATCH", $"/item/{Bob.Wearable}", new OSDMap { ["name"] = "stolen" });

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(svc.Items[Bob.Wearable].Name, Is.EqualTo("a shirt"));
    }

    [Test]
    public void PATCH_category_of_another_resident_is_refused_and_changes_nothing()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "PATCH", $"/category/{Bob.Outfit}", new OSDMap { ["name"] = "stolen" });

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(svc.Folders[Bob.Outfit].Name, Is.EqualTo("Beach Outfit"));
    }

    // ------------------------------------------------------------------ deletes

    [Test]
    public void DELETE_item_of_another_resident_is_refused_and_the_row_survives()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "DELETE", $"/item/{Bob.Wearable}");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(svc.Items.ContainsKey(Bob.Wearable), Is.True);
    }

    [Test]
    public void DELETE_category_of_another_resident_is_refused_and_the_row_survives()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "DELETE", $"/category/{Bob.Outfit}");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(svc.Folders.ContainsKey(Bob.Outfit), Is.True);
        Assert.That(svc.Items.ContainsKey(Bob.OutfitItem), Is.True, "nor may its contents be purged as a side effect");
    }

    [Test]
    public void DELETE_category_children_of_another_resident_is_refused_and_the_children_survive()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "DELETE", $"/category/{Bob.Outfit}/children");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(ChildItems(svc, Bob.Outfit), Is.EqualTo(new[] { Bob.OutfitItem }));
    }

    // ------------------------------------------------------------------ slam and create

    [Test]
    public void PUT_links_into_another_residents_COF_is_refused_and_their_outfit_is_unchanged()
    {
        var svc = World();
        var before = ChildItems(svc, Bob.Cof);

        var slam = new OSDArray
        {
            new OSDMap
            {
                ["name"] = "link to Alice's shirt",
                ["desc"] = "",
                ["linked_id"] = Alice.Wearable,
                ["type"] = (int)AssetType.Link,
            },
        };
        var (status, _) = AsAlice(svc, "PUT", $"/category/{Bob.Cof}/links", slam);

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(ChildItems(svc, Bob.Cof), Is.EqualTo(before), "Bob's COF links must be exactly what they were");
        Assert.That(svc.Items[Bob.CofLink].AssetID, Is.EqualTo(Bob.Wearable));
    }

    [Test]
    public void POST_category_under_another_resident_is_refused_and_creates_nothing()
    {
        var svc = World();
        var before = ChildFolders(svc, Bob.Outfit);

        var (status, _) = AsAlice(svc, "POST", $"/category/{Bob.Outfit}",
            new OSDMap { ["categories"] = new OSDArray { new OSDMap { ["name"] = "planted" } } });

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(ChildFolders(svc, Bob.Outfit), Is.EqualTo(before));
    }

    /// <summary>
    /// The same create, but addressed to Alice's own folder with a body <c>parent_id</c> pointing at Bob's. The URL
    /// is the authority; the mismatch is a malformed request and answers 400 before anything is written. The
    /// backend's own parent check would refuse the write in any case - this makes the refusal say why.
    /// </summary>
    [Test]
    public void POST_category_whose_body_parent_points_at_another_resident_is_400_and_creates_nothing()
    {
        var svc = World();
        var beforeBob = ChildFolders(svc, Bob.Outfit);
        var beforeAlice = ChildFolders(svc, Alice.Outfit);

        var (status, body) = AsAlice(svc, "POST", $"/category/{Alice.Outfit}", new OSDMap
        {
            ["categories"] = new OSDArray
            {
                new OSDMap
                {
                    ["category_id"] = UUID.Zero,
                    ["parent_id"] = Bob.Outfit,
                    ["type_default"] = (int)FolderType.Outfit,
                    ["name"] = "planted",
                },
            },
        });

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.BadRequest));
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(400));
        Assert.That(ChildFolders(svc, Bob.Outfit), Is.EqualTo(beforeBob), "nothing may be created under Bob");
        Assert.That(ChildFolders(svc, Alice.Outfit), Is.EqualTo(beforeAlice), "and nothing under Alice either");
    }

    // ------------------------------------------------------------------ the positive control

    /// <summary>
    /// The same eleven routes against Alice's own objects. Without this the fixture could be satisfied by a
    /// backend that refuses everything, which is not a fix but an outage.
    /// </summary>
    [Test]
    public void every_route_still_works_against_the_callers_own_objects()
    {
        Assert.Multiple(() =>
        {
            var svc = World();
            Assert.That(AsAlice(svc, "GET", $"/item/{Alice.Wearable}").Status, Is.EqualTo(200), "GET /item");
            Assert.That(AsAlice(svc, "GET", $"/category/{Alice.Outfit}/children").Status, Is.EqualTo(200), "GET /children");
            Assert.That(AsAlice(svc, "GET", $"/category/{Alice.Cof}/links").Status, Is.EqualTo(200), "GET /links");

            svc = World();
            Assert.That(AsAlice(svc, "PATCH", $"/item/{Alice.Wearable}", new OSDMap { ["name"] = "renamed" }).Status,
                Is.EqualTo(200), "PATCH /item");
            Assert.That(svc.Items[Alice.Wearable].Name, Is.EqualTo("renamed"));

            svc = World();
            Assert.That(AsAlice(svc, "PATCH", $"/category/{Alice.Outfit}", new OSDMap { ["name"] = "renamed" }).Status,
                Is.EqualTo(200), "PATCH /category");
            Assert.That(svc.Folders[Alice.Outfit].Name, Is.EqualTo("renamed"));

            svc = World();
            Assert.That(AsAlice(svc, "DELETE", $"/item/{Alice.Wearable}").Status, Is.EqualTo(200), "DELETE /item");
            Assert.That(svc.Items.ContainsKey(Alice.Wearable), Is.False);

            svc = World();
            Assert.That(AsAlice(svc, "DELETE", $"/category/{Alice.Outfit}").Status, Is.EqualTo(200), "DELETE /category");
            Assert.That(svc.Folders.ContainsKey(Alice.Outfit), Is.False);

            svc = World();
            Assert.That(AsAlice(svc, "DELETE", $"/category/{Alice.Outfit}/children").Status, Is.EqualTo(200), "DELETE /children");
            Assert.That(ChildItems(svc, Alice.Outfit), Is.Empty);

            svc = World();
            var slam = new OSDArray
            {
                new OSDMap
                {
                    ["name"] = "link to the shirt",
                    ["desc"] = "",
                    ["linked_id"] = Alice.Wearable,
                    ["type"] = (int)AssetType.Link,
                },
            };
            Assert.That(AsAlice(svc, "PUT", $"/category/{Alice.Cof}/links", slam).Status, Is.EqualTo(200), "PUT /links");
            Assert.That(svc.Items.ContainsKey(Alice.CofLink), Is.False, "the old link went");
            Assert.That(ChildItems(svc, Alice.Cof), Has.Count.EqualTo(1), "and exactly one new one is there");

            svc = World();
            var (createStatus, createBody) = AsAlice(svc, "POST", $"/category/{Alice.Outfit}",
                new OSDMap { ["categories"] = new OSDArray { new OSDMap { ["name"] = "Sub Outfit" } } });
            Assert.That(createStatus, Is.EqualTo(200), "POST /category");
            var created = ((OSDArray)createBody["_created_categories"]).Single().AsUUID();
            Assert.That(svc.Folders[created].ParentID, Is.EqualTo(Alice.Outfit));

            // and the create whose body repeats the URL parent, which is what the viewer actually sends
            svc = World();
            var (echoStatus, _) = AsAlice(svc, "POST", $"/category/{Alice.Outfit}", new OSDMap
            {
                ["categories"] = new OSDArray
                {
                    new OSDMap
                    {
                        ["category_id"] = UUID.Zero,
                        ["parent_id"] = Alice.Outfit,
                        ["type_default"] = (int)FolderType.Outfit,
                        ["name"] = "Sub Outfit",
                    },
                },
            });
            Assert.That(echoStatus, Is.EqualTo(200), "POST /category with the body repeating the URL parent");
            Assert.That(ChildFolders(svc, Alice.Outfit), Has.Count.EqualTo(1));
        });
    }
}
