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
        Assert.IsAssignableFrom<OSDMap>(parsed);
        return (response.StatusCode, (OSDMap)parsed);
    }

    private static IReadOnlyList<UUID> ChildFolders(PrincipalIgnoringInventoryService svc, UUID parent)
        => svc.Folders.Values.Where(f => f.ParentID == parent).Select(f => f.ID).OrderBy(i => i).ToList();

    private static IReadOnlyList<UUID> ChildItems(PrincipalIgnoringInventoryService svc, UUID parent)
        => svc.Items.Values.Where(i => i.Folder == parent).Select(i => i.ID).OrderBy(i => i).ToList();

    // ------------------------------------------------------------------ reads

    [Fact]
    public void GET_item_of_another_resident_is_404()
    {
        var svc = World();
        var (status, body) = AsAlice(svc, "GET", $"/item/{Bob.Wearable}");

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.False(body.ContainsKey("item_id"), "no part of Bob's item may travel");
        Assert.Equal("a shirt", svc.Items[Bob.Wearable].Name);
    }

    [Fact]
    public void GET_category_children_of_another_resident_is_404()
    {
        var svc = World();
        var (status, body) = AsAlice(svc, "GET", $"/category/{Bob.Outfit}/children");

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.False(body.ContainsKey("_embedded"), "Bob's children may not be enumerated");
    }

    [Fact]
    public void GET_category_links_of_another_resident_is_404()
    {
        var svc = World();
        var (status, body) = AsAlice(svc, "GET", $"/category/{Bob.Cof}/links");

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.False(body.ContainsKey("_embedded"), "Bob's outfit may not be enumerated");
    }

    // ------------------------------------------------------------------ updates

    [Fact]
    public void PATCH_item_of_another_resident_is_refused_and_changes_nothing()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "PATCH", $"/item/{Bob.Wearable}", new OSDMap { ["name"] = "stolen" });

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal("a shirt", svc.Items[Bob.Wearable].Name);
    }

    [Fact]
    public void PATCH_category_of_another_resident_is_refused_and_changes_nothing()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "PATCH", $"/category/{Bob.Outfit}", new OSDMap { ["name"] = "stolen" });

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal("Beach Outfit", svc.Folders[Bob.Outfit].Name);
    }

    // ------------------------------------------------------------------ PATCH naming a parent outside the agent's inventory

    // A PATCH never moves anything (SL's Item-PATCH and Category-PATCH schemas carry no parent_id; moving is the
    // separate MOVE verb), so a parent_id that differs from the object's own is refused with 400 before any folder
    // is looked up. The answer must therefore be the same whether the named folder is another resident's, the
    // library's, or does not exist: anything else would be the membership oracle the 404 rule above closes.

    /// <summary>A folder in the shared library, owned by a principal that is neither Alice nor Bob.</summary>
    private static readonly UUID LibraryOwner = new("11111111-0000-4000-8000-0000000000ff");
    private static readonly UUID LibraryFolder = new("11111111-0000-4000-8000-000000000001");

    private static (int Status, OSDMap Body) PatchAlicesWearableInto(PrincipalIgnoringInventoryService svc, UUID target)
        => AsAlice(svc, "PATCH", $"/item/{Alice.Wearable}", new OSDMap { ["name"] = "a shirt", ["parent_id"] = target });

    [Fact]
    public void PATCH_own_item_into_another_residents_folder_is_400_like_a_missing_folder_and_moves_nothing()
    {
        var svc = World();
        var bobsChildren = ChildItems(svc, Bob.Clothing);

        var (status, body) = PatchAlicesWearableInto(svc, Bob.Clothing);
        var (missingStatus, missingBody) = PatchAlicesWearableInto(svc, UUID.Random());

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Contains("parent_id", body["message"].AsString());
        Assert.Equal(status, missingStatus);
        Assert.Equal(missingBody["message"].AsString(), body["message"].AsString());
        Assert.Equal(Alice.Clothing, svc.Items[Alice.Wearable].Folder);
        Assert.Equal(bobsChildren, ChildItems(svc, Bob.Clothing));
    }

    [Fact]
    public void PATCH_own_item_into_a_library_folder_is_400_and_moves_nothing()
    {
        var svc = World();
        svc.Seed(LibraryFolder, LibraryOwner, UUID.Zero, "Library", (short)FolderType.Root);

        var (status, body) = PatchAlicesWearableInto(svc, LibraryFolder);

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Contains("parent_id", body["message"].AsString());
        Assert.Equal(Alice.Clothing, svc.Items[Alice.Wearable].Folder);
        Assert.Empty(ChildItems(svc, LibraryFolder));
    }

    [Fact]
    public void PATCH_own_category_into_another_residents_folder_is_400_and_moves_nothing()
    {
        var svc = World();

        var (status, body) = AsAlice(svc, "PATCH", $"/category/{Alice.Outfit}", new OSDMap { ["parent_id"] = Bob.Clothing });

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Contains("parent_id", body["message"].AsString());
        Assert.Equal(Alice.Clothing, svc.Folders[Alice.Outfit].ParentID);
        Assert.Equal(new[] { Bob.Outfit }, ChildFolders(svc, Bob.Clothing));
    }

    [Fact]
    public void PATCH_another_residents_item_with_a_parent_id_is_still_404()
    {
        // The owner check comes first: a body that would be refused must not turn "not yours" into a 400 oracle.
        var svc = World();

        var (status, _) = AsAlice(svc, "PATCH", $"/item/{Bob.Wearable}", new OSDMap { ["parent_id"] = Alice.Clothing });

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal(Bob.Clothing, svc.Items[Bob.Wearable].Folder);
    }

    // ------------------------------------------------------------------ deletes

    [Fact]
    public void DELETE_item_of_another_resident_is_refused_and_the_row_survives()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "DELETE", $"/item/{Bob.Wearable}");

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.True(svc.Items.ContainsKey(Bob.Wearable));
    }

    [Fact]
    public void DELETE_category_of_another_resident_is_refused_and_the_row_survives()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "DELETE", $"/category/{Bob.Outfit}");

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.True(svc.Folders.ContainsKey(Bob.Outfit));
        Assert.True(svc.Items.ContainsKey(Bob.OutfitItem), "nor may its contents be purged as a side effect");
    }

    [Fact]
    public void DELETE_category_children_of_another_resident_is_refused_and_the_children_survive()
    {
        var svc = World();
        var (status, _) = AsAlice(svc, "DELETE", $"/category/{Bob.Outfit}/children");

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal(new[] { Bob.OutfitItem }, ChildItems(svc, Bob.Outfit));
    }

    // ------------------------------------------------------------------ slam and create

    [Fact]
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

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal(before, ChildItems(svc, Bob.Cof));
        Assert.Equal(Bob.Wearable, svc.Items[Bob.CofLink].AssetID);
    }

    [Fact]
    public void POST_category_under_another_resident_is_refused_and_creates_nothing()
    {
        var svc = World();
        var before = ChildFolders(svc, Bob.Outfit);

        var (status, _) = AsAlice(svc, "POST", $"/category/{Bob.Outfit}",
            new OSDMap { ["categories"] = new OSDArray { new OSDMap { ["name"] = "planted" } } });

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal(before, ChildFolders(svc, Bob.Outfit));
    }

    /// <summary>
    /// The same create, but addressed to Alice's own folder with a body <c>parent_id</c> pointing at Bob's. The URL
    /// is the authority; the mismatch is a malformed request and answers 400 before anything is written. The
    /// backend's own parent check would refuse the write in any case - this makes the refusal say why.
    /// </summary>
    [Fact]
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

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Equal(400, body["error_code"].AsInteger());
        Assert.Equal(beforeBob, ChildFolders(svc, Bob.Outfit));
        Assert.Equal(beforeAlice, ChildFolders(svc, Alice.Outfit));
    }

    // ------------------------------------------------------------------ the positive control

    /// <summary>
    /// The same eleven routes against Alice's own objects. Without this the fixture could be satisfied by a
    /// backend that refuses everything, which is not a fix but an outage.
    /// </summary>
    [Fact]
    public void every_route_still_works_against_the_callers_own_objects()
    {
        Assert.Multiple(() =>
        {
            var svc = World();
            Assert.Equal(200, AsAlice(svc, "GET", $"/item/{Alice.Wearable}").Status);
            Assert.Equal(200, AsAlice(svc, "GET", $"/category/{Alice.Outfit}/children").Status);
            Assert.Equal(200, AsAlice(svc, "GET", $"/category/{Alice.Cof}/links").Status);

            svc = World();
            Assert.Equal(200, AsAlice(svc, "PATCH", $"/item/{Alice.Wearable}", new OSDMap { ["name"] = "renamed" }).Status);
            Assert.Equal("renamed", svc.Items[Alice.Wearable].Name);

            svc = World();
            Assert.Equal(200, AsAlice(svc, "PATCH", $"/category/{Alice.Outfit}", new OSDMap { ["name"] = "renamed" }).Status);
            Assert.Equal("renamed", svc.Folders[Alice.Outfit].Name);

            svc = World();
            Assert.Equal(200, AsAlice(svc, "DELETE", $"/item/{Alice.Wearable}").Status);
            Assert.False(svc.Items.ContainsKey(Alice.Wearable));

            svc = World();
            Assert.Equal(200, AsAlice(svc, "DELETE", $"/category/{Alice.Outfit}").Status);
            Assert.False(svc.Folders.ContainsKey(Alice.Outfit));

            svc = World();
            Assert.Equal(200, AsAlice(svc, "DELETE", $"/category/{Alice.Outfit}/children").Status);
            Assert.Empty(ChildItems(svc, Alice.Outfit));

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
            Assert.Equal(200, AsAlice(svc, "PUT", $"/category/{Alice.Cof}/links", slam).Status);
            Assert.False(svc.Items.ContainsKey(Alice.CofLink), "the old link went");
            Assert.Single(ChildItems(svc, Alice.Cof));

            svc = World();
            var (createStatus, createBody) = AsAlice(svc, "POST", $"/category/{Alice.Outfit}",
                new OSDMap { ["categories"] = new OSDArray { new OSDMap { ["name"] = "Sub Outfit" } } });
            Assert.Equal(200, createStatus);
            var created = ((OSDArray)createBody["_created_categories"]).Single().AsUUID();
            Assert.Equal(Alice.Outfit, svc.Folders[created].ParentID);

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
            Assert.Equal(200, echoStatus);
            Assert.Single(ChildFolders(svc, Alice.Outfit));
        });
    }
}
