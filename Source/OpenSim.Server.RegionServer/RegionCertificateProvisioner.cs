/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionCertificateProvisioner"/> implementation. Contains the
/// certificate provisioning logic that previously lived inline in
/// <see cref="OpenSimBase.Initialize"/>; behavior is preserved verbatim and merely
/// composed out of the inheritance chain. The filesystem and certificate operations
/// are exposed as overridable seams so the configuration-driven decision logic can be
/// tested without touching disk.
/// </summary>
public sealed class RegionCertificateProvisioner : IRegionCertificateProvisioner
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

    /// <summary>
    /// Converts a PEM certificate to PKCS12. Defaults to
    /// <see cref="Util.ConvertPemToPKCS12(string, string, string, string)"/>.
    /// </summary>
    public Action<string, string, string, string> ConvertPemToPKCS12 { get; set; } = Util.ConvertPemToPKCS12;

    public void Provision(IConfig startupConfig)
    {
        // Sure is not the right place for this but do the job...
        // Must always be called before (all) / the HTTP servers starting for the Certs creation or renewals.
        if (startupConfig.GetBoolean("EnableSelfsignedCertSupport", false))
        {
            if (!FileExists("SSL\\ssl\\" + startupConfig.GetString("CertFileName") + ".p12") || startupConfig.GetBoolean("CertRenewOnStartup"))
            {
                CreateOrUpdateSelfsignedCert(
                    string.IsNullOrEmpty(startupConfig.GetString("CertFileName")) ? "OpenSim" : startupConfig.GetString("CertFileName"),
                    string.IsNullOrEmpty(startupConfig.GetString("CertHostName")) ? "localhost" : startupConfig.GetString("CertHostName"),
                    string.IsNullOrEmpty(startupConfig.GetString("CertHostIp")) ? "127.0.0.1" : startupConfig.GetString("CertHostIp"),
                    string.IsNullOrEmpty(startupConfig.GetString("CertPassword")) ? string.Empty : startupConfig.GetString("CertPassword")
                );
            }
        }

        if (startupConfig.GetBoolean("EnableCertConverter", false))
        {
            ConvertPemToPKCS12(
               string.IsNullOrEmpty(startupConfig.GetString("outputCertName")) ? "letsencrypt" : startupConfig.GetString("outputCertName"),
               string.IsNullOrEmpty(startupConfig.GetString("PemCertPublicKey")) ? string.Empty : startupConfig.GetString("PemCertPublicKey"),
               string.IsNullOrEmpty(startupConfig.GetString("PemCertPrivateKey")) ? string.Empty : startupConfig.GetString("PemCertPrivateKey"),
               string.IsNullOrEmpty(startupConfig.GetString("outputCertPassword")) ? string.Empty : startupConfig.GetString("outputCertPassword")
           );
        }
    }
}
