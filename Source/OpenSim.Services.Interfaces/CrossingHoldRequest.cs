/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Services.Interfaces;

/// <summary>
/// Sent with an object that crosses into another region together with avatars (seated on it, or standing on it): the
/// receiving region keeps the object at rest, its scripts and motion not started, until every avatar listed has
/// arrived or the hold ran out, and then tells the sending region with ISimulationService.CrossingCompleted.
/// </summary>
public sealed class CrossingHoldRequest
{
    /// <summary>The region that sent the object, and that is told when the crossing is complete.</summary>
    public GridRegion Source { get; set; }

    /// <summary>The avatars that are crossing with the object and are expected to complete their arrival.</summary>
    public List<UUID> ExpectedRiders { get; set; } = new();
}
