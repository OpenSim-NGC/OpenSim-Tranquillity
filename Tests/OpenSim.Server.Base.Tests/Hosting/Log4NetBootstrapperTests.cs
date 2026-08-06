/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

public sealed class Log4NetBootstrapperTests
{
    [Fact]
    public void ResolveConfigPath_UsesConfiguredPath_WhenProvided()
    {
        var sut = new Log4NetBootstrapper();

        string result = sut.ResolveConfigPath("custom.config", "default.config");

        Assert.Equal("custom.config", result);
    }

    [Fact]
    public void ResolveConfigPath_FallsBackToDefault_WhenConfiguredPathMissing()
    {
        var sut = new Log4NetBootstrapper();

        string result = sut.ResolveConfigPath(string.Empty, "default.config");

        Assert.Equal("default.config", result);
    }
}
