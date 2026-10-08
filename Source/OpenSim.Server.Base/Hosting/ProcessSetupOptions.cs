/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Process-level startup settings that can be applied by hosted startup services.
/// HTTP settings apply when shared handlers are created during server startup.
/// </summary>
public sealed class ProcessSetupOptions
{
    public bool SetCurrentCulture { get; init; } = true;
    public bool SetDefaultCurrentCulture { get; init; } = true;

    public int? DefaultConnectionLimit { get; init; } = 32;
    public int? MaxServicePointIdleTime { get; init; } = 30000;
    /// <summary>Maximum pooled-connection lifetime in milliseconds; -1 disables expiration.</summary>
    public int? DnsRefreshTimeout { get; init; } = 180000;
    public bool? Expect100Continue { get; init; } = false;
    public bool? UseNagleAlgorithm { get; init; } = false;

    public bool ConfigureThreadPoolMaxThreads { get; init; } = false;
    public int MinWorkerThreads { get; init; } = 500;
    public int MaxWorkerThreads { get; init; } = 1000;
    public int MinIocpThreads { get; init; } = 1000;
    public int MaxIocpThreads { get; init; } = 2000;
}
