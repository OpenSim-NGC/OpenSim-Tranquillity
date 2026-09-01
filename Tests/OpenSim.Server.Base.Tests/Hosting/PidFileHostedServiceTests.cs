/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Configuration;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

public sealed class PidFileHostedServiceTests
{
    [Fact]
    public async Task StartAsync_UsesStartupPidFileSetting_WhenPresent()
    {
        string pidPath = Path.Combine(Path.GetTempPath(), $"opensim-pid-{Guid.NewGuid():N}.pid");
        try
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Startup:PIDFile"] = pidPath,
                })
                .Build();

            var manager = new FakePidFileManager();
            var sut = new PidFileHostedService(config, manager);

            await sut.StartAsync(CancellationToken.None);

            Assert.Equal(pidPath, manager.CreatedPath);
        }
        finally
        {
            if (File.Exists(pidPath))
                File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task StopAsync_RemovesPidFile()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        var manager = new FakePidFileManager();
        var sut = new PidFileHostedService(config, manager);

        await sut.StopAsync(CancellationToken.None);

        Assert.True(manager.RemoveCalled);
    }

    private sealed class FakePidFileManager : IPidFileManager
    {
        public string ActivePath => string.Empty;
        public string CreatedPath { get; private set; } = string.Empty;
        public bool RemoveCalled { get; private set; }

        public void Create(string path)
        {
            CreatedPath = path;
        }

        public void Remove()
        {
            RemoveCalled = true;
        }
    }
}
