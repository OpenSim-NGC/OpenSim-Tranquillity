using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// AIS-SEC-5. The last two items from the external audit: what an unexpected exception tells the client, and
/// what a cyclic folder graph costs.
///
/// <para><b>Fix A — error hygiene.</b> <c>AisHandler.Dispatch</c> caught unexpected exceptions and put
/// <c>ex.Message</c> straight into the response, without logging the exception server-side. So a connector or
/// database fault reached an untrusted client as text — connection strings, host names, SQL fragments, whatever
/// the exception happened to carry — while the one person who needs the stack, an operator reading the region
/// log, got nothing at all. Both halves of that are wrong, and they are the same line.</para>
///
/// <para><b>Fix B — traversal.</b> <see cref="AisInventory.Walk"/> caps depth at 50, which bounds the damage but
/// does not stop a cycle from being re-walked: A→B→A→B… for fifty levels, and <b>every level is a backend
/// round trip</b>, which on this grid is a call to Robust. A visited set stops it at two.</para>
///
/// <para><b>Why a visited set is safe in <c>Expand</c>, which is not obvious.</b> <c>Without()</c> clones the
/// expanded map minus the current folder and hands the <i>same</i> clone to every sibling, so it implements
/// <b>ancestor-path exclusion</b>: a folder excluded down one branch is still available to a sibling branch. A
/// single shared visited set is stronger — <b>global once-only</b>. Those two differ exactly when a folder is
/// reachable by two distinct paths, i.e. a diamond. <c>InventoryFolderBase.ParentID</c> is a single scalar and
/// <c>GetFolderContent</c> selects children by <c>ParentID == folderId</c>, so every folder is the child of
/// exactly one parent: the graph is a forest plus possible cycles, and a diamond cannot occur. That is why the
/// substitution is equivalence and not an approximation — and <see cref="a_diamond_would_be_the_one_case_where_they_differ"/>
/// records the condition, so if the data model ever gains multi-parenting the assumption fails loudly here.</para>
/// </summary>
[TestFixture]
public class AisErrorHygieneTraversalTests
{
    private const string Cap = "/CAP/5ec50000-0000-4000-8000-000000000000";
    private static readonly UUID Agent = new("a5ec5000-0000-4000-8000-000000000001");

    private static readonly UUID Root = new("00000000-0000-4000-8000-00000000f001");
    private static readonly UUID Clothing = new("00000000-0000-4000-8000-00000000f002");
    private static readonly UUID Item = new("00000000-0000-4000-8000-00000000f003");

    // the cycle: each is the other's parent
    private static readonly UUID CycA = new("00000000-0000-4000-8000-00000000fa01");
    private static readonly UUID CycB = new("00000000-0000-4000-8000-00000000fa02");

    /// <summary>Text a real connector fault might carry. If this reaches the client, the fix is not working.</summary>
    private const string Secret =
        "Server=10.44.0.9;Database=legiongrid;Uid=root;Pwd=hunter2 -- at MySql.Data.MySqlClient.NativeDriver.Open()";

    private sealed class HygRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public HygRequest(string verb, string url, OSD body)
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
        b.AddItem(Item, Clothing, "a shirt");
        return b;
    }

    /// <summary>A↔B: each is the other's parent, so each is the other's only child.</summary>
    private static FakeAisBackend CyclicWorld()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(CycA, CycB, "A", 1);
        b.AddFolder(CycB, CycA, "B", 1);
        return b;
    }

    private static (int Status, OSDMap Body) Send(FakeAisBackend b, string verb, string path, OSD body = null)
    {
        var handler = new AisHandler(Cap, Agent, b);
        var response = new TestOSHttpResponse();
        handler.Handle(new HygRequest(verb, Cap + path, body), response);
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        return (response.StatusCode, parsed as OSDMap ?? new OSDMap());
    }

    private static string Flatten(OSDMap body) => OSDParser.SerializeLLSDXmlString(body);

    // ================================================================== Fix A

    [Test]
    public void an_unexpected_backend_exception_is_logged_and_not_echoed_to_the_client()
    {
        using var log = new CapturedLog();
        var b = World();
        b.ThrowOn = label => label.StartsWith("GetItem(", StringComparison.Ordinal)
            ? new InvalidOperationException(Secret)
            : null;

        var (status, body) = Send(b, "PATCH", $"/item/{Item}", new OSDMap { ["name"] = "x" });
        string wire = Flatten(body);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(500), "an unexpected fault is still a 500");

            // the whole point: nothing of the exception may travel
            Assert.That(wire, Does.Not.Contain("hunter2"), "a credential reached the client");
            Assert.That(wire, Does.Not.Contain("10.44.0.9"), "a host address reached the client");
            Assert.That(wire, Does.Not.Contain("MySql"), "an internal type name reached the client");
            Assert.That(wire, Does.Not.Contain(Secret));
            Assert.That(wire, Does.Not.Contain("InvalidOperationException"));
            Assert.That(wire, Does.Not.Contain(" at "), "no stack frame text");

            // and the operator must get what the client did not
            var errors = log.Entries(LogLevel.Error);
            Assert.That(errors, Is.Not.Empty, "the fault was not logged at Error");
            Assert.That(errors.Any(e => e.Exception is not null), Is.True,
                "the exception object itself must reach the logger, not just a message mentioning a fault");
            Assert.That(errors.Any(e => e.Exception is not null && e.Exception.Message.Contains("hunter2")), Is.True,
                "and it must be the real exception, so the stack is in the log");
            var text = string.Join(" | ", errors.Select(e => e.Message));
            Assert.That(text, Does.Contain("UpdateItem"), "the log names the operation");
            Assert.That(text, Does.Contain(Agent.ToString()), "and the agent");
        });
    }

    /// <summary>
    /// Control: an ordinary refusal is untouched. These messages are written by the handler for the client and
    /// must keep travelling - the fix is about exception text, not about all error text.
    /// </summary>
    [Test]
    public void an_ordinary_error_response_is_unchanged()
    {
        using var log = new CapturedLog();
        var b = World();

        var (status, body) = Send(b, "GET", $"/item/{UUID.Random()}");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(404));
            Assert.That(body["message"].AsString(), Does.StartWith("no item "),
                "the handler's own 404 text is deliberate and still sent");
            Assert.That(log.Entries(LogLevel.Error), Is.Empty, "a 404 is not a server fault and is not logged as one");
        });
    }

    // ================================================================== Fix B

    [Test]
    public void walk_terminates_on_a_cycle_visiting_each_folder_once()
    {
        var b = CyclicWorld();

        var walked = AisInventory.Walk(b, Agent, CycA, AisInventory.MaxDepth);
        var ids = walked.Select(c => c.Folder.ID).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.EqualTo(new[] { CycA, CycB }),
                "a cycle must be walked once, not re-walked to the depth cap");
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count), "no folder visited twice");

            // the cost, which is the real defect: every level is a backend round trip
            int contentCalls = b.Calls.Count(c => c.StartsWith("GetFolderContent(", StringComparison.Ordinal));
            Assert.That(contentCalls, Is.LessThanOrEqualTo(3),
                $"a cycle cost {contentCalls} GetFolderContent round trips; before AIS-SEC-5 it cost one per level to the cap of {AisInventory.MaxDepth}");
        });
    }

    [Test]
    public void the_children_route_on_a_cyclic_graph_answers_the_same_shape_as_before()
    {
        var b = CyclicWorld();

        var (status, body) = Send(b, "GET", $"/category/{CycA}/children?depth={AisInventory.MaxDepth}");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(200));
            Assert.That(body["category_id"].AsUUID(), Is.EqualTo(CycA));

            // A expands and contains B; B expands and contains A as a BARE category - no _embedded - because
            // the traversal refuses to re-enter a folder it has already expanded. That is exactly what the
            // Without() clone produced, and pinning it is what makes the substitution a non-change.
            var aEmbedded = (OSDMap)body["_embedded"];
            var aCats = (OSDMap)aEmbedded["categories"];
            Assert.That(aCats.ContainsKey(CycB.ToString()), Is.True);

            var bNode = (OSDMap)aCats[CycB.ToString()];
            Assert.That(bNode.ContainsKey("_embedded"), Is.True, "B is expanded");
            var bCats = (OSDMap)((OSDMap)bNode["_embedded"])["categories"];
            Assert.That(bCats.ContainsKey(CycA.ToString()), Is.True, "and lists A");

            var aAgain = (OSDMap)bCats[CycA.ToString()];
            Assert.That(aAgain.ContainsKey("_embedded"), Is.False,
                "A appears under B as a bare category - the recursion stops rather than looping");
        });
    }

    /// <summary>
    /// Control: a deep acyclic tree is unaffected, in both the walk and the response nesting. A visited set and
    /// ancestor-path exclusion agree on a forest, and this is the test that would notice if they did not.
    /// </summary>
    [Test]
    public void a_deep_acyclic_tree_is_unchanged()
    {
        var b = new FakeAisBackend(Agent);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        var chain = new List<UUID> { Root };
        for (var i = 0; i < 6; i++)
        {
            var id = new UUID($"00000000-0000-4000-8000-0000000000{(0xb0 + i):x2}");
            b.AddFolder(id, chain[^1], $"level {i}", 1);
            chain.Add(id);
        }
        // a sibling at level 0, so the tree branches rather than being a bare chain
        var sibling = new UUID("00000000-0000-4000-8000-0000000000c9");
        b.AddFolder(sibling, Root, "sibling", 1);

        var walked = AisInventory.Walk(b, Agent, Root, AisInventory.MaxDepth).Select(c => c.Folder.ID).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(walked.Distinct().Count(), Is.EqualTo(walked.Count), "no repeats in a forest");
            Assert.That(walked, Has.Count.EqualTo(8), "root, six levels and the sibling");
            Assert.That(walked[0], Is.EqualTo(Root));
            Assert.That(walked, Does.Contain(sibling));

            // and the nesting still reaches the bottom of the chain
            var (status, body) = Send(b, "GET", $"/category/{Root}/children?depth={AisInventory.MaxDepth}");
            Assert.That(status, Is.EqualTo(200));
            OSDMap node = body;
            for (var i = 1; i < chain.Count; i++)
            {
                var cats = (OSDMap)((OSDMap)node["_embedded"])["categories"];
                Assert.That(cats.ContainsKey(chain[i].ToString()), Is.True, $"level {i} is present");
                node = (OSDMap)cats[chain[i].ToString()];
            }
        });
    }

    /// <summary>
    /// The condition under which ancestor-path exclusion and a visited set would stop agreeing: a folder with two
    /// parents. This asserts the data model forbids it, so the equivalence the fix relies on is not folklore. If
    /// <c>InventoryFolderBase</c> ever gains multi-parenting, this fails and points at AIS-SEC-5.
    /// </summary>
    [Test]
    public void a_diamond_would_be_the_one_case_where_they_differ()
    {
        var folder = new InventoryFolderBase(UUID.Random(), "f", Agent, -1, UUID.Random(), 1);
        var first = folder.ParentID;
        folder.ParentID = UUID.Random();

        Assert.That(folder.ParentID, Is.Not.EqualTo(first),
            "ParentID is a single scalar: setting it replaces the parent rather than adding one, so a folder "
            + "cannot be the child of two folders and no diamond can reach the traversal");
    }
}
