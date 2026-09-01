/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

public sealed class StartupFailureCoordinatorTests
{
    [Fact]
    public void ThrowFatal_AlwaysThrowsInvalidOperationException()
    {
        var lifetime = new FakeHostLifetime();
        var sut = new StartupFailureCoordinator(new NullLogger<StartupFailureCoordinator>(), lifetime);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            sut.ThrowFatal("boom"));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void ThrowFatal_PreservesInnerException()
    {
        var lifetime = new FakeHostLifetime();
        var sut = new StartupFailureCoordinator(new NullLogger<StartupFailureCoordinator>(), lifetime);
        var inner = new InvalidCastException("inner");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            sut.ThrowFatal("boom", inner));

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void RequestStop_CallsHostStopApplication()
    {
        var lifetime = new FakeHostLifetime();
        var sut = new StartupFailureCoordinator(new NullLogger<StartupFailureCoordinator>(), lifetime);

        sut.RequestStop("stop");

        Assert.True(lifetime.StopRequested);
    }

    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public bool StopRequested { get; private set; }

        public void StopApplication() => StopRequested = true;
    }
}
