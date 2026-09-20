using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// AIS-SEC-3. Two mutations arriving for the same folder at the same time.
///
/// <para><b>The defect.</b> <c>SlamFolder</c> snapshots a folder's links, creates the wanted set, then deletes the
/// snapshot. Create-before-delete is the right failure bias (A3: the folder never holds fewer links than it
/// started with), but nothing serialises two mutations on one folder. Two slams that both snapshot the old links,
/// both create their own set, and both delete only what they saw leave the folder holding the <b>union</b> of two
/// outfits. The viewer can produce this — an outfit change while a previous one is still in flight, or two
/// sessions — and one agent can hold caps from more than one region in this process, so it is not even a
/// cross-process problem.</para>
///
/// <para><b>Determinism, not sleeps.</b> Every interleaving here is pinned with
/// <see cref="FakeAisBackend.BeforeCall"/> and a <see cref="ManualResetEventSlim"/>: one thread parks at a named
/// backend call while the other runs. A race test built on <c>Thread.Sleep</c> reports machine load rather than
/// behaviour. The gate fires before the store is touched and holds no lock, so the parked thread blocks nothing
/// it should not — which matters most for <see cref="different_folders_do_not_block_each_other"/>, a test that
/// would otherwise pass for the wrong reason.</para>
///
/// <para><b>What "correct" means here.</b> Not "both requests win" — that is impossible for two replacements of
/// the same folder. The requirement is <b>serialisability</b>: the end state must be one that some serial order
/// of the two requests could have produced, and no response may describe a state that never existed.</para>
/// </summary>
[TestFixture]
public class AisConcurrencyHttpTests
{
    private const string Cap = "/CAP/5ec3ec30-0000-4000-8000-000000000000";
    private static readonly UUID Alice = new("a5ec3000-0000-4000-8000-000000000001");
    private static readonly UUID Bob = new("b5ec3000-0000-4000-8000-000000000001");

    private static readonly UUID Root = new("00000000-0000-4000-8000-00000000c001");
    private static readonly UUID Clothing = new("00000000-0000-4000-8000-00000000c002");
    private static readonly UUID Cof = new("00000000-0000-4000-8000-00000000c003");
    private static readonly UUID Other = new("00000000-0000-4000-8000-00000000c004");

    private static readonly UUID TargetX = new("00000000-0000-4000-8000-0000000000e1");
    private static readonly UUID TargetY = new("00000000-0000-4000-8000-0000000000e2");
    private static readonly UUID TargetZ = new("00000000-0000-4000-8000-0000000000e3");
    private static readonly UUID OldLink = new("00000000-0000-4000-8000-0000000000d1");

    /// <summary>Which logical request the current thread is serving, so the gate can park exactly one of them.</summary>
    private static readonly ThreadLocal<string> Role = new();

    private sealed class ConcRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public ConcRequest(string verb, string url, OSD body)
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

    /// <summary>Root, Clothing, a COF holding one link, a second ordinary folder, and three link targets.</summary>
    private static FakeAisBackend World(UUID owner)
    {
        var b = new FakeAisBackend(owner);
        b.AddFolder(Root, UUID.Zero, "My Inventory", 3, (short)FolderType.Root);
        b.AddFolder(Clothing, Root, "Clothing", 7, (short)FolderType.Clothing);
        b.AddFolder(Cof, Root, "Current Outfit", 11, (short)FolderType.CurrentOutfit);
        b.AddFolder(Other, Root, "Another Outfit", 2, (short)FolderType.Outfit);
        b.CurrentOutfitId = Cof;
        b.AddItem(TargetX, Clothing, "target X");
        b.AddItem(TargetY, Clothing, "target Y");
        b.AddItem(TargetZ, Clothing, "target Z");
        b.AddLink(OldLink, Cof, "the old link", TargetX);
        return b;
    }

    private static OSDArray Slam(params UUID[] targets)
    {
        var a = new OSDArray();
        foreach (var t in targets)
            a.Add(new OSDMap { ["name"] = "link to " + t, ["desc"] = "", ["linked_id"] = t, ["type"] = (int)AssetType.Link });
        return a;
    }

    private static (int Status, OSDMap Body) Send(FakeAisBackend b, UUID agent, string verb, string path, OSD body)
    {
        var handler = new AisHandler(Cap, agent, b);
        var response = new TestOSHttpResponse();
        handler.Handle(new ConcRequest(verb, Cap + path, body), response);
        var parsed = OSDParser.DeserializeLLSDXml(response.RawBuffer);
        return (response.StatusCode, parsed as OSDMap ?? new OSDMap());
    }

    /// <summary>The link targets currently in a folder — the outfit, independent of link row ids.</summary>
    private static HashSet<UUID> Outfit(FakeAisBackend b, UUID folder)
        => b.Items.Values.Where(i => i.Folder == folder && i.AssetType == (int)AssetType.Link)
                         .Select(i => i.AssetID).ToHashSet();

    private static List<UUID> Ids(OSDMap body, string key)
        => body[key] is OSDArray a ? a.Select(o => o.AsUUID()).ToList() : new List<UUID>();

    /// <summary>
    /// Runs <paramref name="first"/> on a thread that parks at the first backend call whose label starts with
    /// <paramref name="parkAt"/>, then runs <paramref name="second"/>, then releases the first. The grace wait
    /// lets the second request either finish (unserialised) or block on the lock (serialised) — both outcomes are
    /// deterministic from here, and only the serialised one pays the grace.
    /// </summary>
    private static ((int Status, OSDMap Body) First, (int Status, OSDMap Body) Second) Interleave(
        FakeAisBackend b, string parkAt, Func<(int, OSDMap)> first, Func<(int, OSDMap)> second)
    {
        using var parked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var secondDone = new ManualResetEventSlim(false);
        var hasParked = 0;

        b.BeforeCall = label =>
        {
            if (Role.Value != "first") return;
            if (!label.StartsWith(parkAt, StringComparison.Ordinal)) return;
            if (Interlocked.CompareExchange(ref hasParked, 1, 0) != 0) return;   // one-shot
            parked.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        (int, OSDMap) firstResult = default, secondResult = default;
        Exception firstError = null, secondError = null;

        var t1 = new Thread(() => { Role.Value = "first"; try { firstResult = first(); } catch (Exception ex) { firstError = ex; } });
        var t2 = new Thread(() => { Role.Value = "second"; try { secondResult = second(); } catch (Exception ex) { secondError = ex; } finally { secondDone.Set(); } });

        t1.Start();
        try
        {
            Assert.That(parked.Wait(TimeSpan.FromSeconds(10)), Is.True, $"the first request never reached {parkAt}");
            t2.Start();
            secondDone.Wait(TimeSpan.FromMilliseconds(750));   // finishes at once when nothing serialises
        }
        finally
        {
            release.Set();   // a parked thread holds the folder lock; leaking it would poison later tests
        }
        Assert.That(t1.Join(TimeSpan.FromSeconds(30)), Is.True, "the first request did not finish");
        Assert.That(t2.Join(TimeSpan.FromSeconds(30)), Is.True, "the second request did not finish");
        b.BeforeCall = null;
        if (firstError is not null) throw firstError;
        if (secondError is not null) throw secondError;
        return (firstResult, secondResult);
    }

    // ------------------------------------------------------------------ (a) slam vs slam

    /// <summary>
    /// Both slams snapshot the old links before either creates — the interleaving that produces the union, and
    /// the one the defect actually needs. (Parking the second slam's snapshot <i>after</i> the first slam's
    /// creates does <b>not</b> reproduce it: the second would then see the first's links and replace them, which
    /// is a legal serial outcome.)
    ///
    /// <para>Correct behaviour is exactly one of the two outfits, whichever the lock ordered second. Never both.</para>
    /// </summary>
    [Test]
    public void two_slams_on_one_folder_never_leave_the_union()
    {
        var b = World(Alice);
        // park slam A immediately before its first create, i.e. after it has snapshotted the old links
        var (a, second) = Interleave(b, "AddItem(",
            () => Send(b, Alice, "PUT", $"/category/{Cof}/links", Slam(TargetX, TargetY)),
            () => Send(b, Alice, "PUT", $"/category/{Cof}/links", Slam(TargetZ)));

        var final = Outfit(b, Cof);
        var setA = new HashSet<UUID> { TargetX, TargetY };
        var setB = new HashSet<UUID> { TargetZ };

        Assert.Multiple(() =>
        {
            Assert.That(final, Is.Not.EqualTo(new HashSet<UUID> { TargetX, TargetY, TargetZ }),
                "the folder holds the UNION of both outfits - this is the AIS-SEC-3 defect");
            Assert.That(final.SetEquals(setA) || final.SetEquals(setB), Is.True,
                $"final outfit must be exactly one of the two requested sets, was [{string.Join(",", final)}]");
            Assert.That(a.Status is 200 or 503, Is.True, $"slam A answered {a.Status}");
            Assert.That(second.Status is 200 or 503, Is.True, $"slam B answered {second.Status}");
        });
    }

    // ------------------------------------------------------------------ (b) slam vs purge

    /// <summary>
    /// A slam racing <c>DELETE /category/{id}/children</c> on the same folder.
    ///
    /// <para><b>PurgeFolderGate is false on purpose, and it is the faithful setting.</b>
    /// <c>IInventoryService.PurgeFolder</c> is the one-argument, <c>onlyIfTrash = true</c> form, whose gate is
    /// "is this folder Trash or Lost and Found" — and a Current Outfit is neither, so the real service refuses it
    /// and <c>AisPurge</c> composes the purge from <c>DeleteItems</c> plus <c>DeleteFolders</c> instead
    /// (<c>AisPurge.Run</c>, <c>XInventoryService.cs:503-528</c>). That composed path is the one that races.</para>
    ///
    /// <para>The purge decides which children to delete, the slam then replaces every one of them, and the purge's
    /// closing re-read finds the slam's brand-new links still there — so it reports <b>500 "only partly purged"</b>
    /// and names links it never saw and was never asked to remove. Serialised, a purge of this folder always
    /// succeeds, whichever order it runs in.</para>
    /// </summary>
    [Test]
    public void a_purge_racing_a_slam_does_not_blame_the_slams_new_links()
    {
        var b = World(Alice);
        b.PurgeFolderGate = _ => false;          // a COF is not Trash: the real service refuses, as above

        // park the purge once it has chosen what to delete, then let the slam replace the folder underneath it
        var (purge, slam) = Interleave(b, "DeleteItems[",
            () => Send(b, Alice, "DELETE", $"/category/{Cof}/children", null),
            () => Send(b, Alice, "PUT", $"/category/{Cof}/links", Slam(TargetX, TargetY)));

        var final = Outfit(b, Cof);
        Assert.Multiple(() =>
        {
            Assert.That(purge.Status, Is.Not.EqualTo(500),
                "the purge blamed children a concurrent slam created - this is the AIS-SEC-3 defect");
            Assert.That(purge.Status is 200 or 503, Is.True, $"purge answered {purge.Status}");
            Assert.That(final.Count == 0 || final.SetEquals(new HashSet<UUID> { TargetX, TargetY }), Is.True,
                $"final state must be empty or exactly the slam's set, was [{string.Join(",", final)}]");
            // whatever the purge claims to have removed, it may not name a link the slam created
            var created = Ids(slam.Body, "_created_items").ToHashSet();
            var removed = Ids(purge.Body, "_removed_items");
            Assert.That(removed.Where(created.Contains), Is.Empty,
                "the purge reported removing a link the concurrent slam had just created");
        });
    }

    // ------------------------------------------------------------------ (c) slam vs create

    /// <summary>
    /// A slam racing <c>POST /category/{COF}</c> creating a link into the same folder. The created link must not
    /// be reported as created and then silently vanish, and the end state must be a legal serial outcome: the
    /// slam's set alone (create first, slam replaced it) or the slam's set plus the new link (slam first).
    /// </summary>
    [Test]
    public void a_create_racing_a_slam_is_either_kept_or_never_claimed()
    {
        var b = World(Alice);
        var body = new OSDMap
        {
            ["links"] = new OSDArray { new OSDMap
                { ["name"] = "created link", ["linked_id"] = TargetZ, ["type"] = (int)AssetType.Link } },
        };

        // park the create immediately before it writes, then let the slam replace the folder underneath it
        var (create, slam) = Interleave(b, "AddItem(",
            () => Send(b, Alice, "POST", $"/category/{Cof}", body),
            () => Send(b, Alice, "PUT", $"/category/{Cof}/links", Slam(TargetX, TargetY)));

        var final = Outfit(b, Cof);
        var createdIds = Ids(create.Body, "_created_items");
        Assert.Multiple(() =>
        {
            Assert.That(create.Status is 200 or 503, Is.True, $"create answered {create.Status}");
            Assert.That(slam.Status is 200 or 503, Is.True, $"slam answered {slam.Status}");

            var slamSet = new HashSet<UUID> { TargetX, TargetY };
            var slamPlusCreate = new HashSet<UUID> { TargetX, TargetY, TargetZ };
            Assert.That(final.SetEquals(slamSet) || final.SetEquals(slamPlusCreate), Is.True,
                $"final state is not a legal serial outcome, was [{string.Join(",", final)}]");

            // if the create answered 200 naming a created link, that link must still exist unless the slam,
            // ordered after it, legitimately replaced the folder - which it can only have done by seeing it
            if (create.Status == 200 && createdIds.Count > 0)
            {
                var stillThere = createdIds.All(id => b.Items.ContainsKey(id));
                var slamSawIt = Ids(slam.Body, "_removed_items").Intersect(createdIds).Any();
                Assert.That(stillThere || slamSawIt, Is.True,
                    "the create reported a link as created, and it is gone without the slam ever having seen it");
            }
        });
    }

    // ------------------------------------------------------------------ (d) and (e): no over-locking

    /// <summary>
    /// Two slams on DIFFERENT folders of the same agent must not wait on each other. The first is parked before
    /// its creates; the second must complete anyway. These pass today by accident and must stay passing — a lock
    /// that serialised the whole agent, or the whole handler, would break outfit changes and saved-outfit edits
    /// happening together.
    /// </summary>
    [Test]
    public void different_folders_do_not_block_each_other()
    {
        var b = World(Alice);
        using var parked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var secondDone = new ManualResetEventSlim(false);
        var hasParked = 0;

        b.BeforeCall = label =>
        {
            if (Role.Value != "first" || !label.StartsWith("AddItem(", StringComparison.Ordinal)) return;
            if (Interlocked.CompareExchange(ref hasParked, 1, 0) != 0) return;
            parked.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        (int Status, OSDMap Body) cofResult = default, otherResult = default;
        var t1 = new Thread(() => { Role.Value = "first"; cofResult = Send(b, Alice, "PUT", $"/category/{Cof}/links", Slam(TargetX)); });
        var t2 = new Thread(() => { Role.Value = "second"; otherResult = Send(b, Alice, "PUT", $"/category/{Other}/links", Slam(TargetZ)); secondDone.Set(); });

        // The stripe array means two unrelated keys CAN share a lock; that is only ever slower, never wrong,
        // but it would make this test fail for a reason that has nothing to do with the property under test.
        // Assert it up front so a future UUID change says so instead of timing out mysteriously.
        Assert.That(AisFolderLocks.StripeOf(Alice, Cof), Is.Not.EqualTo(AisFolderLocks.StripeOf(Alice, Other)),
            "these two folder keys share a lock stripe; pick different test UUIDs");

        t1.Start();
        try
        {
            Assert.That(parked.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first slam never reached its creates");
            t2.Start();
            Assert.That(secondDone.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "a slam on a DIFFERENT folder waited on the parked one - the lock is too coarse");
        }
        finally
        {
            // Always release: a parked thread holds the folder lock, and leaking it would break every later
            // test in this process rather than just this one.
            release.Set();
            t1.Join(TimeSpan.FromSeconds(30));
            t2.Join(TimeSpan.FromSeconds(30));
            b.BeforeCall = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(otherResult.Status, Is.EqualTo(200));
            Assert.That(cofResult.Status, Is.EqualTo(200));
            Assert.That(Outfit(b, Other), Is.EquivalentTo(new[] { TargetZ }));
            Assert.That(Outfit(b, Cof), Is.EquivalentTo(new[] { TargetX }));
        });
    }

    /// <summary>Two different agents slamming their own Current Outfit must not block each other either.</summary>
    [Test]
    public void different_agents_do_not_block_each_other()
    {
        var alice = World(Alice);
        var bob = World(Bob);
        using var parked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var secondDone = new ManualResetEventSlim(false);
        var hasParked = 0;

        alice.BeforeCall = label =>
        {
            if (Role.Value != "first" || !label.StartsWith("AddItem(", StringComparison.Ordinal)) return;
            if (Interlocked.CompareExchange(ref hasParked, 1, 0) != 0) return;
            parked.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        (int Status, OSDMap Body) aliceResult = default, bobResult = default;
        var t1 = new Thread(() => { Role.Value = "first"; aliceResult = Send(alice, Alice, "PUT", $"/category/{Cof}/links", Slam(TargetX)); });
        var t2 = new Thread(() => { Role.Value = "second"; bobResult = Send(bob, Bob, "PUT", $"/category/{Cof}/links", Slam(TargetZ)); secondDone.Set(); });

        Assert.That(AisFolderLocks.StripeOf(Alice, Cof), Is.Not.EqualTo(AisFolderLocks.StripeOf(Bob, Cof)),
            "Alice's and Bob's COF keys share a lock stripe; pick different test agent UUIDs");

        t1.Start();
        try
        {
            Assert.That(parked.Wait(TimeSpan.FromSeconds(10)), Is.True, "Alice's slam never reached its creates");
            t2.Start();
            Assert.That(secondDone.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "Bob waited on Alice - the lock key is missing the agent, so two residents whose COF ids collide would serialise");
        }
        finally
        {
            release.Set();
            t1.Join(TimeSpan.FromSeconds(30));
            t2.Join(TimeSpan.FromSeconds(30));
            alice.BeforeCall = null;
        }

        Assert.Multiple(() =>
        {
            Assert.That(aliceResult.Status, Is.EqualTo(200));
            Assert.That(bobResult.Status, Is.EqualTo(200));
            Assert.That(Outfit(alice, Cof), Is.EquivalentTo(new[] { TargetX }));
            Assert.That(Outfit(bob, Cof), Is.EquivalentTo(new[] { TargetZ }));
        });
    }
}
