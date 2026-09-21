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
/// The A2 mutations driven over HTTP: PATCH and DELETE on a single item or category, asserting the delta envelope
/// the viewer actually applies (AIS-V3-SPEC.md §1d-bis) rather than just a 200. The cases that matter are the
/// ones where a plausible-looking response would be silently useless: an update that omits
/// <c>_updated_category_versions</c> is discarded by the viewer, a version read before the write is stale, and a
/// removal under the wrong key does nothing.
/// </summary>
[TestFixture]
public class AisMutationHttpTests
{
    private const string Cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Clothing = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Outfits = new("66666666-6666-4666-8666-666666666666");
    private static readonly UUID Party = new("77777777-7777-4777-8777-777777777771");
    private static readonly UUID Shirt = new("22222222-2222-4222-8222-222222222222");
    private static readonly UUID PartyHat = new("33333333-3333-4333-8333-333333333331");

    private sealed class MutTestRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        /// <summary>
        /// The body is <see cref="OSD"/>, not <see cref="OSDMap"/>. It used to be <c>OSDMap</c>, and that
        /// narrowing was not harmless: the one caller with a non-map body wrote <c>body as OSDMap</c> to satisfy
        /// it, which silently became <c>null</c> and sent <b>no body at all</b>. A slam body is a bare LLSD
        /// <b>array</b> (<c>llappearancemgr.cpp:2209-2245</c>) — so the one shape the viewer really sends was
        /// exactly the shape this harness could not express.
        /// </summary>
        public MutTestRequest(string verb, string url, OSD body = null)
        {
            HttpMethod = verb;
            Url = new Uri("http://sim.test" + url);
            RawUrl = url;
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

    /// <summary>Root ├ Clothing (shirt) ├ Outfits ├ Party (party hat).</summary>
    private static FakeAisBackend Inventory()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddFolder(Outfits, Clothing, "Outfits", 2);
        b.AddFolder(Party, Outfits, "Party", 5);
        b.AddItem(Shirt, Clothing, "Blue Shirt");
        b.AddItem(PartyHat, Party, "Party Hat");
        return b;
    }

    private static (int Status, OSDMap Body) Send(FakeAisBackend backend, string verb, string path, OSD body = null, AisMode mode = AisMode.Inventory)
    {
        var handler = new AisHandler(Cap, Agent, backend, mode);
        var response = new TestOSHttpResponse();
        handler.Handle(new MutTestRequest(verb, Cap + path, body), response);
        Assert.That(response.ContentType, Is.EqualTo("application/llsd+xml"), $"{verb} {path}");
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.That(parsed, Is.InstanceOf<OSDMap>(), "the viewer forces 500 on a non-map body (llaisapi.cpp:882-885)");
        return (response.StatusCode, (OSDMap)parsed);
    }

    private static OSDMap Versions(OSDMap body)
    {
        Assert.That(body.ContainsKey("_updated_category_versions"), Is.True,
            "without this key the viewer skips the folder entirely: 'Skipping version increment for non-updated category' (llaisapi.cpp:1625-1629)");
        return (OSDMap)body["_updated_category_versions"];
    }

    // ------------------------------------------------------------------ PATCH /item

    [Test]
    public void patching_an_item_name_returns_the_item_as_content_with_its_parents_new_version()
    {
        var b = Inventory();
        var before = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["name"] = "Red Shirt" });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items[Shirt].Name, Is.EqualTo("Red Shirt"), "the write actually happened");

        // there is no "updated" delta key: an updated item is top-level content (§1d-bis)
        Assert.That(body["item_id"].AsUUID(), Is.EqualTo(Shirt));
        Assert.That(body["parent_id"].AsUUID(), Is.EqualTo(Clothing));
        Assert.That(body["name"].AsString(), Is.EqualTo("Red Shirt"));
        Assert.That(body.ContainsKey("_updated_items"), Is.False, "no such key exists in the viewer");

        var versions = Versions(body);
        Assert.That(versions.ContainsKey(Clothing.ToString()), Is.True, "the parent must be listed or the update is discarded");
        Assert.That(versions[Clothing.ToString()].AsInteger(), Is.EqualTo(before + 1), "and it must be the post-write version");
    }

    [Test]
    public void the_reported_version_is_read_after_the_write_not_before()
    {
        // a backend that bumps again underneath the handler proves the version is not cached from before the write
        var b = Inventory();
        b.OnWrite = () => b.Folders[Clothing].Version += 10;

        var (_, body) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["name"] = "Green Shirt" });

        Assert.That(Versions(body)[Clothing.ToString()].AsInteger(), Is.EqualTo(b.Folders[Clothing].Version),
            "the version reported is whatever GetFolder returns after the write, read fresh (tree state T4)");
    }

    /// <summary>
    /// The viewer sends the item's whole <c>asLLSD</c> map (A-Q3, <c>llviewerinventory.cpp:435-454</c>), so a
    /// body is mostly keys carrying values that have not changed. The ones this tree has no column for —
    /// <c>thumbnail</c>, <c>favorite</c>, <c>created_at</c> — and the invariants <c>type</c> / <c>inv_type</c> /
    /// <c>parent_id</c> are ignored rather than refused, because refusing would fail every ordinary rename.
    /// A16 moved <c>asset_id</c>, <c>hash_id</c> and <c>permissions</c> out of this set; the two here are still
    /// no-ops for their own reasons — an unknown transaction has no asset yet, and <c>owner_mask</c> is not a
    /// client-settable field.
    /// </summary>
    [Test]
    public void a_patch_carrying_fields_this_tree_cannot_store_is_ignored_not_refused()
    {
        var b = Inventory();
        var body = new OSDMap
        {
            ["name"] = "Renamed",
            ["item_id"] = Shirt,
            ["parent_id"] = Clothing,
            ["hash_id"] = UUID.Random(),
            ["thumbnail"] = new OSDMap { ["asset_id"] = UUID.Random() },
            ["favorite"] = new OSDMap { ["toggled"] = true },
            ["type"] = 5,
            ["inv_type"] = 18,
            ["created_at"] = 1756900000,
            ["permissions"] = new OSDMap { ["owner_mask"] = 0 },
        };

        var (status, response) = Send(b, "PATCH", $"/item/{Shirt}", body);

        Assert.That(status, Is.EqualTo(200), "an unstorable field must not fail an ordinary rename");
        Assert.That(b.Items[Shirt].Name, Is.EqualTo("Renamed"));
        Assert.That(b.Items[Shirt].CurrentPermissions, Is.EqualTo(0x7fffffffu), "owner_mask is never taken from a body");
        Assert.That(response["item_id"].AsUUID(), Is.EqualTo(Shirt));
    }

    [Test]
    public void patching_sale_info_and_flags_is_applied()
    {
        var b = Inventory();
        var (status, _) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap
        {
            ["sale_info"] = new OSDMap { ["sale_price"] = 250, ["sale_type"] = 2 },
            ["flags"] = 4,
        });
        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items[Shirt].SalePrice, Is.EqualTo(250));
        Assert.That(b.Items[Shirt].SaleType, Is.EqualTo(2));
        Assert.That(b.Items[Shirt].Flags, Is.EqualTo(4u));
    }

    // ------------------------------------------------------------------ A16: the asset id

    /// <summary>
    /// The A16 defect, reproduced. A wearable save uploads a new asset and then PATCHes the item; the PATCH
    /// answered 200 and the item kept its old asset, so the edit survived only in the viewer's cache. Live on
    /// 2026-09-05: asset 44f77403 uploaded at 16:27:24,899, PATCH of item e2c03d62 answered 200 at :24,958, and
    /// the row still read 637022fb. The legacy UDP path did persist it, which is what made the bug look
    /// intermittent.
    /// </summary>
    [Test]
    public void patching_asset_id_persists_it()
    {
        var b = Inventory();
        var uploaded = UUID.Random();
        var before = b.Items[Shirt].AssetID;
        var versionBefore = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["asset_id"] = uploaded });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items[Shirt].AssetID, Is.Not.EqualTo(before), "the item still carries the asset it had before the save");
        Assert.That(b.Items[Shirt].AssetID, Is.EqualTo(uploaded));
        Assert.That(body["asset_id"].AsUUID(), Is.EqualTo(uploaded), "and the response says so");
        Assert.That(Versions(body)[Clothing.ToString()].AsInteger(), Is.EqualTo(versionBefore + 1),
            "an asset change is a change: the parent's version must move or the viewer never re-reads the item");
    }

    /// <summary>
    /// The path a wearable save actually takes. <c>LLViewerInventoryItem::updateServer</c> erases
    /// <c>asset_id</c> from the body and sends <c>hash_id</c> — the xfer transaction id — in its place
    /// (<c>llviewerinventory.cpp:445-452</c>), so the server never sees an asset id at all and must ask the
    /// asset-transaction module which asset that transaction produced.
    /// </summary>
    [Test]
    public void patching_hash_id_resolves_the_transaction_to_the_uploaded_asset()
    {
        var b = Inventory();
        var transaction = UUID.Random();
        var uploaded = UUID.Random();
        b.Transactions[transaction] = uploaded;
        var versionBefore = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["hash_id"] = transaction });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items[Shirt].AssetID, Is.EqualTo(uploaded));
        Assert.That(body["asset_id"].AsUUID(), Is.EqualTo(uploaded), "the response carries the resolved asset, not the transaction");
        Assert.That(Versions(body)[Clothing.ToString()].AsInteger(), Is.EqualTo(versionBefore + 1));
    }

    [Test]
    public void the_map_fields_are_stored_before_the_transaction_is_handed_over()
    {
        // the legacy route's order: InventoryService.UpdateItem (Scene.Inventory.cs:576), then
        // HandleItemUpdateFromTransaction (:579-582), which stores again once the xfer completes
        var b = Inventory();
        var transaction = UUID.Random();
        b.Transactions[transaction] = UUID.Random();

        Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["name"] = "Saved Shirt", ["hash_id"] = transaction });

        var order = b.Calls.Where(c => c.StartsWith("UpdateItem(") || c.StartsWith("ApplyAssetTransaction(")).ToList();
        Assert.That(order.First(), Does.StartWith("UpdateItem("), "the body's own fields are stored first");
        Assert.That(order, Does.Contain($"ApplyAssetTransaction({transaction})"));
        Assert.That(b.Items[Shirt].Name, Is.EqualTo("Saved Shirt"));
    }

    [Test]
    public void an_unresolvable_transaction_leaves_the_asset_alone_and_still_answers_200()
    {
        // a region with no transaction module, or the library: the item keeps what it had
        var b = Inventory();
        b.ResolvesTransactions = false;
        var before = b.Items[Shirt].AssetID;

        var (status, _) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["hash_id"] = UUID.Random() });

        Assert.That(status, Is.EqualTo(200), "an unresolvable hash must not fail the rest of the patch");
        Assert.That(b.Items[Shirt].AssetID, Is.EqualTo(before));
    }

    // ------------------------------------------------------------------ A19: a refused save is not a 200

    [Test]
    public void a_refused_transaction_answers_403_and_does_not_bump_the_category_version()
    {
        // 2026-09-06 09:52:55: a wearable referencing a library texture was refused by the uploader
        // ("REJECTED update with texture 00000000-0000-2222-3333-100000001002 ... because they do not own the
        // texture") and the cap still answered "UpdateItem -> 200" with the item's asset unchanged. The viewer was
        // told the save had succeeded.
        var b = Inventory();
        var transaction = UUID.Random();
        b.RefusedTransactions.Add(transaction);
        var assetBefore = b.Items[Shirt].AssetID;
        var versionBefore = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["hash_id"] = transaction });

        Assert.That(status, Is.EqualTo(403), "a refused save must not be reported as a success");
        Assert.That(b.Items[Shirt].AssetID, Is.EqualTo(assetBefore), "the item still points at its previous asset");
        Assert.That(body.ContainsKey("_updated_category_versions"), Is.False,
            "a failed save must not advance the folder version the viewer holds, or its next fetch skips the folder "
            + "and it never sees the true state (llaisapi.cpp:1625-1629)");
        Assert.That(b.Folders[Clothing].Version, Is.EqualTo(versionBefore));
    }

    [Test]
    public void a_refused_transaction_does_not_fire_the_worn_wearable_rebake()
    {
        // S9's hook is keyed on the asset actually changing, so a refusal must leave it silent - it did before this
        // fix and it must keep doing so, or a refused edit would cost a bake of an outfit that did not change.
        var b = Inventory();
        var transaction = UUID.Random();
        b.RefusedTransactions.Add(transaction);

        Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["hash_id"] = transaction });

        Assert.That(b.AssetChanges, Is.Empty, "nothing changed, so nothing may queue an appearance save");
    }

    [Test]
    public void a_refused_transaction_still_leaves_the_body_fields_it_already_stored()
    {
        // The map fields are applied and stored before the transaction is handed over (the legacy route's order),
        // so a rename that travelled with a refused asset is already written. The 403 is about the ASSET; it does
        // not pretend the rest of the PATCH did not happen, and the viewer re-reads the item either way.
        var b = Inventory();
        var transaction = UUID.Random();
        b.RefusedTransactions.Add(transaction);

        var (status, _) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap
        {
            ["name"] = "Renamed Shirt",
            ["hash_id"] = transaction,
        });

        Assert.That(status, Is.EqualTo(403));
        Assert.That(b.Items[Shirt].Name, Is.EqualTo("Renamed Shirt"));
    }

    [Test]
    public void patching_permissions_applies_next_everyone_and_group_masked_by_base()
    {
        // set_default_permissions changes exactly these three and calls updateServer (llagentwearables.cpp:62-78)
        var b = Inventory();
        b.Items[Shirt].BasePermissions = 0x0000000e;   // copy | modify | transfer, nothing else
        b.Items[Shirt].NextPermissions = 0;
        b.Items[Shirt].EveryOnePermissions = 0;
        b.Items[Shirt].GroupPermissions = 0;

        var (status, _) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap
        {
            ["permissions"] = new OSDMap
            {
                ["next_owner_mask"] = unchecked((int)0xffffffff),
                ["everyone_mask"] = unchecked((int)0xffffffff),
                ["group_mask"] = 0x00000008,
            },
        });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items[Shirt].NextPermissions, Is.EqualTo(0x0000000eu), "masked by base (Scene.Inventory.cs:539)");
        Assert.That(b.Items[Shirt].EveryOnePermissions, Is.EqualTo(0x0000000eu));
        Assert.That(b.Items[Shirt].GroupPermissions, Is.EqualTo(0x00000008u));
    }

    [Test]
    public void patching_permissions_never_takes_base_or_current_from_the_body()
    {
        var b = Inventory();
        var (status, _) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap
        {
            ["permissions"] = new OSDMap { ["base_mask"] = 0, ["owner_mask"] = 0, ["next_owner_mask"] = 8 },
        });
        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items[Shirt].BasePermissions, Is.EqualTo(0x7fffffffu), "base is not a client-settable field");
        Assert.That(b.Items[Shirt].CurrentPermissions, Is.EqualTo(0x7fffffffu), "nor is current");
    }

    [Test]
    public void patching_permissions_leaves_the_export_bit_as_it_stands()
    {
        var b = Inventory();
        b.Items[Shirt].BasePermissions = 0x7fffffff;
        b.Items[Shirt].EveryOnePermissions = 0;

        Send(b, "PATCH", $"/item/{Shirt}", new OSDMap
        {
            ["permissions"] = new OSDMap { ["everyone_mask"] = (int)OpenSim.Framework.PermissionMask.Export },
        });

        Assert.That(b.Items[Shirt].EveryOnePermissions & (uint)OpenSim.Framework.PermissionMask.Export, Is.EqualTo(0u),
            "export is not granted through this route; the legacy path guards it with a creator check we do not reproduce here");
    }

    [Test]
    public void a_patch_that_changes_nothing_writes_nothing()
    {
        var b = Inventory();
        var asset = b.Items[Shirt].AssetID;
        var version = b.Folders[Clothing].Version;

        var (status, _) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap
        {
            ["name"] = "Blue Shirt",
            ["asset_id"] = asset,
        });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Calls, Does.Not.Contain($"UpdateItem({Shirt})"), "an unchanged body must not bump the folder");
        Assert.That(b.Folders[Clothing].Version, Is.EqualTo(version));
    }

    [Test]
    public void patching_an_unknown_item_is_404()
    {
        var (status, body) = Send(Inventory(), "PATCH", $"/item/{UUID.Random()}", new OSDMap { ["name"] = "x" });
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ PATCH /category

    [Test]
    public void patching_a_category_lists_both_the_category_and_its_parent()
    {
        var b = Inventory();
        var (status, body) = Send(b, "PATCH", $"/category/{Outfits}", new OSDMap { ["name"] = "My Outfits" });

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Folders[Outfits].Name, Is.EqualTo("My Outfits"));
        Assert.That(body["category_id"].AsUUID(), Is.EqualTo(Outfits));
        Assert.That(body["name"].AsString(), Is.EqualTo("My Outfits"));

        var versions = Versions(body);
        Assert.That(versions.Keys, Is.EquivalentTo(new[] { Outfits.ToString(), Clothing.ToString() }),
            "parseCategory creates zero-delta entries for the category AND its parent (llaisapi.cpp:1419-1428), so both must be listed");
    }

    [Test]
    public void patching_an_unknown_category_is_404()
    {
        var (status, body) = Send(Inventory(), "PATCH", $"/category/{UUID.Random()}", new OSDMap { ["name"] = "x" });
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ DELETE /item

    [Test]
    public void deleting_an_item_reports_removed_items_and_the_parents_new_version()
    {
        var b = Inventory();
        var before = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "DELETE", $"/item/{Shirt}");

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Items.ContainsKey(Shirt), Is.False, "the item is gone");
        Assert.That(body.ContainsKey("_removed_items"), Is.True);
        Assert.That(((OSDArray)body["_removed_items"]).Select(o => o.AsUUID()), Is.EquivalentTo(new[] { Shirt }));
        Assert.That(body.ContainsKey("item_id"), Is.False, "a delete returns no content, only deltas");
        Assert.That(Versions(body)[Clothing.ToString()].AsInteger(), Is.EqualTo(before + 1));
    }

    [Test]
    public void deleting_an_unknown_item_is_404()
    {
        var (status, body) = Send(Inventory(), "DELETE", $"/item/{UUID.Random()}");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ DELETE /category

    [Test]
    public void deleting_a_category_names_only_the_folder_and_lets_the_viewer_purge_its_descendents()
    {
        var b = Inventory();
        var before = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "DELETE", $"/category/{Outfits}");

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Folders.ContainsKey(Outfits), Is.False);
        Assert.That(b.Folders.ContainsKey(Party), Is.False, "descendents are gone on the server");
        Assert.That(b.Items.ContainsKey(PartyHat), Is.False);

        var removed = ((OSDArray)body["_categories_removed"]).Select(o => o.AsUUID()).ToList();
        Assert.That(removed, Is.EquivalentTo(new[] { Outfits }),
            "only the folder is named: onObjectDeletedFromServer purges descendents locally (llinventorymodel.cpp:2019-2023), so they are implied");
        Assert.That(body.ContainsKey("_removed_items"), Is.False, "the descendent item is not enumerated either");
        Assert.That(Versions(body)[Clothing.ToString()].AsInteger(), Is.EqualTo(before + 1), "the deleted folder's PARENT is the one that changed");
        Assert.That(Versions(body).ContainsKey(Outfits.ToString()), Is.False,
            "the deleted folder must not be listed: the viewer would dereference a category it has just removed (Ledger A-R6)");
    }

    /// <summary>
    /// A2b, replacing A2's 409: the handler passes onlyIfTrash: false through the IInventoryService overload
    /// added in A2b, so a folder outside Trash is deleted rather than silently skipped. The trash gate is armed
    /// on the backend here, so this fails if the handler ever stops passing the flag.
    /// </summary>
    [Test]
    public void a_folder_outside_trash_is_deleted_because_the_handler_passes_only_if_trash_false()
    {
        var b = Inventory();
        b.DeleteFoldersOnlyIfTrash = true;   // armed: with onlyIfTrash true this folder would be skipped
        var before = b.Folders[Clothing].Version;

        var (status, body) = Send(b, "DELETE", $"/category/{Outfits}");

        Assert.That(status, Is.EqualTo(200));
        Assert.That(b.Folders.ContainsKey(Outfits), Is.False, "the folder is gone even though it was never in Trash");
        Assert.That(b.Calls, Does.Contain("DeleteFolders[1, onlyIfTrash=False]"),
            "the AIS route must ask for the unrestricted delete (Ledger A-Q9)");
        Assert.That(((OSDArray)body["_categories_removed"]).Select(o => o.AsUUID()), Is.EquivalentTo(new[] { Outfits }));
        Assert.That(Versions(body)[Clothing.ToString()].AsInteger(), Is.EqualTo(before + 1));
    }

    /// <summary>
    /// Protected folders are still refused, server side. The viewer refuses to send RemoveCategory for a folder
    /// whose type lookupIsProtectedType accepts (llviewerinventory.cpp:1557-1561), so this is defence in depth;
    /// the exact membership of that predicate is UNVERIFIED (llfoldertype.cpp is not a permitted read), so the
    /// server rule is "the root, or any system type except Outfit".
    /// </summary>
    [Test]
    public void a_protected_folder_is_refused_with_403_and_nothing_is_deleted()
    {
        var b = Inventory();

        // a system-typed folder
        var (status, body) = Send(b, "DELETE", $"/category/{Clothing}");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
        AssertErrorBody(body, 403);
        Assert.That(b.Folders.ContainsKey(Clothing), Is.True);
        Assert.That(b.Calls.Any(c => c.StartsWith("DeleteFolders")), Is.False, "the backend is never asked");

        // the inventory root
        var (rootStatus, _) = Send(b, "DELETE", $"/category/{Root}");
        Assert.That(rootStatus, Is.EqualTo((int)HttpStatusCode.Forbidden));
        Assert.That(b.Folders.ContainsKey(Root), Is.True);
    }

    /// <summary>A saved outfit is ordinary user data and must stay deletable.</summary>
    [Test]
    public void a_saved_outfit_folder_is_not_protected()
    {
        var b = Inventory();
        var outfit = new UUID("55555555-5555-4555-8555-555555555551");
        b.AddFolder(outfit, Outfits, "Beach Outfit", 1, (short)FolderType.Outfit);

        var (status, body) = Send(b, "DELETE", $"/category/{outfit}");

        Assert.That(status, Is.EqualTo(200), "FolderType.Outfit is a user folder, not a protected system one");
        Assert.That(b.Folders.ContainsKey(outfit), Is.False);
        Assert.That(((OSDArray)body["_categories_removed"]).Select(o => o.AsUUID()), Is.EquivalentTo(new[] { outfit }));
    }

    /// <summary>
    /// A3 Part 0: the protected set is the viewer's own table, not a guess. Every type whose PROTECTED column is
    /// false in LLFolderDictionary (llfoldertype.cpp:85-127) must be deletable, and every other type - including
    /// one the viewer's table has never heard of, which lookupIsProtectedType defaults to protected (:154-162) -
    /// must be refused.
    /// </summary>
    [TestCase((short)FolderType.None, false, TestName = "protected_set: FT_NONE is deletable")]
    [TestCase((short)FolderType.Outfit, false, TestName = "protected_set: FT_OUTFIT is deletable")]
    [TestCase((short)FolderType.MarketplaceListings, false, TestName = "protected_set: FT_MARKETPLACE_LISTINGS is deletable")]
    [TestCase((short)FolderType.MarkplaceStock, false, TestName = "protected_set: FT_MARKETPLACE_STOCK is deletable")]
    [TestCase((short)30, false, TestName = "protected_set: an ensemble type is deletable")]
    [TestCase((short)FolderType.Clothing, true, TestName = "protected_set: FT_CLOTHING is protected")]
    [TestCase((short)FolderType.Trash, true, TestName = "protected_set: FT_TRASH is protected")]
    [TestCase((short)FolderType.CurrentOutfit, true, TestName = "protected_set: FT_CURRENT_OUTFIT is protected")]
    [TestCase((short)FolderType.MyOutfits, true, TestName = "protected_set: FT_MY_OUTFITS is protected")]
    [TestCase((short)FolderType.Favorites, true, TestName = "protected_set: FT_FAVORITE is protected")]
    [TestCase((short)FolderType.Settings, true, TestName = "protected_set: FT_SETTINGS is protected")]
    [TestCase((short)FolderType.Suitcase, true, TestName = "protected_set: a type the viewer's table lacks defaults to protected")]
    public void the_protected_set_is_the_viewers_table(short folderType, bool expectRefused)
    {
        var b = Inventory();
        var id = new UUID("44444444-4444-4444-8444-44444444444" + (folderType < 0 ? "0" : "1"));
        b.AddFolder(id, Outfits, "subject", 1, folderType);

        var (status, _) = Send(b, "DELETE", $"/category/{id}");

        if (expectRefused)
        {
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden), $"type {folderType} must be protected");
            Assert.That(b.Folders.ContainsKey(id), Is.True);
        }
        else
        {
            Assert.That(status, Is.EqualTo(200), $"type {folderType} must be deletable");
            Assert.That(b.Folders.ContainsKey(id), Is.False);
        }
    }

    /// <summary>The verification survives: a service that really does nothing is a 500, not a false 200.</summary>
    [Test]
    public void a_delete_the_service_did_not_perform_is_reported_as_a_failure()
    {
        var b = Inventory();
        b.AllowWrite = false;   // DeleteFolders returns false and changes nothing

        var (status, body) = Send(b, "DELETE", $"/category/{Outfits}");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.InternalServerError));
        AssertErrorBody(body, 500);
        Assert.That(b.Folders.ContainsKey(Outfits), Is.True);
    }

    [Test]
    public void deleting_an_unknown_category_is_404()
    {
        var (status, body) = Send(Inventory(), "DELETE", $"/category/{UUID.Random()}");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        AssertErrorBody(body, 404);
    }

    // ------------------------------------------------------------------ cross-cutting

    [Test]
    public void tid_is_echoed_on_a_mutation()
    {
        var tid = UUID.Random();
        var (_, body) = Send(Inventory(), "DELETE", $"/item/{Shirt}?tid={tid}");
        Assert.That(body["tid"].AsUUID(), Is.EqualTo(tid));
    }

    [Test]
    public void every_mutation_through_the_library_cap_is_405_and_changes_nothing()
    {
        foreach (var (verb, path) in new (string, string)[]
        {
            ("PATCH", $"/item/{Shirt}"), ("PATCH", $"/category/{Outfits}"),
            ("DELETE", $"/item/{Shirt}"), ("DELETE", $"/category/{Outfits}"),
        })
        {
            var b = Inventory();
            var (status, body) = Send(b, verb, path, new OSDMap { ["name"] = "hacked" }, AisMode.Library);
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.MethodNotAllowed), $"{verb} {path}");
            AssertErrorBody(body, 405);
            Assert.That(b.Items[Shirt].Name, Is.EqualTo("Blue Shirt"), "nothing was written");
            Assert.That(b.Folders[Outfits].Name, Is.EqualTo("Outfits"));
            Assert.That(b.Calls.Any(c => c.StartsWith("Update") || c.StartsWith("Delete")), Is.False,
                "the backend is never even asked");
        }
    }

    [Test]
    public void a_service_that_refuses_the_write_is_a_500_not_a_silent_success()
    {
        var b = Inventory();
        b.AllowWrite = false;
        var (status, body) = Send(b, "PATCH", $"/item/{Shirt}", new OSDMap { ["name"] = "Nope" });
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.InternalServerError));
        AssertErrorBody(body, 500);
    }

    /// <summary>
    /// After A4 nothing in the spec answers 501 on the inventory cap except COPY, which belongs to the library
    /// cap. Purge, slam and create all landed in A3/A4.
    /// </summary>
    [Test]
    public void the_previously_unimplemented_mutations_now_answer_properly()
    {
        var b = Inventory();
        foreach (var (verb, path, expected) in new (string, string, int)[]
        {
            ("DELETE", $"/category/{Outfits}/children", 200),   // purge (A4)
            ("PUT", $"/category/{Outfits}/links", 200),         // slam (A3)
            ("POST", $"/category/{Outfits}", 200),              // create (A3)
            ("COPY", $"/category/{Outfits}", 501),              // library-cap operation
        })
        {
            var handler = new AisHandler(Cap, Agent, b);
            var response = new TestOSHttpResponse();
            // The PUT case must send the bare array it builds. `body as OSDMap` used to stand here and turned it
            // into null, i.e. no body at all — see the remarks on MutTestRequest.
            var body = verb == "PUT" ? (OSD)new OSDArray() : new OSDMap();
            handler.Handle(new MutTestRequest(verb, Cap + path, body), response);
            Assert.That(response.StatusCode, Is.EqualTo(expected), $"{verb} {path}");
        }
    }
    private static void AssertErrorBody(OSDMap body, int code)
    {
        Assert.That(body["error_code"].AsInteger(), Is.EqualTo(code));
        Assert.That(body.ContainsKey("message"), Is.True);
        Assert.That(body.ContainsKey("parent_id"), Is.False);
        Assert.That(body.ContainsKey("item_id"), Is.False);
        Assert.That(body.ContainsKey("category_id"), Is.False);
        Assert.That(body.ContainsKey("_embedded"), Is.False);
    }
}
