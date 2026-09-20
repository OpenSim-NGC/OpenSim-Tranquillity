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
/// AIS-SEC-4. <c>POST /category/{parent}</c> that fails partway must say what it already created.
///
/// <para><b>The defect.</b> <c>CreateInventory</c> adds categories one at a time, then links one at a time. A
/// failure after earlier writes succeeded answered 500 with a body carrying <i>only</i> the error keys - so the
/// objects already in the database were invisible to the client, which could neither adopt them nor clean them up,
/// and a retry created them a second time. <c>AisCopy</c> already got this right and is the precedent this follows:
/// <i>"additive, so a partial copy leaves what it made and risks nothing that existed before"</i>, and its failure
/// response names the counts it created before failing.</para>
///
/// <para><b>The status stays 500, with the delta keys populated. The spec settles this; it is not a coin toss.</b>
/// <c>AIS-V3-SPEC.md</c> §1f says of the viewer, for <i>every</i> response: "always, success or failure -
/// <c>onUpdateReceived(result, type, body)</c> (<c>llaisapi.cpp:946</c>): the body <b>is parsed as an update</b>
/// even on error", and "the completion callback fires at least once, with a null id <b>unless the body carries the
/// ids of §1c</b>". §1c in turn says <c>CREATEINVENTORY</c> "fires once per <c>_created_categories</c> /
/// <c>_created_items</c> entry". So created ids placed in a 500 body are not decoration the viewer discards - it
/// applies them as an update and fires the per-id callbacks. That is exactly the reconciliation the defect denied.
/// </para>
///
/// <para><b>Why not 207.</b> 207 is a 2xx, so <c>llaisapi</c> would take the response as success and never emit the
/// "any other failure" warning the spec's table records for 4xx/5xx (<c>:942-943</c>) - the failure would vanish
/// from the client's own log. 500 with a populated body gives both halves: the client learns what exists, and the
/// failure is still a failure to everyone watching. The conservative choice and the correct one coincide here.</para>
///
/// <para><b>One constraint the spec does impose</b>, and the shape below respects it: an error body "must be a map
/// and must not carry <c>item_id</c>/<c>category_id</c> + <c>parent_id</c> pairs it does not mean". The success
/// path already emits no top-level ids - only <c>_created_categories</c>, <c>_created_items</c>,
/// <c>_embedded</c> and <c>_updated_category_versions</c> - so reusing it verbatim is safe.</para>
/// </summary>
[TestFixture]
public class AisCreatePartialHttpTests
{
    private const string Cap = "/CAP/5ec40000-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("a5ec4000-0000-4000-8000-000000000001");

    private static readonly UUID Root = new("00000000-0000-4000-8000-00000000d001");
    private static readonly UUID Clothing = new("00000000-0000-4000-8000-00000000d002");
    private static readonly UUID TargetA = new("00000000-0000-4000-8000-00000000d0a1");
    private static readonly UUID TargetB = new("00000000-0000-4000-8000-00000000d0a2");
    private static readonly UUID TargetC = new("00000000-0000-4000-8000-00000000d0a3");

    private sealed class CpRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public CpRequest(string verb, string url, OSD body)
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

    private static FakeAisBackend World()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddItem(TargetA, Clothing, "target A");
        b.AddItem(TargetB, Clothing, "target B");
        b.AddItem(TargetC, Clothing, "target C");
        return b;
    }

    private static (int Status, OSDMap Body) Post(FakeAisBackend b, OSDMap body)
    {
        var handler = new AisHandler(Cap, Agent, b);
        var response = new TestOSHttpResponse();
        handler.Handle(new CpRequest("POST", Cap + $"/category/{Clothing}", body), response);
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        Assert.That(parsed, Is.InstanceOf<OSDMap>(), "an error body must still be an LLSD map (spec 1f)");
        return (response.StatusCode, (OSDMap)parsed);
    }

    private static OSDMap Category(string name) => new() { ["name"] = name, ["type_default"] = (int)FolderType.Outfit };
    private static OSDMap Link(UUID target) => new()
        { ["name"] = "link to " + target, ["desc"] = "", ["linked_id"] = target, ["type"] = (int)AssetType.Link };

    private static List<UUID> Ids(OSDMap body, string key)
        => body[key] is OSDArray a ? a.Select(o => o.AsUUID()).ToList() : new List<UUID>();

    // ------------------------------------------------------------------ (a) a category fails partway

    /// <summary>
    /// Three categories, the second refused. The first is already in the database, so the response must name it:
    /// without that the client cannot adopt it and its retry makes a duplicate.
    /// </summary>
    [Test]
    public void a_category_create_that_fails_on_the_second_reports_the_first()
    {
        var b = World();
        var seen = 0;
        b.AddFolderGate = _ => ++seen != 2;      // the second AddFolder is refused

        var (status, body) = Post(b, new OSDMap
        {
            ["categories"] = new OSDArray { Category("first"), Category("second"), Category("third") },
        });

        var reported = Ids(body, "_created_categories");
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(500), "the failure is still a failure");
            Assert.That(reported, Has.Count.EqualTo(1),
                "the category created before the failure must be reported - this is the AIS-SEC-4 defect");
            Assert.That(b.Folders.ContainsKey(reported.FirstOrDefault()), Is.True,
                "and the id reported must be the one that really exists");
            Assert.That(b.Folders.Values.Count(f => f.Name == "first"), Is.EqualTo(1));
            Assert.That(b.Folders.Values.Any(f => f.Name == "second" || f.Name == "third"), Is.False,
                "nothing after the failure may have been written");
            Assert.That(body.ContainsKey("message"), Is.True, "and the error keys are still there for the log");
        });
    }

    // ------------------------------------------------------------------ (b) a link fails after categories

    /// <summary>
    /// Two categories then three links, the second link refused. Everything before it - both categories and the
    /// first link - must be reported, because all of it is in the database.
    /// </summary>
    [Test]
    public void a_link_create_that_fails_on_the_second_reports_the_categories_and_the_first_link()
    {
        var b = World();
        var seen = 0;
        b.AddItemGate = _ => ++seen != 2;        // the second AddItem is refused

        var (status, body) = Post(b, new OSDMap
        {
            ["categories"] = new OSDArray { Category("outfit one"), Category("outfit two") },
            ["links"] = new OSDArray { Link(TargetA), Link(TargetB), Link(TargetC) },
        });

        var categories = Ids(body, "_created_categories");
        var items = Ids(body, "_created_items");
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(500));
            Assert.That(categories, Has.Count.EqualTo(2), "both categories were created and must be reported");
            Assert.That(items, Has.Count.EqualTo(1), "so was the first link");
            Assert.That(categories.All(id => b.Folders.ContainsKey(id)), Is.True);
            Assert.That(items.All(id => b.Items.ContainsKey(id)), Is.True);
            // the one link that did land points at the first target, and nothing later exists
            Assert.That(b.Items[items[0]].AssetID, Is.EqualTo(TargetA));
            Assert.That(b.Items.Values.Any(i => i.AssetID == TargetB || i.AssetID == TargetC), Is.False);
            Assert.That(body.ContainsKey("_updated_category_versions"), Is.True,
                "the parent's version moved, so the viewer needs it or it will not re-read the folder");
        });
    }

    // ------------------------------------------------------------------ (c) the first write fails

    /// <summary>
    /// Nothing was created, so nothing is reported. This is the case whose behaviour must NOT change: the status is
    /// what it was, and the delta keys are absent rather than present-and-empty (absent and empty are the same to
    /// the viewer, and absent is what the success path emits when a collection is empty).
    /// </summary>
    [Test]
    public void a_create_that_fails_on_the_very_first_write_reports_nothing_created()
    {
        var b = World();
        b.AddFolderGate = _ => false;            // every AddFolder refused

        var (status, body) = Post(b, new OSDMap
        {
            ["categories"] = new OSDArray { Category("first"), Category("second") },
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(500), "unchanged from before AIS-SEC-4");
            Assert.That(body.ContainsKey("_created_categories"), Is.False);
            Assert.That(body.ContainsKey("_created_items"), Is.False);
            Assert.That(body.ContainsKey("_embedded"), Is.False);
            Assert.That(b.Folders.Values.Any(f => f.Name == "first" || f.Name == "second"), Is.False);
            Assert.That(body["error_code"].AsInteger(), Is.EqualTo(500));
        });
    }

    // ------------------------------------------------------------------ (d) the happy path is pinned

    /// <summary>
    /// Full success, pinned against the behaviour that shipped before this session so AIS-SEC-4 cannot quietly
    /// change it: status 200, the same four delta/content keys, and no error keys.
    /// </summary>
    [Test]
    public void a_fully_successful_create_is_unchanged()
    {
        var b = World();
        var (status, body) = Post(b, new OSDMap
        {
            ["categories"] = new OSDArray { Category("outfit one"), Category("outfit two") },
            ["links"] = new OSDArray { Link(TargetA), Link(TargetB) },
        });

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(200));
            Assert.That(Ids(body, "_created_categories"), Has.Count.EqualTo(2));
            Assert.That(Ids(body, "_created_items"), Has.Count.EqualTo(2));
            Assert.That(body.ContainsKey("_embedded"), Is.True);
            Assert.That(body.ContainsKey("_updated_category_versions"), Is.True);

            // no error keys on a success, and no top-level ids the spec forbids in any body (1f)
            Assert.That(body.ContainsKey("error_code"), Is.False);
            Assert.That(body.ContainsKey("message"), Is.False);
            Assert.That(body.ContainsKey("category_id"), Is.False);
            Assert.That(body.ContainsKey("item_id"), Is.False);

            var embedded = (OSDMap)body["_embedded"];
            Assert.That(((OSDMap)embedded["categories"]).Count, Is.EqualTo(2));
            Assert.That(((OSDMap)embedded["links"]).Count, Is.EqualTo(2));
            Assert.That(b.Folders.Values.Count(f => f.Name.StartsWith("outfit ")), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The partial-failure body must carry no top-level <c>category_id</c> / <c>item_id</c> either. The spec is
    /// explicit that an error body "must not carry <c>item_id</c>/<c>category_id</c> + <c>parent_id</c> pairs it
    /// does not mean" (§1f), and the viewer parses an error body as an update, so a stray pair would be applied.
    /// </summary>
    [Test]
    public void a_partial_failure_body_carries_no_top_level_ids()
    {
        var b = World();
        var seen = 0;
        b.AddFolderGate = _ => ++seen != 2;

        var (_, body) = Post(b, new OSDMap
        {
            ["categories"] = new OSDArray { Category("first"), Category("second") },
        });

        Assert.Multiple(() =>
        {
            Assert.That(body.ContainsKey("category_id"), Is.False);
            Assert.That(body.ContainsKey("item_id"), Is.False);
            Assert.That(body.ContainsKey("parent_id"), Is.False);
        });
    }
}
