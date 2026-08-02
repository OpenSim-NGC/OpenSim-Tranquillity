/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Server.Base.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

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
}
