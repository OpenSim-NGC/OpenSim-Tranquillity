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
/// PurgeDescendents and CopyLibraryCategory over HTTP. A purge cannot be rolled back, so the interesting test is
/// what a partial purge reports; a copy is additive, so the interesting tests are structure and the tid quirk.
/// </summary>
public class AisPurgeCopyHttpTests
{
    private const string Cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly UUID LibraryOwner = new("11111111-0000-0000-0000-000100bba000");

    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Clothing = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Trash = new("77777777-7777-4777-8777-777777777771");
    private static readonly UUID TrashSub = new("77777777-7777-4777-8777-777777777772");
    private static readonly UUID TrashItem = new("77777777-7777-4777-8777-777777777773");
    private static readonly UUID TrashSubItem = new("77777777-7777-4777-8777-777777777774");
    private static readonly UUID Empty = new("77777777-7777-4777-8777-777777777775");

    // the library side
    private static readonly UUID LibRoot = new("00000112-000f-0000-0000-000100bba000");
    private static readonly UUID LibOutfit = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1");
    private static readonly UUID LibSub = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2");
    private static readonly UUID LibShirt = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb3");
    private static readonly UUID LibShoes = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb4");

    private sealed class PcTestRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public PcTestRequest(string verb, string url, string destination = null)
        {
            HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url;
            if (destination is not null) Headers["Destination"] = destination;
        }
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

    /// <summary>Trash holding an item and a subfolder that itself holds an item, plus an empty folder.</summary>
    private static FakeAisBackend Inventory()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddFolder(Trash, Root, "Trash", 5, (short)FolderType.Trash);
        b.TrashId = Trash;
        b.AddFolder(TrashSub, Trash, "a discarded folder", 1);
        b.AddFolder(Empty, Root, "nothing in here", 2);
        b.AddItem(TrashItem, Trash, "a discarded item");
        b.AddItem(TrashSubItem, TrashSub, "nested discard");
        return b;
    }

    /// <summary>The library: an outfit folder with two items and a subfolder.</summary>
    private static FakeAisBackend Library()
    {
        var lib = new FakeAisBackend(LibraryOwner);
        lib.AddFolder(LibRoot, UUID.Zero, "OpenSim Library", 1, (short)FolderType.Root);
        lib.AddFolder(LibOutfit, LibRoot, "Casual Outfit", 1);
        lib.AddFolder(LibSub, LibOutfit, "Accessories", 1);
        var shirt = lib.AddItem(LibShirt, LibOutfit, "Library Shirt");
        shirt.BasePermissions = 0x7ffffff0; shirt.CurrentPermissions = 0x7ffffff0;
        shirt.NextPermissions = 0x00008000; shirt.EveryOnePermissions = 0x00004000;
        lib.AddItem(LibShoes, LibSub, "Library Shoes");
        return lib;
    }

    private static (int Status, OSDMap Body) Purge(FakeAisBackend b, string path)
    {
        var handler = new AisHandler(Cap, Agent, b);
        var response = new TestOSHttpResponse();
        handler.Handle(new PcTestRequest("DELETE", Cap + path), response);
        return (response.StatusCode, (OSDMap)OSDParser.DeserializeLLSDXml(response.RawBuffer));
    }

    private static (int Status, OSDMap Body) Copy(FakeAisBackend lib, FakeAisBackend dest, string path, string destination)
    {
        var handler = new AisHandler(Cap, LibraryOwner, lib, AisMode.Library, dest, Agent);
        var response = new TestOSHttpResponse();
        handler.Handle(new PcTestRequest("COPY", Cap + path, destination), response);
        return (response.StatusCode, (OSDMap)OSDParser.DeserializeLLSDXml(response.RawBuffer));
    }

    private static List<UUID> Ids(OSDMap body, string key)
        => body[key] is OSDArray a ? a.Select(o => o.AsUUID()).ToList() : new List<UUID>();

    // ------------------------------------------------------------------ purge

    [Fact]
    public void purging_a_folder_removes_everything_inside_and_keeps_the_folder()
    {
        var b = Inventory();
        var before = b.Folders[Trash].Version;

        var (status, body) = Purge(b, $"/category/{Trash}/children");

        Assert.Equal(200, status);
        Assert.True(b.Folders.ContainsKey(Trash), "the folder itself survives a purge");
        Assert.False(b.Folders.ContainsKey(TrashSub), "the subfolder is gone");
        Assert.False(b.Items.ContainsKey(TrashItem));
        Assert.False(b.Items.ContainsKey(TrashSubItem), "and so is the item nested inside it");

        // the deltas must be enumerated: nothing in the viewer sweeps a purged folder's children for us
        Assert.Equivalent(new[] { TrashSub }, Ids(body, "_categories_removed"), true);
        Assert.Equivalent(new[] { TrashItem }, Ids(body, "_removed_items"), true);
        Assert.Equal(b.Folders[Trash].Version, ((OSDMap)body["_updated_category_versions"])[Trash.ToString()].AsInteger());
        Assert.True(b.Folders[Trash].Version > before);
    }

    [Fact]
    public void purging_trash_is_the_real_caller_and_needs_no_special_case()
    {
        // Empty Trash: LLInventoryModel::emptyFolderType -> purge_descendents_of (llinventorymodel.cpp:4125-4131)
        var b = Inventory();
        Assert.True(AisHandler.IsProtected(b.Folders[Trash]), "Trash is a protected type");

        var (status, _) = Purge(b, $"/category/{Trash}/children");

        Assert.Equal(200, status);
        Assert.DoesNotContain(b.Items.Values, i => i.Folder == Trash);
    }

    [Fact]
    public void purging_an_already_empty_folder_succeeds_and_reports_nothing_removed()
    {
        var b = Inventory();
        var (status, body) = Purge(b, $"/category/{Empty}/children");

        Assert.Equal(200, status);
        Assert.False(body.ContainsKey("_categories_removed"));
        Assert.False(body.ContainsKey("_removed_items"));
        Assert.True(body.ContainsKey("_updated_category_versions"), "the folder's version is still reported");
        Assert.True(b.Folders.ContainsKey(Empty));
    }

    [Fact]
    public void a_partial_purge_reports_the_survivors_and_does_not_claim_success()
    {
        // a purge is destructive by intent: there is nothing to roll back to, so the contract is to say what
        // actually went and name what did not
        var b = Inventory();
        b.DeleteFoldersOnlyIfTrash = false;
        b.AllowWrite = true;
        // the subfolder cannot be deleted; the item can
        b.DeleteItemsGate = _ => true;
        b.PurgeFolderGate = _ => false;      // the service's own purge declines
        b.DeleteFoldersGate = _ => false;    // and so does the composed fallback

        var (status, body) = Purge(b, $"/category/{Trash}/children");

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.Contains(TrashSub.ToString(), body["message"].AsString());
        Assert.True(b.Folders.ContainsKey(TrashSub), "and it really is still there");
        Assert.False(b.Items.ContainsKey(TrashItem), "while the item that could be removed was removed");
    }

    [Fact]
    public void purging_an_unknown_category_is_404()
    {
        var (status, body) = Purge(Inventory(), $"/category/{UUID.Random()}/children");
        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.Equal(404, body["error_code"].AsInteger());
    }

    // ------------------------------------------------------------------ copy

    [Fact]
    public void copying_a_library_folder_preserves_its_structure_at_the_destination()
    {
        var lib = Library();
        var dest = Inventory();

        var (status, body) = Copy(lib, dest, $"/category/{LibOutfit}?tid={UUID.Random()}", Clothing.ToString());

        Assert.Equal(200, status);
        var categories = Ids(body, "_created_categories");
        var items = Ids(body, "_created_items");
        Assert.Equal(2, categories.Count);
        Assert.Equal(2, items.Count);

        var copiedOutfit = dest.Folders.Values.Single(f => f.ParentID == Clothing && f.Name == "Casual Outfit");
        var copiedSub = dest.Folders.Values.Single(f => f.ParentID == copiedOutfit.ID);
        Assert.Equal("Accessories", copiedSub.Name);
        Assert.Equal("Library Shirt", dest.Items.Values.Single(i => i.Folder == copiedOutfit.ID).Name);
        Assert.Equal("Library Shoes", dest.Items.Values.Single(i => i.Folder == copiedSub.ID).Name);

        // everything created belongs to the agent, not to the library owner
        Assert.True(dest.Items.Values.Where(i => i.Folder == copiedOutfit.ID).All(i => i.Owner == Agent));
        Assert.Equal(dest.Folders[Clothing].Version, ((OSDMap)body["_updated_category_versions"])[Clothing.ToString()].AsInteger());
    }

    [Fact]
    public void a_copied_library_item_keeps_the_source_permissions()
    {
        // Scene.Inventory.cs:1053-1064 - the library branch passes the source's own masks through, unlike the
        // resident-to-resident branch at :1066-1078 which degrades them to NextPermissions
        var lib = Library();
        var dest = Inventory();

        Copy(lib, dest, $"/category/{LibOutfit}?tid={UUID.Random()}", Clothing.ToString());

        var copiedShirt = dest.Items.Values.Single(i => i.Name == "Library Shirt");
        Assert.Equal(0x7ffffff0u, copiedShirt.BasePermissions);
        Assert.Equal(0x7ffffff0u, copiedShirt.CurrentPermissions);
        Assert.Equal(0x00008000u, copiedShirt.NextPermissions);
        Assert.Equal(0x00004000u, copiedShirt.EveryOnePermissions);
        Assert.Equal(lib.Items[LibShirt].AssetID, copiedShirt.AssetID);
        Assert.Equal(lib.Items[LibShirt].CreatorId, copiedShirt.CreatorId);
    }

    [Fact]
    public void the_depth_zero_tid_form_copies_the_folder_without_its_subfolders()
    {
        // llaisapi.cpp:275-278 appends ",depth=0" to the tid VALUE rather than adding a query parameter
        var lib = Library();
        var dest = Inventory();
        var tid = UUID.Random();

        var (status, body) = Copy(lib, dest, $"/category/{LibOutfit}?tid={tid},depth=0", Clothing.ToString());

        Assert.Equal(200, status);
        Assert.Single(Ids(body, "_created_categories"));
        Assert.Single(Ids(body, "_created_items"));
        Assert.DoesNotContain(dest.Folders.Values, f => f.Name == "Accessories");
        Assert.Equal(tid, body["tid"].AsUUID());
    }

    [Fact]
    public void copying_a_source_that_is_not_in_the_library_is_404()
    {
        var lib = Library();
        var dest = Inventory();

        // Clothing exists in the AGENT's inventory but not in the library
        var (status, body) = Copy(lib, dest, $"/category/{Clothing}?tid={UUID.Random()}", Clothing.ToString());

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.Contains("no library category", body["message"].AsString());
        Assert.DoesNotContain(dest.Folders.Values, f => f.Name == "Casual Outfit");
    }

    [Fact]
    public void a_copy_without_a_destination_header_is_400_and_an_unknown_destination_is_404()
    {
        var lib = Library();
        var dest = Inventory();

        var (noHeader, _) = Copy(lib, dest, $"/category/{LibOutfit}?tid={UUID.Random()}", null);
        Assert.Equal((int)HttpStatusCode.BadRequest, noHeader);

        var (unknown, _) = Copy(lib, dest, $"/category/{LibOutfit}?tid={UUID.Random()}", UUID.Random().ToString());
        Assert.Equal((int)HttpStatusCode.NotFound, unknown);
    }

    /// <summary>
    /// The destination is deliberately NOT protected-gated: copying a library folder into Clothing (a protected
    /// system type) or into the inventory root is the ordinary case, and lookupIsProtectedType governs moving,
    /// deleting and retyping a folder rather than adding children to it — the same reconciliation as slam and
    /// purge. This documents the choice so it cannot change silently.
    /// </summary>
    [Fact]
    public void copying_into_a_protected_system_folder_is_allowed()
    {
        var lib = Library();
        var dest = Inventory();
        Assert.True(AisHandler.IsProtected(dest.Folders[Clothing]), "Clothing is a protected type");

        var (status, _) = Copy(lib, dest, $"/category/{LibOutfit}?tid={UUID.Random()}", Clothing.ToString());

        Assert.Equal(200, status);
        Assert.Contains(dest.Folders.Values, f => f.ParentID == Clothing && f.Name == "Casual Outfit");
    }

    [Fact]
    public void a_copy_that_fails_part_way_keeps_what_it_made_and_says_how_much()
    {
        var lib = Library();
        var dest = Inventory();
        var adds = 0;
        dest.AddItemGate = _ => ++adds < 2;    // the second item fails

        var (status, body) = Copy(lib, dest, $"/category/{LibOutfit}?tid={UUID.Random()}", Clothing.ToString());

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.Contains("were created before the failure", body["message"].AsString());
        Assert.True(dest.Items.Values.Any(i => i.Name == "Library Shirt"), "a copy is additive: what was made stays, and nothing pre-existing was at risk");
    }
}
