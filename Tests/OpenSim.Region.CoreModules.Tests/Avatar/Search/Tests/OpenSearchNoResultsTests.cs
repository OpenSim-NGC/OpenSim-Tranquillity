/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Net.Sockets;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using OpenSimSearch.Modules.OpenSearch;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.Search.Tests;

/// <summary>
/// OpenSimSearch against a search service that answers a query matching nothing with success and no "data" member,
/// as opensim-helpers query.php does. Every query is still answered to the viewer, with no rows, and nothing throws:
/// the module cast the missing member to a list and read its Count, a NullReferenceException on every world map
/// open with the Land for Sale layer on, and the viewer was never answered.
///
/// The search service is a local XML-RPC server on a free loopback port, started per test. The viewer is a stub
/// IClientAPI that keeps the handlers the module subscribes and the replies it is sent. The assembly runs its tests
/// one at a time (AssemblyInfo.cs).
/// </summary>
public class OpenSearchNoResultsTests : OpenSimTestCase
{
    private static readonly string[] s_methods =
    {
        "dir_places_query", "dir_popular_query", "dir_land_query", "dir_events_query", "dir_classified_query",
        "event_info_query", "classifieds_info_query",
    };

    private readonly BaseHttpServer m_server;
    private readonly Scene m_scene;
    private readonly Dictionary<string, Delegate> m_handlers = new();
    private readonly List<(string Method, object?[] Args)> m_sent = new();
    private readonly IClientAPI m_client;

    /// <summary>The "data" member the search service answers with, or null for none.</summary>
    private ArrayList? m_data;

    public OpenSearchNoResultsTests()
    {
        TcpListener free = new(IPAddress.Loopback, 0);
        free.Start();
        uint port = (uint)((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        m_server = new BaseHttpServer(port);
        foreach (string method in s_methods)
        {
            m_server.AddXmlRPCHandler(method, (request, caller) =>
            {
                Hashtable reply = new() { ["success"] = true, ["errorMessage"] = "" };
                if (m_data != null)
                    reply["data"] = m_data;
                return new XmlRpcResponse { Value = reply };
            });
        }
        m_server.Start(false, false);

        IConfigSource config = new IniConfigSource();
        IConfig search = config.AddConfig("Search");
        search.Set("Module", "OpenSimSearch");
        search.Set("SearchURL", $"http://127.0.0.1:{port}/");

        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, config, new OpenSearchModule());

        m_client = StubProxy.Create<IClientAPI>((method, args) =>
        {
            if (method.Name.StartsWith("add_On", StringComparison.Ordinal))
            {
                m_handlers[method.Name.Substring(4)] = (Delegate)args[0]!;
                return (true, null);
            }
            if (method.Name.StartsWith("Send", StringComparison.Ordinal))
                m_sent.Add((method.Name, args));
            return (false, null);
        });
        m_scene.EventManager.TriggerOnNewClient(m_client);
    }

    public override void Dispose()
    {
        m_server.Stop();
        base.Dispose();
    }

    private T Handler<T>(string name) where T : Delegate
    {
        Assert.True(m_handlers.TryGetValue(name, out Delegate? handler), name + " was not subscribed");
        return (T)handler!;
    }

    /// <summary>The one message the viewer was sent.</summary>
    private object?[] SentOnly(string method)
    {
        var sent = Assert.Single(m_sent);
        Assert.Equal(method, sent.Method);
        return sent.Args;
    }

    [Fact]
    public void LandForSaleMap_NoResults_SendsAnEmptyMapReply()
    {
        Handler<MapItemRequest>("OnMapItemRequest")(m_client, 2, 0, false, (uint)GridItemType.LandForSale, 0);

        object?[] args = SentOnly("SendMapItemReply");
        Assert.Empty((mapItemReply[])args[0]!);
        Assert.Equal((uint)GridItemType.LandForSale, args[1]);
    }

    [Fact]
    public void EventsMap_NoResults_SendsAnEmptyMapReply()
    {
        Handler<MapItemRequest>("OnMapItemRequest")(m_client, 2, 0, false, (uint)GridItemType.PgEvent, 0);

        object?[] args = SentOnly("SendMapItemReply");
        Assert.Empty((mapItemReply[])args[0]!);
        Assert.Equal((uint)GridItemType.PgEvent, args[1]);
    }

    [Fact]
    public void DirectoryQueries_NoResults_SendEmptyReplies()
    {
        UUID query = UUID.Random();

        Handler<DirPlacesQuery>("OnDirPlacesQuery")(m_client, query, "nothing", 0, 0, "", 0);
        Handler<DirPopularQuery>("OnDirPopularQuery")(m_client, query, 0);
        Handler<DirLandQuery>("OnDirLandQuery")(m_client, query, 0, 0, 0, 0, 0);
        Handler<DirFindQuery>("OnDirFindQuery")(m_client, query, "nothing", (uint)DirectoryManager.DirFindFlags.DateEvents, 0);
        Handler<DirClassifiedQuery>("OnDirClassifiedQuery")(m_client, query, "nothing", 0, 0, 0);

        Assert.Equal(
            new[] { "SendDirPlacesReply", "SendDirPopularReply", "SendDirLandReply", "SendDirEventsReply", "SendDirClassifiedReply" },
            m_sent.Select(s => s.Method));
        foreach (var (_, args) in m_sent)
        {
            Assert.Equal(query, args[0]);
            Assert.Empty((Array)args[1]!);
        }
    }

    [Fact]
    public void EventInfo_NoResults_SaysTheEventIsNotFound()
    {
        Handler<EventInfoRequest>("OnEventInfoRequest")(m_client, 7);

        Assert.Equal("Couldn't find this event.", SentOnly("SendAgentAlertMessage")[0]);
    }

    [Fact]
    public void ClassifiedInfo_NoResults_SendsNothing()
    {
        Handler<ClassifiedInfoRequest>("OnClassifiedInfoRequest")(UUID.Random(), m_client);

        Assert.Empty(m_sent);
    }

    [Fact]
    public void LandForSaleMap_AParcelHereWithNoLandingPoint_IsLeftOut()
    {
        string region = m_scene.RegionInfo.RegionID.ToString();
        m_data = new ArrayList
        {
            LandRow("no landing point", region, null),
            LandRow("bad landing point", region, "x/y/z"),
            LandRow("for sale", region, "128/64/25"),
        };

        Handler<MapItemRequest>("OnMapItemRequest")(m_client, 2, 0, false, (uint)GridItemType.LandForSale, 0);

        mapItemReply item = Assert.Single((mapItemReply[])SentOnly("SendMapItemReply")[0]!);
        Assert.Equal("for sale", item.name);
        Assert.Equal(m_scene.RegionInfo.RegionLocX * 256 + 128, item.x);
        Assert.Equal(m_scene.RegionInfo.RegionLocY * 256 + 64, item.y);
    }

    [Fact]
    public void EventsMap_AnEventWithNoLandingPoint_IsLeftOut()
    {
        m_data = new ArrayList
        {
            EventRow("no landing point", 1, null),
            EventRow("bad landing point", 2, "x,y,z"),
            EventRow("party", 3, "256128,256064,25"),
        };

        Handler<MapItemRequest>("OnMapItemRequest")(m_client, 2, 0, false, (uint)GridItemType.PgEvent, 0);

        mapItemReply item = Assert.Single((mapItemReply[])SentOnly("SendMapItemReply")[0]!);
        Assert.Equal("party", item.name);
        Assert.Equal(256128u, item.x);
        Assert.Equal(256064u, item.y);
    }

    private static Hashtable LandRow(string name, string region, string? landingPoint)
    {
        Hashtable row = new()
        {
            ["parcel_id"] = UUID.Random().ToString(),
            ["name"] = name,
            ["region_UUID"] = region,
            ["area"] = "512",
            ["sale_price"] = "100",
        };
        if (landingPoint != null)
            row["landing_point"] = landingPoint;
        return row;
    }

    private static Hashtable EventRow(string name, int id, string? landingPoint)
    {
        Hashtable row = new()
        {
            ["name"] = name,
            ["event_id"] = id.ToString(),
            ["unix_time"] = "1700000000",
        };
        if (landingPoint != null)
            row["landing_point"] = landingPoint;
        return row;
    }
}
