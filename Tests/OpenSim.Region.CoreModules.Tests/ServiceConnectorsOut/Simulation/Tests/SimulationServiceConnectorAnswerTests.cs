/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Connectors.Simulation;
using Xunit;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation.Tests;

/// <summary>
/// What SimulationServiceConnector makes of a destination region's answers. CloseAgent returned true after a DELETE
/// that failed. QueryAccess cleared the caller's feature list and read the features from the result wrapper: a JSON
/// answer is parsed into the "_Result" map, so the list the destination sent was never read and came back empty.
/// </summary>
public class SimulationServiceConnectorAnswerTests
{
    /// <summary>A server on a free local port that answers every request with this status and body.</summary>
    private sealed class Answering : IDisposable
    {
        private readonly HttpListener m_listener = new();
        public string Url { get; }
        public ConcurrentQueue<string> Requests { get; } = new();

        public Answering(HttpStatusCode status, string body)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            m_listener.Prefixes.Add(Url);
            m_listener.Start();
            _ = Task.Run(async () =>
            {
                while (m_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await m_listener.GetContextAsync(); }
                    catch { return; }
                    Requests.Enqueue(ctx.Request.HttpMethod + " " + ctx.Request.Url?.PathAndQuery);
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = (int)status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => m_listener.Close();
    }

    private static GridRegion Destination(string url) => new()
    {
        RegionID = UUID.Random(),
        RegionName = "Answer test",
        ServerURI = url,
    };

    [Fact]
    public void CloseAgent_DeliveredDelete_ReturnsTrue()
    {
        UUID agent = UUID.Random();
        using var server = new Answering(HttpStatusCode.OK, "OpenSim agent " + agent);
        GridRegion destination = Destination(server.Url);

        Assert.True(new SimulationServiceConnector().CloseAgent(destination, agent, "code"));
        Assert.Equal(new[] { $"DELETE /agent/{agent}/{destination.RegionID}/?auth=code" }, server.Requests);
    }

    [Fact]
    public void CloseAgent_FailedDelete_ReturnsFalse()
    {
        using (var server = new Answering(HttpStatusCode.InternalServerError, "error"))
        {
            Assert.False(new SimulationServiceConnector().CloseAgent(Destination(server.Url), UUID.Random(), "code"));
            Assert.Single(server.Requests);
        }

        // Nothing listening on the port
        string closed;
        using (var server = new Answering(HttpStatusCode.OK, "unused"))
            closed = server.Url;
        Assert.False(new SimulationServiceConnector().CloseAgent(Destination(closed), UUID.Random(), "code"));
    }

    [Fact]
    public void QueryAccess_JsonAnswer_ReturnsTheDestinationsFeatures()
    {
        UUID offered = UUID.Random();
        UUID first = UUID.Random();
        UUID second = UUID.Random();
        string answer =
            "{\"success\":true,\"reason\":\"\",\"version\":\"SIMULATION/0.7\"," +
            "\"negotiated_inbound_version\":0.7,\"negotiated_outbound_version\":0.7," +
            $"\"features\":[\"{first}\",\"not a uuid\",\"{second}\"]}}";
        using var server = new Answering(HttpStatusCode.OK, answer);

        List<UUID> features = new() { offered };
        EntityTransferContext ctx = new();
        Assert.True(new SimulationServiceConnector().QueryAccess(
            Destination(server.Url), UUID.Random(), null!, true, new Vector3(128, 128, 30), features, ctx, out string reason));

        Assert.Equal(string.Empty, reason);
        Assert.Equal(new[] { first, second }, features);
    }

    [Fact]
    public void QueryAccess_Refused_KeepsTheCallersFeatures()
    {
        UUID offered = UUID.Random();
        string answer =
            "{\"success\":false,\"reason\":\"No room\",\"version\":\"SIMULATION/0.7\"," +
            "\"negotiated_inbound_version\":0.7,\"negotiated_outbound_version\":0.7," +
            $"\"features\":[\"{UUID.Random()}\"]}}";
        using var server = new Answering(HttpStatusCode.OK, answer);

        List<UUID> features = new() { offered };
        Assert.False(new SimulationServiceConnector().QueryAccess(
            Destination(server.Url), UUID.Random(), null!, true, new Vector3(128, 128, 30), features,
            new EntityTransferContext(), out string reason));

        Assert.Equal("No room", reason);
        Assert.Equal(new[] { offered }, features);
    }
}
