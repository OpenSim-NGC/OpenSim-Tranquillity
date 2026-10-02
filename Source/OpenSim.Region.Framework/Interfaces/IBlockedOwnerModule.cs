/*
 * Copyright (c) Legion Builds
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

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// A region-wide list of owners whose rezzing the region refuses. An owner on the list cannot rez,
/// create or duplicate objects in the region by any path: viewer, script (any engine) or capability.
/// The list starts empty; only a region operator adds to it.
/// </summary>
public interface IBlockedOwnerModule
{
    /// <summary>True if the region refuses rezzing by this owner.</summary>
    bool IsBlocked(UUID owner);

    /// <summary>Adds an owner to the region's list. False if the owner was already on it or is UUID.Zero.</summary>
    bool Block(UUID owner);

    /// <summary>Removes an owner from the region's list. False if the owner was not on it.</summary>
    bool Unblock(UUID owner);

    /// <summary>The owners on the region's list (not those blocked only through an estate ban).</summary>
    UUID[] GetBlockedOwners();
}
