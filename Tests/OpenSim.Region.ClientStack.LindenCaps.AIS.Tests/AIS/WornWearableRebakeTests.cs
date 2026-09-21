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
/// S9. Editing a worn wearable — open it, change a colour, Save — changed the asset and produced no rebake.
///
/// <para><b>Why nothing fired.</b> Three signals could have told the region, and none did. The worn SET does not
/// move, because the viewer keeps the item id when it saves (<c>llagentwearables.cpp:319</c>,
/// <c>new_wearable-&gt;setItemID(old_item_id)</c>), so no <c>AgentIsNowWearing</c> follows. The COF churns — the
/// edit panel replaces the link — but a COF version bump is not itself a trigger; only an appearance save is.
/// And the viewer's <c>UpdateAvatarAppearance</c> POST, which <c>LLUpdateAppearanceOnDestroy</c> schedules
/// (<c>llappearancemgr.cpp:534-552</c> → <c>:2571-2575</c>), is deferred while any upload is pending
/// (<c>:3849</c>) and can arrive long after, carrying a <c>cof_version</c> the COF has moved past. Observed
/// 2026-09-05: four edits between 20:30:34 and 20:52:48, no bake after any of them, and a single cap POST at
/// 20:57:40 refused as stale — "client cof_version 578, server 579".</para>
///
/// <para>The AIS <c>UpdateItem</c> that carries the new asset is the one moment the region reliably learns of
/// the change, so that is where the save is queued.</para>
/// </summary>
[TestFixture]
public class WornWearableRebakeTests
{
    private const string Cap = "/CAP/0a1b2c3d-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Clothing = new("11111111-1111-4111-8111-111111111111");
    private static readonly UUID Shirt = new("22222222-2222-4222-8222-222222222222");

    private sealed class Req : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public Req(string verb, string url, OSDMap body = null)
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

    private static FakeAisBackend Inventory()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddItem(Shirt, Clothing, "Blue Shirt");
        return b;
    }

    private static int Send(FakeAisBackend backend, string path, OSDMap body)
    {
        var handler = new AisHandler(Cap, Agent, backend);
        var response = new TestOSHttpResponse();
        handler.Handle(new Req("PATCH", Cap + path, body), response);
        return response.StatusCode;
    }

    // ------------------------------------------------------------------ the handler reports the change

    [Test]
    public void a_patch_that_changes_an_items_asset_reports_it()
    {
        var b = Inventory();
        var uploaded = UUID.Random();

        Assert.That(Send(b, $"/item/{Shirt}", new OSDMap { ["asset_id"] = uploaded }), Is.EqualTo(200));

        Assert.That(b.AssetChanges, Has.Count.EqualTo(1),
            "the AIS UpdateItem is the only reliable moment the region learns a worn wearable was edited");
        Assert.That(b.AssetChanges[0].Item, Is.EqualTo(Shirt));
        Assert.That(b.AssetChanges[0].Asset, Is.EqualTo(uploaded));
    }

    [Test]
    public void a_transaction_resolved_asset_is_reported_too()
    {
        // the path a real wearable save takes: hash_id, not asset_id (llviewerinventory.cpp:445-452)
        var b = Inventory();
        var transaction = UUID.Random();
        var uploaded = UUID.Random();
        b.Transactions[transaction] = uploaded;

        Assert.That(Send(b, $"/item/{Shirt}", new OSDMap { ["hash_id"] = transaction }), Is.EqualTo(200));

        Assert.That(b.AssetChanges, Has.Count.EqualTo(1));
        Assert.That(b.AssetChanges[0].Asset, Is.EqualTo(uploaded));
    }

    [Test]
    public void a_patch_that_does_not_change_the_asset_reports_nothing()
    {
        var b = Inventory();
        var unchanged = b.Items[Shirt].AssetID;

        Send(b, $"/item/{Shirt}", new OSDMap { ["name"] = "Red Shirt", ["asset_id"] = unchanged });

        Assert.That(b.AssetChanges, Is.Empty, "a rename must not cost an appearance save");
    }

    [Test]
    public void an_unresolvable_transaction_reports_nothing()
    {
        var b = Inventory();
        Send(b, $"/item/{Shirt}", new OSDMap { ["hash_id"] = UUID.Random() });
        Assert.That(b.AssetChanges, Is.Empty, "no asset landed, so there is nothing to rebake from");
    }

    // ------------------------------------------------------------------ the rule the module applies

    private static AvatarAppearance Wearing(WearableType slot, UUID itemId, UUID assetId)
    {
        var a = new AvatarAppearance();
        var worn = a.Wearables;
        worn[(int)slot] = new AvatarWearable(itemId, assetId);
        a.Wearables = worn;
        return a;
    }

    [Test]
    public void editing_a_worn_wearable_points_it_at_the_new_asset()
    {
        var oldAsset = UUID.Random();
        var newAsset = UUID.Random();
        var appearance = Wearing(WearableType.Shirt, Shirt, oldAsset);

        Assert.That(AisWornAssets.ApplyTo(appearance, Shirt, newAsset), Is.True);
        Assert.That(appearance.Wearables[(int)WearableType.Shirt].GetAsset(Shirt), Is.EqualTo(newAsset));
        Assert.That(appearance.Wearables[(int)WearableType.Shirt].Count, Is.EqualTo(1),
            "the slot is updated in place, not appended to");
    }

    [Test]
    public void editing_an_item_that_is_not_worn_changes_nothing()
    {
        // the case that would otherwise make every drawer edit cost an appearance save
        var appearance = Wearing(WearableType.Shirt, Shirt, UUID.Random());
        Assert.That(AisWornAssets.ApplyTo(appearance, UUID.Random(), UUID.Random()), Is.False);
    }

    [Test]
    public void a_worn_item_already_carrying_the_asset_changes_nothing()
    {
        var asset = UUID.Random();
        var appearance = Wearing(WearableType.Shirt, Shirt, asset);
        Assert.That(AisWornAssets.ApplyTo(appearance, Shirt, asset), Is.False, "a replayed PATCH must be free");
    }

    [Test]
    public void a_zero_item_or_asset_changes_nothing()
    {
        var appearance = Wearing(WearableType.Shirt, Shirt, UUID.Random());
        Assert.That(AisWornAssets.ApplyTo(appearance, UUID.Zero, UUID.Random()), Is.False);
        Assert.That(AisWornAssets.ApplyTo(appearance, Shirt, UUID.Zero), Is.False);
        Assert.That(AisWornAssets.ApplyTo(null, Shirt, UUID.Random()), Is.False);
    }

    [Test]
    public void the_right_slot_is_found_whichever_one_it_is()
    {
        foreach (var slot in new[] { WearableType.Skin, WearableType.Gloves, WearableType.Socks, WearableType.Skirt })
        {
            var newAsset = UUID.Random();
            var appearance = Wearing(slot, Shirt, UUID.Random());
            Assert.That(AisWornAssets.ApplyTo(appearance, Shirt, newAsset), Is.True, slot.ToString());
            Assert.That(appearance.Wearables[(int)slot].GetAsset(Shirt), Is.EqualTo(newAsset), slot.ToString());
        }
    }
}
