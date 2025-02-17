using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.RobustServer
{
    public sealed class RobustService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RobustService> _logger;

        public RobustService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<RobustService> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is running.", nameof(RobustServer));
            Application.Start(Environment.GetCommandLineArgs());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is stopping.", nameof(RobustServer));

            // Nothing to do here
            // OpenSimServer.Shutdown(m_res);

            return Task.CompletedTask;
        }
    }
}
