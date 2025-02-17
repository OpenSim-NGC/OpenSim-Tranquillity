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

namespace OpenSim.Server.MoneyServer
{
    public sealed class MoneyService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MoneyService> _logger;

        public MoneyService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<MoneyService> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is running.", nameof(MoneyServer));

            // Application.ServiceProvider = _serviceProvider;
            XmlConfigurator.Configure();
            MoneyServerBase app = new MoneyServerBase();
            app.Startup();
            app.Work();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is stopping.", nameof(MoneyServer));

            // Nothing to do here
            // OpenSimServer.Shutdown(m_res);

            return Task.CompletedTask;
        }
    }
}
