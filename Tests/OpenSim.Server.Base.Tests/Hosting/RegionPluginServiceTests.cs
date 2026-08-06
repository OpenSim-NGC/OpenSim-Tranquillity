/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="RegionPluginService"/>, the extracted application-plugin
/// lifecycle helper. These verify the post-initialise and dispose fan-out without
/// depending on the Mono.Addins plugin loader (which requires a live add-in registry).
/// </summary>
public sealed class RegionPluginServiceTests
{
    private sealed class FakeApplicationPlugin : IApplicationPlugin
    {
        public int PostInitialiseCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public string Version => "1.0.0.0";
        public string Name => "FakeApplicationPlugin";

        public void Initialise() { }
        public void Initialise(IOpenSimBase openSim) { }
        public void PostInitialise() => PostInitialiseCalls++;
        public void Dispose() => DisposeCalls++;
    }

    [Fact]
    public void PostInitialise_CallsPostInitialiseOnEachPlugin()
    {
        var sut = new RegionPluginService();
        var p1 = new FakeApplicationPlugin();
        var p2 = new FakeApplicationPlugin();

        sut.PostInitialise(new List<IApplicationPlugin> { p1, p2 });

        Assert.Equal(1, p1.PostInitialiseCalls);
        Assert.Equal(1, p2.PostInitialiseCalls);
        Assert.Equal(0, p1.DisposeCalls);
        Assert.Equal(0, p2.DisposeCalls);
    }

    [Fact]
    public void Dispose_CallsDisposeOnEachPlugin()
    {
        var sut = new RegionPluginService();
        var p1 = new FakeApplicationPlugin();
        var p2 = new FakeApplicationPlugin();

        sut.Dispose(new List<IApplicationPlugin> { p1, p2 });

        Assert.Equal(1, p1.DisposeCalls);
        Assert.Equal(1, p2.DisposeCalls);
        Assert.Equal(0, p1.PostInitialiseCalls);
        Assert.Equal(0, p2.PostInitialiseCalls);
    }

    [Fact]
    public void PostInitialise_WithEmptyList_DoesNotThrow()
    {
        var sut = new RegionPluginService();

        sut.PostInitialise(new List<IApplicationPlugin>());
    }

    [Fact]
    public void Dispose_WithEmptyList_DoesNotThrow()
    {
        var sut = new RegionPluginService();

        sut.Dispose(new List<IApplicationPlugin>());
    }
}
