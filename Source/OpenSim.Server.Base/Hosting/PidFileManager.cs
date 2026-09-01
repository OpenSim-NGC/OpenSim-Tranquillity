/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Base.Hosting;

public sealed class PidFileManager : IPidFileManager
{
    private readonly ILogger<PidFileManager> _logger;

    public PidFileManager(ILogger<PidFileManager> logger)
    {
        _logger = logger;
    }

    public string ActivePath { get; private set; } = string.Empty;

    public void Create(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path))
            _logger.LogError("Previous pid file {Path} still exists on startup. Possibly previously unclean shutdown.", path);

        try
        {
            string pidString = Environment.ProcessId.ToString();

            using FileStream fs = File.Create(path);
            byte[] buf = Encoding.ASCII.GetBytes(pidString);
            fs.Write(buf, 0, buf.Length);

            ActivePath = path;
            _logger.LogInformation("Created pid file {Path}", ActivePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create PID file at {Path}", path);
        }
    }

    public void Remove()
    {
        if (string.IsNullOrEmpty(ActivePath))
            return;

        try
        {
            File.Delete(ActivePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while removing PID file {Path}", ActivePath);
        }

        ActivePath = string.Empty;
    }
}

public sealed class PidFileHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IPidFileManager _pidFileManager;

    public PidFileHostedService(IConfiguration configuration, IPidFileManager pidFileManager)
    {
        _configuration = configuration;
        _pidFileManager = pidFileManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string pidPath = _configuration["Startup:PIDFile"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pidPath))
            pidPath = _configuration["PIDFile"] ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(pidPath))
            _pidFileManager.Create(pidPath);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pidFileManager.Remove();
        return Task.CompletedTask;
    }
}
