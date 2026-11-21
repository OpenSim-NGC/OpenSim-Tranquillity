/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// [Startup] ManagedStatsRemoteFetchURI: the managed stats page, on a real HTTP server.
/// </summary>
public class ManagedStatsPageTests
{
    private const string Category = "managedstatstest";

    [Theory]
    [InlineData("/ManagedStats/")]
    [InlineData("/ManagedStats")]
    [InlineData("/ManagedStats/?cat=" + Category)]
    [InlineData("/ManagedStats?cat=" + Category)]
    public async Task ThePageAnswersWithAndWithoutATrailingSlash(string path)
    {
        await WithPage(async client =>
        {
            HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(Category, await response.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task AContainerAloneChoosesTheContainer()
    {
        await WithPage(async client =>
        {
            HttpResponseMessage response = await client.GetAsync("/ManagedStats/?cont=first");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("first", body);
            Assert.DoesNotContain("second", body);
        });
    }

    private static async Task WithPage(Func<HttpClient, Task> test)
    {
        Stat first = NewStat("first");
        Stat second = NewStat("second");
        StatsManager.RegisterStat(first);
        StatsManager.RegisterStat(second);

        uint port = FreePort();
        BaseHttpServer server = new(port) { ListenIPAddress = IPAddress.Loopback };
        try
        {
            server.Start(false, false);
            new RegionStatusHandlerRegistrar().Register(server, App("ManagedStats"));
            using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await test(client);
        }
        finally
        {
            server.Stop();
            StatsManager.DeregisterStat(first);
            StatsManager.DeregisterStat(second);
        }
    }

    private static Stat NewStat(string container) =>
        new(container + "Count", container + " count", "", "", Category, container,
            StatType.Push, null, StatVerbosity.Info);

    /// <summary>
    /// A region application with only what the status handlers read: the stats paths and the secret.
    /// </summary>
    private static OpenSimBase App(string managedStatsUri)
    {
        var app = (OpenSimBase)RuntimeHelpers.GetUninitializedObject(typeof(OpenSimBase));
        app.userStatsURI = string.Empty;
        app.managedStatsURI = managedStatsUri;
        app.managedStatsPassword = string.Empty;
        typeof(BaseOpenSimServer).GetField("m_osSecret", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(app, Guid.NewGuid().ToString());
        return app;
    }

    private static uint FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return (uint)port;
    }
}
