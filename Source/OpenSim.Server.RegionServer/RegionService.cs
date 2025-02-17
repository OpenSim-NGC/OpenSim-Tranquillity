/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using log4net.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.RegionServer
{
    public sealed class RegionService : IHostedService
    {
        private readonly Task _completedTask = Task.CompletedTask;

        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RegionService> _logger;

        public RegionService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<RegionService> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is running.", nameof(RegionServer));

            XmlConfigurator.Configure();
            //Application.Configure(args);
            Application.Start(Environment.GetCommandLineArgs());

            return _completedTask;
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is stopping.", nameof(RegionServer));

            // Nothing to do here
            // OpenSimServer.Shutdown(m_res);

            return _completedTask;
        }
    }
}
