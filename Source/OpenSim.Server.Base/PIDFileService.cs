using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Base;

public class PIDFileService : IHostedService
{
    private readonly ILogger<PIDFileService> _logger;
    private readonly IConfiguration configuration;

    private bool isPidFileCreated = false;
    private string pidFile = string.Empty;

    public PIDFileService(
        ILogger<PIDFileService> logger,
        IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            pidFile = configuration.GetValue<string>("PidFile");

            if (string.IsNullOrWhiteSpace(pidFile))
                return;

            await WritePidFile();
            
            isPidFileCreated = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Unexpected error when starting {nameof(PIDFileService)}");
        }
    }

    private async Task WritePidFile()
    {
        var processId = Environment.ProcessId.ToString();
        await File.WriteAllTextAsync(pidFile, processId);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (isPidFileCreated)
                File.Delete(pidFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error when deleting PID file");
        }
        
        return Task.CompletedTask;
    }
}