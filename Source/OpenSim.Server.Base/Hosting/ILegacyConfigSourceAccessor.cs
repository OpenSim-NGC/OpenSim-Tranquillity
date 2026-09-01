/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Exposes a legacy Nini <see cref="IConfigSource"/> built from the canonical
/// hosted startup options.
/// </summary>
public interface ILegacyConfigSourceAccessor
{
    /// <summary>
    /// Legacy Nini configuration source used by older OpenSim runtime components.
    /// </summary>
    IConfigSource ConfigSource { get; }
}
