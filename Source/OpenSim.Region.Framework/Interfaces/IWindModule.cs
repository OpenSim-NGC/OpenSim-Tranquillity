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

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// How a wind setter treats a cell. The values are those of the LSL constants WIND_SPEED_DEFAULT,
/// WIND_SPEED_FIXED and WIND_SPEED_TERRAIN_TURBULENCE.
/// </summary>
public enum WindConstants : int
{
    WindSpeedDefault = 0,
    WindSpeedFixed = 1,
    WindSpeedTurbulence = 2,
}

public interface IWindModule : INonSharedRegionModule
{

    /// <summary>
    /// Retrieves the current wind speed at the given Region Coordinates: the wind model's wind plus the wind
    /// volumes' wind there (<see cref="WindVolumes"/>, when weather is on). The vertical part is the volumes' alone.
    /// </summary>
    Vector3 WindSpeed(int x, int y, int z);

    /// <summary>
    /// The same at a position that need not lie on the integer grid, in region coordinates. The wind model sees the
    /// true position, so its own interpolation is not stepped, and so is the weather volumes' falloff. Never waits and
    /// allocates nothing, so a physics step may call it. A position that is not a finite number gives zero.
    /// At a whole-number position it equals <see cref="WindSpeed(int,int,int)"/>.
    /// A module that only knows the integer form gets this form for free, flooring the position.
    /// </summary>
    Vector3 WindSpeedAt(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return Vector3.Zero;

        // Far outside any region; the clamp keeps the int conversion defined.
        const float Limit = 1e6f;
        return WindSpeed(
            (int)MathF.Floor(Math.Clamp(position.X, -Limit, Limit)),
            (int)MathF.Floor(Math.Clamp(position.Y, -Limit, Limit)),
            (int)MathF.Floor(Math.Clamp(position.Z, -Limit, Limit)));
    }

    /// <summary>
    /// Sets (or unsets) the wind at the given region coordinates, for the wind plugins that support it.
    /// The plugin treats the setting as in-memory state: it is not kept across a region restart.
    /// </summary>
    /// <param name="type">A <see cref="WindConstants"/> value</param>
    /// <param name="loc">Region coordinates; the cell containing them is changed</param>
    /// <param name="speed">The wind to hold in the cell when the type is fixed</param>
    void WindSet(int type, Vector3 loc, Vector3 speed);

    /// <summary>
    /// Set Wind Plugin Parameter
    /// </summary>
    void WindParamSet(string plugin, string param, float value);

    /// <summary>
    /// Get Wind Plugin Parameter
    /// </summary>
    float WindParamGet(string plugin, string param);

    /// <summary>
    /// Current active wind model plugin or String.Empty
    /// </summary>
    string WindActiveModelPluginName { get; }

    /// <summary>
    /// The region's wind volumes (the [Weather] section), or null for a module that has none. When
    /// <see cref="IWindVolumeStore.Enabled"/> is false the store accepts no volumes and nothing about the wind changes.
    /// A module that has a store adds it to what <see cref="WindSpeed(int,int,int)"/> and <see cref="WindSpeedAt"/> return.
    /// </summary>
    IWindVolumeStore WindVolumes => null;
}
