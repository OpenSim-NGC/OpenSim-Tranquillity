/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Provisions the GridServer's TLS certificate at startup (optional self-signed
/// certificate creation/renewal), driven by the <c>[Startup]</c> configuration.
/// Extracted from the legacy <c>ServicesServerBase</c> bootstrap so certificate
/// provisioning can be composed and tested independently, and so it runs before the
/// HTTP listeners are created.
/// </summary>
public interface IGridCertificateProvisioner
{
    /// <summary>
    /// Performs any configured certificate provisioning. Must be called before the
    /// HTTP listeners are created so newly created/renewed certificates are available.
    /// </summary>
    /// <param name="startupConfig">The <c>[Startup]</c> config section.</param>
    void Provision(IConfig startupConfig);
}
