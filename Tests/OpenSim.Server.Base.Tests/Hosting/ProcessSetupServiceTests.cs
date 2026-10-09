/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Server.Base.Hosting;
using OpenSim.Framework;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

[Collection("MainConsole")]
public sealed class ProcessSetupServiceTests
{
    [Fact]
    public void ApplyDefaults_SetsDefaultThreadCultureToEnUs()
    {
        var sut = new ProcessSetupService(new NullLogger<ProcessSetupService>());

        sut.ApplyDefaults();

        Assert.Equal("en-US", System.Globalization.CultureInfo.DefaultThreadCurrentCulture?.Name);
    }

    [Fact]
    public void Apply_WithThreadPoolConfig_DoesNotThrow()
    {
        var sut = new ProcessSetupService(new NullLogger<ProcessSetupService>());

        var options = new ProcessSetupOptions
        {
            ConfigureThreadPoolMaxThreads = true,
            MinWorkerThreads = 10,
            MaxWorkerThreads = 100,
            MinIocpThreads = 10,
            MaxIocpThreads = 200,
        };

        var ex = Record.Exception(() => sut.Apply(options));

        Assert.Null(ex);
    }

    [Fact]
    public void ApplyDefaults_PreservesThreeMinuteConnectionLifetime()
    {
        var savedRedir = WebUtil.SharedSocketsHttpHandler;
        var savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        try
        {
            var sut = new ProcessSetupService(new NullLogger<ProcessSetupService>());
            sut.ApplyDefaults();
            WebUtil.SetupHTTPClients(false, false, null, 0);
            Assert.Equal(TimeSpan.FromMinutes(3), WebUtil.SharedSocketsHttpHandler.PooledConnectionLifetime);
            Assert.Equal(TimeSpan.FromMinutes(3), WebUtil.SharedSocketsHttpHandlerNoRedir.PooledConnectionLifetime);
            using var legacy = WebUtil.CreateLegacyHttpHandler();
            Assert.Equal(TimeSpan.FromMinutes(3), legacy.PooledConnectionLifetime);
        }
        finally
        {
            WebUtil.SharedSocketsHttpHandler.Dispose();
            WebUtil.SharedSocketsHttpHandlerNoRedir.Dispose();
            WebUtil.SharedSocketsHttpHandler = savedRedir;
            WebUtil.SharedSocketsHttpHandlerNoRedir = savedNoRedir;
            WebUtil.ConfigureHTTPDefaults(32, 30000, 180000, false);
        }
    }

    [Fact]
    public void Apply_MapsHttpDefaultsToBothSharedHandlers()
    {
        var savedRedir = WebUtil.SharedSocketsHttpHandler;
        var savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        try
        {
            var sut = new ProcessSetupService(new NullLogger<ProcessSetupService>());
            sut.Apply(new ProcessSetupOptions
            {
                DefaultConnectionLimit = 7,
                MaxServicePointIdleTime = 12000,
                DnsRefreshTimeout = 23000,
                Expect100Continue = true
            });
            WebUtil.SetupHTTPClients(false, false, null, 0);
            foreach (var handler in new[] { WebUtil.SharedSocketsHttpHandler, WebUtil.SharedSocketsHttpHandlerNoRedir })
            {
                Assert.Equal(7, handler.MaxConnectionsPerServer);
                Assert.Equal(TimeSpan.FromSeconds(12), handler.PooledConnectionIdleTimeout);
                Assert.Equal(TimeSpan.FromSeconds(23), handler.PooledConnectionLifetime);
            }
            using var client = WebUtil.GetNewGlobalHttpClient(1000);
            Assert.True(client.DefaultRequestHeaders.ExpectContinue);
        }
        finally
        {
            WebUtil.SharedSocketsHttpHandler.Dispose();
            WebUtil.SharedSocketsHttpHandlerNoRedir.Dispose();
            WebUtil.SharedSocketsHttpHandler = savedRedir;
            WebUtil.SharedSocketsHttpHandlerNoRedir = savedNoRedir;
            WebUtil.ConfigureHTTPDefaults(32, 30000, 180000, false);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ConfigureHttpDefaults_PreservesInfiniteAndNoReuseLifetime(int timeout)
    {
        var savedRedir = WebUtil.SharedSocketsHttpHandler;
        var savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        try
        {
            WebUtil.ConfigureHTTPDefaults(null, timeout, timeout, null);
            WebUtil.SetupHTTPClients(false, false, null, 0);
            Assert.Equal(TimeSpan.FromMilliseconds(timeout), WebUtil.SharedSocketsHttpHandler.PooledConnectionLifetime);
            Assert.Equal(TimeSpan.FromMilliseconds(timeout), WebUtil.SharedSocketsHttpHandler.PooledConnectionIdleTimeout);
        }
        finally
        {
            WebUtil.SharedSocketsHttpHandler.Dispose();
            WebUtil.SharedSocketsHttpHandlerNoRedir.Dispose();
            WebUtil.SharedSocketsHttpHandler = savedRedir;
            WebUtil.SharedSocketsHttpHandlerNoRedir = savedNoRedir;
            WebUtil.ConfigureHTTPDefaults(32, 30000, 180000, false);
        }
    }

    [Fact]
    public void ConfigureHttpDefaults_RejectsInvalidSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebUtil.ConfigureHTTPDefaults(0, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebUtil.ConfigureHTTPDefaults(null, -2, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebUtil.ConfigureHTTPDefaults(null, null, -2, null));
    }
}
