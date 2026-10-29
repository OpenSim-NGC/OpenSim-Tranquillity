/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Server.Base.Hosting;

public sealed class ProcessSetupService : IProcessSetupService
{
    private readonly ILogger<ProcessSetupService> _logger;

    public ProcessSetupService(ILogger<ProcessSetupService> logger)
    {
        _logger = logger;
    }

    public void ApplyDefaults()
    {
        Apply(new ProcessSetupOptions());
    }

    public void Apply(ProcessSetupOptions options)
    {
        if (options.SetCurrentCulture)
            Culture.SetCurrentCulture();

        if (options.SetDefaultCurrentCulture)
            Culture.SetDefaultCurrentCulture();

        WebUtil.ConfigureHTTPDefaults(options.DefaultConnectionLimit, options.MaxServicePointIdleTime,
            options.DnsRefreshTimeout, options.Expect100Continue);

        if (options.UseNagleAlgorithm == true)
            _logger.LogWarning("UseNagleAlgorithm=true is not supported by the shared HTTP transport; TCP_NODELAY remains enabled.");

        if (options.ConfigureThreadPoolMaxThreads)
        {
            ThreadPool.GetMaxThreads(out int workerThreads, out int iocpThreads);

            int boundedWorkerThreads = Math.Max(options.MinWorkerThreads, Math.Min(workerThreads, options.MaxWorkerThreads));
            int boundedIocpThreads = Math.Max(options.MinIocpThreads, Math.Min(iocpThreads, options.MaxIocpThreads));

            if (!ThreadPool.SetMaxThreads(boundedWorkerThreads, boundedIocpThreads))
                _logger.LogWarning("Unable to set thread pool max threads to worker={WorkerThreads}, iocp={IocpThreads}", boundedWorkerThreads, boundedIocpThreads);
        }
    }
}
