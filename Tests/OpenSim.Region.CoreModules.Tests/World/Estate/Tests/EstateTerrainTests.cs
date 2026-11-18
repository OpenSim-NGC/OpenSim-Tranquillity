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


using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Estate.Tests;

/// <summary>
/// The estate tools' terrain: the limits and water height they set, and a terrain file they upload.
/// </summary>
public class EstateTerrainTests : OpenSimTestCase
{
    // The limits and the water height were stored as sent: a limit that is not a number turned the limits off, as
    // every comparison with it is false.
    [Fact]
    public void TheTerrainLimitsAreNumbersInTheirRanges()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        EstateManagementModule estate = new EstateManagementModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), estate);
        RegionSettings rs = scene.RegionInfo.RegionSettings;

        estate.SetRegionTerrainSettings(20f, 4f, -4f, false, false, 0, false, false, 0);
        Assert.Equal((20.0, 4.0, -4.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));

        estate.SetRegionTerrainSettings(25f, float.NaN, -4f, false, false, 0, false, false, 0);
        estate.SetRegionTerrainSettings(float.PositiveInfinity, 4f, -4f, false, false, 0, false, false, 0);
        Assert.Equal((20.0, 4.0, -4.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));

        estate.SetRegionTerrainSettings(500f, -3f, 7f, false, false, 0, false, false, 0);
        Assert.Equal((100.0, 0.0, 0.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));
        estate.SetRegionTerrainSettings(-5f, 400f, -400f, false, false, 0, false, false, 0);
        Assert.Equal((0.0, 100.0, -100.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));
    }
}
