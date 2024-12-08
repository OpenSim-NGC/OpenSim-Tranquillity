using log4net.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.MoneyServer
{
    public sealed class MoneyService : IHostedService
    {
        private readonly Task _completedTask = Task.CompletedTask;

        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MoneyService> _logger;

        private int m_res;

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

            return _completedTask;
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is stopping.", nameof(MoneyServer));

            // Nothing to do here
            // OpenSimServer.Shutdown(m_res);

            return _completedTask;
        }
    }
}
