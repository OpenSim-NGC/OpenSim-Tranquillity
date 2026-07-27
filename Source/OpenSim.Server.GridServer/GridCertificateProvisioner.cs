/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Default <see cref="IGridCertificateProvisioner"/> implementation. Contains the
/// self-signed certificate provisioning logic that previously lived inline in the
/// legacy <c>ServicesServerBase</c> constructor; behaviour is preserved verbatim and
/// merely composed out of the inheritance chain. The filesystem and certificate
/// operations are exposed as overridable seams so the configuration-driven decision
/// logic can be tested without touching disk.
/// </summary>
public sealed class GridCertificateProvisioner : IGridCertificateProvisioner
{
    /// <summary>
    /// Probe for an existing certificate file. Defaults to <see cref="File.Exists"/>.
    /// </summary>
    public Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>
    /// Creates or renews a self-signed certificate. Defaults to
    /// <see cref="Util.CreateOrUpdateSelfsignedCert"/>.
    /// </summary>
    public Action<string, string, string, string> CreateOrUpdateSelfsignedCert { get; set; } = Util.CreateOrUpdateSelfsignedCert;

    public void Provision(IConfig startupConfig)
    {
        if (!startupConfig.GetBoolean("EnableRobustSelfsignedCertSupport", false))
            return;

        if (!FileExists("SSL\\ssl\\" + startupConfig.GetString("RobustCertFileName") + ".p12")
            || startupConfig.GetBoolean("RobustCertRenewOnStartup"))
        {
            CreateOrUpdateSelfsignedCert(
                string.IsNullOrEmpty(startupConfig.GetString("RobustCertFileName")) ? "Robust" : startupConfig.GetString("RobustCertFileName"),
                string.IsNullOrEmpty(startupConfig.GetString("RobustCertHostName")) ? "localhost" : startupConfig.GetString("RobustCertHostName"),
                string.IsNullOrEmpty(startupConfig.GetString("RobustCertHostIp")) ? "127.0.0.1" : startupConfig.GetString("RobustCertHostIp"),
                string.IsNullOrEmpty(startupConfig.GetString("RobustCertPassword")) ? string.Empty : startupConfig.GetString("RobustCertPassword")
            );
        }
    }
}
