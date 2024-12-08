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

        private int m_res;

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
