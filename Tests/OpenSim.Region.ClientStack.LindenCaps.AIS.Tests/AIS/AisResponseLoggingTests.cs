/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// Every mutation must say, in the log, what it answered — the status and the delta keys with their contents.
/// A live diagnosis once could not separate "the viewer rejected our delta" from "our delta was fine", because
/// nothing recorded what had been sent back; both hypotheses died on the same missing evidence. This is that
/// evidence, made permanent.
/// </summary>
public class AisResponseLoggingTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");
    private static readonly UUID Cof = new("71c3c184-410b-4dae-b20a-855741cf1faf");

    /// <summary>An agent with a Current Outfit folder holding one link, which is the shape step 10 exercises.</summary>
    private static (FakeAisBackend Backend, AisHandler Handler, UUID LinkId) Inventory()
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
        backend.AddFolder(Cof, root, "Current Outfit", 500, (short)FolderType.CurrentOutfit);
        backend.CurrentOutfitId = Cof;

        var target = UUID.Random();
        backend.AddItem(target, root, "Skirt");
        var linkId = UUID.Random();
        backend.AddLink(linkId, Cof, "Skirt", target);

        return (backend, new AisHandler("/" + UUID.Random(), Agent, backend), linkId);
    }

    /// <summary>Minimal IOSHttpRequest: only what the handler reads (verb, raw url, empty body).</summary>
    private sealed class LogTestRequest : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public LogTestRequest(string verb, string url) { HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url; }
        public string HttpMethod { get; }
        public Uri Url { get; }
        public string RawUrl { get; }
        public string UriPath => Url.AbsolutePath;
        public System.IO.Stream InputStream { get; set; } = new System.IO.MemoryStream();
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
        public System.Collections.Generic.HashSet<string> QueryFlags => throw new NotImplementedException();
        public System.Collections.Generic.Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
        public System.Net.IPEndPoint RemoteIPEndPoint => new(System.Net.IPAddress.Loopback, 1);
        public System.Net.IPEndPoint LocalIPEndPoint => new(System.Net.IPAddress.Loopback, 2);
        public string UserAgent => "test";
        public double ArrivalTS => 0;
    }

    private static void Send(AisHandler handler, string verb, string path)
    {
        var url = handler.CapPath + path;
        handler.Dispatch(AisRouter.Parse(verb, url, handler.CapPath), new LogTestRequest(verb, url), new TestOSHttpResponse());
    }

    // ------------------------------------------------------------------ the log line

    [Fact]
    public void a_removal_logs_its_status_and_the_delta_keys_it_actually_sent()
    {
        var (_, handler, linkId) = Inventory();

        using var log = new CapturedLog();
        Send(handler, "DELETE", "/item/" + linkId);

        var line = log.Messages(LogLevel.Debug).SingleOrDefault(m => m.Contains("RemoveItem ->"));
        Assert.True(line is not null, "a mutation must log what it answered");
        Assert.Contains("200", line);
        Assert.Contains(AisMutation.RemovedItems, line);
        Assert.Contains(linkId.ToString(), line);
        Assert.Contains(AisMutation.UpdatedCategoryVersions, line);
        Assert.Contains(Cof.ToString(), line);
    }

    [Fact]
    public void a_failed_mutation_logs_its_status_and_reason()
    {
        var (_, handler, _) = Inventory();

        using var log = new CapturedLog();
        Send(handler, "DELETE", "/item/" + UUID.Random());   // no such item

        var line = log.Messages(LogLevel.Debug).SingleOrDefault(m => m.Contains("RemoveItem ->"));
        Assert.True(line is not null, "a mutation that fails is exactly the case A10 needed");
        Assert.Contains("404", line);
    }

    [Fact]
    public void a_fetch_does_not_log_a_response_line()
    {
        var (_, handler, _) = Inventory();

        using var log = new CapturedLog();
        Send(handler, "GET", "/category/current/links");

        Assert.False(log.Messages(LogLevel.Debug).Any(m => m.Contains("FetchCOF ->")), "fetch bodies are whole inventory listings; logging them would bury the mutations");
    }

    [Fact]
    public void fetch_cof_logs_the_folder_current_resolved_to()
    {
        var (_, handler, _) = Inventory();

        using var log = new CapturedLog();
        Send(handler, "GET", "/category/current/links");

        var line = log.Messages(LogLevel.Debug).SingleOrDefault(m => m.Contains("resolved \"current\""));
        Assert.True(line is not null, "the A7 WARN only fires when there is more than one candidate, so the ordinary case recorded nothing");
        Assert.Contains(Cof.ToString(), line);
        Assert.Contains("500", line);
    }

    // ------------------------------------------------------------------ the summariser

    [Fact]
    public void every_delta_key_of_the_contract_is_rendered()
    {
        var a = new UUID("11111111-1111-4111-8111-111111111111");
        var b = new UUID("22222222-2222-4222-8222-222222222222");
        var body = new OSDMap
        {
            ["category_id"] = Cof,
            [AisMutation.CreatedCategories] = new OSDArray { OSD.FromUUID(a) },
            [AisMutation.CreatedItems] = new OSDArray { OSD.FromUUID(b) },
            [AisMutation.CategoriesRemoved] = new OSDArray { OSD.FromUUID(a) },
            [AisMutation.RemovedItems] = new OSDArray { OSD.FromUUID(b) },
            [AisMutation.CategoryItemsRemoved] = new OSDArray { OSD.FromUUID(b) },
            [AisMutation.BrokenLinksRemoved] = new OSDArray { OSD.FromUUID(a) },
            [AisMutation.UpdatedCategoryVersions] = new OSDMap { [Cof.ToString()] = 500 },
        };

        var summary = AisMutation.SummariseDeltas(body);

        foreach (var key in AisMutation.DeltaKeys)
            Assert.Contains(key, summary);
        Assert.Contains("category_id=" + Cof, summary);
        Assert.Contains(Cof + ":500", summary);
        Assert.Multiple(() => { Assert.Contains(a.ToString(), summary); Assert.Contains(b.ToString(), summary); });
    }

    /// <summary>
    /// The case worth seeing at a glance: a mutation that changed something and reported nothing. That is how a
    /// viewer's model goes stale, and it must not look like an ordinary line.
    /// </summary>
    [Fact]
    public void a_response_with_no_deltas_says_so()
    {
        Assert.Equal("empty body", AisMutation.SummariseDeltas(new OSDMap()));
        Assert.Equal("no deltas", AisMutation.SummariseDeltas(new OSDMap { ["tid"] = UUID.Random() }));
        Assert.Equal("empty body", AisMutation.SummariseDeltas(null));
    }

    [Fact]
    public void absent_keys_are_omitted_rather_than_printed_empty()
    {
        var body = new OSDMap { [AisMutation.RemovedItems] = new OSDArray { OSD.FromUUID(Cof) } };

        var summary = AisMutation.SummariseDeltas(body);

        Assert.Contains(AisMutation.RemovedItems, summary);
        Assert.DoesNotContain(AisMutation.CreatedItems, summary);
        Assert.DoesNotContain(AisMutation.UpdatedCategoryVersions, summary);
    }

    // ------------------------------------------------------------------ classification

    [Fact]
    public void every_mutation_is_classified_as_one_and_no_fetch_is()
    {
        foreach (var op in new[]
        {
            AisOperation.CreateInventory, AisOperation.SlamFolder, AisOperation.RemoveCategory,
            AisOperation.RemoveItem, AisOperation.PurgeDescendents, AisOperation.UpdateCategory,
            AisOperation.UpdateItem, AisOperation.CopyCategory,
        })
            Assert.True(AisOperations.IsMutation(op), $"{op} changes inventory");

        foreach (var op in new[]
        {
            AisOperation.Unknown, AisOperation.FetchItem, AisOperation.FetchCategoryChildren,
            AisOperation.FetchCategoryCategories, AisOperation.FetchCategorySubset, AisOperation.FetchCOF,
            AisOperation.FetchCategoryLinks, AisOperation.FetchOrphans,
        })
            Assert.False(AisOperations.IsMutation(op), $"{op} does not");
    }

    /// <summary>
    /// Guards the cost claim. With DEBUG off nothing may be logged — the summariser is only reached inside the
    /// level guard, so a production log level pays two predicates and no allocation.
    /// </summary>
    [Fact]
    public void nothing_is_logged_when_debug_is_off()
    {
        var (_, handler, linkId) = Inventory();

        using var log = new CapturedLog { Enabled = LogLevel.Information };
        Send(handler, "DELETE", "/item/" + linkId);

        Assert.Empty(log.Messages(LogLevel.Debug));
    }
}
