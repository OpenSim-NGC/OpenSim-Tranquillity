/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using OpenSim.Framework.Servers;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// A <see cref="ServerBase"/> that bridges the legacy console shutdown path to the
/// generic host lifetime.
/// </summary>
/// <remarks>
/// In the legacy model the interactive "quit"/"shutdown" console commands (registered by
/// <see cref="ServerBase.RegisterCommonCommands"/>) and the Ctrl-C handler both call
/// <see cref="ServerBase.Shutdown"/>, whose process exit was provided by a derived class
/// overriding <c>ShutdownSpecific()</c> (for example <c>ServicesServerBase</c> called
/// <c>Environment.Exit</c>).
///
/// The hosted servers register a plain <see cref="ServerBase"/> instance for DI, which no
/// longer inherits that behaviour, so typing "shutdown" did nothing and the process kept
/// running. This class restores the extension point by translating the shutdown request into
/// <see cref="IHostApplicationLifetime.StopApplication"/>, letting the host stop every hosted
/// service (including the console runner) and exit cleanly — without calling
/// <c>Environment.Exit</c>.
///
/// <see cref="HostLifetime"/> is assigned after the host is built (the lifetime is only
/// available then). Until it is set, shutdown requests are safely ignored.
/// </remarks>
public class HostLifetimeServerBase : ServerBase
{
    /// <summary>
    /// The host application lifetime used to request a cooperative shutdown. Assigned by the
    /// composition root after <c>IHostBuilder.Build()</c>.
    /// </summary>
    public IHostApplicationLifetime HostLifetime { get; set; }

    protected override void ShutdownSpecific()
    {
        base.ShutdownSpecific();
        HostLifetime?.StopApplication();
    }
}
