/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionStartupScriptService"/> implementation. Contains the
/// startup/shutdown/timed-script orchestration that previously lived inline in
/// <see cref="OpenSim"/>; behavior is preserved verbatim and merely composed out of
/// the inheritance chain. The protected <c>RunCommandScript</c> on the server base is
/// bridged in via a delegate.
/// </summary>
public sealed class RegionStartupScriptService : IRegionStartupScriptService
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly Action<string> _runCommandScript;

    private System.Timers.Timer _scriptTimer;
    private string _timedScript = "disabled";

    public RegionStartupScriptService(Action<string> runCommandScript)
    {
        _runCommandScript = runCommandScript;
    }

    public void RunStartupScript(string startupCommandsFile)
    {
        if (String.IsNullOrEmpty(startupCommandsFile))
        {
            m_log.LogInformation("[STARTUP]: No startup command script specified. Moving on...");
        }
        else
        {
            _runCommandScript(startupCommandsFile);
        }
    }

    public void StartTimerScript(string timedScript, int timeIntervalSeconds)
    {
        _timedScript = timedScript;

        // Start timer script (run a script every xx seconds)
        if (_timedScript != "disabled")
        {
            _scriptTimer = new System.Timers.Timer()
            {
                Enabled = true,
                Interval = timeIntervalSeconds * 1000,
            };
            _scriptTimer.Elapsed += RunAutoTimerScript;
        }
    }

    public void RunShutdownScript(string shutdownCommandsFile)
    {
        if (shutdownCommandsFile != String.Empty)
        {
            _runCommandScript(shutdownCommandsFile);
        }
    }

    public void Stop()
    {
        if (_timedScript != "disabled")
        {
            _scriptTimer.Dispose();
            _timedScript = "disabled";
        }
    }

    /// <summary>
    /// Timer to run a specific text file as console commands.  Configured in the main ini file.
    /// </summary>
    private void RunAutoTimerScript(object sender, EventArgs e)
    {
        if (_timedScript != "disabled")
        {
            _runCommandScript(_timedScript);
        }
    }
}
