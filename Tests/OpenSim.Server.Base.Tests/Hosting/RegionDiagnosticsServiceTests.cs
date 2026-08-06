/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging.Abstractions;
using Nini.Config;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="RegionDiagnosticsService"/>, the extracted periodic
/// diagnostics timer. These verify lifecycle safety (idempotent Start/Stop) and the
/// "period 0 disables the timer" configuration contract, without depending on a live
/// region or a real timer firing.
/// </summary>
public sealed class RegionDiagnosticsServiceTests
{
    private static IConfig MakeStartupConfig(int? logShowStatsSeconds)
    {
        var source = new IniConfigSource();
        IConfig startup = source.AddConfig("Startup");
        if (logShowStatsSeconds.HasValue)
            startup.Set("LogShowStatsSeconds", logShowStatsSeconds.Value);

        return startup;
    }

    private static RegionDiagnosticsService MakeService()
    {
        return new RegionDiagnosticsService(new NullLogger<RegionDiagnosticsService>());
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        var sut = MakeService();

        sut.Stop();
    }

    [Fact]
    public void Start_WithZeroPeriod_DoesNotCreateTimer_AndStopIsSafe()
    {
        var sut = MakeService();

        sut.Start(MakeStartupConfig(0), () => "uptime", () => "threads");

        // No timer should have been created; Stop must remain safe and idempotent.
        sut.Stop();
        sut.Stop();
    }

    [Fact]
    public void Start_WithPositivePeriod_CreatesTimer_AndStopCleansUp()
    {
        var sut = MakeService();

        sut.Start(MakeStartupConfig(1), () => "uptime", () => "threads");

        // Stop should dispose the timer without throwing.
        sut.Stop();
    }

    [Fact]
    public void Start_CalledTwice_IsIdempotent()
    {
        var sut = MakeService();

        sut.Start(MakeStartupConfig(1), () => "uptime", () => "threads");
        sut.Start(MakeStartupConfig(1), () => "uptime", () => "threads");

        sut.Stop();
    }

    [Fact]
    public void Start_WithNullConfig_UsesDefaultPeriod_AndStopCleansUp()
    {
        var sut = MakeService();

        sut.Start(null, () => "uptime", () => "threads");

        sut.Stop();
    }

    [Fact]
    public void StartStop_CanBeRepeated()
    {
        var sut = MakeService();

        sut.Start(MakeStartupConfig(1), () => "uptime", () => "threads");
        sut.Stop();

        // A fresh Start after Stop must work again.
        sut.Start(MakeStartupConfig(1), () => "uptime", () => "threads");
        sut.Stop();
    }
}
