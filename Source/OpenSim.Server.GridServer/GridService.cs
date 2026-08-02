/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Runtime.InteropServices;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Server.Base.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Host-lifetime orchestration for GridServer.
/// </summary>
/// <remarks>
/// All infrastructure boot and shutdown work is delegated to
/// <see cref="IGridServerRuntime"/>. This service only coordinates startup and
/// shutdown through the generic host and never takes ownership of the process
/// lifetime or performs side effects in its constructor.
/// </remarks>
public class GridService : IHostedService
{
    private readonly ILogger<GridService> _logger;
    private readonly IServerBase _serverBase;
    private readonly IStartupFailureCoordinator _startupFailureCoordinator;
    private readonly IGridServerRuntime _runtime;

    public GridService(
        ILogger<GridService> logger,
        IServerBase serverBase,
        IStartupFailureCoordinator startupFailureCoordinator,
        IGridServerRuntime runtime)
    {
        _logger = logger;
        _serverBase = serverBase;
        _startupFailureCoordinator = startupFailureCoordinator;
        _runtime = runtime;
    }

    public Task StartAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} is running.", nameof(GridService));

        _runtime.Initialize();
        Startup();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} is stopping.", nameof(GridService));

        _runtime.Stop();

        return Task.CompletedTask;
    }

    public virtual void Startup()
    {
        _logger.LogInformation("[STARTUP]: Beginning startup processing");
        _logger.LogInformation("[STARTUP]: Version: " + _serverBase.Version);
        _logger.LogInformation($"[STARTUP]: Operating system version: {Environment.OSVersion}, .NET platform {Util.RuntimePlatformStr}, Runtime {Environment.Version}");
        _logger.LogInformation($"[STARTUP]: Processor Architecture: {RuntimeInformation.ProcessArchitecture}({(BitConverter.IsLittleEndian ? "le" : "be")} {(Environment.Is64BitProcess ? "64" : "32")}bit)");
        _logger.LogInformation($"[STARTUP]: Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB");

        try
        {
            _serverBase.RegisterCommonCommands();
            _serverBase.RegisterCommonComponents(_serverBase.Config);
        }
        catch (Exception e)
        {
            _startupFailureCoordinator.ThrowFatal("Fatal error while registering startup components.", e);
        }
    }
}
