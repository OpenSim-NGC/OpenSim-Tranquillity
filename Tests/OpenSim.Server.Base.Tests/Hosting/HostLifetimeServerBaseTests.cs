/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Tests for <see cref="HostLifetimeServerBase"/>, which bridges the legacy console
/// "quit"/"shutdown" path to <see cref="IHostApplicationLifetime.StopApplication"/> so the
/// hosted MoneyServer/GridServer actually exit when the shutdown command is issued.
/// </summary>
public sealed class HostLifetimeServerBaseTests
{
    [Fact]
    public void ShutdownSpecific_RequestsHostStop_WhenLifetimeAssigned()
    {
        var lifetime = new FakeHostLifetime();
        var sut = new TestableHostLifetimeServerBase { HostLifetime = lifetime };

        sut.InvokeShutdownSpecific();

        Assert.True(lifetime.StopApplicationCalled);
    }

    [Fact]
    public void ShutdownSpecific_DoesNotThrow_WhenLifetimeMissing()
    {
        var sut = new TestableHostLifetimeServerBase();

        var exception = Record.Exception(() => sut.InvokeShutdownSpecific());

        Assert.Null(exception);
    }

    /// <summary>
    /// Exposes the protected <c>ShutdownSpecific</c> so the bridge can be exercised without the
    /// heavier <c>ServerBase.Shutdown()</c> path (which depends on a started stats collector).
    /// </summary>
    private sealed class TestableHostLifetimeServerBase : HostLifetimeServerBase
    {
        public void InvokeShutdownSpecific() => ShutdownSpecific();
    }

    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public bool StopApplicationCalled { get; private set; }

        public CancellationToken ApplicationStarted => _startedCts.Token;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;

        public void StopApplication()
        {
            StopApplicationCalled = true;
            _stoppingCts.Cancel();
        }
    }
}
